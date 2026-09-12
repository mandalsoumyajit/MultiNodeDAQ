using System.Net.Sockets;
using System.Text.Json;
using MultiNodeDAQ.Protocol;
using MultiNodeDAQ.Core;
using MultiNodeDAQ.Acquisition;
using MultiNodeDAQ.Simulator;

var arg=new Arguments(args);int checks=0;
void Check(bool ok,string reason){checks++;if(!ok)throw new Exception(reason);}
void Reject(Action action,string reason){try{action();}catch(ContractException){checks++;return;}throw new Exception("Accepted: "+reason);}
JsonElement Json(object x)=>JsonSerializer.SerializeToElement(x);
async Task<(JsonElement Host,JsonElement Simulator)> Run(Scenario scenario,string label)
{
    await using var receiver=new Receiver(new(Port:0));receiver.Start();var fleet=new Fleet(scenario,"127.0.0.1",receiver.Port);
    string dir=arg.Get("output",".artifacts/stage1");Directory.CreateDirectory(dir);
    using var metrics=new StreamWriter(Path.Combine(dir,label+"-metrics.jsonl"));
    var run=fleet.RunAsync();var started=System.Diagnostics.Stopwatch.StartNew();
    while(!run.IsCompleted)
    {
        await Task.WhenAny(run,Task.Delay(1000));await metrics.WriteLineAsync(JsonSerializer.Serialize(new{elapsed_seconds=started.Elapsed.TotalSeconds,host=receiver.Snapshot(),simulator=fleet.Snapshot()}));
        await metrics.FlushAsync();
        if(started.Elapsed.TotalSeconds>scenario.Seconds+20)throw new Exception("Fleet failed to finish");
    }
    await run;await Task.Delay(200);
    var h=Json(receiver.Snapshot());var s=Json(fleet.Snapshot());
    await File.WriteAllTextAsync(Path.Combine(dir,label+"-summary.json"),JsonSerializer.Serialize(new{scenario,host=h,simulator=s},new JsonSerializerOptions{WriteIndented=true}));
    return(h,s);
}
void Clean((JsonElement Host,JsonElement Simulator) result,int units)
{
    var hs=result.Host.GetProperty("units").EnumerateArray().ToArray();var ss=result.Simulator.GetProperty("units").EnumerateArray().ToArray();
    Check(hs.Length==units,"unit count");Check(result.Host.GetProperty("errors").GetInt64()==0,"host errors");
    foreach(var s in ss)
    {
        var h=hs.Single(x=>x.GetProperty("unit").GetString()==s.GetProperty("unit").GetString());
        Check(h.GetProperty("sample_errors").GetUInt64()==0,"sample mismatch");Check(h.GetProperty("missing_rows").GetUInt64()==0,"missing rows");
        Check(h.GetProperty("rows").GetUInt64()==s.GetProperty("produced_rows").GetUInt64(),"produced/received");
        Check(h.GetProperty("rows").GetUInt64()>0,"no samples");Check(s.GetProperty("dropped_rows").GetUInt64()==0,"producer drops");
        Check(s.GetProperty("peak_bytes").GetInt32()<=s.GetProperty("buffer_limit").GetInt32(),"buffer overflow");Check(h.GetProperty("recent_hashes").GetInt32()<=1024,"unbounded history");
    }
}
if(arg.Has("benchmark"))
{
    int nodes=arg.Int("nodes",2);double seconds=arg.Double("seconds",1800);
    var result=await Run(new(){Nodes=nodes,Seconds=seconds},$"baseline-{nodes}-{seconds}");Clean(result,nodes);
    Console.WriteLine($"PASS benchmark: {nodes} units x {seconds}s; {checks} assertions.");return;
}
var root=Path.GetFullPath("fixtures");
foreach(var name in new[]{"hello","status","command","ack","timing","gap"}){Metadata.Read(Wire.Decode(File.ReadAllBytes(Path.Combine(root,name+".bin"))));checks++;}
var fixture=Wire.Decode(File.ReadAllBytes(Path.Combine(root,"hello.bin")));
Reject(()=>Metadata.Read(fixture with{Payload="{\"protocol\":1}"u8.ToArray()}),"missing hello metadata");
var machine=new CommandState();
Frame Command(ulong id,string op,object data)=>Metadata.Json(4,fixture.Unit,fixture.Session,id,0,new{request_id=id.ToString(),op,args=data});
var startEarly=Json(machine.Handle(Command(1,"start",new{}),0,0,0));Check(!startEarly.GetProperty("ok").GetBoolean(),"start before arm");
var arm=Command(2,"arm",new{config=new{id=1,sample_rate_hz=25000,encoding=1,synthetic=true}});
var first=Json(machine.Handle(arm,0,0,0));Check(first.GetProperty("ok").GetBoolean(),"arm");
var retry=Json(machine.Handle(arm,999,0,0));Check(first.GetRawText()==retry.GetRawText(),"idempotent reply");
Check(!Json(machine.Handle(Command(2,"start",new{}),0,0,0)).GetProperty("ok").GetBoolean(),"conflicting id");
Check(Json(machine.Handle(Command(3,"start",new{}),0,0,0)).GetProperty("state").GetString()=="sampling","start state");
Check(!Json(machine.Handle(Command(4,"commit_through",new{next_sample="99"}),0,0,0)).GetProperty("ok").GetBoolean(),"no false durable ack");
// Real TCP splitting/coalescing and exact sample verification.
Clean(await Run(new(){Nodes=2,Seconds=3,FragmentBytes=7,CoalesceFrames=4},"fragmented"),2);
Clean(await Run(new(){Nodes=2,Seconds=3,Encoding=2},"int32"),2);
// Malformed clients cannot disrupt a healthy independently running unit.
await using(var receiver=new Receiver(new(Port:0)))
{
    receiver.Start();var scenario=new Scenario{Nodes=1,Seconds=3};var fleet=new Fleet(scenario,"127.0.0.1",receiver.Port);var run=fleet.RunAsync();
    await Task.Delay(300);
    using(var bad=new TcpClient()){await bad.ConnectAsync("127.0.0.1",receiver.Port);await bad.GetStream().WriteAsync("ELD1\x01\0\x01\0\xff\xff\xff\xff"u8.ToArray());}
    await run;await Task.Delay(100);var h=Json(receiver.Snapshot());
    Check(h.GetProperty("errors").GetInt64()>0,"malformed not rejected");Check(h.GetProperty("units")[0].GetProperty("sample_errors").GetUInt64()==0,"healthy corrupted");
    Check(h.GetProperty("units")[0].GetProperty("rows").GetUInt64()==Json(fleet.Snapshot()).GetProperty("units")[0].GetProperty("produced_rows").GetUInt64(),"healthy lost data");
}
var faults=await Run(new(){Nodes=2,Seconds=7,BufferBytes=65536,Faults=[new(0,1,"pause",0.5),new(0,2,"disconnect",0.2),new(0,3,"reboot"),new(0,4,"delay",0.2),new(0,5,"duplicate")]},"faults");
Check(faults.Host.GetProperty("units").GetArrayLength()==3,"reboot session");
Check(faults.Simulator.GetProperty("units")[0].GetProperty("dropped_rows").GetUInt64()>0,"finite buffer did not drop");
Check(faults.Host.GetProperty("units").EnumerateArray().Any(u=>u.GetProperty("reconnects").GetInt32()>0),"no reconnect");
Check(faults.Host.GetProperty("units").EnumerateArray().Any(u=>u.GetProperty("recovered_rows").GetUInt64()>0),"no delayed recovery");
Check(faults.Host.GetProperty("units").EnumerateArray().Any(u=>u.GetProperty("duplicate_frames").GetUInt64()>0),"no duplicate detection");
var healthy=faults.Host.GetProperty("units").EnumerateArray().Single(u=>u.GetProperty("unit").GetString()!.EndsWith("02000000"));
Check(healthy.GetProperty("missing_rows").GetUInt64()==0 && healthy.GetProperty("sample_errors").GetUInt64()==0,"healthy affected by faults");
// CRC failure isolates the damaged client; its following frames retain full sample indices.
var corrupt=await Run(new(){Nodes=2,Seconds=3,Faults=[new(0,1,"corrupt")]},"crc-fault");
Check(corrupt.Host.GetProperty("errors").GetInt64()>0,"CRC fault accepted");
Check(corrupt.Host.GetProperty("units").EnumerateArray().Single(u=>u.GetProperty("unit").GetString()!.EndsWith("02000000")).GetProperty("missing_rows").GetUInt64()==0,"CRC fault affected peer");
var tail=await Run(new(){Nodes=1,Seconds=3,Faults=[new(0,2,"drop",2)]},"tail-loss");
var th=tail.Host.GetProperty("units")[0];var ts=tail.Simulator.GetProperty("units")[0];
Check(th.GetProperty("missing_rows").GetUInt64()>0,"lost tail hidden");
Check(th.GetProperty("missing_rows").GetUInt64()+th.GetProperty("rows").GetUInt64()==ts.GetProperty("produced_rows").GetUInt64(),"tail accounting");
Reject(()=>Metadata.Read(fixture with{Payload=JsonSerializer.SerializeToUtf8Bytes(new{firmware="test",protocol=1,synthetic=true,axes=new[]{"X","Y","Z"},encodings=new[]{1},capabilities=Array.Empty<string>(),label=42})}),"non-string label");
await using(var receiver=new Receiver(new(Port:0)))
{
    receiver.Start();using var client=new TcpClient();await client.ConnectAsync("127.0.0.1",receiver.Port);var stream=client.GetStream();using var parser=new FrameStream();
    await stream.WriteAsync(Wire.Encode(fixture));using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var welcome=await parser.ReadAsync(stream,deadline.Token);Check(welcome?.Kind==5,"welcome ACK");
    var command=await parser.ReadAsync(stream,deadline.Token);var cm=Metadata.Read(command!);
    var wrong=Metadata.Json(5,fixture.Unit,fixture.Session,fixture.Sequence+1,0,new{request_id=Metadata.Text(cm,"request_id"),ok=true,state="armed",effective_sample="0",error=(string?)null,details=new{config=new{id=fixture.Config,sample_rate_hz=12345,encoding=1,synthetic=true}}});
    await stream.WriteAsync(Wire.Encode(wrong));await Task.Delay(100);
    Check(Json(receiver.Snapshot()).GetProperty("errors").GetInt64()>0,"mismatched config ACK accepted");
}
// Stage 3 subscribers retain at most two seconds, independent of recorder queues.
var hub=new LiveHub();var subscription=hub.Subscribe([Convert.ToHexString(fixture.Unit)]);
for(ulong i=0;i<300;i++)hub.Publish(Synthetic.Data(fixture.Unit,fixture.Session,i,i*256,256,25000,2,"counter",17));
var subscriptionState=Json(subscription.Snapshot());
Check(subscriptionState.GetProperty("skipped_rows").GetInt64()>0,"slow analysis did not report skipped work");
Check(subscriptionState.GetProperty("peak_bytes").GetInt64()<=LiveHub.Subscription.Limit,"analysis byte bound");
long retainedRows=0;while(subscription.Take() is {} packet)retainedRows+=packet.Frame.Count;
Check(retainedRows<=50000 && retainedRows>0,"analysis history bound");hub.Remove(subscription);
Console.WriteLine($"PASS: {checks} integration assertions (Stages 1 and 3).");
