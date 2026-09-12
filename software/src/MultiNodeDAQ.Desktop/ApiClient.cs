using System.Net.Sockets;
using System.Text.Json;
using MultiNodeDAQ.Protocol;
namespace MultiNodeDAQ.Desktop;

public sealed class ApiClient(int port,string token)
{
    public async Task<JsonElement> Call(string op,object args,CancellationToken cancellation=default,int timeoutSeconds=4)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        using var client=new TcpClient();await client.ConnectAsync("127.0.0.1",port,deadline.Token);client.NoDelay=true;var stream=client.GetStream();
        await Request(stream,1,"authenticate",new{protocol=1,role="gui",token},deadline.Token);
        return await Request(stream,2,op,args,deadline.Token);
    }
    private static async Task<JsonElement> Request(NetworkStream stream,ulong id,string op,object args,CancellationToken token)
    {
        await stream.WriteAsync(Ipc.Encode(1,id,JsonSerializer.SerializeToUtf8Bytes(new{op,request_id=id.ToString(),args})),token);
        byte[] prefix=new byte[12];await stream.ReadExactlyAsync(prefix,token);uint size=Wire.U32(prefix,8);Wire.Check(size is >=28 and <=Ipc.MaxLength,"IPC length");
        byte[] bytes=new byte[size];prefix.CopyTo(bytes,0);await stream.ReadExactlyAsync(bytes.AsMemory(12),token);var reply=Ipc.Decode(bytes);
        using var doc=JsonDocument.Parse(reply.Payload);var root=doc.RootElement;
        Wire.Check(reply.Kind==1&&reply.Request==id&&root.GetProperty("request_id").GetString()==id.ToString(),"IPC response correlation");
        Wire.Check(root.GetProperty("ok").GetBoolean(),root.GetProperty("error").ToString());return root.GetProperty("details").Clone();
    }
}
