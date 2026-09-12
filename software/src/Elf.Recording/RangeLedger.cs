using System.Security.Cryptography;
using Elf.Protocol;
namespace Elf.Recording;

public readonly record struct SampleRange(ulong First, ulong End);

/// <summary>Bounded coverage in RAM; exact historical block fingerprints on disposable disk.</summary>
public sealed class RangeLedger : IDisposable
{
    private readonly List<SampleRange> ranges = [];
    private readonly FileStream fingerprints;
    public IReadOnlyList<SampleRange> Ranges => ranges;
    public ulong Rows { get; private set; }
    public ulong Extent { get; private set; }
    public ulong Contiguous => ranges.Count > 0 && ranges[0].First == 0 ? ranges[0].End : 0;
    public ulong Missing => Extent - Rows;
    public RangeLedger(string? scratchDirectory = null)
    {
        string directory = scratchDirectory ?? Path.GetTempPath();
        Directory.CreateDirectory(directory);
        fingerprints = new(Path.Combine(directory, $"elf-index-{Guid.NewGuid():N}.tmp"), FileMode.CreateNew,
            FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.DeleteOnClose);
    }
    public void Seed(IEnumerable<SampleRange> prior, ulong extent)
    {
        Wire.Check(ranges.Count == 0 && fingerprints.Length == 0, "ledger already initialized");
        foreach (var r in prior)
        {
            Wire.Check(r.First < r.End && r.End <= extent && (ranges.Count == 0 || ranges[^1].End < r.First), "prior ranges");
            Wire.Check(ranges.Count < 4096, "range capacity"); ranges.Add(r); Rows += r.End - r.First;
        }
        Extent = extent;
    }
    public void ObserveEnd(ulong end) => Extent = Math.Max(Extent, end);
    public bool Add(Frame frame)
    {
        ulong end = checked(frame.FirstSample + frame.Count);
        byte[] hash = SHA256.HashData(Wire.Encode(frame with { Sequence = 0 }));
        bool overlap = ranges.Any(r => r.First < end && frame.FirstSample < r.End);
        if (overlap)
        {
            fingerprints.Flush(); fingerprints.Position = 0; Span<byte> entry = stackalloc byte[48];
            while (fingerprints.Position < fingerprints.Length)
            {
                fingerprints.ReadExactly(entry);
                if (Wire.U64(entry, 0) == frame.FirstSample && Wire.U64(entry, 8) == end)
                {
                    Wire.Check(entry[16..].SequenceEqual(hash), "conflicting duplicate sample range");
                    fingerprints.Position = fingerprints.Length; return false;
                }
            }
            throw new ContractException("overlapping range or duplicate outside this segment's evidence");
        }
        int i = ranges.FindIndex(r => r.First > frame.FirstSample); if (i < 0) i = ranges.Count;
        ranges.Insert(i, new(frame.FirstSample, end));
        if (i > 0 && ranges[i - 1].End == ranges[i].First)
        { ranges[i - 1] = new(ranges[i - 1].First, ranges[i].End); ranges.RemoveAt(i); i--; }
        if (i + 1 < ranges.Count && ranges[i].End == ranges[i + 1].First)
        { ranges[i] = new(ranges[i].First, ranges[i + 1].End); ranges.RemoveAt(i + 1); }
        Wire.Check(ranges.Count <= 4096, "range capacity");
        Span<byte> record = stackalloc byte[48]; Wire.P64(record, 0, frame.FirstSample); Wire.P64(record, 8, end); hash.CopyTo(record[16..]);
        fingerprints.Position = fingerprints.Length; fingerprints.Write(record);
        Rows += frame.Count; ObserveEnd(end); return true;
    }
    public void Dispose() => fingerprints.Dispose();
}
