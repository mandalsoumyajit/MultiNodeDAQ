namespace MultiNodeDAQ.Protocol;
public static class Ipc
{
    public const int MaxLength=2_097_152;
    public static byte[] Encode(ushort kind,ulong request,byte[] payload)
    {
        Validate(kind,payload);byte[] b=new byte[payload.Length+28];"ELI1"u8.CopyTo(b);
        Wire.P16(b,4,1);Wire.P16(b,6,kind);Wire.P32(b,8,(uint)b.Length);Wire.P64(b,12,request);Wire.P32(b,20,(uint)payload.Length);
        payload.CopyTo(b,24);Wire.P32(b,b.Length-4,Wire.Crc(b.AsSpan(0,b.Length-4)));return b;
    }
    static void Validate(ushort kind,byte[] payload)
    {
        Wire.Check(kind is >=1 and <=3 && payload.Length<=MaxLength-28,"ipc kind/length");
        if(kind==2){var f=Wire.Decode(payload);Wire.Check(f.Kind==2 && f.Encoding==2,"ipc array");}
        else Wire.JsonObject(payload);
    }
    public static (ushort Kind,ulong Request,byte[] Payload) Decode(ReadOnlySpan<byte>b)
    {
        Wire.Check(b.Length>=28 && b.Length<=MaxLength,"ipc length");Wire.Check(b[..4].SequenceEqual("ELI1"u8) && Wire.U16(b,4)==1,"ipc magic/version");
        Wire.Check(Wire.U32(b,8)==b.Length && Wire.U32(b,20)==b.Length-28,"ipc sizes");
        Wire.Check(Wire.U32(b,b.Length-4)==Wire.Crc(b[..^4]),"ipc crc");
        var p=b.Slice(24,b.Length-28).ToArray();ushort k=Wire.U16(b,6);Validate(k,p);return(k,Wire.U64(b,12),p);
    }
}
