using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using MultiNodeDAQ.Protocol;
using MultiNodeDAQ.Core;
using MultiNodeDAQ.Acquisition;
using MultiNodeDAQ.Recording;
using MultiNodeDAQ.Simulator;

var argsMap=new Arguments(args);int checks=0;
void Check(bool value,string name){checks++;if(!value)throw new Exception(name);}
JsonElement Json(object value)=>JsonSerializer.SerializeToElement(value);
string root=Path.GetFullPath(argsMap.Get("output",".artifacts/stage2"));Directory.CreateDirectory(root);
string NewDir(string name)=>Path.Combine(root,name+"-"+Guid.NewGuid().ToString("N"));
byte[] unit=Convert.FromHexString("53000000000000000000000001000000"),session=Guid.NewGuid().ToByteArray(true);
Frame Hello()=>Metadata.Json(1,unit,session,0,0,new{firmware="recording-test/1",protocol=1,synthetic=true,axes=new[]{"X","Y","Z"},encodings=new[]{1,2},capabilities=new[]{"status","arm","start","stop"},mode="counter",seed=17});
Frame Config()=>Metadata.Json(5,unit,session,1,0,new{request_id="1",ok=true,state="armed",effective_sample="0",error=(string?)null,details=new{config=new{id=1,sample_rate_hz=25000,encoding=1,synthetic=true}}});
Frame Data(ulong first,ulong seq=2)=>Synthetic.Data(unit,session,seq,first,256,25000,1,"counter",17);
void Setup(RecordingSession r){Check(r.Accept(Hello()),"HELLO enqueue");Check(r.Accept(Config()),"config enqueue");}
if(argsMap.Has("crash-writer"))
{
    await using var r=new RecordingSession(new(argsMap.Get("crash-writer",""),FlushSeconds:.1));Setup(r);
    for(ulong n=0;;n+=256){r.Accept(Data(n,n/256+2));if(n==5120)File.WriteAllText(Path.Combine(r.DirectoryPath,"ready"),"ready");await Task.Delay(10);}
}
async Task<(VerificationResult Verification,JsonElement Source,JsonElement Recorded,string Directory)> Run(Scenario scenario,string name,long segmentBytes=256*1024*1024)
{
    string dir=NewDir(name);await using var recording=new RecordingSession(new(dir,SegmentBytes:segmentBytes,FlushSeconds:name.StartsWith("benchmark")?1:.2));
    var receiver=new Receiver(new(Port:0),recording);receiver.Start();var fleet=new Fleet(scenario,"127.0.0.1",receiver.Port);
    using var sourceStop=new CancellationTokenSource(TimeSpan.FromSeconds(scenario.Seconds+30));var run=fleet.RunAsync(sourceStop.Token);var watch=Stopwatch.StartNew();
    using(var metrics=new StreamWriter(Path.Combine(root,name+"-metrics.jsonl")))
    {
        while(!run.IsCompleted)
        {
            await Task.WhenAny(run,Task.Delay(1000));var snapshot=new{elapsed_seconds=watch.Elapsed.TotalSeconds,host=receiver.Snapshot(),recording=recording.Snapshot(),source=fleet.Snapshot()};
            await metrics.WriteLineAsync(JsonSerializer.Serialize(snapshot));await metrics.FlushAsync();
            if(recording.Failed){sourceStop.Cancel();break;}
        }
    }
    await run;await Task.Delay(100);bool clean=await receiver.StopSourcesAsync();await receiver.DisposeAsync();await recording.CompleteAsync(clean);
    Check(!recording.Failed,"recording faulted: "+JsonSerializer.Serialize(recording.Snapshot()));
    Console.WriteLine("Scanning "+dir);var verification=RecordingReader.Verify(dir,progress:s=>{if(argsMap.Has("benchmark") || s.Status!="complete")Console.WriteLine($"  {Path.GetFileName(s.Path)}: {s.Status}, {s.Rows} rows");});
    var source=Json(fleet.Snapshot());var recorded=Json(recording.Snapshot());
    await File.WriteAllTextAsync(Path.Combine(root,name+"-summary.json"),JsonSerializer.Serialize(new{directory=dir,scenario,verification,source,recorded},new JsonSerializerOptions{WriteIndented=true}));
    Check(verification.Status=="complete","verification: "+string.Join("; ",verification.Errors));Check(verification.SampleErrors==0,"sample errors");
    ulong produced=source.GetProperty("units").EnumerateArray().Aggregate(0UL,(n,s)=>n+s.GetProperty("produced_rows").GetUInt64());
    Check(verification.Rows==produced,"recorded/produced mismatch");
    Check(recorded.GetProperty("queued_bytes").GetInt64()==0,"undrained queue");Check(recorded.GetProperty("peak_queued_bytes").GetInt64()<=64*1024*1024,"global queue bound");
    foreach(var w in recorded.GetProperty("units").EnumerateArray()){Check(w.GetProperty("peak_bytes").GetInt64()<=4*1024*1024,"unit queue bound");Check(w.GetProperty("written_rows").GetUInt64()==w.GetProperty("flushed_rows").GetUInt64(),"written/flushed mismatch");}
    return(verification,source,recorded,dir);
}
if(argsMap.Has("benchmark"))
{
    int nodes=argsMap.Int("nodes",16);double seconds=argsMap.Double("seconds",7200);
    await Run(new(){Nodes=nodes,Seconds=seconds,Encoding=2},$"benchmark-{nodes}-{seconds}");
    Console.WriteLine($"PASS Stage 2 endurance: {nodes} units x {seconds}s; {checks} assertions.");return;
}
var basic=await Run(new(){Nodes=2,Seconds=3},"rotation",32768);
Check(basic.Verification.Files.Length>2,"rotation missing");
var identity=basic.Verification.Files[0];var read=RecordingReader.ReadRange(basic.Directory,identity.Unit!,identity.AcquisitionSession!,123,1000);
Check(read.Blocks.Sum(x=>(long)x.Count)==1000 && read.Missing.Length==0,"range reader");
foreach(var f in read.Blocks){var values=Wire.Samples(f);for(int i=0;i<values.Length;i++)Check(values[i]==(int)(((f.FirstSample+(ulong)(i/3))*3+(ulong)(i%3)+17)&0xffffff)-8388608,"replay values");}
// Reconnect-independent long-history duplicate verification, including delayed gap repair.
string history=NewDir("history");
await using(var r=new RecordingSession(new(history,QueueBytes:16*1024*1024,PerUnitBytes:16*1024*1024,SegmentBytes:32768)))
{
    Setup(r);for(ulong n=256;n<1100*256;n+=256)Check(r.Accept(Data(n,n/256+2)),"history enqueue");
    Check(r.Accept(Data(0,2000)),"late first block");Check(r.Accept(Data(256,2001)),"old duplicate");await r.CompleteAsync();Check(!r.Failed,"history recording failed");
}
var hv=RecordingReader.Verify(history);Check(hv.Status=="complete" && hv.Rows==1100UL*256 && hv.Files[^1].ContiguousSample==1100UL*256,"history verification");
// Known gaps remain visible and prevent contiguous commit advancement.
string gaps=NewDir("gaps");
await using(var r=new RecordingSession(new(gaps)))
{
    Setup(r);r.Accept(Data(256));r.Accept(Metadata.Json(3,unit,session,9,768,new{state="idle",next_sample="768",buffer_rows=0,dropped_rows="512"}));await r.CompleteAsync();
    Check(r.Committed(Convert.ToHexString(unit),Convert.ToHexString(session))==0,"commit crossed missing prefix");
}
var gv=RecordingReader.Verify(gaps);Check(gv.Status=="complete" && gv.Files[0].MissingRows==512,"gap/tail persistence");
var gr=RecordingReader.ReadRange(gaps,Convert.ToHexString(unit),Convert.ToHexString(session),0,768);Check(gr.Missing.Length==2 && gr.Blocks.Sum(b=>(long)b.Count)==256,"replay gap preservation");
// A real node connection must not receive commit_through until Flush(true) returns.
string gated=NewDir("gated-flush");using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
await using(var r=new RecordingSession(new(gated,FlushSeconds:.1,OutputFactory:p=>new GateOutput(p,entered,release))))
{
    var receiver=new Receiver(new(Port:0),r);receiver.Start();using var client=new TcpClient(){NoDelay=true};await client.ConnectAsync("127.0.0.1",receiver.Port);
    var stream=client.GetStream();using var parser=new FrameStream();using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var hello=Hello();var body=System.Text.Json.Nodes.JsonNode.Parse(hello.Payload)!;body["capabilities"]=JsonSerializer.SerializeToNode(new[]{"status","arm","start","stop","commit_through"});
    await stream.WriteAsync(Wire.Encode(hello with{Payload=JsonSerializer.SerializeToUtf8Bytes(body)}));
    Check((await parser.ReadAsync(stream,timeout.Token))!.Kind==5,"gated hello ACK");
    var arm=Metadata.Read((await parser.ReadAsync(stream,timeout.Token))!);Check(Metadata.Text(arm,"op")=="arm","gated arm");
    var config=Config();var configBody=System.Text.Json.Nodes.JsonNode.Parse(config.Payload)!;configBody["request_id"]=Metadata.Text(arm,"request_id");
    await stream.WriteAsync(Wire.Encode(config with{Payload=JsonSerializer.SerializeToUtf8Bytes(configBody)}));
    var startCommand=Metadata.Read((await parser.ReadAsync(stream,timeout.Token))!);ulong seq=2;
    await stream.WriteAsync(Wire.Encode(Metadata.Json(5,unit,session,seq++,0,new{request_id=Metadata.Text(startCommand,"request_id"),ok=true,state="sampling",effective_sample="0",error=(string?)null,details=new{}})));
    await stream.WriteAsync(Wire.Encode(Data(0,seq++)));
    Check(entered.Wait(5000),"flush gate not reached");
    Frame Status(string state)=>Metadata.Json(3,unit,session,seq++,256,new{state,next_sample="256",buffer_rows=0,dropped_rows="0"});
    await stream.WriteAsync(Wire.Encode(Status("sampling")));await Task.Delay(100);
    Check(client.Available==0 && r.Committed(Convert.ToHexString(unit),Convert.ToHexString(session))==0,"premature durable ACK");
    release.Set();JsonElement? commitCommand=null;
    for(int i=0;i<40 && commitCommand is null;i++)
    {await Task.Delay(25);await stream.WriteAsync(Wire.Encode(Status("sampling")));if(client.Available>0)commitCommand=Metadata.Read((await parser.ReadAsync(stream,timeout.Token))!);}
    Check(commitCommand is not null && Metadata.Text(commitCommand.Value,"op")=="commit_through" && Metadata.Counter(Metadata.Field(commitCommand.Value,"args"),"next_sample")==256,"missing durable commit command");
    await stream.WriteAsync(Wire.Encode(Metadata.Json(5,unit,session,seq++,256,new{request_id=Metadata.Text(commitCommand!.Value,"request_id"),ok=true,state="sampling",effective_sample="256",error=(string?)null,details=new{}})));
    await stream.WriteAsync(Wire.Encode(Status("idle")));await Task.Delay(100);client.Close();await receiver.DisposeAsync();await r.CompleteAsync();Check(!r.Failed,"gated recording failure");
}
// Damaged files are never repaired by scanning.
byte[] original=File.ReadAllBytes(basic.Verification.Files[0].Path);
foreach(int cut in new[]{1,35,original.Length-1,original.Length-10})
{
    string path=Path.Combine(root,$"cut-{cut}.elflog");File.WriteAllBytes(path,original.AsSpan(0,cut).ToArray());
    var s=LogScanner.Scan(path);Check(s.Status=="truncated","truncation classification");Check(File.ReadAllBytes(path).AsSpan().SequenceEqual(original.AsSpan(0,cut)),"scanner modified original");
}
byte[] damaged=(byte[])original.Clone();damaged[80]^=1;string corrupt=Path.Combine(root,"crc-corrupt.elflog");File.WriteAllBytes(corrupt,damaged);Check(LogScanner.Scan(corrupt).Status=="corrupt","interior CRC");
string trailing=Path.Combine(root,"after-footer.elflog");File.WriteAllBytes(trailing,original.Concat(original.AsSpan(36,20).ToArray()).ToArray());Check(LogScanner.Scan(trailing).Status=="corrupt","bytes after footer");
foreach(string kind in new[]{"commit","footer"})
{
    using var result=new MemoryStream();result.Write(original.AsSpan(0,36));
    foreach(var record in LogScanner.Records(basic.Verification.Files[0].Path))
    {
        byte[] payload=record.Payload;
        if((kind=="commit" && record.Kind==3)||(kind=="footer" && record.Kind==4))
        {
            var json=System.Text.Json.Nodes.JsonNode.Parse(payload)!;
            json[kind=="commit"?"next_sample":"sample_rows"]="99999999";
            payload=JsonSerializer.SerializeToUtf8Bytes(json);
        }
        result.Write(LogFormat.Record(record.Kind,payload));
    }
    string path=Path.Combine(root,"bad-"+kind+".elflog");File.WriteAllBytes(path,result.ToArray());Check(LogScanner.Scan(path).Status=="corrupt","semantic "+kind);
}
// Failed flushes never advance the release watermark.
string badFlush=NewDir("bad-flush");
await using(var r=new RecordingSession(new(badFlush,OutputFactory:p=>new FaultOutput(p,failFlush:true))))
{Setup(r);r.Accept(Data(0));await r.CompleteAsync();Check(r.Failed,"failed flush accepted");Check(r.Committed(Convert.ToHexString(unit),Convert.ToHexString(session))==0,"commit advanced after failed flush");}
Check(RecordingReader.Verify(badFlush).Status!="complete","failed flush claimed complete");
// Partial writes model storage failure/full conditions without filling the actual disk.
string partial=NewDir("partial-write");
await using(var r=new RecordingSession(new(partial,OutputFactory:p=>new FaultOutput(p,failAfter:2000))))
{Setup(r);r.Accept(Data(0));await r.CompleteAsync();Check(r.Failed,"partial write accepted");}
Check(RecordingReader.Verify(partial).Status!="complete","partial write claimed complete");
// A blocked writer cannot make the queue unbounded or silently discard accepted data.
string blocked=NewDir("blocked");
await using(var r=new RecordingSession(new(blocked,QueueBytes:8192,PerUnitBytes:8192,OutputFactory:p=>new FaultOutput(p,delayMs:30))))
{Setup(r);for(ulong n=0;n<50*256;n+=256)r.Accept(Data(n));await r.CompleteAsync();Check(r.Failed,"queue overrun hidden");Check(Json(r.Snapshot()).GetProperty("peak_queued_bytes").GetInt64()<=8192,"queue exceeded bound");}
// Conflicting historical data must fault the recording rather than overwrite evidence.
string conflict=NewDir("conflict");
await using(var r=new RecordingSession(new(conflict)))
{Setup(r);r.Accept(Data(0));var changed=Data(0,3);changed.Payload[0]^=1;r.Accept(changed);await r.CompleteAsync();Check(r.Failed,"conflict hidden");}
// Kill only the helper process created by this test; this is not a power-loss test.
string crash=NewDir("process-kill");var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true};
if(Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet",StringComparison.OrdinalIgnoreCase))start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);start.ArgumentList.Add("--crash-writer");start.ArgumentList.Add(crash);
using(var child=Process.Start(start)!)
{
    var waiting=Stopwatch.StartNew();while(!File.Exists(Path.Combine(crash,"ready")) && !child.HasExited && waiting.Elapsed.TotalSeconds<10)await Task.Delay(20);
    Check(!child.HasExited && File.Exists(Path.Combine(crash,"ready")),"crash helper did not start");child.Kill();await child.WaitForExitAsync();
}
var cv=RecordingReader.Verify(crash);Check(cv.Status!="complete" && cv.Files.Any(f=>f.ValidBytes>36),"kill recovery");
var crashedFile=cv.Files.First(f=>f.Rows>0);var recovered=RecordingReader.ReadRange(crash,crashedFile.Unit!,crashedFile.AcquisitionSession!,0,256,true);
Check(!recovered.SourceComplete && recovered.Blocks.Sum(f=>(long)f.Count)==256,"explicit crash-prefix recovery");
Console.WriteLine($"PASS: {checks} Stage 2 assertions.");

sealed class FaultOutput(string path,bool failFlush=false,long failAfter=long.MaxValue,int delayMs=0) : ILogOutput
{
    private readonly FileStream file=new(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read);
    public long Position=>file.Position;
    public void Write(byte[] bytes){if(delayMs>0)Thread.Sleep(delayMs);int accepted=(int)Math.Min(bytes.Length,Math.Max(0,failAfter-file.Position));file.Write(bytes.AsSpan(0,accepted));if(accepted<bytes.Length)throw new IOException("injected partial write / disk full");}
    public void FlushDurable(){if(failFlush)throw new IOException("injected flush failure");file.Flush(true);}
    public void Dispose()=>file.Dispose();
}

sealed class GateOutput(string path,ManualResetEventSlim entered,ManualResetEventSlim release) : ILogOutput
{
    private readonly DiskLogOutput disk=new(path);
    public long Position=>disk.Position;
    public void Write(byte[] bytes)=>disk.Write(bytes);
    public void FlushDurable(){entered.Set();if(!release.Wait(8000))throw new IOException("test flush gate timeout");disk.FlushDurable();}
    public void Dispose()=>disk.Dispose();
}
