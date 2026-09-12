using MultiNodeDAQ.Protocol;
namespace MultiNodeDAQ.Recording;
public static class LogFormat
{
    public static byte[] Header(byte[] session)
    {
        Wire.Check(session.Length==16 && session.Any(x=>x!=0),"log session");var b=new byte[36];"ELFLOG1\0"u8.CopyTo(b);
        Wire.P16(b,8,1);Wire.P32(b,12,36);session.CopyTo(b,16);Wire.P32(b,32,Wire.Crc(b.AsSpan(0,32)));return b;
    }
    public static byte[] ReadHeader(ReadOnlySpan<byte>b)
    {
        Wire.Check(b.Length==36,"header length");Wire.Check(b[..8].SequenceEqual("ELFLOG1\0"u8) && Wire.U16(b,8)==1 && Wire.U16(b,10)==0 && Wire.U32(b,12)==36,"header fields");
        Wire.Check(Wire.U32(b,32)==Wire.Crc(b[..32]),"header crc");var id=b.Slice(16,16).ToArray();Wire.Check(id.Any(x=>x!=0),"session");return id;
    }
    static void Validate(ushort kind,byte[] p)
    {
        Wire.Check(kind is >=1 and <=4 && p.Length<=2_097_132,"record kind/size");
        if(kind==1){var f=Wire.Decode(p);Wire.Check(f.Kind!=2 || f.Encoding==2,"log samples must be int32");}
        else Wire.JsonObject(p);
    }
    public static byte[] Record(ushort kind,byte[] p)
    {
        Validate(kind,p);byte[] b=new byte[p.Length+20];"ELR1"u8.CopyTo(b);Wire.P16(b,4,1);Wire.P16(b,6,kind);Wire.P32(b,8,(uint)b.Length);Wire.P32(b,12,(uint)p.Length);p.CopyTo(b,16);Wire.P32(b,b.Length-4,Wire.Crc(b.AsSpan(0,b.Length-4)));return b;
    }
    public static (ushort Kind,byte[] Payload) ReadRecord(ReadOnlySpan<byte>b)
    {
        Wire.Check(b.Length>=20 && b.Length<=2_097_152,"record length");Wire.Check(b[..4].SequenceEqual("ELR1"u8) && Wire.U16(b,4)==1,"record magic/version");
        Wire.Check(Wire.U32(b,8)==b.Length && Wire.U32(b,12)==b.Length-20,"record sizes");Wire.Check(Wire.U32(b,b.Length-4)==Wire.Crc(b[..^4]),"record crc");
        ushort k=Wire.U16(b,6);byte[] p=b.Slice(16,b.Length-20).ToArray();Validate(k,p);return(k,p);
    }
}
