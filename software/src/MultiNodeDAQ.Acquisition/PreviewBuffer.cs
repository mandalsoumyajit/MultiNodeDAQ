using System.Diagnostics;
using MultiNodeDAQ.Protocol;
namespace MultiNodeDAQ.Acquisition;

// Display-only buffer. Dropping old display samples never affects recording.
public sealed class PreviewBuffer
{
    private readonly object gate=new();
    private readonly Queue<Frame> frames=[];
    private int rows;
    private long received;
    public void Add(Frame f)
    {
        lock(gate)
        {
            if(f.Count>16384)f=f with{FirstSample=f.FirstSample+f.Count-16384,Count=16384,Payload=f.Payload[^196608..]};
            if(frames.Count>0 && (frames.Last().Rate!=f.Rate || frames.Last().Config!=f.Config || f.FirstSample<frames.Last().FirstSample+frames.Last().Count)){frames.Clear();rows=0;}
            frames.Enqueue(f);rows+=(int)f.Count;received=Stopwatch.GetTimestamp();
            while(rows>16384 && frames.Count>1)rows-=(int)frames.Dequeue().Count;
        }
    }
    public object Snapshot(double seconds)
    {
        lock(gate)
        {
            if(frames.Count==0)return new{available=false};
            var last=frames.Last();ulong end=last.FirstSample+last.Count;
            ulong first=end>(ulong)(seconds*last.Rate)?end-(ulong)(seconds*last.Rate):0;
            int bins=400;double width=Math.Max(1,(end-first)/(double)bins);
            var low=Enumerable.Range(0,3).Select(_=>Enumerable.Repeat(double.PositiveInfinity,bins).ToArray()).ToArray();
            var high=Enumerable.Range(0,3).Select(_=>Enumerable.Repeat(double.NegativeInfinity,bins).ToArray()).ToArray();
            double[] sum=new double[3],square=new double[3];long count=0,clipped=0;uint flags=0;ulong missing=0,cursor=first;
            foreach(var f in frames)
            {
                var values=Wire.Samples(f);flags|=f.Flags;
                ulong start=Math.Max(first,f.FirstSample);if(start>=f.FirstSample+f.Count)continue;
                if(start>cursor)missing+=start-cursor;
                for(ulong s=start;s<f.FirstSample+f.Count;s++)
                {
                    int i=checked((int)(s-f.FirstSample)),b=Math.Min(bins-1,(int)((s-first)/width));count++;
                    for(int a=0;a<3;a++){double v=values[i*3+a];low[a][b]=Math.Min(low[a][b],v);high[a][b]=Math.Max(high[a][b],v);sum[a]+=v;square[a]+=v*v;if(v<=-8388608||v>=8388607)clipped++;}
                }
                cursor=f.FirstSample+f.Count;
            }
            return new{available=true,first_sample=first.ToString(),end_sample=end.ToString(),rate=last.Rate,age_seconds=Stopwatch.GetElapsedTime(received).TotalSeconds,
                minimum=low.Select(a=>a.Select(v=>double.IsFinite(v)?(double?)v:null).ToArray()).ToArray(),maximum=high.Select(a=>a.Select(v=>double.IsFinite(v)?(double?)v:null).ToArray()).ToArray(),
                mean=sum.Select(v=>count>0?v/count:0).ToArray(),rms=square.Select(v=>count>0?Math.Sqrt(v/count):0).ToArray(),rows=count,clipped_values=clipped,flags,missing_rows=missing,calibration=last.Calibration,timing=last.Timing,units="ADC codes",buffer_limit_rows=16384};
        }
    }
}
