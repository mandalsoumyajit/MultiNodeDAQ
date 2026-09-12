using System.Text.Json;
using System.Security.Cryptography;
using Elf.Protocol;
using Elf.Recording;
string fixtureDir=args.Length>0?Path.GetFullPath(args[0]):Path.GetFullPath("fixtures");
int checks=0;
void Assert(bool value,string message){checks++;if(!value)throw new Exception(message);}
void Reject(Action action,string name){try{action();}catch(ContractException){checks++;return;}throw new Exception("Accepted invalid: "+name);}
using(var doc=JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtureDir,"manifest.json"))))
{
    foreach(var c in doc.RootElement.GetProperty("cases").EnumerateArray())
    {
        string name=c.GetProperty("file").GetString()!;byte[] b=File.ReadAllBytes(Path.Combine(fixtureDir,name));
        Assert(Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant()==c.GetProperty("sha256").GetString(),"fixture hash "+name);
        void Read()
        {
            byte[] roundtrip;
            switch(c.GetProperty("format").GetString())
            {
                case "wire":
                    var f=Wire.Decode(b);roundtrip=Wire.Encode(f);
                    if(c.TryGetProperty("samples",out var a))Assert(Wire.Samples(f).SequenceEqual(a.EnumerateArray().Select(x=>x.GetInt32())),"samples "+name);
                    break;
                case "header":roundtrip=LogFormat.Header(LogFormat.ReadHeader(b));break;
                case "record":var rec=LogFormat.ReadRecord(b);roundtrip=LogFormat.Record(rec.Kind,rec.Payload);break;
                case "ipc":var ipc=Ipc.Decode(b);roundtrip=Ipc.Encode(ipc.Kind,ipc.Request,ipc.Payload);break;
                default:throw new Exception("unknown fixture format");
            }
            Assert(roundtrip.SequenceEqual(b),"byte roundtrip "+name);
        }
        if(c.GetProperty("valid").GetBoolean())Read();else Reject(Read,name);
    }
}
foreach(var name in new[]{"log_header.bin","log_record.bin","ipc_block.bin"})
{
    byte[] b=File.ReadAllBytes(Path.Combine(fixtureDir,name));
    for(int n=0;n<b.Length;n++)
    {
        int length=n;
        Reject(()=>{if(name=="log_header.bin")LogFormat.ReadHeader(b.AsSpan(0,length));else if(name=="log_record.bin")LogFormat.ReadRecord(b.AsSpan(0,length));else Ipc.Decode(b.AsSpan(0,length));},name+" truncation "+n);
    }
}
Assert(Wire.Crc("123456789"u8)==0xcbf43926,"CRC known vector");
byte[] packed=File.ReadAllBytes(Path.Combine(fixtureDir,"data_i24.bin"));var sample=Wire.Decode(packed);
Assert(Convert.ToHexString(sample.Unit).ToLowerInvariant()=="00112233445566778899aabbccddeeff","uuid byte order");
Assert(sample.FirstSample==1234567890123,"u64 counter");
Assert(Wire.Encode(Wire.Normalize(sample)).SequenceEqual(File.ReadAllBytes(Path.Combine(fixtureDir,"data_i32.bin"))),"normalize matches golden int32");
for(int n=0;n<packed.Length;n++){int len=n;Reject(()=>Wire.Decode(packed.AsSpan(0,len)),"truncation "+n);}
Assert(Wire.PeekLength(packed.AsSpan(0,12))==packed.Length,"bounded prefix");
Reject(()=>Wire.Encode(sample with{Channels=2}),"encoder channels");
Reject(()=>Wire.Encode(sample with{Count=4097}),"encoder count");
Reject(()=>Wire.Encode(sample with{Rate=0}),"encoder rate");
Reject(()=>Wire.Encode(sample with{Unit=[]}),"encoder identity");
Reject(()=>Wire.Encode(sample with{Flags=16}),"encoder flags");
Reject(()=>Wire.Encode(sample with{FirstSample=ulong.MaxValue-1}),"counter overflow");
_ = Wire.Encode(sample with{FirstSample=ulong.MaxValue-2});
Console.WriteLine($"PASS: {checks} C# contract assertions; independent fixture roundtrips, sample decoding, corruption, CRC, bounds and truncation.");
