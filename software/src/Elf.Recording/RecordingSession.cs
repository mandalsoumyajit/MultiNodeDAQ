using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Elf.Protocol;
namespace Elf.Recording;

public interface ILogOutput : IDisposable
{
    long Position { get; }
    void Write(byte[] bytes);
    void FlushDurable();
}
public sealed class DiskLogOutput(string path) : ILogOutput
{
    private readonly FileStream stream = new(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read,65536,FileOptions.SequentialScan);
    public long Position => stream.Position;
    public void Write(byte[] bytes) => stream.Write(bytes);
    public void FlushDurable() => stream.Flush(true);
    public void Dispose() => stream.Dispose();
}
public sealed record RecordingOptions(string Directory, long QueueBytes=64*1024*1024, long PerUnitBytes=4*1024*1024,
    long SegmentBytes=256*1024*1024, double FlushSeconds=1, double DrainSeconds=15,
    Func<string,ILogOutput>? OutputFactory=null);

public sealed class RecordingSession : IAsyncDisposable
{
    private readonly RecordingOptions options;
    private readonly object gate=new();
    private readonly Dictionary<string,UnitWriter> writers=[];
    private readonly List<object> segments=[];
    private readonly Queue<string> problems=[];
    private readonly CancellationTokenSource timerStop=new();
    private readonly Task timer;
    private long queued,peak;
    private bool closed,failed,finished;
    private string state="recording";
    public byte[] Id {get;}=Guid.NewGuid().ToByteArray(true);
    public string DirectoryPath {get;}
    public RecordingSession(RecordingOptions options)
    {
        Wire.Check(options.QueueBytes>=4096 && options.PerUnitBytes>=4096 && options.PerUnitBytes<=options.QueueBytes && options.SegmentBytes>=4096 && options.FlushSeconds>0 && options.DrainSeconds>0,"recording options");
        this.options=options;DirectoryPath=Path.GetFullPath(options.Directory);
        Wire.Check(!Directory.Exists(DirectoryPath),"recording directory must be new");Directory.CreateDirectory(DirectoryPath);
        SaveManifest();timer=Task.Run(Tick);
    }
    public bool Failed {get{lock(gate)return failed;}}
    private bool Reserve(int bytes)
    {lock(gate){if(closed || failed || queued+bytes>options.QueueBytes)return false;queued+=bytes;peak=Math.Max(peak,queued);return true;}}
    private void Release(int bytes){lock(gate)queued-=bytes;}
    public void Fail(string reason)
    {lock(gate){failed=true;state="faulted";problems.Enqueue(reason);while(problems.Count>64)problems.Dequeue();}}
    public bool Accept(Frame frame)
    {
        UnitWriter writer;string key=Convert.ToHexString(frame.Unit)+":"+Convert.ToHexString(frame.Session);
        lock(gate)
        {
            if(closed || failed)return false;
            if(!writers.TryGetValue(key,out writer!))
            {
                if(frame.Kind!=1 || writers.Count>=128){Fail("new recording stream requires HELLO and an available registry slot");return false;}
                writer=new(this,frame.Unit,frame.Session);writers.Add(key,writer);
            }
        }
        if(!writer.Enqueue(new(frame,null,null,frame.Kind==2?checked((int)frame.Count*12+256):frame.Payload.Length+256)))
        {Fail("recording queue capacity exceeded; recording is incomplete");return false;}
        return true;
    }
    public void Event(string unit,string session,string kind,object details)
    {
        UnitWriter? writer;lock(gate)writers.TryGetValue(unit+":"+session,out writer);
        if(writer is not null && !writer.Enqueue(new(null,kind,details,512)))Fail("event queue capacity exceeded");
    }
    public ulong Committed(string unit,string session)
    {UnitWriter? writer;lock(gate)writers.TryGetValue(unit+":"+session,out writer);return writer?.Committed??0;}
    private async Task Tick()
    {
        try
        {
            while(true)
            {
                await Task.Delay(TimeSpan.FromSeconds(options.FlushSeconds),timerStop.Token);
                UnitWriter[] all;lock(gate)all=writers.Values.ToArray();foreach(var w in all)w.FlushRequest();
                SaveManifest();
            }
        }
        catch(OperationCanceledException){}
        catch(Exception e){Fail("manifest/timer: "+e.Message);}
    }
    private void Segment(object summary)
    {lock(gate){Wire.Check(segments.Count<4096,"segment registry capacity");segments.Add(summary);}}
    public object Snapshot()
    {
        UnitWriter[] all;object[] closedSegments;string[] issues;string current;long bytes,high;
        lock(gate){all=writers.Values.ToArray();closedSegments=segments.ToArray();issues=problems.ToArray();current=state;bytes=queued;high=peak;}
        return new{recording_session=Convert.ToHexString(Id).ToLowerInvariant(),state=current,directory=DirectoryPath,
            queued_bytes=bytes,peak_queued_bytes=high,queue_limit=options.QueueBytes,per_unit_limit=options.PerUnitBytes,
            flush_seconds=options.FlushSeconds,errors=issues,units=all.Select(w=>w.Snapshot()).ToArray(),segments=closedSegments,
            software=new{version=typeof(RecordingSession).Assembly.GetName().Version?.ToString(),build=System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(RecordingSession).Assembly)?.InformationalVersion,runtime=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription},
            durability="FileStream.Flush(true) completed before committed counter; power-loss behavior requires storage qualification"};
    }
    private void SaveManifest()
    {
        byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(Snapshot(),new JsonSerializerOptions{WriteIndented=true});
        string temp=Path.Combine(DirectoryPath,"manifest.pending.json");
        using(var file=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None)){file.Write(bytes);file.Flush(true);}
        File.Move(temp,Path.Combine(DirectoryPath,"manifest.json"),true);
    }
    public async Task CompleteAsync(bool clean=true)
    {
        lock(gate){if(closed)return;closed=true;state=failed?"faulted":"draining";}
        timerStop.Cancel();await timer;
        if(!clean)Fail("acquisition did not finish a confirmed drain");
        UnitWriter[] all;lock(gate)all=writers.Values.ToArray();foreach(var w in all)w.Complete();
        try{await Task.WhenAll(all.Select(w=>w.Task)).WaitAsync(TimeSpan.FromSeconds(options.DrainSeconds));}
        catch(TimeoutException){Fail("recording drain timeout; files may have incomplete tails");}
        lock(gate){finished=true;state=failed?"faulted":"complete";}
        try{SaveManifest();}catch(Exception e){Fail("final manifest: "+e.Message);}
    }
    public async ValueTask DisposeAsync(){if(!finished)await CompleteAsync(false);timerStop.Dispose();}

    private sealed record Work(Frame? Frame,string? Event,object? Details,int Cost);
    private sealed class UnitWriter
    {
        private readonly RecordingSession owner;
        private readonly byte[] unit,session;
        private readonly string unitText,sessionText;
        private readonly Channel<Work> channel=Channel.CreateBounded<Work>(new BoundedChannelOptions(2048){SingleReader=true,FullMode=BoundedChannelFullMode.Wait});
        private readonly object queueGate=new();
        private readonly Dictionary<string,Frame> definitions=[];
        private long definitionBytes;
        private readonly Dictionary<uint,(uint Rate,ushort Encoding)> configs=[];
        private readonly RangeLedger ledger;
        private long queuedBytes,peakBytes; private bool flushPending;
        private ILogOutput? output;private string filename="";private int number;
        private ulong records,segmentRows,writtenRows,duplicates,committed,flushedRows;
        public ulong Committed {get{lock(queueGate)return committed;}}
        public Task Task {get;}
        public UnitWriter(RecordingSession owner,byte[] unit,byte[] session)
        {
            this.owner=owner;this.unit=unit;this.session=session;unitText=Convert.ToHexString(unit).ToLowerInvariant();sessionText=Convert.ToHexString(session).ToLowerInvariant();
            ledger=new(owner.DirectoryPath);Task=System.Threading.Tasks.Task.Run(Run);
        }
        public object Snapshot(){lock(queueGate)return new{unit=unitText,acquisition_session=sessionText,queued_bytes=queuedBytes,peak_bytes=peakBytes,written_rows=writtenRows,flushed_rows=flushedRows,committed_next_sample=committed,duplicate_frames=duplicates,active_segment=filename};}
        public bool Enqueue(Work work)
        {
            lock(queueGate)
            {
                if(queuedBytes+work.Cost>owner.options.PerUnitBytes || !owner.Reserve(work.Cost))return false;
                queuedBytes+=work.Cost;peakBytes=Math.Max(peakBytes,queuedBytes);
                if(channel.Writer.TryWrite(work))return true;
                queuedBytes-=work.Cost;owner.Release(work.Cost);return false;
            }
        }
        public void FlushRequest()
        {
            lock(queueGate)
            {
                if(flushPending)return;flushPending=true;
                if(!Enqueue(new(null,"flush",null,128)))flushPending=false;
            }
        }
        public void Complete()=>channel.Writer.TryComplete();
        private byte[] Json(object body)=>JsonSerializer.SerializeToUtf8Bytes(body);
        private void Event(string kind,object details)=>Append(2,Json(new{ @event=kind,monotonic_ns=((ulong)(Stopwatch.GetTimestamp()*(1e9/Stopwatch.Frequency))).ToString(),details}));
        private void Append(ushort kind,byte[] payload)
        {output!.Write(LogFormat.Record(kind,payload));records++;}
        private void Open()
        {
            filename=$"{unitText}-{sessionText}-{number++:D5}.elflog";
            output=(owner.options.OutputFactory??(p=>new DiskLogOutput(p)))(Path.Combine(owner.DirectoryPath,filename));output.Write(LogFormat.Header(owner.Id));records=0;segmentRows=0;
            Event("recording_started",new{unit=unitText,acquisition_session=sessionText,segment=number-1,
                prior_extent=ledger.Extent.ToString(),prior_ranges=ledger.Ranges.Select(r=>new{first=r.First.ToString(),end=r.End.ToString()}).ToArray(),
                software_build=System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(RecordingSession).Assembly)?.InformationalVersion});
            foreach(var frame in definitions.Values)Append(1,Wire.Encode(frame));
        }
        private void Flush()
        {
            if(output is null)return;
            Append(3,Json(new{through_offset=output.Position.ToString(),unit=unitText,acquisition_session=sessionText,next_sample=ledger.Contiguous.ToString()}));
            output.FlushDurable();lock(queueGate){committed=ledger.Contiguous;flushedRows=writtenRows;}
        }
        private void CloseSegment()
        {
            if(output is null)return;Flush();string status=owner.Failed?"incomplete":"complete";
            Append(4,Json(new{status,records=records.ToString(),sample_rows=segmentRows.ToString()}));output.FlushDurable();output.Dispose();output=null;
            string path=Path.Combine(owner.DirectoryPath,filename);using var file=File.OpenRead(path);
            owner.Segment(new{file=filename,unit=unitText,acquisition_session=sessionText,segment=number-1,status,rows=segmentRows,bytes=file.Length,sha256=Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant()});
        }
        private void Define(string key,Frame frame)
        {
            long next=definitionBytes-(definitions.TryGetValue(key,out var old)?old.Payload.Length+100:0)+frame.Payload.Length+100;
            Wire.Check(next<=1_048_576 && (definitions.Count<130 || definitions.ContainsKey(key)),"record metadata byte capacity");
            definitions[key]=frame;definitionBytes=next;
        }
        private void Process(Frame raw)
        {
            Wire.Validate(raw);
            if(raw.Kind!=2)
            {
                var o=Metadata.Read(raw);
                if(raw.Kind==1)Define("hello",raw);
                if(raw.Kind==5 && Metadata.Bool(o,"ok") && Metadata.Field(o,"details").TryGetProperty("config",out var c))
                {
                    uint id=Metadata.Number(c,"id"),rate=Metadata.Number(c,"sample_rate_hz"),enc=Metadata.Number(c,"encoding");
                    Wire.Check(id>0 && rate is >=1 and <=1_000_000 && enc is 1 or 2,"record config");
                    Wire.Check(!configs.TryGetValue(id,out var old)||old==(rate,(ushort)enc),"record config mutation");
                    Wire.Check(configs.Count<64 || configs.ContainsKey(id),"record config capacity");configs[id]=(rate,(ushort)enc);Define("config"+id,raw);
                }
                if(raw.Kind==6){Wire.Check(definitions.Count<130 || definitions.ContainsKey("timing"+raw.Timing),"record timing capacity");Define("timing"+raw.Timing,raw);}
            }
            if(output is null)Open();
            if(raw.Kind==2)
            {
                Wire.Check(configs.TryGetValue(raw.Config,out var config) && config==(raw.Rate,raw.Encoding),"record DATA before configuration");
                Frame frame=Wire.Normalize(raw);
                if(output!.Position>=owner.options.SegmentBytes){CloseSegment();Open();}
                if(!ledger.Add(frame)){lock(queueGate)duplicates++;Event("duplicate_suppressed",new{first=frame.FirstSample.ToString(),count=frame.Count});return;}
                Append(1,Wire.Encode(frame));segmentRows+=frame.Count;lock(queueGate)writtenRows+=frame.Count;
            }
            else
            {
                Append(1,Wire.Encode(raw));var o=Metadata.Read(raw);
                if(raw.Kind==7)ledger.ObserveEnd(Metadata.Counter(o,"first_sample")+Metadata.Counter(o,"count"));
                if(raw.Kind==3 && Metadata.State(o)=="idle" && Metadata.Number(o,"buffer_rows")==0)ledger.ObserveEnd(Metadata.Counter(o,"next_sample"));
            }
        }
        private async Task Run()
        {
            try
            {
                await foreach(var work in channel.Reader.ReadAllAsync())
                {
                    try
                    {
                        if(work.Frame is not null)Process(work.Frame);
                        else if(work.Event=="flush"){Flush();lock(queueGate)flushPending=false;}
                        else {if(output is null)Open();Event(work.Event!,work.Details!);}
                    }
                    finally{lock(queueGate)queuedBytes-=work.Cost;owner.Release(work.Cost);}
                }
                CloseSegment();
            }
            catch(Exception e){owner.Fail($"{unitText}/{sessionText}: {e.Message}");channel.Writer.TryComplete(e);}
            finally
            {
                try{output?.Dispose();}catch(Exception e){owner.Fail("close: "+e.Message);}ledger.Dispose();
                while(channel.Reader.TryRead(out var pending)){lock(queueGate)queuedBytes-=pending.Cost;owner.Release(pending.Cost);}
            }
        }
    }
}
