using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Input;
namespace MultiNodeDAQ.Desktop;

public sealed record PlotData(string XLabel,string YLabel,double[] X,double?[][] Low,double?[][] High,double?[][]? Heat=null,double[]? HeatY=null);
public sealed class PlotView : FrameworkElement
{
    private PlotData? data;
    public bool[] VisibleAxes {get;set;}=[true,true,true];
    public PlotData? Data {get=>data;set{data=value;InvalidateVisual();}}
    public PlotView(){MinHeight=100;MouseMove+=Hover;}
    private void Hover(object sender,MouseEventArgs e)
    {
        if(data is null||data.X.Length==0||data.Heat is not null)return;
        int i=Math.Clamp((int)((e.GetPosition(this).X-64)/Math.Max(1,ActualWidth-80)*(data.X.Length-1)),0,data.X.Length-1);
        ToolTip=$"{data.XLabel}: {data.X[i]:G6}\n"+string.Join("\n",Enumerable.Range(0,data.Low.Length).Select(a=>$"{(char)('X'+a)}: {data.Low[a][Math.Min(i,data.Low[a].Length-1)]:G6} … {data.High[a][Math.Min(i,data.High[a].Length-1)]:G6}"));
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);double w=ActualWidth,h=ActualHeight;if(w<100||h<80)return;
        var box=new Rect(64,16,w-82,h-62);var ink=Brushes.SlateGray;
        void Text(string s,double x,double y)=>dc.DrawText(new FormattedText(s,CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),11,ink,VisualTreeHelper.GetDpi(this).PixelsPerDip),new(x,y));
        dc.DrawRectangle(Brushes.White,null,new Rect(0,0,w,h));
        if(data is null||data.X.Length==0){Text("No current data",70,35);return;}
        double xmin=data.X.Min(),xmax=data.X.Max();if(xmax<=xmin)xmax=xmin+1;
        var values=data.Low.Concat(data.High).SelectMany(a=>a).Where(v=>v.HasValue&&double.IsFinite(v.Value)).Select(v=>v!.Value).ToArray();
        double ymin=values.Length>0?values.Min():0,ymax=values.Length>0?values.Max():1;
        if(data.HeatY is {Length:>0}){ymin=data.HeatY.Min();ymax=data.HeatY.Max();}
        else {double pad=Math.Max(1e-12,(ymax-ymin)*.06);ymin-=pad;ymax+=pad;}
        if(ymax<=ymin)ymax=ymin+1;
        double X(double x)=>box.X+(x-xmin)/(xmax-xmin)*box.Width;
        double Y(double y)=>box.Bottom-(y-ymin)/(ymax-ymin)*box.Height;
        for(int i=0;i<3;i++){double x=box.X+box.Width*i/2,y=box.Y+box.Height*i/2;dc.DrawLine(new Pen(Brushes.Gainsboro,.5),new(x,box.Y),new(x,box.Bottom));dc.DrawLine(new Pen(Brushes.Gainsboro,.5),new(box.X,y),new(box.Right,y));Text((xmin+(xmax-xmin)*i/2).ToString("G4",CultureInfo.InvariantCulture),Math.Min(x-10,w-60),box.Bottom+5);Text((ymax-(ymax-ymin)*i/2).ToString("G4",CultureInfo.InvariantCulture),2,y-7);}
        dc.PushClip(new RectangleGeometry(box));
        if(data.Heat is {} heat)
        {
            var finite=heat.SelectMany(a=>a).Where(v=>v.HasValue&&double.IsFinite(v.Value)).Select(v=>v!.Value).ToArray();double max=finite.Length>0?finite.Max():1,min=finite.Length>0?finite.Min():0;
            for(int y=0;y<heat.Length;y++)for(int x=0;x<heat[y].Length;x++)
            {
                var v=heat[y][x];double t=v.HasValue?Math.Clamp((v.Value-min)/Math.Max(1e-30,max-min),0,1):0;
                var brush=v.HasValue?new SolidColorBrush(Color.FromRgb((byte)(20+235*t),(byte)(35+160*Math.Sqrt(t)),(byte)(90+110*(1-t)))):Brushes.LightGray;
                dc.DrawRectangle(brush,null,new Rect(box.X+x*box.Width/heat[y].Length,box.Bottom-(y+1)*box.Height/heat.Length,box.Width/heat[y].Length+.2,box.Height/heat.Length+.2));
            }
            dc.Pop();ToolTip=$"Color magnitude range: {min:G6} to {max:G6}. Gray cells are outside the valid grid.";
        }
        else
        {
            Brush[] colors=[Brushes.RoyalBlue,Brushes.SeaGreen,Brushes.DarkOrange];
            for(int a=0;a<data.Low.Length;a++)
            {
                if(a<3&&!VisibleAxes[a])continue;var pen=new Pen(colors[a%3],1.2);Point? previous=null;
                for(int i=0;i<data.X.Length&&i<data.Low[a].Length;i++)
                {
                    if(data.Low[a][i] is not {} lo||data.High[a][i] is not {} hi||!double.IsFinite(lo)||!double.IsFinite(hi)){previous=null;continue;}
                    var p=new Point(X(data.X[i]),Y(lo));var top=new Point(p.X,Y(hi));dc.DrawLine(pen,p,top);
                    if(lo==hi&&previous is {} old)dc.DrawLine(pen,old,p);previous=lo==hi?p:null;
                }
            }
            dc.Pop();
        }
        Text(data.XLabel,box.X, h-16);Text(data.YLabel,box.X,0);
    }
}
