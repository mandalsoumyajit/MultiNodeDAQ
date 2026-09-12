using System.Globalization;
using System.Text.Json;
namespace MultiNodeDAQ.Protocol;

/// <summary>Semantic validation is separate from the binary envelope, preserving Stage 0 fixtures.</summary>
public static class Metadata
{
    public static JsonElement Field(JsonElement o,string key)
    { Wire.Check(o.ValueKind==JsonValueKind.Object && o.TryGetProperty(key,out _),"missing "+key);return o.GetProperty(key); }
    public static string Text(JsonElement o,string key)
    { var v=Field(o,key);Wire.Check(v.ValueKind==JsonValueKind.String,"string "+key);return v.GetString()!; }
    public static bool Bool(JsonElement o,string key)
    { var v=Field(o,key);Wire.Check(v.ValueKind is JsonValueKind.True or JsonValueKind.False,"bool "+key);return v.GetBoolean(); }
    public static uint Number(JsonElement o,string key)
    { var v=Field(o,key);Wire.Check(v.TryGetUInt32Safe(out _),"uint32 "+key);return v.GetUInt32(); }
    private static bool TryGetUInt32Safe(this JsonElement v,out uint n){n=0;return v.ValueKind==JsonValueKind.Number && v.TryGetUInt32(out n);}
    public static ulong Counter(JsonElement o,string key)
    { string s=Text(o,key);Wire.Check(s.Length is >=1 and <=20 && s.All(char.IsAsciiDigit) && ulong.TryParse(s,NumberStyles.None,CultureInfo.InvariantCulture,out _),"counter "+key);return ulong.Parse(s,CultureInfo.InvariantCulture); }
    public static void Object(JsonElement o,string key)=>Wire.Check(Field(o,key).ValueKind==JsonValueKind.Object,"object "+key);
    public static string State(JsonElement o){string s=Text(o,"state");Wire.Check(s is "idle" or "armed" or "sampling","state");return s;}
    public static JsonElement Read(Frame f)
    {
        Wire.Validate(f);Wire.Check(f.Kind!=2,"metadata expected");
        using var d=JsonDocument.Parse(f.Payload);var o=d.RootElement;
        switch(f.Kind)
        {
            case 1:
                Text(o,"firmware");Wire.Check(Number(o,"protocol")==1,"protocol");Bool(o,"synthetic");
                var a=Field(o,"axes");Wire.Check(a.ValueKind==JsonValueKind.Array && a.GetArrayLength()==3 && a.EnumerateArray().Select(x=>x.ValueKind==JsonValueKind.String?x.GetString():null).SequenceEqual(new[]{"X","Y","Z"}),"axes");
                var e=Field(o,"encodings");Wire.Check(e.ValueKind==JsonValueKind.Array && e.GetArrayLength() is >=1 and <=2 && e.EnumerateArray().All(x=>x.TryGetUInt32Safe(out var n) && n is 1 or 2),"encodings");
                var c=Field(o,"capabilities");Wire.Check(c.ValueKind==JsonValueKind.Array && c.GetArrayLength()<=32 && c.EnumerateArray().All(x=>x.ValueKind==JsonValueKind.String),"capabilities");
                foreach(string key in new[]{"mode","label"})if(o.TryGetProperty(key,out _))Text(o,key);
                if(o.TryGetProperty("seed",out _))Number(o,"seed");
                if(o.TryGetProperty("preferred_encoding",out _))Wire.Check(Number(o,"preferred_encoding") is 1 or 2,"preferred encoding");
                if(o.TryGetProperty("state",out _))State(o);if(o.TryGetProperty("next_sample",out _))Counter(o,"next_sample");
                break;
            case 3:State(o);Counter(o,"next_sample");Number(o,"buffer_rows");Counter(o,"dropped_rows");break;
            case 4:Wire.Check(Counter(o,"request_id")!=0,"zero request");Text(o,"op");Object(o,"args");break;
            case 5:
                Counter(o,"request_id");bool ok=Bool(o,"ok");State(o);Counter(o,"effective_sample");Object(o,"details");
                var err=Field(o,"error");Wire.Check(ok?err.ValueKind==JsonValueKind.Null:err.ValueKind==JsonValueKind.String,"ack error");break;
            case 6:
                Wire.Check(Number(o,"model_id")==f.Timing && f.Timing!=0,"timing id");string id=Text(o,"reference_epoch");Wire.Check(id.Length==32 && id.All(x=>char.IsAsciiHexDigit(x) && !char.IsUpper(x)),"epoch");
                Counter(o,"anchor_sample");Wire.Check(long.TryParse(Text(o,"anchor_time_ns"),NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out _),"anchor time");
                Wire.Check(Counter(o,"period_num_ns")>0 && Number(o,"period_den")>0,"period");Counter(o,"uncertainty_ns");
                Wire.Check(Counter(o,"valid_from_sample")<Counter(o,"valid_to_sample_exclusive"),"timing interval");break;
            case 7:
                ulong first=Counter(o,"first_sample"),count=Counter(o,"count");Wire.Check(first==f.FirstSample && count>0 && first<=ulong.MaxValue-count,"gap");Text(o,"reason");Bool(o,"recoverable");break;
        }
        return o.Clone();
    }
    public static Frame Json(ushort kind,byte[] unit,byte[] session,ulong sequence,ulong first,object value,uint rate=25000,uint config=1,uint timing=0)
        =>new(kind,0,unit,session,sequence,first,0,rate,config,0,timing,0,0,JsonSerializer.SerializeToUtf8Bytes(value));
}
