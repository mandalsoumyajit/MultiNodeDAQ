using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using MultiNodeDAQ.Acquisition;
using MultiNodeDAQ.Recording;
using MultiNodeDAQ.Protocol;
namespace MultiNodeDAQ.Desktop;

public sealed record NodeView(string Unit,string Session,string Label,string Identity,string States,string Health,Brush Color,JsonElement Raw);
public partial class MainWindow : Window
{
    private ApiClient api=null!;
    private int port=45101;
    private string token="";
    private readonly DispatcherTimer timer=new(){Interval=TimeSpan.FromMilliseconds(200)};
    private readonly CancellationTokenSource stop=new();
    private bool polling,paused,closing,allowClose,replayBusy,playing;
    private JsonElement? status,analysis,preview;
    private long desiredRevision;
    private readonly Queue<(double Time,double[] Rms,double[] Power,double[] Psd)> history=[];
    private string lastResult="",selection="",lastError="";
    private Process? python;
    private ReplayIndex? replay;
    private RangeResult? replayRange;
    private ulong replayFirst;
    private long replayTick;
    private readonly string profile=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MultiNodeDAQ","connection.json");
    public MainWindow()
    {
        InitializeComponent();Title="MultiNodeDAQ "+System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(App).Assembly)?.InformationalVersion;if(Environment.GetCommandLineArgs().Contains("--minimized"))WindowState=WindowState.Minimized;
        try{if(File.Exists(profile)){using var p=JsonDocument.Parse(File.ReadAllBytes(profile));port=p.RootElement.GetProperty("port").GetInt32();token=p.RootElement.GetProperty("token").GetString()??"";}}catch(Exception e){AddEvent("Connection profile: "+e.Message);}
        token=Environment.GetEnvironmentVariable("MULTINODEDAQ_IPC_TOKEN")??token;
        if(int.TryParse(Environment.GetEnvironmentVariable("MULTINODEDAQ_IPC_PORT"),out int configured))port=configured;
        if(token.Length<32)token=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        api=new(port,token);timer.Tick+=async(_,_)=>{if(playing&&Workspace.SelectedIndex==1)await Run(AdvanceReplay);};timer.Tick+=async(_,_)=>await Poll();Loaded+=async(_,_)=>{timer.Start();await Poll();};
    }
    private void SaveProfile(){Directory.CreateDirectory(Path.GetDirectoryName(profile)!);File.WriteAllText(profile,JsonSerializer.Serialize(new{port,token}));}
    private void AddEvent(string message){if(message.Length>2000)message=message[..2000]+"…";Events.Items.Insert(0,DateTime.Now.ToString("HH:mm:ss")+"  "+message);while(Events.Items.Count>100)Events.Items.RemoveAt(Events.Items.Count-1);}
    private static string Text(JsonElement x,string name)=>x.TryGetProperty(name,out var v)?v.ToString():"unavailable";
    private static double Number(JsonElement x,string name,double fallback=0)=>x.TryGetProperty(name,out var v)&&v.TryGetDouble(out var n)?n:fallback;
    private NodeView? Selected=>Fleet.SelectedItem as NodeView;
    private async Task Poll()
    {
        if(polling||closing)return;polling=true;
        try
        {
            var current=await api.Call("status",new{compact=true,analysis_unit=Selected?.Unit??"",analysis_session=Selected?.Session??""},stop.Token);status=current;lastError="";ConnectionStatus.Text=$"Local service · 127.0.0.1:{port} · connected";ConnectionStatus.Foreground=Brushes.SeaGreen;
            var acquisition=current.GetProperty("acquisition");var recording=current.GetProperty("recording");string recordState=recording.ValueKind==JsonValueKind.Null?"preview only":Text(recording,"state");
            RecordingStatus.Text=recordState.ToUpperInvariant()+"\n"+(recording.ValueKind==JsonValueKind.Null?"Choose New recording to begin logging":Text(recording,"directory"));RecordingStatus.Foreground=recordState=="faulted"?Brushes.Firebrick:Brushes.DarkSlateGray;
            var nodes=acquisition.GetProperty("units").EnumerateArray().Select(n=>
            {
                double age=Number(n,"data_age_seconds",double.PositiveInfinity);bool connected=n.GetProperty("connected").GetBoolean();string stream=!connected?"offline":age>.5?"stale / idle":"streaming";
                var workerState=current.GetProperty("analysis_summary").EnumerateArray().FirstOrDefault(a=>Text(a,"unit").Equals(Text(n,"unit"),StringComparison.OrdinalIgnoreCase)&&Text(a,"acquisition_session").Equals(Text(n,"session"),StringComparison.OrdinalIgnoreCase));
                string worker=workerState.ValueKind==JsonValueKind.Undefined?"unavailable":Number(workerState,"age_seconds")>3?"stale":workerState.GetProperty("valid").GetBoolean()?"current":"invalid";
                string health=$"Analysis: {worker} · Age {(double.IsFinite(age)?age.ToString("F2")+" s":"unavailable")} · missing {Text(n,"missing_rows")} · errors {Text(n,"sample_errors")}";
                return new NodeView(Text(n,"unit"),Text(n,"session"),Text(n,"label")+(n.GetProperty("synthetic").GetBoolean()?" · synthetic":""),Text(n,"unit")[^8..]+" · "+Text(n,"session")[..8],$"Sampling: {Text(n,"state")}\nLink: {stream} · Log: {recordState}",health,!connected||age>.5||Number(n,"missing_rows")>0?Brushes.DarkGoldenrod:Brushes.DarkSlateGray,n.Clone());
            }).ToArray();
            string? key=Selected?.Unit+":"+Selected?.Session;Fleet.ItemsSource=nodes;Fleet.SelectedItem=nodes.FirstOrDefault(n=>n.Unit+":"+n.Session==key)??nodes.FirstOrDefault();
            if(recording.ValueKind!=JsonValueKind.Null&&recordState is not ("draining" or "starting"))
            {
                double bytes=acquisition.GetProperty("units").EnumerateArray().Sum(n=>Number(n,"average_payload_bytes_per_second"))*4/3;
                string disk="Free space unavailable";try{var drive=new DriveInfo(Path.GetPathRoot(Text(recording,"directory"))!);disk=$"Free {drive.AvailableFreeSpace/1e9:F1} GB · {(bytes>0?(drive.AvailableFreeSpace/bytes/3600).ToString("F1")+" h estimated":"duration unavailable")}";}catch(IOException){}
                DiskHealth.Text=$"Queue {Number(recording,"queued_bytes")/1048576:F2} / {Number(recording,"queue_limit")/1048576:F0} MiB\nEstimated input {bytes/1e6:F2} MB/s\n{disk}\nFlush interval {Text(recording,"flush_seconds")} s\n"+string.Join("; ",(recording.TryGetProperty("errors",out var errors)?errors.EnumerateArray().Select(x=>x.ToString()):[]));
            }
            foreach(var d in acquisition.GetProperty("diagnostics").EnumerateArray()){string m=d.ToString();if(!Events.Items.Cast<string>().Any(x=>x.EndsWith(m,StringComparison.Ordinal)))AddEvent(m);}
            if(Selected is {} node)
            {
                SelectedTitle.Text=node.Label;
                var settings=await api.Call("analysis_settings",new{},stop.Token);desiredRevision=(long)Number(settings,"revision");
                analysis=current.GetProperty("analysis").EnumerateArray().Where(a=>Text(a.GetProperty("result"),"unit").Equals(node.Unit,StringComparison.OrdinalIgnoreCase)&&Text(a.GetProperty("result"),"acquisition_session").Equals(node.Session,StringComparison.OrdinalIgnoreCase)).Select(a=>(JsonElement?)a.Clone()).FirstOrDefault();
                if(!paused)
                {
                    preview=await api.Call("preview",new{unit=node.Unit,acquisition_session=node.Session,seconds=Span.SelectedIndex==0?.05:Span.SelectedIndex==2?.5:.1},stop.Token);
                    if(Selected?.Unit!=node.Unit||Selected?.Session!=node.Session)return;
                    if(preview.Value.GetProperty("available").GetBoolean())
                    {
                        Wave.Data=Envelope(preview.Value);double age=Number(preview.Value,"age_seconds");
                        WaveHealth.Text=$"{(age>.5?"STALE":"Live")} · received {age:F3} s ago · missing in view {Text(preview.Value,"missing_rows")} · clipped values {Text(preview.Value,"clipped_values")} · flags {Text(preview.Value,"flags")}\n"+"Mean "+string.Join(" / ",Vector(preview.Value,"mean").Select(x=>x.ToString("G4")))+" · RMS "+string.Join(" / ",Vector(preview.Value,"rms").Select(x=>x.ToString("G4")));
                        WaveHealth.Foreground=age>.5||Number(preview.Value,"flags")>0||Number(preview.Value,"clipped_values")>0?Brushes.DarkGoldenrod:Brushes.DarkSlateGray;
                    }
                    else{Wave.Data=null;WaveHealth.Text="No sample data";}
                    RenderAnalysis();
                }
                else WaveHealth.Text="DISPLAY PAUSED · acquisition and recording continue. Resume to show current data.";
            }
            Footer.Text=$"{nodes.Length} acquisition sessions · GUI refresh 5 Hz maximum · "+(paused?"plots paused":"live monitoring");
            
        }
        catch(Exception e)
        {
            if(stop.IsCancellationRequested)return;ConnectionStatus.Text="SERVICE UNAVAILABLE · "+e.Message;ConnectionStatus.Foreground=Brushes.Firebrick;RecordingStatus.Text="Recording state UNKNOWN · last connection lost";WaveHealth.Text="STALE · no current service data";AnalysisHealth.Text="Analysis unavailable · service connection lost";
            Fleet.ItemsSource=Fleet.Items.Cast<NodeView>().Select(n=>n with{States="Sampling / link / recording UNKNOWN",Color=Brushes.DarkGoldenrod}).ToArray();
            if(lastError!=e.Message){AddEvent(e.Message);lastError=e.Message;}
            
        }
        finally{polling=false;}
    }
    public static PlotData Envelope(JsonElement p)
    {
        var lo=p.GetProperty("minimum").EnumerateArray().Select(a=>a.EnumerateArray().Select(v=>v.ValueKind==JsonValueKind.Null?(double?)null:v.GetDouble()).ToArray()).ToArray();
        var hi=p.GetProperty("maximum").EnumerateArray().Select(a=>a.EnumerateArray().Select(v=>v.ValueKind==JsonValueKind.Null?(double?)null:v.GetDouble()).ToArray()).ToArray();
        double duration=(ulong.Parse(Text(p,"end_sample"))-ulong.Parse(Text(p,"first_sample")))/Number(p,"rate")*1000;
        return new("Local time before latest sample (ms)","ADC codes",Enumerable.Range(0,lo[0].Length).Select(i=>-duration+i*duration/lo[0].Length).ToArray(),lo,hi);
    }
    private static double[] Vector(JsonElement o,string name)=>o.GetProperty(name).EnumerateArray().Select(x=>x.GetDouble()).ToArray();
    private void RenderAnalysis()
    {
        if(analysis is not {} a){Spectrum.Data=null;AnalysisHealth.Text="Analysis unavailable · start the optional Python worker";return;}
        var r=a.GetProperty("result");var p=r.GetProperty("parameters");var v=r.GetProperty("values");double age=Number(a,"age_seconds");long revision=(long)Number(p,"revision");bool valid=r.GetProperty("valid").GetBoolean();
        string setting=revision==desiredRevision?$"Applied r{revision}":$"Requested r{desiredRevision} · worker still r{revision}";
        AnalysisHealth.Text=$"{(age>3?"STALE":valid?"Current":"INVALID")} · age {age:F2} s · {setting} · Δf {Text(p,"actual_df")} Hz · Δα {Text(p,"actual_dalpha")} Hz\n"+(valid?$"Reduced diagnostic preview · skipped {Text(r,"skipped_rows")} rows · {Text(p,"units")}":Text(v,"reason"));
        if(a.TryGetProperty("rejection",out var rejection)&&rejection.ValueKind==JsonValueKind.Object)AnalysisHealth.Text+="\nREJECTED r"+Text(rejection,"rejected_revision")+": "+Text(rejection,"reason");
        AnalysisHealth.Foreground=age>3||!valid||revision!=desiredRevision?Brushes.DarkGoldenrod:Brushes.DarkSlateGray;
        if(!valid){Spectrum.Data=null;history.Clear();lastResult="";return;}
        string identity=Text(r,"first_sample")+":"+revision;if(lastResult!=identity)
        {
            if(lastResult.Length>0&&!lastResult.EndsWith(":"+revision))history.Clear();lastResult=identity;history.Enqueue((double.Parse(Text(r,"first_sample"),System.Globalization.CultureInfo.InvariantCulture)/Number(p,"rate"),Vector(v,"rms"),v.TryGetProperty("band_power",out _)?Vector(v,"band_power"):[double.NaN,double.NaN,double.NaN],v.GetProperty("psd").EnumerateArray().Select(x=>x[AnalysisAxis.SelectedIndex].GetDouble()).ToArray()));while(history.Count>120)history.Dequeue();
        }
        int axis=Math.Max(0,AnalysisAxis.SelectedIndex),view=AnalysisView.SelectedIndex;
        if(view==0)
        {
            var heat=v.GetProperty("scf_magnitude").EnumerateArray().Select(row=>row.EnumerateArray().Select(cell=>(double?)cell[axis].GetDouble()).ToArray()).ToArray();
            var mask=v.GetProperty("scf_grid_valid");for(int y=0;y<heat.Length;y++)for(int x=0;x<heat[y].Length;x++)if(!mask[y][x].GetBoolean())heat[y][x]=null;
            Spectrum.Data=new("Cyclic frequency α (Hz)","Spectral frequency (Hz) · code²/Hz",Vector(v,"alpha_hz"),[],[],heat,Vector(v,"scf_frequency_hz"));
        }
        else if(view==1||view==2)
        {
            string name=view==1?"psd":"asd";var lines=Enumerable.Range(0,3).Select(ax=>v.GetProperty(name).EnumerateArray().Select(row=>(double?)row[ax].GetDouble()).ToArray()).ToArray();Spectrum.Data=new("Frequency (Hz)",view==1?"PSD (code²/Hz)":"ASD (code/√Hz)",Vector(v,"frequency_hz"),lines,lines);
        }
        else if(view==4||view==5)
        {
            var lines=Enumerable.Range(0,3).Select(ax=>history.Select(h=>(double?)(view==4?h.Rms[ax]:h.Power[ax])).ToArray()).ToArray();Spectrum.Data=new("Local sample time (s)",view==4?"RMS (ADC codes)":"Band power 0–"+Text(v,"band_max_hz")+" Hz (code²)",history.Select(h=>h.Time).ToArray(),lines,lines);
        }
        else
        {
            var hs=history.ToArray();var frequencies=Vector(v,"frequency_hz");var heat=Enumerable.Range(0,frequencies.Length).Select(y=>hs.Select(h=>h.Psd.Length==frequencies.Length?(double?)(10*Math.Log10(Math.Max(1e-30,h.Psd[y]))):null).ToArray()).ToArray();Spectrum.Data=new("Local sample time (s)","Frequency (Hz) · PSD dB re 1 code²/Hz",hs.Select(h=>h.Time).ToArray(),[],[],heat,frequencies);
        }
    }
    private void UnitChanged(object sender,SelectionChangedEventArgs e)
    {
        if(Selected is not {} n)return;string key=n.Unit+":"+n.Session;if(key==selection)return;selection=key;history.Clear();lastResult="";analysis=null;preview=null;if(Wave is not null)Wave.Data=null;if(Spectrum is not null)Spectrum.Data=null;
    }
    private void AxesChanged(object sender,RoutedEventArgs e){if(Wave is null)return;Wave.VisibleAxes=[XVisible.IsChecked==true,YVisible.IsChecked==true,ZVisible.IsChecked==true];Wave.InvalidateVisual();}
    private void AnalysisViewChanged(object sender,SelectionChangedEventArgs e){if(Spectrum is null||AnalysisAxis is null)return;if(ReferenceEquals(sender,AnalysisAxis)){history.Clear();lastResult="";}if(!paused)RenderAnalysis();}
    private void WorkspaceChanged(object sender,SelectionChangedEventArgs e){if(!ReferenceEquals(e.Source,Workspace))return;if(Workspace.SelectedIndex==0){playing=false;if(PlayButton is not null)PlayButton.Content="Play";}}
    private void PausePlots(object sender,RoutedEventArgs e){paused=!paused;PauseButton.Content=paused?"Resume plots":"Pause plots";}
    private async Task Run(Func<Task> action){try{await action();}catch(Exception e){AddEvent(e.Message);MessageBox.Show(this,e.Message,"MultiNodeDAQ",MessageBoxButton.OK,MessageBoxImage.Warning);}}
    private async void StartRecording(object sender,RoutedEventArgs e)=>await Run(async()=>
    {
        var dialog=new OpenFolderDialog{Title="Choose parent folder for a NEW recording"};if(dialog.ShowDialog(this)!=true)return;
        string path=Path.Combine(dialog.FolderName,"Session-"+DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));await api.Call("start_recording",new{directory=path},stop.Token);AddEvent("Recording started: "+path);
    });
    private async void StopRecording(object sender,RoutedEventArgs e)=>await Run(async()=>
    {
        if(MessageBox.Show(this,"Stop sampling on all units, drain their buffers and finalize this recording? You can restart sampling for preview afterward.","Stop recording",MessageBoxButton.OKCancel)!=MessageBoxResult.OK)return;
        var result=await api.Call("stop_recording",new{},stop.Token,40);AddEvent("Recording finalized: "+result.ToString());
    });
    private async Task Command(string op)
    {
        if(Selected is not {} n)return;
        if(op=="start"&&Text(n.Raw,"state")=="idle")
        {
            var armed=await api.Call("node_command",new{unit=n.Unit,acquisition_session=n.Session,op="arm",args=new{config=n.Raw.GetProperty("config")}},stop.Token,8);
            if(!armed.GetProperty("ok").GetBoolean())throw new InvalidOperationException("Arm rejected: "+Text(armed,"error"));
            if(status?.GetProperty("acquisition").GetProperty("auto_start").GetBoolean()==true){AddEvent(n.Label+": armed; automatic start pending");return;}
        }
        var reply=await api.Call("node_command",new{unit=n.Unit,acquisition_session=n.Session,op,args=new{}},stop.Token,8);
        if(!reply.GetProperty("ok").GetBoolean())throw new InvalidOperationException("Node rejected command: "+Text(reply,"error"));AddEvent(n.Label+": "+op+" acknowledged at sample "+Text(reply,"effective_sample"));
    }
    private async void StartSampling(object sender,RoutedEventArgs e)=>await Run(()=>Command("start"));
    private async void StopSampling(object sender,RoutedEventArgs e)=>await Run(()=>Command("stop"));
    private void UnitDetails(object sender,RoutedEventArgs e){if(Selected is {} n)MessageBox.Show(this,JsonSerializer.Serialize(n.Raw,new JsonSerializerOptions{WriteIndented=true})+"\nCalibration, battery and RSSI: unavailable.",n.Label);}
    private void Connection(object sender,RoutedEventArgs e)
    {
        var dialog=new FieldsDialog("Local service connection",[("Port",port.ToString()),("Token",token)]);dialog.Owner=this;if(dialog.ShowDialog()!=true)return;
        if(!int.TryParse(dialog.Values[0],out int next)||next is <1 or >65535||dialog.Values[1].Length is <32 or >256){MessageBox.Show(this,"Port must be 1–65535; token must be 32–256 characters.");return;}
        port=next;token=dialog.Values[1];api=new(port,token);SaveProfile();AddEvent("Connection updated; token stored in current user's local application data.");
    }
    private async void AnalysisSettings(object sender,RoutedEventArgs e)=>await Run(async()=>
    {
        var requested=await api.Call("analysis_settings",new{},stop.Token);var s=requested.GetProperty("settings");
        string[] names=["rate","df","dalpha","max_frequency","max_alpha","hop_fraction","pair_batch"];string[] defaults=["25000","10","5","5000","1000","0.25","256"];
        var fields=new FieldsDialog("FAM settings · all workers",names.Select((name,i)=>(new[]{"Sample rate (Hz)","Frequency resolution Δf (Hz)","Cyclic resolution Δα (Hz)","Maximum frequency (Hz)","Maximum cyclic frequency (Hz)","Hop fraction (0.25 = 75% overlap)","Pair batch (integer)"}[i],s.ValueKind==JsonValueKind.Null?defaults[i]:Text(s,name))).ToArray(),"Paper preset: 25 kHz, Δf 10 Hz, Δα 5 Hz, hop 0.25 (75% overlap). Changes reset partial analysis windows. Recording is unaffected. Worker validation and applied revision are shown in the analysis status.");fields.Owner=this;if(fields.ShowDialog()!=true)return;
        var values=new Dictionary<string,object>();for(int i=0;i<names.Length;i++){double n=double.Parse(fields.Values[i],System.Globalization.CultureInfo.InvariantCulture);if(!double.IsFinite(n)||n<=0)throw new ArgumentException("Settings must be positive finite numbers.");if(i==6&&n!=Math.Truncate(n))throw new ArgumentException("Pair batch must be an integer.");values[names[i]]=i==6?(object)checked((int)n):n;}
        var result=await api.Call("configure_analysis",new{settings=values},stop.Token);desiredRevision=(long)Number(result,"revision");AddEvent("Analysis settings requested r"+desiredRevision+"; waiting for worker acknowledgment.");
    });
    private static string FindHost()
    {
        string packaged=Path.Combine(AppContext.BaseDirectory,"host","MultiNodeDAQ.Host.exe");if(File.Exists(packaged))return packaged;
        var d=new DirectoryInfo(AppContext.BaseDirectory);while(d is not null){string path=Path.Combine(d.FullName,"src","MultiNodeDAQ.Host","bin","Release","net10.0","MultiNodeDAQ.Host.exe");if(File.Exists(path))return path;d=d.Parent;}
        throw new FileNotFoundException("Host executable not found. Install the complete desktop package or build the solution in Release mode.");
    }
    private async void StartService(object sender,RoutedEventArgs e)=>await Run(async()=>
    {
        try{await api.Call("status",new{},stop.Token,1);AddEvent("Local service already running.");return;}catch(Exception ex) when(ex is IOException or System.Net.Sockets.SocketException or OperationCanceledException){}
        var info=new ProcessStartInfo(FindHost()){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=Path.GetDirectoryName(profile)!};Directory.CreateDirectory(info.WorkingDirectory);info.ArgumentList.Add("--ipc-port");info.ArgumentList.Add(port.ToString());info.Environment["MULTINODEDAQ_IPC_TOKEN"]=token;
        // A private .NET SDK is only needed in a development checkout, not a self-contained release.
        string? root=FindSoftware();if(root is not null)info.Environment["DOTNET_ROOT"]=Path.Combine(root,".tools","dotnet");
        Process.Start(info);SaveProfile();AddEvent("Independent local service launched on sensor port 45100 (loopback). Use a configured host for field networking.");
    });
    private static string? FindSoftware(){var d=new DirectoryInfo(AppContext.BaseDirectory);while(d is not null){if(File.Exists(Path.Combine(d.FullName,"MultiNodeDAQ.slnx")))return d.FullName;d=d.Parent;}return null;}
    private async void StartPython(object sender,RoutedEventArgs e)=>await Run(async()=>
    {
        if(Selected is not {} n)throw new InvalidOperationException("Select a connected unit first.");
        var choose=new OpenFileDialog{Title="Select Python from the installed optional analysis environment",Filter="Python executable|python.exe"};string? root=FindSoftware();if(root is not null)choose.InitialDirectory=Path.Combine(root,".venv","Scripts");if(choose.ShowDialog(this)!=true)return;
        if(python is {HasExited:false}){python.Kill();await python.WaitForExitAsync();}python?.Dispose();
        var info=new ProcessStartInfo(choose.FileName){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true};info.Environment["MULTINODEDAQ_IPC_TOKEN"]=token;foreach(string arg in new[]{"-m","multinodedaq.worker","--port",port.ToString(),"--units",n.Unit})info.ArgumentList.Add(arg);
        python=Process.Start(info)??throw new IOException("Python did not start");python.ErrorDataReceived+=(_,a)=>{if(a.Data is {} text)Dispatcher.InvokeAsync(()=>AddEvent("Python: "+text));};python.OutputDataReceived+=(_,a)=>{if(a.Data is {} text)Dispatcher.InvokeAsync(()=>AddEvent("Python: "+text));};python.BeginErrorReadLine();python.BeginOutputReadLine();AddEvent("Python launched for "+n.Label+"; waiting for valid result.");
    });
    private void SaveSummary(object sender,RoutedEventArgs e)
    {
        var save=new SaveFileDialog{Filter="JSON report|*.json",FileName="MultiNodeDAQ-summary.json"};if(save.ShowDialog(this)!=true)return;
        try{File.WriteAllText(save.FileName,JsonSerializer.Serialize(new{captured=DateTimeOffset.UtcNow,status,events=Events.Items.Cast<string>().ToArray(),build=typeof(App).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute),false).Cast<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion},new JsonSerializerOptions{WriteIndented=true}));}catch(Exception ex){MessageBox.Show(this,ex.Message);}
    }
    private async void OpenReplay(object sender,RoutedEventArgs e)=>await Run(async()=>
    {
        if(replayBusy)return;var choose=new OpenFolderDialog{Title="Open complete recording"};if(choose.ShowDialog(this)!=true)return;playing=false;PlayButton.Content="Play";replayBusy=true;ReplayHealth.Text="Verifying and building sparse index…";
        try{replay=await Task.Run(()=>ReplayIndex.Open(choose.FolderName,stop.Token));ReplayUnits.ItemsSource=replay.Units;ReplayUnits.SelectedIndex=0;ReplayEvents.Text=string.Join("\n",replay.Events);ReplayHealth.Text=$"REPLAY · {replay.Verification.Rows:N0} verified rows · {replay.Units.Count} streams · {choose.FolderName}";}
        finally{replayBusy=false;}await DrawReplay();
    });
    private async void ReplayUnitChanged(object sender,SelectionChangedEventArgs e){if(ReplayUnits.SelectedItem is ReplayUnit u){replayFirst=u.First;Seek.Value=0;if(!replayBusy)await Run(DrawReplay);}}
    private async void SeekChanged(object sender,RoutedPropertyChangedEventArgs<double> e){if(ReplayUnits?.SelectedItem is ReplayUnit u&&!replayBusy){replayFirst=u.First+(ulong)((u.End-u.First)*Seek.Value/1000);await Run(DrawReplay);}}
    private void PlayReplay(object sender,RoutedEventArgs e){playing=!playing;PlayButton.Content=playing?"Pause":"Play";replayTick=Stopwatch.GetTimestamp();}
    private async Task AdvanceReplay()
    {
        if(replayBusy||ReplayUnits.SelectedItem is not ReplayUnit u)return;double elapsed=Stopwatch.GetElapsedTime(replayTick).TotalSeconds;replayTick=Stopwatch.GetTimestamp();double speed=Speed.SelectedIndex switch{0=>.25,2=>2,3=>4,_=>1};
        replayFirst=Math.Min(u.End,replayFirst+(ulong)(elapsed*u.Rate*speed));if(replayFirst>=u.End){playing=false;PlayButton.Content="Play";}await DrawReplay();
    }
    private async Task DrawReplay()
    {
        if(replayBusy||replay is null||ReplayUnits.SelectedItem is not ReplayUnit u)return;replayBusy=true;
        try
        {
            uint count=(uint)Math.Min((ulong)Math.Min(12500,u.Rate/2),u.End-replayFirst);if(count==0){ReplayPlot.Data=null;return;}
            replayRange=await Task.Run(()=>replay.Read(u,replayFirst,count));var buffer=new PreviewBuffer();foreach(var f in replayRange.Blocks)buffer.Add(f);var p=JsonSerializer.SerializeToElement(buffer.Snapshot(.5));ReplayPlot.Data=p.GetProperty("available").GetBoolean()?Envelope(p):null;
            ReplayPosition.Text=$"Sample {replayFirst} · {count} requested rows · {replayRange.Missing.Length} gap intervals · original flags/configuration/timing retained";Seek.Value=(replayFirst-u.First)*1000.0/Math.Max(1,u.End-u.First);
        }
        finally{replayBusy=false;}
    }
    private async void ExportReplay(object sender,RoutedEventArgs e)=>await Run(async()=>
    {
        if(replayRange is not {} range)throw new InvalidOperationException("Open a replay range first.");var save=new SaveFileDialog{Filter="CSV with metadata sidecar|*.csv",FileName="samples.csv"};if(save.ShowDialog(this)!=true)return;await Task.Run(()=>ReplayIndex.ExportCsv(save.FileName,range));AddEvent("Exported CSV + JSON metadata: "+save.FileName);
    });
    private async void OnClosing(object? sender,CancelEventArgs e)
    {
        if(allowClose)return;e.Cancel=true;if(closing)return;
        var choice=MessageBox.Show(this,"Keep the receiver and recorder running after closing this window?\n\nYes: continue in background\nNo: stop sources, finalize recording and shut down service\nCancel: return to application","Close MultiNodeDAQ",MessageBoxButton.YesNoCancel,MessageBoxImage.Question);
        if(choice==MessageBoxResult.Cancel)return;closing=true;
        try{if(choice==MessageBoxResult.No)await api.Call("shutdown",new{},stop.Token,5);SaveProfile();timer.Stop();stop.Cancel();allowClose=true;Close();}
        catch(Exception ex){closing=false;MessageBox.Show(this,"Shutdown could not be confirmed: "+ex.Message+"\nChoose background continuation to close without controlling the service.");}
    }
}

