using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using MultiNodeDAQ.Protocol;
namespace MultiNodeDAQ.Acquisition;

public sealed class LocalApi : IAsyncDisposable
{
    private readonly Receiver receiver;
    public Action? ShutdownRequested {get;set;}
    private readonly Func<object?> recording;
    private readonly byte[] token;
    private readonly TcpListener listener;
    private readonly CancellationTokenSource stop=new();
    private readonly object gate=new();
    private readonly HashSet<Task> clients=[];
    private readonly Dictionary<string,(JsonElement Result,long Time)> results=[];
    private readonly Dictionary<string,JsonElement> rejections=[];
    private readonly SemaphoreSlim slots=new(16);
    private Task? accept;
    private int disposed;
    private long settingsRevision;
    private JsonElement? analysisSettings;
    public LocalApi(Receiver receiver,string token,int port=45101,Func<object?>? recording=null)
    {Wire.Check(token.Length>=32 && token.Length<=256,"IPC token length");this.receiver=receiver;this.token=System.Text.Encoding.UTF8.GetBytes(token);this.recording=recording??(()=>null);listener=new(IPAddress.Loopback,port);}
    public int Port=>((IPEndPoint)listener.LocalEndpoint).Port;
    public void Start(){listener.Start();accept=Accept();}
    private async Task Accept()
    {
        try{while(!stop.IsCancellationRequested){var c=await listener.AcceptTcpClientAsync(stop.Token);if(!slots.Wait(0)){c.Dispose();continue;}var t=Handle(c);lock(gate)clients.Add(t);_=t.ContinueWith(_=>{lock(gate)clients.Remove(t);slots.Release();},TaskScheduler.Default);}}
        catch(Exception e) when(e is OperationCanceledException or SocketException or ObjectDisposedException){}
    }
    private static async Task<(ushort Kind,ulong Request,byte[] Payload)> Read(NetworkStream s,CancellationToken ct)
    {
        byte[] prefix=new byte[12];await s.ReadExactlyAsync(prefix,ct);
        Wire.Check(prefix.AsSpan(0,4).SequenceEqual("ELI1"u8) && Wire.U16(prefix,4)==1,"IPC prefix");uint n=Wire.U32(prefix,8);Wire.Check(n is >=28 and <=Ipc.MaxLength,"IPC length");
        byte[] b=new byte[n];prefix.CopyTo(b,0);await s.ReadExactlyAsync(b.AsMemory(12),ct);return Ipc.Decode(b);
    }
    private static Task Send(NetworkStream s,ushort kind,ulong id,object payload,CancellationToken ct)=>s.WriteAsync(Ipc.Encode(kind,id,JsonSerializer.SerializeToUtf8Bytes(payload)),ct).AsTask();
    private async Task Handle(TcpClient client)
    {
        LiveHub.Subscription? subscription=null;
        using(client)
        try
        {
            client.NoDelay=true;client.SendBufferSize=16384;var stream=client.GetStream();bool authenticated=false;ulong previous=0;
            while(!stop.IsCancellationRequested)
            {
                using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);deadline.CancelAfter(TimeSpan.FromSeconds(authenticated?30:5));
                var message=await Read(stream,deadline.Token);using var doc=JsonDocument.Parse(message.Payload);var o=doc.RootElement;
                if(message.Kind==3)
                {
                    Wire.Check(authenticated && message.Request==0 && message.Payload.Length<=262144,"result authentication/size");
                    string u=Metadata.Text(o,"unit"),sid=Metadata.Text(o,"acquisition_session");Wire.Check(u.Length==32 && sid.Length==32 && u.All(char.IsAsciiHexDigit) && sid.All(char.IsAsciiHexDigit),"result identity");
                    Metadata.Text(o,"result");Metadata.Text(o,"algorithm");Metadata.Counter(o,"first_sample");Metadata.Number(o,"count");Metadata.Bool(o,"valid");Metadata.Object(o,"parameters");Metadata.Object(o,"values");
                    lock(gate){string key=u+":"+sid;Wire.Check(results.ContainsKey(key)||results.Count<128,"result capacity");results[key]=(o.Clone(),Stopwatch.GetTimestamp());if(o.GetProperty("values").TryGetProperty("rejected_revision",out _))rejections[key]=o.GetProperty("values").Clone();else if(o.GetProperty("valid").GetBoolean()&&o.GetProperty("parameters").TryGetProperty("revision",out var applied)&&applied.GetInt64()==settingsRevision)rejections.Remove(key);}continue;
                }
                Wire.Check(message.Kind==1 && message.Request>previous && Metadata.Counter(o,"request_id")==message.Request,"control correlation");previous=message.Request;
                string op=Metadata.Text(o,"op");var args=Metadata.Field(o,"args");object? value=null;string? error=null;
                try
                {
                    if(!authenticated)
                    {
                        Wire.Check(op=="authenticate" && Metadata.Number(args,"protocol")==1 && Metadata.Text(args,"role") is "analysis" or "reader" or "gui","authentication required");
                        Wire.Check(CryptographicOperations.FixedTimeEquals(token,System.Text.Encoding.UTF8.GetBytes(Metadata.Text(args,"token"))),"authentication failed");authenticated=true;
                        value=new{protocol=1,capabilities=new[]{"status","subscribe","node_command","result","analysis_settings","configure_analysis","preview","start_recording","stop_recording","shutdown"}};
                    }
                    else if(op=="status")
                    {
                        object[] health;bool compact=args.TryGetProperty("compact",out var compactValue)&&compactValue.ValueKind==JsonValueKind.True;lock(gate)health=results.Where(x=>!compact||(args.TryGetProperty("analysis_unit",out var selectedUnit)&&args.TryGetProperty("analysis_session",out var selectedSession)&&x.Key.Equals(selectedUnit.GetString()+":"+selectedSession.GetString(),StringComparison.OrdinalIgnoreCase))).Select(x=>(object)new{result=x.Value.Result,age_seconds=Stopwatch.GetElapsedTime(x.Value.Time).TotalSeconds,stale=Stopwatch.GetElapsedTime(x.Value.Time).TotalSeconds>3,rejection=rejections.TryGetValue(x.Key,out var rejected)?(JsonElement?)rejected:null}).ToArray();
                        object[] summaries;lock(gate)summaries=results.Values.Select(x=>(object)new{unit=x.Result.GetProperty("unit"),acquisition_session=x.Result.GetProperty("acquisition_session"),valid=x.Result.GetProperty("valid"),age_seconds=Stopwatch.GetElapsedTime(x.Time).TotalSeconds}).ToArray();
                        value=new{acquisition=receiver.Snapshot(),recording=recording(),analysis=health,analysis_summary=summaries};
                    }
                    else if(op=="preview")
                    {double span=Metadata.Field(args,"seconds").GetDouble();Wire.Check(double.IsFinite(span)&&span>=.01&&span<=.5,"preview span 0.01 to 0.5 seconds");value=receiver.Preview(Metadata.Text(args,"unit"),Metadata.Text(args,"acquisition_session"),span);}
                    else if(op=="start_recording")value=receiver.Recording.Start(Metadata.Text(args,"directory"));
                    else if(op=="stop_recording")
                    {
                        // Confirm the terminal sample watermark before finalizing. Sampling can be restarted explicitly.
                        bool drained=await receiver.StopSourcesAsync();value=await receiver.Recording.StopAsync(drained);
                    }
                    else if(op=="shutdown"){Wire.Check(ShutdownRequested is not null,"shutdown not available");value=new{state="shutdown requested"};}
                    else if(op=="analysis_settings")
                    {lock(gate)value=new{revision=settingsRevision,settings=analysisSettings};}
                    else if(op=="configure_analysis")
                    {
                        var settings=Metadata.Field(args,"settings");Wire.Check(settings.ValueKind==JsonValueKind.Object,"settings object");
                        string[] allowed=["rate","df","dalpha","max_frequency","max_alpha","hop_fraction","pair_batch"];
                        Wire.Check(settings.EnumerateObject().Count()==allowed.Length && settings.EnumerateObject().All(p=>allowed.Contains(p.Name)),"supply all documented FAM settings");
                        double Get(string name){var v=Metadata.Field(settings,name);Wire.Check(v.TryGetDouble(out double n) && double.IsFinite(n) && n>0,"positive finite "+name);return n;}
                        double rate=Get("rate"),df=Get("df"),da=Get("dalpha"),hop=Get("hop_fraction");
                        Wire.Check(rate<=1_000_000 && df<=rate/4 && da<=rate && hop<=.5 && Get("max_frequency")<=rate/2 && Get("max_alpha")<=rate,"FAM setting bounds");
                        Wire.Check(Metadata.Number(settings,"pair_batch") is >=1 and <=4096,"pair batch");
                        lock(gate){analysisSettings=settings.Clone();settingsRevision++;rejections.Clear();value=new{revision=settingsRevision,settings=analysisSettings,state="requested"};}
                    }
                    else if(op=="subscribe")
                    {
                        Wire.Check(Metadata.Text(args,"stream")=="samples" && Metadata.Number(args,"history_seconds")==0,"samples only; initial history must be zero");
                        subscription=receiver.Live.Subscribe(Metadata.Field(args,"units").EnumerateArray().Select(x=>x.GetString()??"").ToArray());value=new{subscription_id="1",limits=subscription.Snapshot()};
                    }
                    else if(op=="node_command")
                    {
                        string u=Metadata.Text(args,"unit").ToUpperInvariant(),sid=Metadata.Text(args,"acquisition_session").ToUpperInvariant();
                        var snapshot=JsonSerializer.SerializeToElement(receiver.Snapshot());Wire.Check(snapshot.GetProperty("units").EnumerateArray().Any(x=>x.GetProperty("unit").GetString()==u && x.GetProperty("session").GetString()==sid && x.GetProperty("connected").GetBoolean()),"session offline");
                        value=await receiver.CommandAsync(u,Metadata.Text(args,"op"),Metadata.Field(args,"args"),deadline.Token,sid);
                    }
                    else throw new ContractException("unsupported operation");
                }
                catch(Exception e) when(e is ContractException or TimeoutException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException){error=e.Message;}
                await Send(stream,1,message.Request,new{op,request_id=message.Request.ToString(),ok=error is null,error,details=value},deadline.Token);
                if(op=="shutdown" && error is null)ShutdownRequested?.Invoke();
                if(!authenticated)break;
                if(subscription is not null)
                {
                    while(!stop.IsCancellationRequested)
                    {
                        var p=subscription.Take();if(p is null){await Task.Delay(10,stop.Token);continue;}
                        using var writeDeadline=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);writeDeadline.CancelAfter(TimeSpan.FromSeconds(2));
                        await Send(stream,1,0,new{op="subscription_status",request_id="0",details=subscription.Snapshot()},writeDeadline.Token);
                        await stream.WriteAsync(Ipc.Encode(1,0,p.Context),writeDeadline.Token);await stream.WriteAsync(p.Bytes,writeDeadline.Token);
                    }
                }
            }
        }
        catch(Exception e) when(e is IOException or SocketException or OperationCanceledException or ContractException or JsonException or InvalidOperationException or ObjectDisposedException){}
        finally{if(subscription is not null)receiver.Live.Remove(subscription);}
    }
    public async ValueTask DisposeAsync(){if(Interlocked.Exchange(ref disposed,1)!=0)return;stop.Cancel();listener.Stop();if(accept is not null)await accept;Task[] tasks;lock(gate)tasks=clients.ToArray();await Task.WhenAll(tasks);stop.Dispose();}
}
