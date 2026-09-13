using System.IO;
using System.Text.Json;
namespace MultiNodeDAQ.Desktop;

// Display preferences follow permanent unit IDs, never DHCP addresses or boot sessions.
public sealed class UnitAliases(string path)
{
    private Dictionary<string,string> values=new(StringComparer.OrdinalIgnoreCase);
    private static string Key(string unit)
    {
        if(unit.Length!=32 || !unit.All(Uri.IsHexDigit))throw new ArgumentException("Unit ID must contain 32 hexadecimal digits.");
        return unit.ToUpperInvariant();
    }
    private static string Clean(string alias)
    {
        if(alias.Any(char.IsControl))throw new ArgumentException("Alias cannot contain control characters.");
        alias=alias.Trim();
        if(alias.Length>64)throw new ArgumentException("Alias must be 64 characters or fewer.");
        return alias;
    }
    public string Get(string unit)=>values.GetValueOrDefault(Key(unit),"");
    public string Display(string unit,string firmwareLabel)=>Get(unit) is {Length:>0} alias?alias:firmwareLabel;
    public void Load()
    {
        if(!File.Exists(path))return;
        var stored=JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText(path))??throw new JsonException("Alias file must be an object.");
        var next=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(var (unit,alias) in stored)
        {
            if(alias is null)throw new JsonException("Alias must be a string.");
            string value=Clean(alias);if(value.Length>0)next.Add(Key(unit),value);
        }
        values=next;
    }
    public void Set(string unit,string alias)
    {
        string key=Key(unit),value=Clean(alias);
        var next=new Dictionary<string,string>(values,StringComparer.OrdinalIgnoreCase);
        if(value.Length==0)next.Remove(key);else next[key]=value;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{File.WriteAllText(temp,JsonSerializer.Serialize(next,new JsonSerializerOptions{WriteIndented=true}));File.Move(temp,path,true);}
        finally{if(File.Exists(temp))File.Delete(temp);}
        values=next;
    }
}