public sealed class FieldsDialog : Window
{
    private readonly TextBox[] inputs;
    public string[] Values=>inputs.Select(x=>x.Text.Trim()).ToArray();
    public FieldsDialog(string title,(string Label,string Value)[] fields,string? note=null)
    {
        Title=title;Width=520;SizeToContent=SizeToContent.Height;WindowStartupLocation=WindowStartupLocation.CenterOwner;ResizeMode=ResizeMode.NoResize;var panel=new StackPanel{Margin=new Thickness(18)};Content=panel;
        if(note is not null)panel.Children.Add(new TextBlock{Text=note,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(3,3,3,10)});
        inputs=fields.Select(f=>{panel.Children.Add(new TextBlock{Text=f.Label});var box=new TextBox{Text=f.Value};panel.Children.Add(box);return box;}).ToArray();
        if(title.StartsWith("FAM"))
        {
            var presets=new StackPanel{Orientation=Orientation.Horizontal};foreach(var preset in new[]{("Paper defaults",new[]{"25000","10","5","5000","1000","0.25","256"}),("Quick preview",new[]{"25000","50","10","2000","500","0.25","256"})}){var b=new Button{Content=preset.Item1};b.Click+=(_,_)=>{for(int i=0;i<inputs.Length;i++)inputs[i].Text=preset.Item2[i];};presets.Children.Add(b);}panel.Children.Insert(1,presets);
        }
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};var cancel=new Button{Content="Cancel",IsCancel=true};var apply=new Button{Content="Apply",IsDefault=true};apply.Click+=(_,_)=>DialogResult=true;buttons.Children.Add(cancel);buttons.Children.Add(apply);panel.Children.Add(buttons);
    }
}
