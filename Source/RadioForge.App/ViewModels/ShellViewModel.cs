using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RadioForge.Core;
using RadioForge.Core.Audio;
using RadioForge.Core.Station;
using RadioForge.Core.Streaming;

namespace RadioForge.App.ViewModels;

public enum PageId { Dashboard, Studio, Playlists, Soundboard, Servers, Scheduler, Library, Recordings, AudioSettings, Statistics, Logs, Settings }
public sealed record NavItem(PageId Page, string Icon, string Title);

public sealed partial class ShellViewModel : ObservableObject
{
    public StationController Station { get; private set; }
    public AppSettings Settings => Station.Settings;
    public AudioEngine Engine => Station.Engine;
    public static IReadOnlyList<NavItem> NavItems { get; } = new NavItem[]
    {
        new(PageId.Dashboard, "⌂", "Dashboard"), new(PageId.Studio, "🎚", "Studio"), new(PageId.Playlists, "♫", "Playlist Manager"),
        new(PageId.Soundboard, "▦", "Soundboard"), new(PageId.Servers, "📡", "Streaming Servers"), new(PageId.Scheduler, "📅", "Broadcast Scheduler"),
        new(PageId.Library, "📚", "Audio Library"), new(PageId.Recordings, "⏺", "Recordings"), new(PageId.AudioSettings, "🎧", "Audio Settings"),
        new(PageId.Statistics, "📈", "Statistics"), new(PageId.Logs, "📜", "Logs"), new(PageId.Settings, "⚙", "Application Settings"),
    };

    [ObservableProperty] private NavItem _selectedNav = NavItems[0];
    [ObservableProperty] private string _clock = "";
    [ObservableProperty] private string _stationName = "";
    [ObservableProperty] private bool _isOnAir;
    [ObservableProperty] private string _onAirText = "OFF AIR";
    [ObservableProperty] private string _listenersText = "Listeners: n/a";
    [ObservableProperty] private string _nowPlayingText = "—";
    [ObservableProperty] private string _artistText = "";
    [ObservableProperty] private string _nextText = "—";
    [ObservableProperty] private string _programText = "";
    [ObservableProperty] private string _scheduleText = "";
    [ObservableProperty] private string _micText = "MIC MUTED";
    [ObservableProperty] private bool _micLive;
    [ObservableProperty] private string _cpuText = "CPU 0%";
    [ObservableProperty] private string _outputText = "";
    [ObservableProperty] private string _streamsText = "No servers";
    [ObservableProperty] private string _uptimeText = "00:00:00";
    [ObservableProperty] private string _recordingText = "Not recording";
    [ObservableProperty] private bool _isRecording;
    [ObservableProperty] private string _encoderText = "";
    [ObservableProperty] private string _healthText = "";
    [ObservableProperty] private string _playPauseText = "▶  Play";
    [ObservableProperty] private string _duckText = "";
    [ObservableProperty] private byte[]? _artwork;
    [ObservableProperty] private string _trackTimeText = "";
    [ObservableProperty] private double _trackProgress;
    [ObservableProperty] private string _broadcastButtonText = "START BROADCAST";
    [ObservableProperty] private string _toast = "";

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private TimeSpan _lastCpu; private DateTime _lastCpuAt = DateTime.UtcNow;
    private string _artPath = "";
    public event Action<PageId>? NavigateRequested;
    public event Action<string, string>? Balloon;

    public ShellViewModel(StationController station)
    {
        Station = station; StationName = station.Profile.Name;
        station.Notifications.Raised += n => Application.Current.Dispatcher.BeginInvoke(() => { Toast = $"{n.Title}: {n.Message}"; Balloon?.Invoke(n.Title, n.Message); });
        _timer.Tick += (_, _) => Update(); _timer.Start(); Update();
    }

    partial void OnSelectedNavChanged(NavItem value) => NavigateRequested?.Invoke(value.Page);
    public void Navigate(PageId p) => SelectedNav = NavItems.First(n => n.Page == p);

