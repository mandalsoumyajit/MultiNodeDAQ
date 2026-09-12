using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
namespace MultiNodeDAQ.Protocol;

public sealed class ContractException(string message) : Exception(message);
public sealed record Frame(ushort Kind, uint Flags, byte[] Unit, byte[] Session,
    ulong Sequence, ulong FirstSample, uint Count, uint Rate, uint Config,
    uint Calibration, uint Timing, ushort Channels, ushort Encoding, byte[] Payload);

public static class Wire
{
    public const int HeaderSize = 96, MaxFrame = 1_048_576;
    public static uint Crc(ReadOnlySpan<byte> data)
    {
        uint crc = 0xffffffff;
        foreach (byte b in data) { crc ^= b; for (int i=0;i<8;i++) crc = (crc>>1) ^ ((crc&1)!=0 ? 0xedb88320u : 0u); }
        return crc ^ 0xffffffff;
    }
    public static void Check(bool ok, string why) { if (!ok) throw new ContractException(why); }
    public static uint U32(ReadOnlySpan<byte> b,int p)=>BinaryPrimitives.ReadUInt32LittleEndian(b[p..]);
    public static ushort U16(ReadOnlySpan<byte> b,int p)=>BinaryPrimitives.ReadUInt16LittleEndian(b[p..]);
    public static ulong U64(ReadOnlySpan<byte> b,int p)=>BinaryPrimitives.ReadUInt64LittleEndian(b[p..]);
    public static void P32(Span<byte>b,int p,uint v)=>BinaryPrimitives.WriteUInt32LittleEndian(b[p..],v);
    public static void P16(Span<byte>b,int p,ushort v)=>BinaryPrimitives.WriteUInt16LittleEndian(b[p..],v);
    public static void P64(Span<byte>b,int p,ulong v)=>BinaryPrimitives.WriteUInt64LittleEndian(b[p..],v);
    public static int PeekLength(ReadOnlySpan<byte> b)
    {
        Check(b.Length>=12,"prefix incomplete"); Check(b[..4].SequenceEqual("ELD1"u8),"magic");
        Check(U16(b,4)==1,"version"); Check(U16(b,6) is >=1 and <=7,"kind");
        uint n=U32(b,8); Check(n>=100 && n<=MaxFrame,"length"); return (int)n;
    }
    public static void Validate(Frame f)
    {
        Check(f.Kind is >=1 and <=7,"kind");
        Check(f.Unit.Length==16 && f.Session.Length==16 && f.Unit.Any(x=>x!=0) && f.Session.Any(x=>x!=0),"identity");
        Check((f.Flags&~15u)==0,"flags"); Check(f.Payload.Length<=MaxFrame-100,"payload limit");
        if(f.Kind==2)
        {
            Check(f.Encoding is 1 or 2 && f.Channels==3 && f.Count is >=1 and <=4096 && f.Rate is >=1 and <=1_000_000,"data shape");
            Check(f.Config!=0,"config id"); Check(f.FirstSample<=ulong.MaxValue-f.Count,"counter overflow");
            Check(f.Payload.Length==(long)f.Count*3*(f.Encoding==1?3:4),"payload size");
            if(f.Encoding==2) foreach(int v in Samples(f)) Check(v is >=-8388608 and <=8388607,"sample range");
        }
        else
        {
            Check(f.Encoding==0 && f.Channels==0 && f.Count==0 && f.Flags==0,"json shape");
            JsonObject(f.Payload);
        }
    }
    public static void JsonObject(byte[] b)
    {
        try
        {
            _=new UTF8Encoding(false,true).GetString(b);
            using var d=JsonDocument.Parse(b,new JsonDocumentOptions{MaxDepth=16});
            Check(d.RootElement.ValueKind==JsonValueKind.Object,"json object");
            UniqueKeys(d.RootElement);
        }
        catch(Exception e) when(e is JsonException or DecoderFallbackException) { throw new ContractException("json"); }
    }
    private static void UniqueKeys(JsonElement e)
    {
        if(e.ValueKind==JsonValueKind.Object)
        {
            var seen=new HashSet<string>(); foreach(var p in e.EnumerateObject()) { Check(seen.Add(p.Name),"duplicate json key"); UniqueKeys(p.Value); }
        }
        else if(e.ValueKind==JsonValueKind.Array) foreach(var x in e.EnumerateArray()) UniqueKeys(x);
    }
    public static byte[] Encode(Frame f)
    {
        Validate(f); byte[] b=new byte[100+f.Payload.Length]; "ELD1"u8.CopyTo(b);
        P16(b,4,1);P16(b,6,f.Kind);P32(b,8,(uint)b.Length);P32(b,12,f.Flags);
        f.Unit.CopyTo(b,16);f.Session.CopyTo(b,32);P64(b,48,f.Sequence);P64(b,56,f.FirstSample);
        P32(b,64,f.Count);P32(b,68,f.Rate);P32(b,72,f.Config);P32(b,76,f.Calibration);P32(b,80,f.Timing);
        P16(b,84,f.Channels);P16(b,86,f.Encoding);P32(b,88,(uint)f.Payload.Length);
        f.Payload.CopyTo(b,96);P32(b,b.Length-4,Crc(b.AsSpan(0,b.Length-4)));return b;
    }
    public static Frame Decode(ReadOnlySpan<byte> b)
    {
        int n=PeekLength(b);Check(b.Length==n,"exact length");Check(U32(b,92)==0,"reserved");
        Check(U32(b,88)==n-100,"payload length");Check(U32(b,n-4)==Crc(b[..(n-4)]),"crc");
        var f=new Frame(U16(b,6),U32(b,12),b.Slice(16,16).ToArray(),b.Slice(32,16).ToArray(),U64(b,48),U64(b,56),U32(b,64),U32(b,68),U32(b,72),U32(b,76),U32(b,80),U16(b,84),U16(b,86),b.Slice(96,n-100).ToArray());
        Validate(f);return f;
    }
    public static int[] Samples(Frame f)
    {
        Check(f.Kind==2 && f.Encoding is 1 or 2,"not samples");
        int width=f.Encoding==1?3:4;Check(f.Payload.Length%width==0,"sample bytes");
        var a=new int[f.Payload.Length/width];
        for(int i=0;i<a.Length;i++)
        {
            int p=i*width;
            if(width==4) a[i]=BinaryPrimitives.ReadInt32LittleEndian(f.Payload.AsSpan(p));
            else { int v=f.Payload[p]|f.Payload[p+1]<<8|f.Payload[p+2]<<16;a[i]=(v&0x800000)!=0?v|unchecked((int)0xff000000):v; }
        }
        return a;
    }
    public static Frame Normalize(Frame f)
    {
        Validate(f);if(f.Kind!=2)return f;var a=Samples(f);byte[] p=new byte[a.Length*4];
        for(int i=0;i<a.Length;i++)BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(4*i),a[i]);
        return f with {Encoding=2,Payload=p};
    }
}
