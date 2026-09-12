using System.Security.Cryptography;
using System.Text.Json;
using MultiNodeDAQ.Protocol;
namespace MultiNodeDAQ.Recording;

public sealed record VerificationResult(string Status, ulong Rows, ulong SampleErrors, ScanResult[] Files, string[] Errors, bool IntegrityValid);
public sealed record RangeResult(string Unit,string AcquisitionSession,ulong First,ulong Count,Frame[] Blocks,Frame[] Metadata,SampleRange[] Missing,bool SourceComplete);
public static class RecordingReader
{
    public static VerificationResult Verify(string directory, bool verifyCounter=true, Action<ScanResult>? progress=null)
    {
        string root=Path.GetFullPath(directory);var results=new List<ScanResult>();var errors=new List<string>();bool integrity=true;
        var paths=Directory.GetFiles(root,"*.elflog").Order(StringComparer.Ordinal).ToArray();
        var prior=new Dictionary<string,(SampleRange[] Ranges,ulong Extent)>();
        var nextSegment=new Dictionary<string,uint>();
        string? recording=null;ulong rows=0,bad=0;var declared=new HashSet<string>(StringComparer.Ordinal);
        JsonElement? manifest=null;
        try{using var doc=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root,"manifest.json")));manifest=doc.RootElement.Clone();}
        catch(FileNotFoundException e){errors.Add("manifest unavailable: "+e.Message);}
        catch(Exception e) when(e is IOException or JsonException){integrity=false;errors.Add("manifest invalid/unreadable: "+e.Message);}
        try
        {
        if(manifest is {} m)
        {
            if(Metadata.Text(m,"state")!="complete")errors.Add("manifest is not complete");
            foreach(var s in Metadata.Field(m,"segments").EnumerateArray())
            {
                string name=Metadata.Text(s,"file");
                if(Path.GetFileName(name)!=name || !declared.Add(name)){integrity=false;errors.Add("invalid/duplicate manifest path");continue;}
                string path=Path.Combine(root,name);
                if(!File.Exists(path)){integrity=false;errors.Add("missing segment: "+name);continue;}
                using var input=File.OpenRead(path);
                if(!Convert.ToHexString(SHA256.HashData(input)).Equals(Metadata.Text(s,"sha256"),StringComparison.OrdinalIgnoreCase)){integrity=false;errors.Add("segment hash mismatch: "+name);}
            }
            foreach(string path in paths)if(!declared.Contains(Path.GetFileName(path)))errors.Add("unlisted segment: "+Path.GetFileName(path));
        }
        }
        catch(Exception e) when(e is ContractException or InvalidOperationException or FormatException or IOException){integrity=false;errors.Add("invalid manifest: "+e.Message);manifest=null;}
        if(paths.Length==0){integrity=false;errors.Add("no recording segments");}
        foreach(string path in paths)
        {
            string name=Path.GetFileNameWithoutExtension(path),key=name;
            try
            {
                var first=LogScanner.Records(path).First();Wire.Check(first.Kind==2,"missing start record");
                using var document=JsonDocument.Parse(first.Payload);var details=Metadata.Field(document.RootElement,"details");
                string u=Metadata.Text(details,"unit"),a=Metadata.Text(details,"acquisition_session");uint segment=Metadata.Number(details,"segment");
                key=u+":"+a;
                Wire.Check(name==$"{u}-{a}-{segment:D5}" && segment==nextSegment.GetValueOrDefault(key),"segment name/order mismatch");
                nextSegment[key]=segment+1;
            }
            catch(Exception e) when(e is ContractException or IOException or InvalidOperationException or JsonException){integrity=false;errors.Add(name+": "+e.Message);}
            var previous=prior.GetValueOrDefault(key,(Array.Empty<SampleRange>(),0UL));
            var scan=LogScanner.Scan(path,verifyCounter,expectedPrior:previous.Item1,expectedExtent:previous.Item2);
            results.Add(scan);progress?.Invoke(scan);rows+=scan.Rows;bad+=scan.SampleErrors;
            ulong extent=scan.Ranges.Aggregate(0UL,(n,r)=>n+r.End-r.First)+scan.MissingRows;
            prior[key]=(scan.Ranges,extent);
            recording??=scan.RecordingSession;
            if(scan.RecordingSession!=recording){integrity=false;errors.Add("mixed recording session: "+name);}
            if(scan.Status=="corrupt")integrity=false;
            if(scan.Status!="complete")errors.Add(name+": "+scan.Status+" "+scan.Error);
            if(scan.SampleErrors>0){integrity=false;errors.Add(name+": counter value errors");}
        }
        if(manifest is {} final && recording is not null && Metadata.Text(final,"recording_session")!=recording){integrity=false;errors.Add("manifest recording-session mismatch");}
        return new(errors.Count==0?"complete":"incomplete_or_corrupt",rows,bad,results.ToArray(),errors.ToArray(),integrity);
    }
    public static RangeResult ReadRange(string directory,string unit,string session,ulong first,uint count,bool allowIncomplete=false)
    {
        Wire.Check(count is >=1 and <=1_000_000 && first<=ulong.MaxValue-count,"reader range");
        unit=unit.ToLowerInvariant();session=session.ToLowerInvariant();ulong end=first+count;
        var verification=Verify(directory,false);
        Wire.Check(verification.Status=="complete" || (allowIncomplete && verification.IntegrityValid),"range read requires complete recording or explicit incomplete-prefix recovery with valid integrity");
        var blocks=new List<Frame>();var metadata=new Dictionary<string,Frame>();
        foreach(var file in verification.Files.Where(f=>f.Unit==unit && f.AcquisitionSession==session))
        foreach(var r in LogScanner.Records(file.Path,file.ValidBytes))
        {
            if(r.Kind!=1)continue;var frame=Wire.Decode(r.Payload);
            if(frame.Kind!=2)
            {
                string? key=frame.Kind==1?"hello":frame.Kind==6?"timing"+frame.Timing:null;
                if(frame.Kind==5){var o=Metadata.Read(frame);if(Metadata.Bool(o,"ok") && Metadata.Field(o,"details").TryGetProperty("config",out var c))key="config"+Metadata.Number(c,"id");}
                if(key is not null)metadata[key]=frame;continue;
            }
            ulong a=Math.Max(first,frame.FirstSample),b=Math.Min(end,frame.FirstSample+frame.Count);if(a>=b)continue;
            blocks.Add(frame with{FirstSample=a,Count=(uint)(b-a),Payload=frame.Payload.AsSpan(checked((int)(a-frame.FirstSample)*12),checked((int)(b-a)*12)).ToArray()});
        }
        var missing=new List<SampleRange>();ulong cursor=first;
        foreach(var block in blocks.OrderBy(b=>b.FirstSample))
        {Wire.Check(block.FirstSample>=cursor,"overlapping replay blocks");if(block.FirstSample>cursor)missing.Add(new(cursor,block.FirstSample));cursor=block.FirstSample+block.Count;}
        if(cursor<end)missing.Add(new(cursor,end));
        return new(unit,session,first,count,blocks.OrderBy(b=>b.FirstSample).ToArray(),metadata.Values.ToArray(),missing.ToArray(),verification.Status=="complete");
    }
}
