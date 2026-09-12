using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MultiNodeDAQ.Acquisition;
using MultiNodeDAQ.Desktop;
using MultiNodeDAQ.Protocol;
using MultiNodeDAQ.Recording;
using MultiNodeDAQ.Simulator;

static class Program
{
    static int checks;
    static void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
    static JsonElement Json(object value)=>JsonSerializer.SerializeToElement(value);
    [STAThread] static void Main()
    {
        string root=Path.GetFullPath(".artifacts/stage4/test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var p=Task.Run(()=>Exercise(root)).GetAwaiter().GetResult();
            var uiReceiver=new Receiver(new(Port:0));uiReceiver.Start();var uiApi=new LocalApi(uiReceiver,new string('c',64),0,()=>uiReceiver.Recording.Snapshot());uiApi.Start();
            using var uiStop=new CancellationTokenSource();var uiFleet=new Fleet(new(){Nodes=2,Seconds=10,Mode="tone"},"127.0.0.1",uiReceiver.Port);var uiRun=Task.Run(()=>uiFleet.RunAsync(uiStop.Token));
            Environment.SetEnvironmentVariable("MULTINODEDAQ_IPC_TOKEN",new string('c',64));Environment.SetEnvironmentVariable("MULTINODEDAQ_IPC_PORT",uiApi.Port.ToString());
            Thread.Sleep(400);var app=new App();app.InitializeComponent();var window=new MainWindow();
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
            var polling=(Task)typeof(MainWindow).GetMethod("Poll",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(window,null)!;
            var pump=new System.Windows.Threading.DispatcherFrame();polling.ContinueWith(_=>window.Dispatcher.BeginInvoke(()=>pump.Continue=false));System.Windows.Threading.Dispatcher.PushFrame(pump);polling.GetAwaiter().GetResult();
            Check(((System.Windows.Controls.TextBlock)window.FindName("ConnectionStatus")).Text.Contains("connected"),"actual GUI status polling");
            Check(((PlotView)window.FindName("Wave")).Data is not null,"actual GUI live waveform");
            SynchronizationContext.SetSynchronizationContext(null);uiStop.Cancel();Task.Run(async()=>{try{await uiRun;}catch(OperationCanceledException){}await uiApi.DisposeAsync();await uiReceiver.DisposeAsync();}).GetAwaiter().GetResult();
            var wave=(PlotView)window.FindName("Wave");wave.Data=MainWindow.Envelope(p);
            if(File.Exists(Path.Combine(root,"status.json")))
            {
                using var statusDoc=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root,"status.json")));
                var list=(System.Windows.Controls.ListBox)window.FindName("Fleet");
                list.ItemsSource=statusDoc.RootElement.GetProperty("acquisition").GetProperty("units").EnumerateArray().Select(n=>new NodeView(n.GetProperty("unit").GetString()!,n.GetProperty("session").GetString()!,n.GetProperty("label").GetString()!+" · synthetic",n.GetProperty("unit").GetString()![..8],"Sampling: sampling\nLink: streaming · Log: recording","Captured diagnostic data · local counters",Brushes.DarkSlateGray,n.Clone())).ToArray();
                ((System.Windows.Controls.TextBlock)window.FindName("RecordingStatus")).Text="RECORDING · Stage 4 automated rendering check";
            }
            string? analysisFile=Directory.GetFiles(".artifacts/stage4","analysis.json",SearchOption.AllDirectories).LastOrDefault();
            if(analysisFile is not null)
            {
                using var doc=JsonDocument.Parse(File.ReadAllBytes(analysisFile));
                typeof(MainWindow).GetField("analysis",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(window,(JsonElement?)doc.RootElement.Clone());
                typeof(MainWindow).GetField("desiredRevision",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(window,1L);
                typeof(MainWindow).GetMethod("RenderAnalysis",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(window,null);
                foreach(int view in Enumerable.Range(0,6)){((System.Windows.Controls.ComboBox)window.FindName("AnalysisView")).SelectedIndex=view;Check(((PlotView)window.FindName("Spectrum")).Data is not null,"analysis view "+view);}
                ((System.Windows.Controls.ComboBox)window.FindName("AnalysisView")).SelectedIndex=0;
            }
            ((System.Windows.Controls.TextBlock)window.FindName("ConnectionStatus")).Text="Stage 4 rendering test · real received samples";
            ((System.Windows.Controls.TextBlock)window.FindName("SelectedTitle")).Text="sim-01 · synthetic";
            ((System.Windows.Controls.TextBlock)window.FindName("WaveHealth")).Text="Real C# envelope · unsynchronized · ADC codes";
            var content=(FrameworkElement)window.Content;window.Content=null;content.Measure(new Size(1180,870));content.Arrange(new Rect(0,0,1180,870));content.UpdateLayout();
            var bitmap=new RenderTargetBitmap(1180,870,96,96,PixelFormats.Pbgra32);bitmap.Render(content);
            using(var file=File.Create(Path.Combine(root,"desktop.png"))){var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));encoder.Save(file);}
            var plot=new PlotView{Data=MainWindow.Envelope(p)};plot.Measure(new Size(800,220));plot.Arrange(new Rect(0,0,800,220));plot.UpdateLayout();
            double[] ms=new double[50];for(int i=0;i<ms.Length;i++){var sw=Stopwatch.StartNew();plot.InvalidateVisual();plot.UpdateLayout();var target=new RenderTargetBitmap(800,220,96,96,PixelFormats.Pbgra32);target.Render(plot);ms[i]=sw.Elapsed.TotalMilliseconds;}
            Check(ms.Max()<200,"waveform drawing within refresh budget");
            var heat=Enumerable.Range(0,32).Select(y=>Enumerable.Range(0,64).Select(x=>(double?)(Math.Sin(x*.1)*Math.Sin(y*.2))).ToArray()).ToArray();
            plot.Data=new("alpha (Hz)","frequency (Hz)",Enumerable.Range(0,64).Select(i=>(double)i).ToArray(),[],[],heat,Enumerable.Range(0,32).Select(i=>(double)i).ToArray());
            for(int i=0;i<ms.Length;i++){var sw=Stopwatch.StartNew();plot.InvalidateVisual();plot.UpdateLayout();var target=new RenderTargetBitmap(800,220,96,96,PixelFormats.Pbgra32);target.Render(plot);ms[i]=sw.Elapsed.TotalMilliseconds;}
            Check(ms.Max()<200,"SCF drawing within refresh budget");
            File.WriteAllText(Path.Combine(root,"report.json"),JsonSerializer.Serialize(new{checks,scf_render_median_ms=ms.Order().ElementAt(25),scf_render_max_ms=ms.Max(),runtime=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription},new JsonSerializerOptions{WriteIndented=true}));
            Console.WriteLine($"PASS: {checks} Stage 4 assertions. Artifacts: {root}");
        }
        catch(Exception e){Console.Error.WriteLine(e);Environment.ExitCode=1;}
    }
    static async Task<JsonElement> Exercise(string root)
    {
        await using var receiver=new Receiver(new(Port:0));receiver.Start();
        string token=new('a',64);await using var api=new LocalApi(receiver,token,0,()=>receiver.Recording.Snapshot());api.Start();var client=new ApiClient(api.Port,token);
        using var cancel=new CancellationTokenSource();var fleet=new Fleet(new(){Nodes=2,Seconds=20,Mode="counter"},"127.0.0.1",receiver.Port);var simulation=fleet.RunAsync(cancel.Token);
        try
        {
            await Task.Delay(800);
            var status=await client.Call("status",new{});var nodes=status.GetProperty("acquisition").GetProperty("units").EnumerateArray().ToArray();Check(nodes.Length==2,"two nodes visible without Python");
            File.WriteAllText(Path.Combine(root,"status.json"),status.GetRawText());var node=nodes[0];string unit=node.GetProperty("unit").GetString()!,session=node.GetProperty("session").GetString()!;
            var preview=await client.Call("preview",new{unit,acquisition_session=session,seconds=.1});Check(preview.GetProperty("available").GetBoolean(),"preview available");
            Check(preview.GetProperty("age_seconds").GetDouble()<.5,"preview received age below 500ms");Check(preview.GetProperty("minimum")[0].GetArrayLength()==400,"bounded envelope");
            Check(status.GetProperty("analysis").GetArrayLength()==0,"Python absence explicit");
            bool rejected=false;try{await new ApiClient(api.Port,new string('b',64)).Call("status",new{});}catch(ContractException){rejected=true;}Check(rejected,"bad token rejected");
            rejected=false;try{await client.Call("node_command",new{unit,acquisition_session=new string('0',32),op="stop",args=new{}});}catch(ContractException){rejected=true;}Check(rejected,"obsolete acquisition session rejected");
            string directory=Path.Combine(root,"recording");await client.Call("start_recording",new{directory});
            rejected=false;try{await client.Call("start_recording",new{directory=Path.Combine(root,"other")});}catch(ContractException){rejected=true;}Check(rejected,"duplicate recording start rejected");
            await Task.Delay(1600);
            // Replacing GUI clients and pausing polling does not interrupt the in-process independent service.
            client=new ApiClient(api.Port,token);var before=Json(receiver.Snapshot()).GetProperty("units")[0].GetProperty("rows").GetUInt64();await Task.Delay(600);
            Check(Json(receiver.Snapshot()).GetProperty("units")[0].GetProperty("rows").GetUInt64()>before,"recording continues without GUI polling");
            var stopped=await client.Call("stop_recording",new{},timeoutSeconds:40);Check(stopped.GetProperty("state").GetString()=="complete","recording clean stop");
            var verification=RecordingReader.Verify(directory);Check(verification.Status=="complete","recording independently verifies");Check(verification.SampleErrors==0,"counter data exact");
            var index=ReplayIndex.Open(directory);Check(index.Units.Count==2,"replay unit selection");var u=index.Units[0];var range=index.Read(u,u.First+100,10000);
            var reference=RecordingReader.ReadRange(directory,u.Unit,u.Session,u.First+100,10000);
            Check(range.Blocks.SelectMany(Wire.Samples).SequenceEqual(reference.Blocks.SelectMany(Wire.Samples)),"indexed replay matches full validated reader");
            Check(range.Blocks.Sum(f=>(long)f.Count)==10000,"seek range exact");
            string export=Path.Combine(root,"range.csv");ReplayIndex.ExportCsv(export,range);Check(File.ReadLines(export).Count()==10001&&File.Exists(export+".json"),"range export and metadata");
            await client.Call("node_command",new{unit,acquisition_session=session,op="arm",args=new{config=node.GetProperty("config")}});
            await Task.Delay(500);Check(Json(receiver.Snapshot()).GetProperty("units")[0].GetProperty("state").GetString()=="sampling","sampling restarts for preview");
            await client.Call("start_recording",new{directory=Path.Combine(root,"second")});await Task.Delay(500);
            await client.Call("stop_recording",new{},timeoutSeconds:40);Check(RecordingReader.Verify(Path.Combine(root,"second")).Status=="complete","second recording with cached metadata verifies");
            // Preview flags, clipping, gaps and finite memory are independently constructed.
            var buffer=new PreviewBuffer();byte[] values=new byte[12*100];for(int i=0;i<300;i++)Wire.P32(values,i*4,(uint)(i%2==0?8388607:1));
            var frame=range.Blocks[0] with{FirstSample=0,Count=100,Payload=values,Flags=1};buffer.Add(frame);buffer.Add(frame with{FirstSample=200});
            var diagnostics=Json(buffer.Snapshot(.1));Check(diagnostics.GetProperty("clipped_values").GetInt64()==300,"clipping detected");Check(diagnostics.GetProperty("missing_rows").GetUInt64()==100,"display gap retained");
            for(int i=0;i<300;i++)buffer.Add(frame with{FirstSample=(ulong)i*100});Check(Json(buffer.Snapshot(.5)).GetProperty("rows").GetInt64()<=16384,"preview retention bounded");
            return preview;
        }
        finally
        {
            await receiver.StopSourcesAsync();cancel.Cancel();try{await simulation;}catch(OperationCanceledException){}
            if(receiver.Recording.Active)await receiver.Recording.StopAsync();
        }
    }
}
