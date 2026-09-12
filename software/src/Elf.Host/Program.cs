using System.Text.Json;
using Elf.Acquisition;
using Elf.Core;
var argsMap=new Arguments(args);
if(argsMap.Has("help")){Console.WriteLine("Elf.Host --address 127.0.0.1 --port 45100 --seconds 30 --summary host.json [--interactive] [--no-auto-start]");return;}
using var stop=new CancellationTokenSource();Console.CancelKeyPress+=(_,e)=>{e.Cancel=true;stop.Cancel();};
await using var receiver=new Receiver(new(argsMap.Get("address","127.0.0.1"),argsMap.Int("port",45100),AutoStart:!argsMap.Has("no-auto-start")));
receiver.Start();Console.WriteLine($"Receiver listening on {argsMap.Get("address","127.0.0.1")}:{receiver.Port}; Stage 1 validation only, NO RECORDING.");
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
    while(!stop.IsCancellationRequested){await Task.Delay(1000,stop.Token);Console.WriteLine(JsonSerializer.Serialize(receiver.Snapshot()));}
}
catch(OperationCanceledException){}
// Console input can remain blocked on Windows despite cancellation; it must not hold up shutdown.
if(interactive is { IsCompleted: true }){try{await interactive;}catch(OperationCanceledException){}}
await File.WriteAllTextAsync(argsMap.Get("summary","host-summary.json"),JsonSerializer.Serialize(receiver.Snapshot(),new JsonSerializerOptions{WriteIndented=true}));
