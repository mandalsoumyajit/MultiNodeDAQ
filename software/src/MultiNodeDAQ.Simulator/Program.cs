using System.Text.Json;
using MultiNodeDAQ.Core;
using MultiNodeDAQ.Simulator;
var a=new Arguments(args);
if(a.Has("help")){Console.WriteLine("MultiNodeDAQ.Simulator --scenario scenarios/baseline-2.json --host 127.0.0.1 --port 45100 [--nodes 2 --seconds 30 --mode counter --encoding 1] --summary sim.json");return;}
var scenario=a.Has("scenario")?JsonSerializer.Deserialize<Scenario>(File.ReadAllText(a.Get("scenario","")),new JsonSerializerOptions{PropertyNameCaseInsensitive=true})!:new Scenario();
scenario.Nodes=a.Int("nodes",scenario.Nodes);scenario.Seconds=a.Double("seconds",scenario.Seconds);scenario.Mode=a.Get("mode",scenario.Mode);scenario.Encoding=(ushort)a.Int("encoding",scenario.Encoding);scenario.Validate();
using var stop=new CancellationTokenSource();Console.CancelKeyPress+=(_,e)=>{e.Cancel=true;stop.Cancel();};
var fleet=new Fleet(scenario,a.Get("host","127.0.0.1"),a.Int("port",45100));
Console.WriteLine($"Simulating {scenario.Nodes} units for {scenario.Seconds}s; {scenario.Mode}; finite buffer {scenario.BufferBytes} bytes/unit.");
try{await fleet.RunAsync(stop.Token);}catch(OperationCanceledException){}
await File.WriteAllTextAsync(a.Get("summary","sim-summary.json"),JsonSerializer.Serialize(fleet.Snapshot(),new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine(JsonSerializer.Serialize(fleet.Snapshot()));