    private void Update()
    {
        var st = Station; var eng = st.Engine;
        if (Toast.Length > 0 && (DateTime.UtcNow - _toastAt).TotalSeconds > 8) Toast = "";
        Clock = TimeZoneInfo.ConvertTime(DateTime.UtcNow, st.Profile.TimeZone).ToString("HH:mm:ss");
        StationName = st.Profile.Name;
        int streaming = st.Streams.StreamingCount, total = st.Streams.Destinations.Count(d => d.Profile.Enabled);
        IsOnAir = streaming > 0; OnAirText = IsOnAir ? "ON AIR" : (st.Streams.AnyActive ? "CONNECTING" : "OFF AIR");
        var l = st.Streams.TotalListeners; ListenersText = l.HasValue ? $"Listeners: {l}" : "Listeners: n/a";
        var np = st.NowPlaying; NowPlayingText = np.Title; ArtistText = np.Artist;
        var manual = st.Profile.Metadata;
        if (manual.ManualOverride && !string.IsNullOrWhiteSpace(manual.ManualText)) { NowPlayingText = manual.ManualText; ArtistText = "Live / manual"; }
        NextText = st.NextText; ProgramText = string.IsNullOrWhiteSpace(manual.ProgramName) ? "" : manual.ProgramName; ScheduleText = st.ScheduleText;
        var mic = st.Profile.Audio.Mic;
        MicLive = !mic.Muted && (!mic.PushToTalk || eng.Mic.PushToTalkDown);
        MicText = mic.Muted ? "MIC MUTED" : mic.PushToTalk ? (eng.Mic.PushToTalkDown ? "MIC LIVE (PTT)" : "MIC PTT — hold") : "MIC LIVE";
        DuckText = eng.DuckReductionDb > 1 ? $"Ducking −{eng.DuckReductionDb:0} dB" : "";
        OutputText = eng.OutputDeviceName;
        StreamsText = st.Streams.Destinations.Count == 0 ? "No servers configured" : $"{streaming}/{total} streaming";
        UptimeText = st.Uptime.ToString(@"hh\:mm\:ss");
        BroadcastButtonText = st.Streams.AnyActive ? "STOP BROADCAST" : "START BROADCAST";
        IsRecording = st.Recorder.State != RecorderState.Idle;
        RecordingText = st.Recorder.State switch { RecorderState.Recording => $"● REC {st.Recorder.Elapsed:hh\\:mm\\:ss}  {st.Recorder.FileSize / 1048576.0:0.0} MB", RecorderState.Paused => $"❚❚ Paused {st.Recorder.Elapsed:hh\\:mm\\:ss}", _ => "Not recording" };
        EncoderText = st.Streams.Hub.ActiveEncoders == 0 ? "No active encoders" : $"{st.Streams.Hub.ActiveEncoders} encoder(s) running";
        var bad = st.Streams.Destinations.Count(d => d.State is StreamState.AuthFailed or StreamState.Error or StreamState.ServerUnavailable);
        var rec = st.Streams.Destinations.Count(d => d.State == StreamState.Reconnecting);
        HealthText = !st.Streams.AnyActive ? "Idle" : bad == 0 && rec == 0 ? "All servers healthy" : $"{bad} failed, {rec} reconnecting";
        PlayPauseText = eng.Music.State == PlayerState.Playing ? "❚❚  Pause" : "▶  Play";
        TrackTimeText = eng.Music.DurationSec > 0 ? $"{TimeSpan.FromSeconds(eng.Music.PositionSec):m\\:ss} / {TimeSpan.FromSeconds(eng.Music.DurationSec):m\\:ss}" : "";
        TrackProgress = eng.Music.DurationSec > 0 ? eng.Music.PositionSec / eng.Music.DurationSec : 0;
        if (np.Path != _artPath) { _artPath = np.Path; Artwork = string.IsNullOrEmpty(np.Path) ? null : Database.LoadArtwork(np.Path); }
        var p = Process.GetCurrentProcess(); var now = DateTime.UtcNow;
        var cpu = (p.TotalProcessorTime - _lastCpu).TotalMilliseconds / ((now - _lastCpuAt).TotalMilliseconds * Environment.ProcessorCount) * 100;
        if ((now - _lastCpuAt).TotalSeconds >= 1) { CpuText = $"CPU {Math.Max(0, cpu):0}%"; _lastCpu = p.TotalProcessorTime; _lastCpuAt = now; }
    }

    // ---- commands
    [RelayCommand]
    private async Task ToggleBroadcast()
    {
        if (Station.Streams.AnyActive) { await Station.StopBroadcastAsync(); return; }
        var problems = Station.StartBroadcast();
        if (problems.Count > 0) Toast = problems[0];
    }
    [RelayCommand] private void StartBroadcast() { var p = Station.StartBroadcast(); if (p.Count > 0) Toast = p[0]; }
    [RelayCommand] private Task StopBroadcast() => Station.StopBroadcastAsync();
    [RelayCommand] private void PlayPause() => Station.PlayPause();
    [RelayCommand] private void Next() => Station.NextTrack();
    [RelayCommand] private void Previous() => Station.PreviousTrack();
    [RelayCommand] private void StopMusic() => Station.StopMusic();
    [RelayCommand] private void ToggleMic() => Station.ToggleMic();
    [RelayCommand] private void ToggleRecording() { var e = Station.ToggleRecording(); if (e != null) Toast = e; }
    [RelayCommand] private void OpenSoundboard() => Navigate(PageId.Soundboard);
    [RelayCommand] private void ToggleSpeakers() => Station.Profile.Audio.SpeakersEnabled = !Station.Profile.Audio.SpeakersEnabled;
    [RelayCommand] private void DismissToast() => Toast = "";
    private DateTime _toastAt;
    partial void OnToastChanged(string value) { if (!string.IsNullOrEmpty(value)) _toastAt = DateTime.UtcNow; }

    public void Rebind(StationController s) { Station = s; OnPropertyChanged(nameof(Station)); OnPropertyChanged(nameof(Engine)); OnPropertyChanged(nameof(Settings)); s.Notifications.Raised += n => Application.Current.Dispatcher.BeginInvoke(() => { Toast = $"{n.Title}: {n.Message}"; Balloon?.Invoke(n.Title, n.Message); }); }
}
