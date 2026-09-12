using MultiNodeDAQ.Protocol;
using MultiNodeDAQ.Recording;
namespace MultiNodeDAQ.Acquisition;

// Serializes recorder attachment and frame admission, never performs disk I/O on the receiver path.
public sealed class RecordingControl : IAsyncDisposable
{
    private readonly object gate=new();
    private readonly Dictionary<string,Dictionary<string,Frame>> definitions=[];
    private RecordingSession? current;
    private object? last;
    private bool stopping,starting,lastFailed;
    private readonly HashSet<string> seeded=[];
    public RecordingControl(RecordingSession? initial=null){current=initial;}
    public bool Active {get{lock(gate)return current is not null;}}
    public bool Failed {get{lock(gate)return current?.Failed??lastFailed;}}
    public object? Snapshot(){lock(gate)return current?.Snapshot()??(stopping||starting?new{state=starting?"starting":"draining"}:last);}
    public void Accept(Frame f)
    {
        lock(gate)
        {
            string key=Convert.ToHexString(f.Unit)+":"+Convert.ToHexString(f.Session);
            if(!definitions.TryGetValue(key,out var defs)){Wire.Check(definitions.Count<128,"recording context capacity");definitions[key]=defs=[];}
            string? id=f.Kind==1?"hello":f.Kind==6?"timing"+f.Timing:null;
            if(f.Kind==5){var o=Metadata.Read(f);if(Metadata.Bool(o,"ok")&&Metadata.Field(o,"details").TryGetProperty("config",out var c))id="config"+Metadata.Number(c,"id");}
            if(id is not null){Wire.Check(defs.ContainsKey(id)||defs.Count<130,"recording definition capacity");Wire.Check(defs.Values.Sum(x=>x.Payload.Length)+f.Payload.Length<=2_097_152,"recording context bytes");defs[id]=f;}
            if(current is not null){if(seeded.Add(key)&&f.Kind!=1)foreach(var def in defs.Values.OrderBy(x=>x.Kind==1?0:1))current.Accept(def);current.Accept(f);}
        }
    }
    public void Event(string unit,string session,string kind,object details){lock(gate)current?.Event(unit,session,kind,details);}
    public ulong Committed(string unit,string session){lock(gate)return current?.Committed(unit,session)??0;}
    public object Start(string directory)
    {
        lock(gate){Wire.Check(current is null&&!stopping&&!starting,"recording active or draining");starting=true;}
        try
        {
            // Directory/manifest I/O cannot hold the frame-admission lock.
            var next=new RecordingSession(new(directory));
            lock(gate){current=next;seeded.Clear();lastFailed=false;return next.Snapshot();}
        }
        finally{lock(gate)starting=false;}
    }
    public async Task<object?> StopAsync(bool clean=true)
    {
        RecordingSession? target;lock(gate){Wire.Check(!stopping&&!starting,"recording is starting or already draining");target=current;if(target is null)return last;stopping=true;current=null;}
        try{await target.CompleteAsync(clean);lock(gate){last=target.Snapshot();lastFailed=target.Failed;return last;}}
        finally{await target.DisposeAsync();lock(gate)stopping=false;}
    }
    public async ValueTask DisposeAsync(){if(Active)await StopAsync(false);}
}
