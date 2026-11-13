using System.Windows;
using System.Windows.Controls;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using RadioForge.App.ViewModels;
using RadioForge.Core.Audio;

namespace RadioForge.App.Views;

public partial class StudioView : UserControl
{
    private bool _ready;
    public StudioView() { InitializeComponent(); LayoutPersist.Attach(Split, "studio"); Loaded += (_, _) => Init(); }

    private void Init()
    {
        var vm = (ShellViewModel)DataContext;
        var ins = AudioDevices.Inputs(); var outs = AudioDevices.Outputs();
        ins.Insert(0, new DeviceInfo("", "Windows default input", false)); outs.Insert(0, new DeviceInfo("", "(none)", false));
        _ready = false;
        MicDevice.ItemsSource = ins; MonDevice.ItemsSource = outs;
        MicDevice.SelectedValue = vm.Station.Profile.Audio.Mic.DeviceId; MonDevice.SelectedValue = vm.Station.Profile.Audio.Mic.MonitorDeviceId;
        MonToggle.IsChecked = vm.Station.Profile.Audio.Mic.MonitorEnabled;
        _ready = true;
    }

    private ShellViewModel Vm => (ShellViewModel)DataContext;

    private void OnMicDevice(object s, SelectionChangedEventArgs e)
    {
        if (!_ready || MicDevice.SelectedValue is not string id) return;
        Vm.Station.Profile.Audio.Mic.DeviceId = id; Vm.Station.Engine.MicCapture.DeviceId = id;
        Vm.Station.Engine.RestartCaptures();
    }

    private void OnPttDown(object s, System.Windows.Input.MouseButtonEventArgs e) { Vm.Engine.Mic.PushToTalkDown = true; PttBtn.Content = "● ON AIR — release to stop"; }
    private void OnPttUp(object s, System.Windows.Input.MouseEventArgs e) { Vm.Engine.Mic.PushToTalkDown = false; PttBtn.Content = "HOLD TO TALK"; }

    private void OnMonitor(object s, RoutedEventArgs e)
    {
        var err = Vm.Engine.SetMicMonitor(MonToggle.IsChecked == true);
        if (err != null) { MonToggle.IsChecked = false; TestText.Text = err; } else TestText.Text = MonToggle.IsChecked == true ? "Monitoring on." : "Monitoring off.";
    }

    private async void OnMicTest(object s, RoutedEventArgs e)
    {
        try
        {
            var eng = Vm.Engine;
            if (!eng.MicCapture.IsRunning) { TestText.Text = "No microphone is running. Choose an input device first."; return; }
            var wasMuted = Vm.Station.Profile.Audio.Mic.Muted;
            TestText.Text = "Recording 4 seconds — speak now…";
            var cap = new CaptureSource("MicTest", Vm.Station.Profile.Audio.Mic.DeviceId, false, 44100); cap.Start();
            var pcm = new List<float>(); var buf = new float[8820]; var sw = System.Diagnostics.Stopwatch.StartNew(); float peak = 0;
            await Task.Run(() => { while (sw.ElapsedMilliseconds < 4000) { cap.Read(buf, 0, buf.Length); pcm.AddRange(buf); foreach (var v in buf) peak = Math.Max(peak, Math.Abs(v)); Thread.Sleep(100); } });
            cap.Dispose();
            TestText.Text = peak < 0.005f ? "No signal detected — check the selected device and Windows microphone privacy settings." : $"Playing back… (peak {Db.ToDb(peak):0} dBFS)";
            var bp = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2)) { BufferDuration = TimeSpan.FromSeconds(6) };
            var arr = pcm.ToArray(); var bytes = new byte[arr.Length * 4]; Buffer.BlockCopy(arr, 0, bytes, 0, bytes.Length); bp.AddSamples(bytes, 0, bytes.Length);
            using var outDev = new WaveOutEvent(); outDev.Init(bp); outDev.Play();
            await Task.Delay(4300); TestText.Text = "Test finished.";
        }
        catch (Exception ex) { TestText.Text = "Microphone test failed: " + ex.Message; }
    }
}
