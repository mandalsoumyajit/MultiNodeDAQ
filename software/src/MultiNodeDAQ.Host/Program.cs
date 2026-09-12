using System.Text.Json;
using MultiNodeDAQ.Acquisition;
using MultiNodeDAQ.Core;
using MultiNodeDAQ.Recording;
using MultiNodeDAQ.Protocol;
var argsMap=new Arguments(args);
if(argsMap.Has("version")){Console.WriteLine(System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(RecordingSession).Assembly)?.InformationalVersion);return;}
if(argsMap.Has("verify"))
{
    string path=argsMap.Get("verify","");object result;
    if(File.Exists(path)){var scan=LogScanner.Scan(path);result=scan;Environment.ExitCode=scan.Status=="complete" && scan.SampleErrors==0?0:2;}
    else{var scan=RecordingReader.Verify(path,progress:s=>Console.Error.WriteLine($"Scanned {Path.GetFileName(s.Path)}: {s.Status}, {s.Rows} rows"));result=scan;Environment.ExitCode=scan.Status=="complete"?0:2;}
    string json=JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true});Console.WriteLine(json);
    if(argsMap.Has("summary"))await File.WriteAllTextAsync(argsMap.Get("summary",""),json);return;
}
if(argsMap.Has("replay"))
{
    var range=RecordingReader.ReadRange(argsMap.Get("replay",""),argsMap.Get("unit",""),argsMap.Get("session",""),ulong.Parse(argsMap.Get("first","0")),uint.Parse(argsMap.Get("count","1024")),argsMap.Has("allow-incomplete"));
    string output=Path.GetFullPath(argsMap.Get("output","replay.csv"));using var file=new StreamWriter(new FileStream(output,FileMode.CreateNew,FileAccess.Write));
    await file.WriteLineAsync("sample,x,y,z,flags,config,calibration,timing");double speed=argsMap.Double("speed",0);
    Wire.Check(speed>=0 && double.IsFinite(speed),"replay speed");
    foreach(var block in range.Blocks)
    {
        var values=Wire.Samples(block);for(int i=0;i<block.Count;i++)await file.WriteLineAsync($"{block.FirstSample+(ulong)i},{values[3*i]},{values[3*i+1]},{values[3*i+2]},{block.Flags},{block.Config},{block.Calibration},{block.Timing}");
        if(speed>0)await Task.Delay(TimeSpan.FromSeconds(block.Count/(block.Rate*speed)));
    }
    await File.WriteAllTextAsync(output+".json",JsonSerializer.Serialize(new{range.Unit,range.AcquisitionSession,range.First,range.Count,range.Missing,range.SourceComplete,metadata=range.Metadata.Select(f=>Convert.ToBase64String(Wire.Encode(f))).ToArray()},new JsonSerializerOptions{WriteIndented=true}));
    Console.WriteLine($"Replayed {range.Blocks.Sum(f=>(long)f.Count)} rows to {output}; {range.Missing.Length} gap intervals.");return;
}
if(argsMap.Has("help")){Console.WriteLine("MultiNodeDAQ.Host [--ipc-port 45101] [--record NEW_DIRECTORY] [--address 127.0.0.1 --port 45100 --seconds 30 --interactive --no-auto-start] | --verify FILE_OR_DIRECTORY | --replay DIRECTORY --unit HEX --session HEX [--first 0 --count 1024 --output replay.csv --speed 0 --allow-incomplete] | --version");return;}
using var stop=new CancellationTokenSource();Console.CancelKeyPress+=(_,e)=>{e.Cancel=true;stop.Cancel();};
await using var recording=argsMap.Has("record")?new RecordingSession(new(argsMap.Get("record",""),SegmentBytes:(long)argsMap.Int("segment-mib",256)*1024*1024,FlushSeconds:argsMap.Double("flush-seconds",1))):null;
var receiver=new Receiver(new(argsMap.Get("address","127.0.0.1"),argsMap.Int("port",45100),AutoStart:!argsMap.Has("no-auto-start")),recording);
receiver.Start();
string? ipcToken=Environment.GetEnvironmentVariable("MULTINODEDAQ_IPC_TOKEN");
await using var api=argsMap.Has("ipc-port")?new LocalApi(receiver,ipcToken??throw new ArgumentException("Set MULTINODEDAQ_IPC_TOKEN to a random token of at least 32 characters"),argsMap.Int("ipc-port",45101),()=>recording?.Snapshot()):null;
api?.Start();
Console.WriteLine($"Receiver listening on {argsMap.Get("address","127.0.0.1")}:{receiver.Port}; recording: {recording?.DirectoryPath??"OFF"}.");
if(argsMap.Double("seconds",0)>0)stop.CancelAfter(TimeSpan.FromSeconds(argsMap.Double("seconds",0)));
Task? interactive=null;
if(argsMap.Has("interactive"))interactive=Task.Run(async()=>
{
    Console.WriteLine("Commands: status UNIT, stop UNIT, start UNIT, arm UNIT [CONFIG RATE ENCODING]; quit exits. Unit is 32 hex digits.");
    while(!stop.IsCancellationRequested)
    {
        string? line=await Console.In.ReadLineAsync(stop.Token);if(line is null)break;if(line=="quit"){stop.Cancel();break;}
        string[] fields=line.Split(' ',StringSplitOptions.RemoveEmptyEntries);
        if(fields.Length!=2 && !(fields.Length==5 && fields[0]=="arm")){Console.WriteLine("Expected: operation UNIT");continue;}
        try{object commandArgs=fields[0]=="arm"?new{config=new{id=fields.Length==5?uint.Parse(fields[2]):1u,sample_rate_hz=fields.Length==5?uint.Parse(fields[3]):25000u,encoding=fields.Length==5?uint.Parse(fields[4]):1u,synthetic=true}}:new{};Console.WriteLine((await receiver.CommandAsync(fields[1],fields[0],commandArgs,stop.Token)).GetRawText());}catch(Exception e){Console.WriteLine("Command failed: "+e.Message);}
    }
});
try
{
    while(!stop.IsCancellationRequested){await Task.Delay(1000,stop.Token);Console.WriteLine(JsonSerializer.Serialize(new{acquisition=receiver.Snapshot(),recording=recording?.Snapshot()}));}
}
catch(OperationCanceledException){}
// Console input can remain blocked on Windows despite cancellation; it must not hold up shutdown.
if(interactive is { IsCompleted: true }){try{await interactive;}catch(OperationCanceledException){}}
bool drained=await receiver.StopSourcesAsync();
await receiver.DisposeAsync();
if(recording is not null)await recording.CompleteAsync(drained);
await File.WriteAllTextAsync(argsMap.Get("summary","host-summary.json"),JsonSerializer.Serialize(new{acquisition=receiver.Snapshot(),recording=recording?.Snapshot()},new JsonSerializerOptions{WriteIndented=true}));
if(recording?.Failed==true)Environment.ExitCode=2;
