using System.Text.Json;
using MultiNodeDAQ.Protocol;
namespace MultiNodeDAQ.Recording;

public sealed record ReplayUnit(string Unit,string Session,ulong First,ulong End,uint Rate);
public sealed class ReplayIndex
{
    private sealed record Entry(string Path,long Offset,ulong First,ulong End);
    private readonly Dictionary<string,List<Entry>> entries=[];
    private readonly Dictionary<string,Frame[]> metadata=[];
    private readonly HashSet<string> unordered=[];
    private readonly Dictionary<string,(long Length,DateTime Time)> files=[];
    public required string DirectoryPath {get;init;}
    public required VerificationResult Verification {get;init;}
    public List<ReplayUnit> Units {get;}=[];
    public List<string> Events {get;}=[];
    public static ReplayIndex Open(string directory,CancellationToken token=default)
    {
        var verification=RecordingReader.Verify(directory,false, _=>token.ThrowIfCancellationRequested());
        Wire.Check(verification.Status=="complete","Replay requires a complete verified session. Use the recovery tools for incomplete recordings.");
        var result=new ReplayIndex{DirectoryPath=Path.GetFullPath(directory),Verification=verification};int entryCount=0;
        foreach(var group in verification.Files.GroupBy(s=>s.Unit+":"+s.AcquisitionSession))
        {
            var index=new List<Entry>();var defs=new Dictionary<string,Frame>();ulong first=ulong.MaxValue,end=0;uint rate=0;ulong next=0;
            foreach(var scan in group)
            {
                var file=new FileInfo(scan.Path);result.files[scan.Path]=(file.Length,file.LastWriteTimeUtc);bool initial=true;ulong priorFirst=0;
                foreach(var r in LogScanner.Records(scan.Path))
                {
                    token.ThrowIfCancellationRequested();
                    if(r.Kind!=1){if(result.Events.Count<1024)result.Events.Add(Path.GetFileName(scan.Path)+" @ "+r.Offset+": "+System.Text.Encoding.UTF8.GetString(r.Payload));continue;}
                    var f=Wire.Decode(r.Payload);
                    if(f.Kind!=2)
                    {
                        string? id=f.Kind==1?"hello":f.Kind==6?"timing"+f.Timing:null;
                        if(f.Kind==5){var o=Metadata.Read(f);if(Metadata.Bool(o,"ok")&&Metadata.Field(o,"details").TryGetProperty("config",out var c))id="config"+Metadata.Number(c,"id");}
                        if(id is not null){Wire.Check(defs.ContainsKey(id)||defs.Count<130,"replay metadata capacity");defs[id]=f;Wire.Check(defs.Values.Sum(x=>x.Payload.Length)<=1048576,"replay metadata bytes");}
                        continue;
                    }
                    if(!initial&&f.FirstSample<priorFirst)result.unordered.Add(scan.Path);priorFirst=f.FirstSample;
                    first=Math.Min(first,f.FirstSample);end=Math.Max(end,f.FirstSample+f.Count);rate=f.Rate;
                    if(initial||f.FirstSample>=next){Wire.Check(++entryCount<=250000,"replay sparse index capacity");index.Add(new(scan.Path,r.Offset,f.FirstSample,f.FirstSample+f.Count));next=f.FirstSample+8192;initial=false;}
                }
            }
            var sample=group.First();if(first==ulong.MaxValue)continue;
            result.Units.Add(new(sample.Unit!,sample.AcquisitionSession!,first,end,rate));result.entries[group.Key]=index;result.metadata[group.Key]=defs.Values.ToArray();
        }
        return result;
    }
    public RangeResult Read(ReplayUnit unit,ulong first,uint count)
    {
        Wire.Check(count is >0 and <=1000000 && first<=ulong.MaxValue-count,"replay range");
        string key=unit.Unit+":"+unit.Session;ulong end=first+count;var blocks=new List<Frame>();
        foreach(var group in entries[key].GroupBy(x=>x.Path))
        {
            var file=new FileInfo(group.Key);var prior=files[group.Key];Wire.Check(file.Length==prior.Length&&file.LastWriteTimeUtc==prior.Time,"recording changed; reopen to verify");
            bool ordered=!unordered.Contains(group.Key);var selected=ordered?(group.LastOrDefault(x=>x.First<=first)??group.First()):group.First();
            if(ordered&&group.First().First>=end)continue;
            using var stream=File.OpenRead(group.Key);stream.Position=selected.Offset;
            while(stream.Position<stream.Length)
            {
                byte[] prefix=new byte[16];stream.ReadExactly(prefix);uint size=Wire.U32(prefix,8);Wire.Check(size is >=20 and <=2097152,"replay record length");
                byte[] bytes=new byte[size];prefix.CopyTo(bytes,0);stream.ReadExactly(bytes.AsSpan(16));var r=LogFormat.ReadRecord(bytes);if(r.Kind!=1)continue;
                var f=Wire.Decode(r.Payload);if(f.Kind!=2)continue;if(ordered&&f.FirstSample>=end)break;
                ulong a=Math.Max(first,f.FirstSample),b=Math.Min(end,f.FirstSample+f.Count);if(a>=b)continue;
                blocks.Add(f with{FirstSample=a,Count=(uint)(b-a),Payload=f.Payload.AsSpan(checked((int)(a-f.FirstSample)*12),checked((int)(b-a)*12)).ToArray()});
            }
        }
        var unique=blocks.OrderBy(f=>f.FirstSample).GroupBy(f=>f.FirstSample).Select(g=>g.First()).ToArray();var missing=new List<SampleRange>();ulong cursor=first;
        foreach(var f in unique){if(f.FirstSample>cursor)missing.Add(new(cursor,f.FirstSample));cursor=Math.Max(cursor,f.FirstSample+f.Count);}if(cursor<end)missing.Add(new(cursor,end));
        return new(unit.Unit,unit.Session,first,count,unique,metadata[key],missing.ToArray(),true);
    }
    public static void ExportCsv(string path,RangeResult range)
    {
        using var output=new StreamWriter(new FileStream(path,FileMode.CreateNew,FileAccess.Write));
        output.WriteLine("sample,x,y,z,flags,config,calibration,timing");
        foreach(var f in range.Blocks){var values=Wire.Samples(f);for(int i=0;i<f.Count;i++)output.WriteLine($"{f.FirstSample+(ulong)i},{values[3*i]},{values[3*i+1]},{values[3*i+2]},{f.Flags},{f.Config},{f.Calibration},{f.Timing}");}
        using var sidecar=new FileStream(path+".json",FileMode.CreateNew,FileAccess.Write);
        JsonSerializer.Serialize(sidecar,new{range.Unit,range.AcquisitionSession,range.First,range.Count,range.Missing,range.SourceComplete,metadata=range.Metadata.Select(f=>Convert.ToBase64String(Wire.Encode(f)))});
    }
}
