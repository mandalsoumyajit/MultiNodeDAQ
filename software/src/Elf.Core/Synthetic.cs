using Elf.Protocol;
namespace Elf.Core;
public static class Synthetic
{
    public static readonly string[] Modes=["counter","noise","tone","burst","sweep","clipping"];
    public static int Value(ulong row,int axis,string mode,uint seed,uint rate)
    {
        uint h=unchecked((uint)row ^ (uint)(row>>32)*0x9e3779b9u ^ seed ^ (uint)axis*0x85ebca6bu);
        h^=h>>16;h*=0x7feb352du;h^=h>>15;h*=0x846ca68bu;h^=h>>16;
        if(mode=="counter")return (int)((row*3+(uint)axis+seed)&0xffffff)-8388608;
        if(mode=="noise")return (int)(h&0xffffff)-8388608;
        double t=(double)row/rate, frequency=1000+axis*250;
        double amplitude=mode=="clipping"?12_000_000:2_000_000;
        if(mode=="burst" && t%2>=0.25)amplitude=0;
        double phase=mode=="sweep"?2*Math.PI*(600*t+220*t*t):2*Math.PI*frequency*t;
        return (int)Math.Clamp(Math.Round(amplitude*Math.Sin(phase)),-8388608,8388607);
    }
    public static Frame Data(byte[] unit,byte[] session,ulong seq,ulong first,uint count,uint rate,ushort encoding,string mode,uint seed,uint config=1)
    {
        Wire.Check(Modes.Contains(mode),"mode");int width=encoding==1?3:4;byte[] p=new byte[count*3*width];bool clipped=false;
        for(uint row=0;row<count;row++)for(int axis=0;axis<3;axis++)
        {
            int v=Value(first+row,axis,mode,seed,rate),offset=((int)row*3+axis)*width;
            clipped|=v is -8388608 or 8388607;
            for(int b=0;b<width;b++)p[offset+b]=(byte)(v>>(8*b));
        }
        return new(2,clipped && mode=="clipping"?8u:0u,unit,session,seq,first,count,rate,config,0,0,3,encoding,p);
    }
}
