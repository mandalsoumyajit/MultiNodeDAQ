using System.Buffers;
namespace MultiNodeDAQ.Protocol;

/// <summary>One owned pooled receive buffer per connection; never buffers multiple payloads.</summary>
public sealed class FrameStream : IDisposable
{
    private readonly byte[] prefix=new byte[12];
    private byte[]? buffer;
    public int Capacity=>buffer?.Length??12;
    public async ValueTask<Frame?> ReadAsync(Stream stream,CancellationToken token)
    {
        int got=await stream.ReadAsync(prefix.AsMemory(0,12),token);
        if(got==0)return null;
        await stream.ReadExactlyAsync(prefix.AsMemory(got,12-got),token);
        int size=Wire.PeekLength(prefix);
        if(buffer is null || buffer.Length<size)
        {
            if(buffer is not null)ArrayPool<byte>.Shared.Return(buffer);
            buffer=ArrayPool<byte>.Shared.Rent(size);
        }
        prefix.CopyTo(buffer,0);
        await stream.ReadExactlyAsync(buffer.AsMemory(12,size-12),token);
        return Wire.Decode(buffer.AsSpan(0,size));
    }
    public void Dispose(){if(buffer is not null){ArrayPool<byte>.Shared.Return(buffer);buffer=null;}}
}
