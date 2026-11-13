using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using NAudio.Wave;
using RadioForge.App.ViewModels;
using RadioForge.Core;
using RadioForge.Core.Station;

namespace RadioForge.App.Views;

public sealed record RecRow(RecordingInfo R)
{
    public DateTime Started => R.Started; public string Source => R.Source; public string Format => R.Format; public string Path => R.Path;
    public string DurText => TimeSpan.FromSeconds(R.DurationSec).ToString(@"hh\:mm\:ss");
    public string SizeText => R.Size > 1048576 ? $"{R.Size / 1048576.0:0.0} MB" : $"{R.Size / 1024} KB";
}

public partial class RecordingsView : UserControl
{
    private WaveOutEvent? _play; private AudioFileReader? _reader;
    private ShellViewModel Vm => (ShellViewModel)DataContext;
    private readonly System.Windows.Threading.DispatcherTimer _t = new() { Interval = TimeSpan.FromMilliseconds(500) };

    public RecordingsView()
    {
        InitializeComponent();
        SrcBox.ItemsSource = Enum.GetValues<RecordSource>(); FmtBox.ItemsSource = Enum.GetValues<RecordFormat>(); KbpsBox.ItemsSource = new[] { 128, 192, 256, 320 };
        Loaded += (_, _) => { Load(); Vm.Station.Recorder.Completed += OnDone; _t.Start(); }; Unloaded += (_, _) => { _t.Stop(); Vm.Station.Recorder.Completed -= OnDone; };
        _t.Tick += (_, _) => { var s = Vm.Station.Recorder.State; RecBtn.Content = s == RecorderState.Idle ? "⏺ Start recording" : "■ Stop recording"; PauseBtn.Content = s == RecorderState.Paused ? "▶ Resume" : "❚❚ Pause"; PauseBtn.IsEnabled = s != RecorderState.Idle; };
    }

    private void OnDone(RecordingInfo r) => Dispatcher.BeginInvoke(() => { Load(); Msg.Text = "Saved: " + r.Path; });
    private void Load() => Grid.ItemsSource = Vm.Station.Db.GetRecordings().Select(r => new RecRow(r)).ToList();

    private async void OnRec(object s, RoutedEventArgs e)
    {
        var rec = Vm.Station.Recorder;
        if (rec.State == RecorderState.Idle) { var err = rec.Start(); Msg.Text = err ?? "Recording to " + rec.CurrentFile; Vm.Station.SaveProfile(); }
        else await rec.StopAsync();
    }
    private void OnPause(object s, RoutedEventArgs e) => Vm.Station.Recorder.TogglePause();
    private void OnFolder(object s, RoutedEventArgs e)
    {
        var d = new System.Windows.Forms.FolderBrowserDialog { SelectedPath = Vm.Station.Profile.Recording.Folder };
        if (d.ShowDialog() == System.Windows.Forms.DialogResult.OK) Vm.Station.Profile.Recording.Folder = d.SelectedPath;
    }
    private RecRow? Sel => Grid.SelectedItem as RecRow;
    private void OnPlay(object s, RoutedEventArgs e)
    {
        if (Sel == null) return; OnStopPlay(null!, null!);
        if (!File.Exists(Sel.Path)) { Msg.Text = "File no longer exists."; return; }
        try { _reader = new AudioFileReader(Sel.Path); _play = new WaveOutEvent(); _play.Init(_reader); _play.Play(); Msg.Text = "Playing " + Path.GetFileName(Sel.Path); _play.PlaybackStopped += (_, _) => Dispatcher.BeginInvoke(() => OnStopPlay(null!, null!)); }
        catch (Exception ex) { Msg.Text = "Cannot play: " + ex.Message; }
    }
    private void OnStopPlay(object s, RoutedEventArgs e) { try { _play?.Dispose(); _reader?.Dispose(); } catch { } _play = null; _reader = null; }
    private void OnOpenFolder(object s, RoutedEventArgs e)
    {
        var dir = Sel != null ? Path.GetDirectoryName(Sel.Path) : (string.IsNullOrEmpty(Vm.Station.Profile.Recording.Folder) ? AppPaths.DefaultRecordings : Vm.Station.Profile.Recording.Folder);
        if (dir != null && Directory.Exists(dir)) Process.Start("explorer.exe", dir);
    }
    private void OnDelete(object s, RoutedEventArgs e)
    {
        if (Sel == null) return; OnStopPlay(null!, null!);
        if (MessageBox.Show($"Permanently delete this recording and its file?\n\n{Sel.Path}\n\nThis cannot be undone.", "Delete recording", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { if (File.Exists(Sel.Path)) File.Delete(Sel.Path); } catch (Exception ex) { Msg.Text = "Cannot delete file: " + ex.Message; return; }
        Vm.Station.Db.DeleteRecording(Sel.R.Id); Load();
    }
}
