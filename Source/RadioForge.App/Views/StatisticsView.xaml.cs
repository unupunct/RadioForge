using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using RadioForge.App.ViewModels;
using RadioForge.Core;
using RadioForge.Core.Streaming;

namespace RadioForge.App.Views;

public sealed record StatRow(string Name, string Status, string Bitrate, string Format, string Uptime, int Reconnects, string Listeners, string Availability, string LastSuccess, string LastError);

public partial class StatisticsView : UserControl
{
    private readonly DispatcherTimer _t = new() { Interval = TimeSpan.FromSeconds(2) };
    private static readonly Color[] Cols = { Color.FromRgb(0, 229, 160), Color.FromRgb(0, 184, 255), Color.FromRgb(255, 61, 203), Color.FromRgb(255, 210, 63), Color.FromRgb(255, 120, 60), Color.FromRgb(160, 120, 255) };
    private ShellViewModel Vm => (ShellViewModel)DataContext;
    private readonly (string label, TimeSpan span)[] _windows = { ("Last 2 min", TimeSpan.FromMinutes(2)), ("Last 10 min", TimeSpan.FromMinutes(10)), ("Last hour", TimeSpan.FromHours(1)), ("Last 6 hours", TimeSpan.FromHours(6)) };

    public StatisticsView() { InitializeComponent(); WindowBox.ItemsSource = _windows.Select(w => w.label).ToList(); WindowBox.SelectedIndex = 0; Loaded += (_, _) => { _t.Start(); Refresh(); }; Unloaded += (_, _) => _t.Stop(); _t.Tick += (_, _) => Refresh(); }
    private TimeSpan Span => _windows[Math.Max(0, WindowBox.SelectedIndex)].span;
    private void OnWindow(object s, SelectionChangedEventArgs e) { if (IsLoaded) Refresh(); }

    private void Refresh()
    {
        var st = Vm.Station; var dests = st.Streams.Destinations.ToList();
        Table.ItemsSource = dests.Select(d => new StatRow(d.Profile.Name, d.State.ToString(), $"{d.BitrateKbps:0} kbps", $"{d.Profile.Codec} {d.Profile.BitrateKbps}k {d.Profile.SampleRate / 1000.0:0.#}kHz {(d.Profile.Channels == 1 ? "mono" : "stereo")}",
            d.ConnectedSince.HasValue ? d.Uptime.ToString(@"hh\:mm\:ss") : "—", d.ReconnectCount, d.Listeners?.ToString() ?? "n/a", $"{st.Stats.Availability(d.Profile.Id, Span):P0}", d.LastSuccess?.ToString("T") ?? "never", d.LastError)).ToList();
        void Fill(Controls.LineChart ch, Func<StatSample, double?> f, double? fixedMax = null)
        {
            ch.Window = Span; ch.FixedMax = fixedMax; ch.Series = dests.Select((d, i) => (d.Profile.Name, Cols[i % Cols.Length], st.Stats.Snapshot(d.Profile.Id, Span).Select(s => (s.Time, v: f(s))).Where(p => p.v.HasValue).Select(p => (p.Time, p.v!.Value)).ToList())).ToList(); ch.Refresh();
        }
        Fill(ChBitrate, s => s.BitrateKbps); Fill(ChListeners, s => s.Listeners < 0 ? null : s.Listeners);
        Fill(ChAvail, s => s.Status == nameof(StreamState.Streaming) ? 100 : 0, 100);
        Fill(ChStab, s => s.Status == nameof(StreamState.Streaming) ? 100 : s.Status is nameof(StreamState.Reconnecting) or nameof(StreamState.Connecting) or nameof(StreamState.Connected) ? 50 : 0, 100);
        UptimeText.Text = st.BroadcastStart.HasValue ? $"On air since {st.BroadcastStart:T}  —  uptime {st.Uptime:hh\\:mm\\:ss}" : "Not broadcasting.";
        Events.ItemsSource = Log.Snapshot().Where(l => l.Category.StartsWith("Stream") || l.Category is "Station" or "Notify" or "Scheduler" or "Recorder" or "Audio").TakeLast(150).Reverse().Select(l => l.ToString()).ToList();
    }

    private void OnExport(object s, RoutedEventArgs e)
    {
        var d = new Microsoft.Win32.SaveFileDialog { Filter = "CSV|*.csv", FileName = $"radioforge-stats-{DateTime.Now:yyyyMMdd-HHmm}.csv" };
        if (d.ShowDialog() == true) { try { Vm.Station.Stats.ExportCsv(d.FileName, DateTime.Now - Span); } catch (Exception ex) { MessageBox.Show("Export failed: " + ex.Message); } }
    }
}
