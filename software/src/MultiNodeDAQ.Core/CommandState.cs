using System.Text.Json;
using MultiNodeDAQ.Protocol;
namespace MultiNodeDAQ.Core;
public sealed class CommandState
{
    private readonly Dictionary<ulong,(string Payload,object Reply)> cache=[];
    private ulong highest;
    public string State {get;private set;}="idle";
    public uint Rate {get;private set;}=25000;
    public ushort Encoding {get;private set;}=1;
    public uint Config {get;private set;}=1;
    private readonly Dictionary<uint,(uint Rate,ushort Encoding)> configs=[];
    public object Handle(Frame f,ulong next,uint buffered,ulong dropped)
    {
        ulong request=0;
        object Reply(bool ok,string? error,object? details=null)=>new{request_id=request.ToString(),ok,state=State,effective_sample=next.ToString(),error,details=details??new{}};
        try
        {
            var o=Metadata.Read(f);request=Metadata.Counter(o,"request_id");string raw=Convert.ToHexString(f.Payload);
            if(cache.TryGetValue(request,out var prior))return prior.Payload==raw?prior.Reply:Reply(false,"invalid_args");
            // Bounded replay protection: old expired IDs never execute again.
            if(request<=highest)return Reply(false,"invalid_args");
            string op=Metadata.Text(o,"op");var args=Metadata.Field(o,"args");object answer;
            if(op is "status" or "start" or "stop")Wire.Check(!args.EnumerateObject().Any(),"empty args required");
            switch(op)
            {
                case "status":answer=Reply(true,null,new{state=State,next_sample=next.ToString(),buffer_rows=buffered,dropped_rows=dropped.ToString()});break;
                case "arm":
                    if(State!="idle"){answer=Reply(false,"invalid_state");break;}
                    var c=Metadata.Field(args,"config");uint id=Metadata.Number(c,"id"),rate=Metadata.Number(c,"sample_rate_hz"),enc=Metadata.Number(c,"encoding");
                    Wire.Check(id>0 && rate is >=1 and <=1_000_000 && enc is 1 or 2 && Metadata.Bool(c,"synthetic"),"config");
                    Wire.Check(!configs.TryGetValue(id,out var old) || old==(rate,(ushort)enc),"config immutable");Wire.Check(configs.ContainsKey(id)||configs.Count<64,"config limit");
                    Config=id;Rate=rate;Encoding=(ushort)enc;configs[id]=(rate,(ushort)enc);State="armed";answer=Reply(true,null,new{config=new{id, sample_rate_hz=rate,encoding=enc,synthetic=true}});break;
                case "start":if(State!="armed")answer=Reply(false,"invalid_state");else{State="sampling";answer=Reply(true,null);}break;
                case "stop":State="idle";answer=Reply(true,null);break;
                case "recover":
                    ulong first=Metadata.Counter(args,"first_sample"),count=Metadata.Counter(args,"count");Wire.Check(count>0 && first<=ulong.MaxValue-count,"range");answer=Reply(false,"unavailable");break;
                default:answer=Reply(false,"unsupported");break;
            }
            highest=request;cache[request]=(raw,answer);if(cache.Count>256)cache.Remove(cache.Keys.Min());return answer;
        }
        catch(ContractException){return Reply(false,"invalid_args");}
    }
}
