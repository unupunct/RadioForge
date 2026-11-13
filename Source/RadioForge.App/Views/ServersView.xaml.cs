using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using RadioForge.App.ViewModels;
using RadioForge.Core;
using RadioForge.Core.Streaming;

namespace RadioForge.App.Views;

public sealed class StepIcon : IValueConverter
{
    public static readonly StepIcon Instance = new();
    public object Convert(object v, Type t, object p, CultureInfo c) => v switch { StepStatus.Pass => "✔", StepStatus.Fail => "✖", StepStatus.Warn => "⚠", StepStatus.Skipped => "–", _ => "ℹ" };
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public partial class ServersView : UserControl
{
    private readonly DispatcherTimer _t = new() { Interval = TimeSpan.FromSeconds(1) };
    private ShellViewModel Vm => (ShellViewModel)DataContext;
    private StreamDestination? Cur => List.SelectedItem as StreamDestination;
    private bool _loadingPw;

    public ServersView()
    {
        InitializeComponent(); LayoutPersist.Attach(Split, "servers");
        Editor.DataContext = EncoderPanel.DataContext = new ServerProfile(); // neutral placeholder until a server is selected (no binding errors)
        TypeBox.ItemsSource = Enum.GetValues<ServerType>(); CodecBox.ItemsSource = Enum.GetValues<AudioCodec>();
        BitrateBox.ItemsSource = new[] { 32, 48, 64, 96, 128, 160, 192, 256, 320 }; RateBox.ItemsSource = new[] { 22050, 32000, 44100, 48000 }; ChBox.ItemsSource = new[] { 1, 2 };
        Loaded += (_, _) => { _t.Start(); if (List.SelectedItem == null && List.Items.Count > 0) List.SelectedIndex = 0; };
        Unloaded += (_, _) => _t.Stop();
        _t.Tick += (_, _) => RefreshLive();
        CodecBox.SelectionChanged += (_, _) => UpdateNote();
    }

    private void OnSelect(object s, SelectionChangedEventArgs e)
    {
        var d = Cur; Detail.Visibility = d == null ? Visibility.Collapsed : Visibility.Visible; if (d == null) return;
        Editor.DataContext = d.Profile; EncoderPanel.DataContext = d.Profile;
        _loadingPw = true; Pw.Password = d.Profile.Password; _loadingPw = false;
        TestSteps.ItemsSource = null; UpdateNote(); RefreshLive(); DetailScroll.ScrollToTop();
    }

    private void UpdateNote()
    {
        var p = Cur?.Profile; if (p == null) return;
        var ff = Core.Streaming.Ffmpeg.Locate(Vm.Settings.FfmpegPath);
        CodecNote.Text = p.Codec == AudioCodec.Mp3 ? "MP3 uses the built-in LAME encoder. Actual bitrate/format compatibility depends on your streaming host."
            : ff == null ? $"⚠ {p.Codec} needs FFmpeg, which was not found. Download it in Application Settings → Advanced (one click) or set its path." : $"{p.Codec} is encoded with FFmpeg ({ff}). Compatibility depends on your streaming host.";
    }

    private void RefreshLive()
    {
        var d = Cur; if (d == null) return;
        UptimeTxt.Text = d.ConnectedSince.HasValue ? $"Uptime: {d.Uptime:hh\\:mm\\:ss}" : "Uptime: —";
        var logs = Log.Snapshot(d.Category).TakeLast(60).Reverse().Select(l => l.ToString()).ToList(); // newest first (no auto-scroll)
        if (LogBox.Items.Count != logs.Count) LogBox.ItemsSource = logs;
    }

    private void OnPw(object s, RoutedEventArgs e) { if (!_loadingPw && Cur != null) { Cur.Profile.Password = Pw.Password; Vm.Station.SaveProfile(); } }

    private void Add(ServerProfile p)
    {
        p.Name = $"{p.Type} {Vm.Station.Streams.Destinations.Count + 1}"; p.StreamName = Vm.Station.Profile.Name; p.Genre = Vm.Station.Profile.Genre; p.Website = Vm.Station.Profile.Website; p.Description = Vm.Station.Profile.Description;
        Vm.Station.Profile.Servers.Add(p); var d = Vm.Station.AddDestination(p); Vm.Station.SaveProfile(); List.SelectedItem = d;
    }
    private void OnAddIce(object s, RoutedEventArgs e) => Add(new ServerProfile { Type = ServerType.Icecast2, Port = 8000, Mount = "/stream", Username = "source" });
    private void OnAddSc1(object s, RoutedEventArgs e) => Add(new ServerProfile { Type = ServerType.ShoutcastV1, Port = 8000 });
    private void OnAddSc2(object s, RoutedEventArgs e) => Add(new ServerProfile { Type = ServerType.ShoutcastV2, Port = 8000, StreamId = 1 });
    private void OnDup(object s, RoutedEventArgs e)
    {
        if (Cur == null) return; var c = Cur.Profile.Clone(); c.Id = Guid.NewGuid().ToString("N"); c.Name += " copy";
        Vm.Station.Profile.Servers.Add(c); var d = Vm.Station.AddDestination(c); Vm.Station.SaveProfile(); List.SelectedItem = d;
    }
    private async void OnDelete(object s, RoutedEventArgs e)
    {
        if (Cur == null) return;
        if (MessageBox.Show($"Delete server \"{Cur.Profile.Name}\"?", "Delete server", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        await Vm.Station.RemoveDestinationAsync(Cur);
    }
    private void OnStart(object s, RoutedEventArgs e) { if (Cur != null) { Vm.Station.BroadcastStart ??= DateTime.Now; Vm.Station.Streams.Start(Cur); } }
    private async void OnStop(object s, RoutedEventArgs e) { if (Cur != null) await Cur.StopAsync(); }
    private void OnStartAll(object s, RoutedEventArgs e) { var p = Vm.Station.StartBroadcast(); if (p.Count > 0) Vm.DismissToastCommand.Execute(null); }
    private async void OnStopAll(object s, RoutedEventArgs e) => await Vm.Station.StopBroadcastAsync();

    private async void OnTest(object s, RoutedEventArgs e)
    {
        if (Cur == null) return; var btn = (Button)s; btn.IsEnabled = false; TestNote.Text = "Testing…";
        try
        {
            Vm.Station.SaveProfile();
            var r = await ServerTester.TestAsync(Cur.Profile, Vm.Station.FfmpegPath);
            TestSteps.ItemsSource = r.Steps; TestNote.Text = r.Summary;
            Core.Log.Info(Cur.Category, "Connection test: " + r.Summary);
        }
        catch (Exception ex) { TestNote.Text = "Test failed: " + ex.Message; }
        finally { btn.IsEnabled = true; }
    }
}
