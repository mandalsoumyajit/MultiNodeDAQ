namespace Elf.Core;
public sealed class Arguments
{
    private readonly Dictionary<string,string> values=[];
    public Arguments(string[] args){for(int i=0;i<args.Length;i++){if(!args[i].StartsWith("--"))throw new ArgumentException("Expected --option");string key=args[i][2..];values[key]=i+1<args.Length&&!args[i+1].StartsWith("--")?args[++i]:"true";}}
    public string Get(string key,string fallback)=>values.GetValueOrDefault(key,fallback);
    public bool Has(string key)=>values.ContainsKey(key);
    public int Int(string key,int fallback)=>int.Parse(Get(key,fallback.ToString()),System.Globalization.CultureInfo.InvariantCulture);
    public double Double(string key,double fallback)=>double.Parse(Get(key,fallback.ToString(System.Globalization.CultureInfo.InvariantCulture)),System.Globalization.CultureInfo.InvariantCulture);
}
