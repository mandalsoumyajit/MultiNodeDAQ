using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using MultiNodeDAQ.Core;
using MultiNodeDAQ.Protocol;
namespace MultiNodeDAQ.Simulator;

public sealed record Fault(int Node=0,double AtSeconds=1,string Kind="pause",double DurationSeconds=0.5);
public sealed class Scenario
{
    public int Nodes {get;set;}=2;
    public double Seconds {get;set;}=30;
    public uint Rate {get;set;}=25000;
    public ushort Encoding {get;set;}=1;
    public uint BlockRows {get;set;}=256;
    public string Mode {get;set;}="counter";
    public uint Seed {get;set;}=17;
    public int BufferBytes {get;set;}=1_048_576;
    public int FragmentBytes {get;set;}
    public int CoalesceFrames {get;set;}=1;
    public double ClockPpm {get;set;}
    public Fault[] Faults {get;set;}=[];
    public void Validate()
    {
        Wire.Check(Nodes is >=1 and <=128 && Seconds>0 && Seconds<=86400,"scenario duration/nodes");
        Wire.Check(Rate is >=1 and <=1_000_000 && Encoding is 1 or 2 && BlockRows is >=1 and <=4096,"scenario sampling");
        Wire.Check(BufferBytes>=(int)BlockRows*3*(Encoding==1?3:4)+100 && BufferBytes<=16*1024*1024,"buffer");
        Wire.Check(FragmentBytes>=0 && FragmentBytes<=Wire.MaxFrame && CoalesceFrames is >=1 and <=16,"transport options");
        Wire.Check(double.IsFinite(ClockPpm)&&Math.Abs(ClockPpm)<=10000 && Synthetic.Modes.Contains(Mode),"signal options");
        foreach(var f in Faults)Wire.Check(f.Node>=0&&f.Node<Nodes && f.AtSeconds>=0 && f.DurationSeconds>=0 && f.Kind is "pause" or "disconnect" or "reboot" or "drop" or "delay" or "duplicate" or "corrupt","fault");
    }
}
public sealed class SimNode
{
    private readonly Scenario scenario;private readonly int index;private readonly string host;private readonly int port;
    private readonly object gate=new();private readonly Queue<Frame> queue=[];
    private CommandState model=new();private readonly byte[] unit;
    private byte[] session=Guid.NewGuid().ToByteArray(true);
    private ulong next,sequence,produced,dropped,sent,bufferRows;
    private int queuedBytes,peakBytes,reconnects,reboots,failures;private bool done,duplicate,corrupt;
    private double pauseUntil,dropUntil,releaseAt;private Frame? delayed;
    private long sampleEpoch;private ulong sampleBase;
    private TcpClient? client;
    private readonly Stopwatch clock=Stopwatch.StartNew();
    public SimNode(Scenario scenario,int index,string host,int port)
    {this.scenario=scenario;this.index=index;this.host=host;this.port=port;unit=new byte[16];unit[0]=0x53;Wire.P32(unit,12,(uint)index+1);}
    public object Snapshot(){lock(gate)return new{index,unit=Convert.ToHexString(unit),session=Convert.ToHexString(session),produced_rows=produced,sent_rows=sent,dropped_rows=dropped,buffer_rows=bufferRows,queued_bytes=queuedBytes,peak_bytes=peakBytes,buffer_limit=scenario.BufferBytes,reconnects,reboots,failures,done};}
    private void Enqueue(Frame f)
    {
        int size=f.Payload.Length+100;
        if(queuedBytes+size>scenario.BufferBytes){dropped+=f.Count;return;}
        queue.Enqueue(f);queuedBytes+=size;bufferRows+=f.Count;peakBytes=Math.Max(peakBytes,queuedBytes);
    }
    private Frame Json(ushort kind,object body)=>Metadata.Json(kind,unit,session,sequence++,next,body,scenario.Rate,model.Config);
    private object Status()=>new{state=done?"idle":model.State,next_sample=next.ToString(),buffer_rows=(uint)Math.Min(bufferRows,uint.MaxValue),dropped_rows=dropped.ToString()};
    private async Task Producer(CancellationToken token)
    {
        var faults=scenario.Faults.Where(f=>f.Node==index).OrderBy(f=>f.AtSeconds).ToList();int fi=0;
        try
        {
            while(clock.Elapsed.TotalSeconds<scenario.Seconds)
            {
                token.ThrowIfCancellationRequested();
                lock(gate)
                {
                    double seconds=clock.Elapsed.TotalSeconds;
                    while(fi<faults.Count && seconds>=faults[fi].AtSeconds)
                    {
                        var f=faults[fi++];switch(f.Kind)
                        {
                            case "pause":pauseUntil=seconds+f.DurationSeconds;break;
                            case "drop":dropUntil=seconds+f.DurationSeconds;break;
                            case "disconnect":client?.Dispose();pauseUntil=seconds+f.DurationSeconds;break;
                            case "reboot":
                                client?.Dispose();dropped+=bufferRows+(delayed?.Count??0);queue.Clear();queuedBytes=0;bufferRows=0;delayed=null;
                                session=Guid.NewGuid().ToByteArray(true);next=0;sequence=0;sampleEpoch=0;model=new();reboots++;break;
                            case "delay":releaseAt=seconds+f.DurationSeconds;break;
                            case "duplicate":duplicate=true;break;
                            case "corrupt":corrupt=true;break;
                        }
                    }
                    if(model.State=="sampling")
                    {
                        double elapsed=Stopwatch.GetElapsedTime(sampleEpoch).TotalSeconds;
                        ulong target=sampleBase+(ulong)(elapsed*model.Rate*(1+scenario.ClockPpm/1e6));
                        // Bound catch-up work after a scheduler stall: older complete blocks are explicit producer drops.
                        ulong available=(target-next)/scenario.BlockRows;
                        if(available>128){ulong skipped=(available-128)*scenario.BlockRows;next+=skipped;produced+=skipped;dropped+=skipped;}
                        while(target-next>=scenario.BlockRows)
                        {
                            var data=Synthetic.Data(unit,session,0,next,scenario.BlockRows,model.Rate,model.Encoding,scenario.Mode,scenario.Seed,model.Config);
                            next+=scenario.BlockRows;produced+=scenario.BlockRows;
                            if(seconds<dropUntil){dropped+=data.Count;continue;}
                            if(releaseAt>seconds && delayed is null){delayed=data;continue;}
                            Enqueue(data);if(duplicate){Enqueue(data);duplicate=false;}
                        }
                    }
                    if(delayed is not null && seconds>=releaseAt){Enqueue(delayed);delayed=null;releaseAt=0;}
                }
                await Task.Delay(1,token);
            }
        }
        finally{lock(gate){done=true;if(delayed is not null){Enqueue(delayed);delayed=null;}}}
    }
    public async Task RunAsync(CancellationToken token=default)
    {
        var producer=Producer(token);bool ever=false;
        try
        {
            while(!token.IsCancellationRequested)
            {
                lock(gate){if(done && queue.Count==0)break;}
                if(clock.Elapsed.TotalSeconds>scenario.Seconds+10){lock(gate){dropped+=bufferRows;queue.Clear();queuedBytes=0;bufferRows=0;}break;}
                double pause;lock(gate)pause=pauseUntil;
                if(clock.Elapsed.TotalSeconds<pause){await Task.Delay(5,token);continue;}
                using var local=new TcpClient(){NoDelay=true,SendBufferSize=64*1024};using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(token);
                lifetime.CancelAfter(TimeSpan.FromSeconds(Math.Max(1,scenario.Seconds+10-clock.Elapsed.TotalSeconds)));
                using var send=new SemaphoreSlim(1,1);Task? commands=null;
                try
                {
                    await local.ConnectAsync(host,port,token).AsTask().WaitAsync(TimeSpan.FromSeconds(3),token);
                    byte[] connectedSession;
                    lock(gate){client=local;if(ever)reconnects++;ever=true;connectedSession=session;}
                    var stream=local.GetStream();using var parser=new FrameStream();
                    // Assign sequence numbers only under the write lock, across DATA and control replies.
                    async Task SendOne(ushort kind,object body)
                    {
                        await send.WaitAsync(lifetime.Token);try{Frame f;lock(gate){Wire.Check(session==connectedSession,"reboot");f=Json(kind,body);}await stream.WriteAsync(Wire.Encode(f),lifetime.Token);}finally{send.Release();}
                    }
                    object hello;lock(gate)hello=new{firmware="elf-simulator/1",software_build=System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(SimNode).Assembly)?.InformationalVersion,protocol=1,synthetic=true,axes=new[]{"X","Y","Z"},encodings=new[]{1,2},capabilities=new[]{"status","arm","start","stop","recover"},label=$"sim-{index+1:00}",mode=scenario.Mode,seed=scenario.Seed,preferred_encoding=scenario.Encoding,state=model.State,next_sample=next.ToString()};
                    await SendOne(1,hello);
                    using(var handshake=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
                    {handshake.CancelAfter(TimeSpan.FromSeconds(5));var ack=await parser.ReadAsync(stream,handshake.Token);Wire.Check(ack?.Kind==5,"HELLO ACK");var a=Metadata.Read(ack!);Wire.Check(Metadata.Bool(a,"ok")&&Metadata.Counter(a,"request_id")==0,"HELLO refused");}
                    commands=Task.Run(async()=>
                    {
                        while(!lifetime.IsCancellationRequested)
                        {
                            var command=await parser.ReadAsync(stream,lifetime.Token);if(command is null)throw new IOException("receiver closed");
                            Wire.Check(command.Kind==4 && command.Unit.AsSpan().SequenceEqual(unit) && command.Session.AsSpan().SequenceEqual(connectedSession),"command target");
                            object reply;lock(gate)
                            {
                                Wire.Check(session==connectedSession,"reboot");string before=model.State;
                                reply=model.Handle(command,next,(uint)Math.Min(bufferRows,uint.MaxValue),dropped);
                                if(before!="sampling"&&model.State=="sampling"){sampleEpoch=Stopwatch.GetTimestamp();sampleBase=next;}
                            }
                            await SendOne(5,reply);
                        }
                    },lifetime.Token);
                    double lastStatus=clock.Elapsed.TotalSeconds;
                    while(!lifetime.IsCancellationRequested)
                    {
                        if(commands.IsCompleted)await commands;
                        List<Frame> batch=[];bool finished;bool corruptBatch=false;
                        lock(gate)
                        {
                            Wire.Check(session==connectedSession,"reboot");finished=done&&queue.Count==0;
                            if(clock.Elapsed.TotalSeconds>=pauseUntil)
                            {
                                for(int i=0;i<scenario.CoalesceFrames && queue.TryDequeue(out var f);i++)
                                {queuedBytes-=f.Payload.Length+100;bufferRows-=f.Count;batch.Add(f);}
                                if(batch.Count>0 && corrupt){corruptBatch=true;corrupt=false;}
                            }
                        }
                        if(batch.Count>0)
                        {
                            await send.WaitAsync(lifetime.Token);try
                            {
                                byte[] bytes;
                                lock(gate){bytes=batch.SelectMany(f=>Wire.Encode(f with{Sequence=sequence++})).ToArray();}
                                if(corruptBatch)bytes[^1]^=1;
                                if(scenario.FragmentBytes==0)await stream.WriteAsync(bytes,lifetime.Token);
                                else for(int off=0;off<bytes.Length;off+=scenario.FragmentBytes)await stream.WriteAsync(bytes.AsMemory(off,Math.Min(scenario.FragmentBytes,bytes.Length-off)),lifetime.Token);
                                lock(gate)sent+=(ulong)batch.Sum(f=>(long)f.Count);
                            }
                            catch{lock(gate)dropped+=(ulong)batch.Sum(f=>(long)f.Count);throw;}
                            finally{send.Release();}
                        }
                        if(finished){object status;lock(gate)status=Status();await SendOne(3,status);await Task.Delay(50,lifetime.Token);break;}
                        if(clock.Elapsed.TotalSeconds-lastStatus>=1){object status;lock(gate)status=Status();await SendOne(3,status);lastStatus=clock.Elapsed.TotalSeconds;}
                        if(batch.Count==0)await Task.Delay(1,lifetime.Token);
                    }
                }
                catch(Exception e) when(e is SocketException or IOException or OperationCanceledException or ObjectDisposedException or ContractException or TimeoutException)
                {lock(gate)failures++;if(token.IsCancellationRequested)break;}
                finally
                {
                    lifetime.Cancel();local.Dispose();if(commands is not null)try{await commands;}catch(Exception e) when(e is IOException or OperationCanceledException or ObjectDisposedException or SocketException or ContractException){}
                    lock(gate)if(ReferenceEquals(client,local))client=null;
                }
                await Task.Delay(50,token);
            }
        }
        finally{await producer;}
    }
}
public sealed class Fleet
{
    public SimNode[] Nodes {get;}
    public Fleet(Scenario scenario,string host,int port){scenario.Validate();Nodes=Enumerable.Range(0,scenario.Nodes).Select(i=>new SimNode(scenario,i,host,port)).ToArray();}
    public Task RunAsync(CancellationToken token=default)=>Task.WhenAll(Nodes.Select(n=>n.RunAsync(token)));
    public object Snapshot()=>new{units=Nodes.Select(n=>n.Snapshot()).ToArray()};
}
