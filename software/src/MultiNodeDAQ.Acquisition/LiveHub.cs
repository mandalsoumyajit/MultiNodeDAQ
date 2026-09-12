using System.Diagnostics;
using System.Text.Json;
using MultiNodeDAQ.Protocol;
namespace MultiNodeDAQ.Acquisition;

// Only bounded memory operations run on the acquisition thread. No subscriber I/O.
public sealed class LiveHub
{
    private readonly object gate=new();
    private readonly Dictionary<string,Dictionary<string,byte[]>> definitions=[];
    private readonly List<Subscription> subscribers=[];
    public Subscription Subscribe(string[] units)
    {
        Wire.Check(units.Length is >0 and <=16 && units.All(u=>u.Length==32 && u.All(char.IsAsciiHexDigit)),"selected unit IDs");
        lock(gate){Wire.Check(subscribers.Count<8,"subscription capacity");var s=new Subscription(units.Select(u=>u.ToUpperInvariant()).ToHashSet());subscribers.Add(s);return s;}
    }
    public void Remove(Subscription s){lock(gate)subscribers.Remove(s);}
    public void Publish(Frame frame)
    {
        lock(gate)
        {
            string key=Convert.ToHexString(frame.Unit)+":"+Convert.ToHexString(frame.Session);
            if(!definitions.TryGetValue(key,out var defs))
            {if(definitions.Count>=128)return;defs=[];definitions[key]=defs;}
            if(frame.Kind!=2)
            {
                string? id=frame.Kind==1?"hello":frame.Kind==6?"timing"+frame.Timing:null;
                if(frame.Kind==5){var o=Metadata.Read(frame);if(Metadata.Bool(o,"ok") && Metadata.Field(o,"details").TryGetProperty("config",out var c))id="config"+Metadata.Number(c,"id");}
                if(id is not null){var b=Wire.Encode(frame);if(defs.Count<130 && defs.Values.Sum(x=>x.Length)+b.Length<=1_048_576)defs[id]=b;}
                return;
            }
            if(subscribers.Count==0)return;
            var block=Wire.Normalize(frame);
            byte[] context=JsonSerializer.SerializeToUtf8Bytes(new{op="context",request_id="0",unit=Convert.ToHexString(frame.Unit).ToLowerInvariant(),acquisition_session=Convert.ToHexString(frame.Session).ToLowerInvariant(),metadata=defs.Values.Select(Convert.ToBase64String).ToArray()});
            var packet=new Packet(block,context,Ipc.Encode(2,0,Wire.Encode(block)),Stopwatch.GetTimestamp());
            foreach(var s in subscribers)if(s.Units.Contains(Convert.ToHexString(frame.Unit)))s.Offer(packet);
        }
    }
    public sealed record Packet(Frame Frame,byte[] Context,byte[] Bytes,long Created){public long Cost=>Context.Length+Bytes.Length+28;}
    public sealed class Subscription(HashSet<string> units)
    {
        public HashSet<string> Units {get;}=units;
        private readonly object gate=new();
        private readonly Queue<Packet> queue=[];
        private readonly Dictionary<string,double> durations=[];
        private long bytes,skipped,peak;
        public const long Limit=8*1024*1024;
        public void Offer(Packet p)
        {
            lock(gate)
            {
                string u=Convert.ToHexString(p.Frame.Unit);
                if(p.Cost>Limit){skipped+=p.Frame.Count;return;}
                while(queue.Count>0 && (bytes+p.Cost>Limit || durations.GetValueOrDefault(u)+p.Frame.Count/(double)p.Frame.Rate>2 || Stopwatch.GetElapsedTime(queue.Peek().Created).TotalSeconds>2))Drop();
                queue.Enqueue(p);bytes+=p.Cost;peak=Math.Max(peak,bytes);durations[u]=durations.GetValueOrDefault(u)+p.Frame.Count/(double)p.Frame.Rate;
            }
        }
        private Packet Pop(){var p=queue.Dequeue();bytes-=p.Cost;string u=Convert.ToHexString(p.Frame.Unit);durations[u]-=p.Frame.Count/(double)p.Frame.Rate;return p;}
        private void Drop(){skipped+=Pop().Frame.Count;}
        public Packet? Take(){lock(gate){while(queue.Count>0 && Stopwatch.GetElapsedTime(queue.Peek().Created).TotalSeconds>2)Drop();return queue.Count>0?Pop():null;}}
        public object Snapshot(){lock(gate)return new{queued_bytes=bytes,peak_bytes=peak,byte_limit=Limit,skipped_rows=skipped,history_seconds=2};}
    }
}
