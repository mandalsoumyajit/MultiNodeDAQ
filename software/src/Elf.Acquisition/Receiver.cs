using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Elf.Protocol;
namespace Elf.Acquisition;

public sealed record ReceiverOptions(string Address="127.0.0.1",int Port=45100,int MaxConnections=32,int MaxSessions=128,bool AutoStart=true,bool VerifyCounter=true,int FrameTimeoutSeconds=15);
public sealed class SessionStats
{
    public required string Unit {get;init;}
    public required string Session {get;init;}
    public string Label {get;set;}="";
    public string State {get;set;}="idle";
    public bool Connected {get;set;}
    public bool Synthetic {get;set;}
    public string Mode {get;set;}="unknown";
    public uint Seed {get;set;}
    public ulong Frames,Rows,Bytes,NextSample,Missing,Recovered,Duplicates,Conflicts,ReportedDrops,SequenceGaps,SampleErrors;
    public int Reconnects,ParserCapacity;
    public long LastDataTicks,FirstDataTicks;
    public uint BufferRows;
    internal ulong LastSequence,HostSequence,Request;
    internal readonly Dictionary<uint,(uint Rate,ushort Encoding)> Configs=[];
    internal readonly Dictionary<uint,string> Timings=[];
    internal readonly Dictionary<ulong,(ulong End,byte[] Hash)> Recent=[];
    internal readonly Queue<ulong> RecentOrder=[];
    internal readonly List<(ulong Start,ulong End)> Holes=[];
}
public sealed class Receiver : IAsyncDisposable
{
    private readonly ReceiverOptions options;
    private readonly TcpListener listener;
    private readonly CancellationTokenSource stop=new();
    private readonly object gate=new();
    private readonly Dictionary<string,SessionStats> sessions=[];
    private readonly Dictionary<string,Connection> active=[];
    private readonly HashSet<Task> tasks=[];
    private readonly SemaphoreSlim slots;
    private Task? accept;
    private long rejected,errors,connections;
    private readonly Queue<string> diagnostics=[];
    private sealed class Pending(string op,object args,TaskCompletionSource<JsonElement>? completion=null)
    { public string Op=op;public JsonElement Args=JsonSerializer.SerializeToElement(args);public long Created=Stopwatch.GetTimestamp();public TaskCompletionSource<JsonElement>? Completion=completion; }
    private sealed class Connection(TcpClient client,SessionStats stats)
    {
        public TcpClient Client=client;public SessionStats Stats=stats;public readonly SemaphoreSlim Write=new(1,1);
        public readonly Dictionary<ulong,Pending> Pending=[];
    }
    public Receiver(ReceiverOptions options)
    {
        Wire.Check(options.MaxConnections is >=1 and <=128 && options.MaxSessions>=options.MaxConnections && options.MaxSessions<=4096,"receiver limits");
        this.options=options;listener=new(IPAddress.Parse(options.Address),options.Port);slots=new(options.MaxConnections);
    }
    public int Port=>((IPEndPoint)listener.LocalEndpoint).Port;
    public void Start(){listener.Start(options.MaxConnections);accept=AcceptLoop();}
    private void Diagnostic(string text){lock(gate){diagnostics.Enqueue(text);while(diagnostics.Count>64)diagnostics.Dequeue();}}
    private async Task AcceptLoop()
    {
        try
        {
            while(!stop.IsCancellationRequested)
            {
                var client=await listener.AcceptTcpClientAsync(stop.Token);
                if(!slots.Wait(0)){Interlocked.Increment(ref rejected);client.Dispose();continue;}
                client.NoDelay=true;client.ReceiveBufferSize=64*1024;client.SendBufferSize=16*1024;
                var task=Handle(client);lock(gate)tasks.Add(task);
                _=task.ContinueWith(t=>{lock(gate)tasks.Remove(t);slots.Release();},TaskScheduler.Default);
            }
        }
        catch(Exception e) when(e is OperationCanceledException or SocketException or ObjectDisposedException){if(!stop.IsCancellationRequested)Diagnostic(e.Message);}
    }
    private async Task<Frame?> Read(FrameStream parser,NetworkStream stream)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);deadline.CancelAfter(TimeSpan.FromSeconds(options.FrameTimeoutSeconds));
        return await parser.ReadAsync(stream,deadline.Token);
    }
    private async Task Handle(TcpClient client)
    {
        Connection? connection=null;
        using(client)using(var parser=new FrameStream())
        try
        {
            var stream=client.GetStream();var hello=await Read(parser,stream);Wire.Check(hello is not null && hello.Kind==1,"HELLO required");
            var h=Metadata.Read(hello!);string unit=Convert.ToHexString(hello!.Unit),sid=Convert.ToHexString(hello.Session),key=unit+":"+sid;
            lock(gate)
            {
                Wire.Check(!active.ContainsKey(unit),"duplicate active unit");
                if(!sessions.TryGetValue(key,out var stats))
                {
                    Wire.Check(sessions.Count<options.MaxSessions,"session registry full");
                    stats=new(){Unit=unit,Session=sid,Synthetic=Metadata.Bool(h,"synthetic"),Mode=h.TryGetProperty("mode",out var mode)?mode.GetString()??"unknown":"unknown",Seed=h.TryGetProperty("seed",out _)?Metadata.Number(h,"seed"):0};sessions.Add(key,stats);
                }
                else {Wire.Check(hello.Sequence>stats.LastSequence,"reconnect sequence");Wire.Check(stats.Synthetic==Metadata.Bool(h,"synthetic") && stats.Mode==(h.TryGetProperty("mode",out _)?Metadata.Text(h,"mode"):"unknown") && stats.Seed==(h.TryGetProperty("seed",out _)?Metadata.Number(h,"seed"):0),"session metadata changed");stats.Reconnects++;}
                stats.Label=h.TryGetProperty("label",out var label)?label.GetString()??unit:unit;
                stats.State=h.TryGetProperty("state",out _)?Metadata.State(h):"idle";stats.Connected=true;stats.LastSequence=hello.Sequence;
                // A continuing session reuses acknowledged configs; new sampling sessions cannot silently invent one.
                connection=new(client,stats);active.Add(unit,connection);connections++;
            }
            await SendFrame(connection,Metadata.Json(5,hello.Unit,hello.Session,0,0,new{request_id="0",ok=true,state=connection.Stats.State,effective_sample=connection.Stats.NextSample.ToString(),error=(string?)null,details=new{}}));
            if(options.AutoStart && connection.Stats.State=="idle")
            {
                uint encoding=h.TryGetProperty("preferred_encoding",out _)?Metadata.Number(h,"preferred_encoding"):1;
                await Issue(connection,"arm",new{config=new{id=hello.Config,sample_rate_hz=hello.Rate,encoding,synthetic=connection.Stats.Synthetic}},null);
            }
            else if(options.AutoStart && connection.Stats.State=="armed")await Issue(connection,"start",new{},null);
            int burst=0;
            while(!stop.IsCancellationRequested)
            {
                if(++burst%64==0)await Task.Yield();
                var f=await Read(parser,stream);if(f is null)break;
                Wire.Check(f.Unit.AsSpan().SequenceEqual(hello.Unit) && f.Session.AsSpan().SequenceEqual(hello.Session),"identity changed without handshake");
                lock(gate)
                {
                    Wire.Check(connection.Pending.Values.All(p=>Stopwatch.GetElapsedTime(p.Created).TotalSeconds<5),"command ACK timeout");
                    var s=connection.Stats;Wire.Check(f.Sequence>s.LastSequence,"nonmonotonic sequence");s.SequenceGaps+=f.Sequence-s.LastSequence-1;s.LastSequence=f.Sequence;s.ParserCapacity=parser.Capacity;
                }
                if(f.Kind==2)Data(connection.Stats,f);
                else
                {
                    var o=Metadata.Read(f);
                    if(f.Kind==5)
                    {
                        ulong id=Metadata.Counter(o,"request_id");Pending? pending;
                        lock(gate)
                        {
                            Wire.Check(connection.Pending.Remove(id,out pending),"unsolicited ACK");
                            var s=connection.Stats;s.State=Metadata.State(o);
                            if(pending!.Op=="arm" && Metadata.Bool(o,"ok"))
                            {
                                var c=Metadata.Field(Metadata.Field(o,"details"),"config");uint config=Metadata.Number(c,"id"),rate=Metadata.Number(c,"sample_rate_hz");uint encodingValue=Metadata.Number(c,"encoding");Wire.Check(encodingValue is 1 or 2,"ack encoding");ushort encoding=(ushort)encodingValue;
                                Wire.Check(config>0 && rate is >=1 and <=1_000_000 && (s.Configs.ContainsKey(config)||s.Configs.Count<64),"ack config");
                                var expected=Metadata.Field(pending.Args,"config");Wire.Check(config==Metadata.Number(expected,"id") && rate==Metadata.Number(expected,"sample_rate_hz") && encodingValue==Metadata.Number(expected,"encoding") && Metadata.Bool(c,"synthetic")==Metadata.Bool(expected,"synthetic"),"ACK configuration differs from request");
                                Wire.Check(!s.Configs.TryGetValue(config,out var old)||old==(rate,encoding),"mutated config");s.Configs[config]=(rate,encoding);
                            }
                        }
                        pending!.Completion?.TrySetResult(o);
                        if(pending.Op=="arm" && options.AutoStart && Metadata.Bool(o,"ok"))await Issue(connection,"start",new{},null);
                    }
                    else if(f.Kind==3)
                    {
                        lock(gate)
                        {
                            var s=connection.Stats;s.State=Metadata.State(o);s.ReportedDrops=Metadata.Counter(o,"dropped_rows");s.BufferRows=Metadata.Number(o,"buffer_rows");
                            // A drained idle source declares a terminal watermark, including a lost tail.
                            ulong next=Metadata.Counter(o,"next_sample");
                            if(s.State=="idle" && s.BufferRows==0 && next>s.NextSample)
                            {Wire.Check(s.Holes.Count<1024,"gap index capacity");s.Holes.Add((s.NextSample,next));s.Missing+=next-s.NextSample;s.NextSample=next;}
                        }
                    }
                    else if(f.Kind==6){lock(gate){var t=connection.Stats.Timings;Wire.Check(t.Count<64 || t.ContainsKey(f.Timing),"timing capacity");string raw=o.GetRawText();Wire.Check(!t.TryGetValue(f.Timing,out var prior)||prior==raw,"timing mutation");t[f.Timing]=raw;}}
                    else if(f.Kind==7){Diagnostic(unit+" reported GAP "+o.GetRawText());}
                    else throw new ContractException("unexpected node message");
                }
            }
        }
        catch(Exception e) when(e is ContractException or IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        { if(!stop.IsCancellationRequested){Interlocked.Increment(ref errors);Diagnostic(e.GetType().Name+": "+e.Message);} }
        finally
        {
            if(connection is not null)lock(gate)
            {
                active.Remove(connection.Stats.Unit);connection.Stats.Connected=false;
                foreach(var pending in connection.Pending.Values)pending.Completion?.TrySetException(new IOException("node disconnected"));connection.Pending.Clear();
            }
        }
    }
    private void Data(SessionStats s,Frame f)
    {
        byte[] digest=SHA256.HashData(Wire.Encode(Wire.Normalize(f) with{Sequence=0}));ulong end=f.FirstSample+f.Count;
        ulong bad=0;
        if(options.VerifyCounter && s.Synthetic && s.Mode=="counter")
        {
            var values=Wire.Samples(f);for(int i=0;i<values.Length;i++)
            {int expected=(int)(((f.FirstSample+(ulong)(i/3))*3+(ulong)(i%3)+s.Seed)%16777216)-8388608;if(values[i]!=expected)bad++;}
        }
        lock(gate)
        {
            Wire.Check(s.Configs.TryGetValue(f.Config,out var config) && config==(f.Rate,f.Encoding),"unacknowledged config");
            Wire.Check(f.Calibration==0,"calibration not supported in Stage 1");Wire.Check(f.Timing==0 || s.Timings.ContainsKey(f.Timing),"undefined timing model");
            if(s.FirstDataTicks==0)s.FirstDataTicks=Stopwatch.GetTimestamp();
            s.SampleErrors+=bad;s.Frames++;s.Bytes+=(ulong)f.Payload.Length;s.LastDataTicks=Stopwatch.GetTimestamp();
            if(s.Recent.TryGetValue(f.FirstSample,out var old))
            {
                if(old.End==end && old.Hash.AsSpan().SequenceEqual(digest)){s.Duplicates++;return;}
                s.Conflicts++;throw new ContractException("conflicting duplicate range");
            }
            if(f.FirstSample<s.NextSample)
            {
                int hole=s.Holes.FindIndex(x=>x.Start<=f.FirstSample && x.End>=end);
                Wire.Check(hole>=0,"unverifiable old or overlapping range");var range=s.Holes[hole];s.Holes.RemoveAt(hole);
                if(range.Start<f.FirstSample)s.Holes.Add((range.Start,f.FirstSample));if(end<range.End)s.Holes.Add((end,range.End));
                s.Recovered+=f.Count;s.Missing-=f.Count;
            }
            else
            {
                if(f.FirstSample>s.NextSample){s.Holes.Add((s.NextSample,f.FirstSample));s.Missing+=f.FirstSample-s.NextSample;}
                s.NextSample=end;
            }
            Wire.Check(s.Holes.Count<=1024,"gap index capacity");s.Rows+=f.Count;
            s.Recent[f.FirstSample]=(end,digest);s.RecentOrder.Enqueue(f.FirstSample);if(s.RecentOrder.Count>1024)s.Recent.Remove(s.RecentOrder.Dequeue());
        }
    }
    private ulong NextHost(Connection c){lock(gate)return c.Stats.HostSequence++;}
    private async Task SendFrame(Connection c,Frame f)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await c.Write.WaitAsync(timeout.Token);try{await c.Client.GetStream().WriteAsync(Wire.Encode(f with{Sequence=NextHost(c)}),timeout.Token);}finally{c.Write.Release();}
    }
    private async Task<ulong> Issue(Connection c,string op,object args,TaskCompletionSource<JsonElement>? completion)
    {
        ulong id;lock(gate){Wire.Check(c.Pending.Count<8,"pending command capacity");id=++c.Stats.Request;c.Pending[id]=new(op,args,completion);}
        try{await SendFrame(c,Metadata.Json(4,Convert.FromHexString(c.Stats.Unit),Convert.FromHexString(c.Stats.Session),0,c.Stats.NextSample,new{request_id=id.ToString(),op,args}));}
        catch{lock(gate)c.Pending.Remove(id);throw;}
        return id;
    }
    public async Task<JsonElement> CommandAsync(string unit,string op,object args,CancellationToken token=default)
    {
        Connection c;lock(gate){Wire.Check(active.TryGetValue(unit.ToUpperInvariant(),out _),"unit offline");c=active[unit.ToUpperInvariant()];}
        var tcs=new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);ulong id=await Issue(c,op,args,tcs);
        try{return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5),token);}finally{lock(gate)c.Pending.Remove(id);}
    }
    public object Snapshot()
    {
        lock(gate)return new
        {
            timestamp=DateTimeOffset.UtcNow,connections,rejected,errors,active=active.Count,
            limits=new{max_connections=options.MaxConnections,max_sessions=options.MaxSessions,max_frame_bytes=Wire.MaxFrame,recent_hashes_per_session=1024,max_gap_intervals=1024},
            diagnostics=diagnostics.ToArray(),units=sessions.Values.Select(s=>new
            {
                unit=s.Unit,session=s.Session,label=s.Label,state=s.State,connected=s.Connected,mode=s.Mode,
                frames=s.Frames,rows=s.Rows,payload_bytes=s.Bytes,next_sample=s.NextSample,missing_rows=s.Missing,recovered_rows=s.Recovered,
                duplicate_frames=s.Duplicates,conflicts=s.Conflicts,sample_errors=s.SampleErrors,reported_drops=s.ReportedDrops,sequence_gaps=s.SequenceGaps,reconnects=s.Reconnects,
                average_payload_bytes_per_second=s.FirstDataTicks==0?0:s.Bytes/Math.Max(0.001,Stopwatch.GetElapsedTime(s.FirstDataTicks).TotalSeconds),
                source_buffer_rows=s.BufferRows,receiver_queued_bytes=0,
                data_age_seconds=s.LastDataTicks==0?(double?)null:Stopwatch.GetElapsedTime(s.LastDataTicks).TotalSeconds,
                parser_capacity=s.ParserCapacity,recent_hashes=s.Recent.Count,gap_intervals=s.Holes.Count,pending_commands=active.TryGetValue(s.Unit,out var c)?c.Pending.Count:0
            }).ToArray(),memory=new{managed_bytes=GC.GetTotalMemory(false),working_set_bytes=Environment.WorkingSet}
        };
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel();listener.Stop();if(accept is not null)await accept;
        Task[] remaining;lock(gate){foreach(var c in active.Values)c.Client.Dispose();remaining=tasks.ToArray();}
        await Task.WhenAll(remaining);stop.Dispose();
    }
}
