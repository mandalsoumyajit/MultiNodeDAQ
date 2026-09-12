using System.Text.Json;
using MultiNodeDAQ.Protocol;
namespace MultiNodeDAQ.Recording;

public sealed record ScanResult(string Path, string Status, long ValidBytes, long? ErrorOffset, string? Error,
    ulong Records, ulong Rows, ulong Duplicates, ulong MissingRows, ulong ContiguousSample, ulong CommitMarker,
    ulong SampleErrors, string? RecordingSession, string? Unit, string? AcquisitionSession, bool PriorContext,
    SampleRange[] Ranges);

/// <summary>Read-only verification. No resynchronization or repair of source files.</summary>
public static class LogScanner
{
    public static IEnumerable<(long Offset, long End, ushort Kind, byte[] Payload)> Records(string path, long? through=null)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, FileOptions.SequentialScan);
        byte[] header = new byte[36]; stream.ReadExactly(header); LogFormat.ReadHeader(header);
        byte[] prefix = new byte[16];
        while (stream.Position < Math.Min(stream.Length,through??long.MaxValue))
        {
            long start = stream.Position; stream.ReadExactly(prefix);
            Wire.Check(prefix.AsSpan(0,4).SequenceEqual("ELR1"u8) && Wire.U16(prefix,4)==1, "record prefix");
            uint length = Wire.U32(prefix,8);
            Wire.Check(length is >=20 and <=2_097_152 && Wire.U32(prefix,12)==length-20, "record length");
            byte[] record = new byte[length]; prefix.CopyTo(record,0); stream.ReadExactly(record.AsSpan(16));
            var parsed = LogFormat.ReadRecord(record); yield return (start,stream.Position,parsed.Kind,parsed.Payload);
        }
    }
    public static ScanResult Scan(string path, bool verifyCounter = true, Action<Frame>? observe = null,
        SampleRange[]? expectedPrior = null, ulong? expectedExtent = null, string? scratchDirectory = null)
    {
        using var ledger = new RangeLedger(scratchDirectory);
        ulong records=0,rows=0,duplicates=0,errors=0,commit=0; long valid=0,at=0;
        string? recording=null,unit=null,session=null; bool footer=false,prior=false,started=false,hello=false;
        string state="incomplete",mode="unknown"; uint seed=0; bool synthetic=false;
        var configs=new Dictionary<uint,(uint Rate,string Json)>(); var timings=new Dictionary<uint,string>();
        try
        {
            using(var h=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite))
            {byte[] bytes=new byte[36];h.ReadExactly(bytes);recording=Convert.ToHexString(LogFormat.ReadHeader(bytes)).ToLowerInvariant();valid=36;}
            foreach(var record in Records(path))
            {
                at=record.Offset; Wire.Check(!footer,"bytes after footer");
                if(record.Kind==1)
                {
                    Wire.Check(started,"FRAME before recording_started");var f=Wire.Decode(record.Payload);
                    Wire.Check(Convert.ToHexString(f.Unit).Equals(unit,StringComparison.OrdinalIgnoreCase) && Convert.ToHexString(f.Session).Equals(session,StringComparison.OrdinalIgnoreCase),"file identity mismatch");
                    if(f.Kind==2)
                    {
                        Wire.Check(hello && configs.TryGetValue(f.Config,out var config) && config.Rate==f.Rate,"undefined configuration");
                        Wire.Check(f.Calibration==0 && (f.Timing==0 || timings.ContainsKey(f.Timing)),"undefined calibration/timing");
                        if(verifyCounter && synthetic && mode=="counter")
                        {var values=Wire.Samples(f);for(int i=0;i<values.Length;i++){int expected=(int)(((f.FirstSample+(ulong)(i/3))*3+(ulong)(i%3)+seed)&0xffffff)-8388608;if(values[i]!=expected)errors++;}}
                        if(ledger.Add(f))rows+=f.Count;else duplicates++;
                    }
                    else
                    {
                        var o=Metadata.Read(f);
                        if(f.Kind==1)
                        {hello=true;synthetic=Metadata.Bool(o,"synthetic");mode=o.TryGetProperty("mode",out _)?Metadata.Text(o,"mode"):"unknown";seed=o.TryGetProperty("seed",out _)?Metadata.Number(o,"seed"):0;}
                        if(f.Kind==5 && Metadata.Bool(o,"ok") && Metadata.Field(o,"details").TryGetProperty("config",out var c))
                        {
                            uint id=Metadata.Number(c,"id"),rate=Metadata.Number(c,"sample_rate_hz"),encoding=Metadata.Number(c,"encoding");
                            Wire.Check(id>0 && rate is >=1 and <=1_000_000 && encoding is 1 or 2,"configuration definition");Metadata.Bool(c,"synthetic");
                            string raw=c.GetRawText();Wire.Check(!configs.TryGetValue(id,out var old)||old.Json==raw,"configuration mutation");
                            Wire.Check(configs.Count<64 || configs.ContainsKey(id),"configuration capacity");configs[id]=(rate,raw);
                        }
                        if(f.Kind==6)
                        {Wire.Check(timings.Count<64 || timings.ContainsKey(f.Timing),"timing capacity");string raw=o.GetRawText();Wire.Check(!timings.TryGetValue(f.Timing,out var old)||old==raw,"timing mutation");timings[f.Timing]=raw;}
                        if(f.Kind==7)ledger.ObserveEnd(Metadata.Counter(o,"first_sample")+Metadata.Counter(o,"count"));
                        if(f.Kind==3 && Metadata.State(o)=="idle" && Metadata.Number(o,"buffer_rows")==0)ledger.ObserveEnd(Metadata.Counter(o,"next_sample"));
                    }
                    observe?.Invoke(f);
                }
                else
                {
                    using var doc=JsonDocument.Parse(record.Payload);var o=doc.RootElement;
                    if(record.Kind==2)
                    {
                        string kind=Metadata.Text(o,"event");Metadata.Counter(o,"monotonic_ns");
                        if(kind=="recording_started")
                        {
                            Wire.Check(!started && records==0,"segment start ordering");started=true;
                            var d=Metadata.Field(o,"details");unit=Metadata.Text(d,"unit");session=Metadata.Text(d,"acquisition_session");
                            Wire.Check(unit.Length==32 && unit.All(char.IsAsciiHexDigit) && session.Length==32 && session.All(char.IsAsciiHexDigit),"segment identity");
                            var a=Metadata.Field(d,"prior_ranges");Wire.Check(a.ValueKind==JsonValueKind.Array && a.GetArrayLength()<=4096,"prior range shape");
                            var ranges=a.EnumerateArray().Select(x=>new SampleRange(Metadata.Counter(x,"first"),Metadata.Counter(x,"end"))).ToArray();
                            ulong extent=Metadata.Counter(d,"prior_extent");prior=ranges.Length>0 || extent>0;
                            if(expectedPrior is not null)Wire.Check(ranges.SequenceEqual(expectedPrior) && extent==expectedExtent,"segment chain discontinuity");
                            ledger.Seed(ranges,extent);
                        }
                    }
                    if(record.Kind==3)
                    {
                        Wire.Check(started && Metadata.Counter(o,"through_offset")==(ulong)record.Offset,"commit offset");
                        Wire.Check(Metadata.Text(o,"unit")==unit && Metadata.Text(o,"acquisition_session")==session,"commit identity");
                        ulong next=Metadata.Counter(o,"next_sample");Wire.Check(next==ledger.Contiguous && next>=commit,"commit crosses gap or regresses");commit=next;
                    }
                    if(record.Kind==4)
                    {
                        Wire.Check(started && Metadata.Counter(o,"records")==records && Metadata.Counter(o,"sample_rows")==rows,"footer counters");
                        state=Metadata.Text(o,"status");Wire.Check(state is "complete" or "incomplete","footer state");footer=true;
                    }
                }
                records++;valid=record.End;
            }
            Wire.Check(started,"missing recording_started");
            return new(path,footer?state:"incomplete",valid,null,footer?null:"missing footer",records,rows,duplicates,ledger.Missing,ledger.Contiguous,commit,errors,recording,unit,session,prior,ledger.Ranges.ToArray());
        }
        catch(Exception e) when(e is EndOfStreamException or ContractException or IOException or JsonException)
        {
            bool truncated=e is EndOfStreamException && !footer;
            return new(path,truncated?"truncated":"corrupt",valid,Math.Max(at,valid),e.Message,records,rows,duplicates,ledger.Missing,ledger.Contiguous,commit,errors,recording,unit,session,prior,ledger.Ranges.ToArray());
        }
    }
}
