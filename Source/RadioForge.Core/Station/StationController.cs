using System.Collections.ObjectModel;
using System.IO.Compression;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using RadioForge.Core.Audio;
using RadioForge.Core.Streaming;

namespace RadioForge.Core.Station;

// ------------------------------------------------------------------------------------------ queue
public sealed class PlaybackQueue
{
    private readonly object _g = new();
    private List<string> _items = new(); private List<int> _order = new(); private int _pos = -1; private readonly Random _rnd = new();
    private readonly Queue<string> _after = new();
    public bool Shuffle { get; set; }
    public bool Repeat { get; set; } = true;
    public int Count { get { lock (_g) return _items.Count; } }
    public IReadOnlyList<string> Items { get { lock (_g) return _items.ToList(); } }

    public void Load(IEnumerable<string> paths, bool shuffle)
    {
        lock (_g) { _items = paths.ToList(); Shuffle = shuffle; _pos = -1; Reorder(); }
    }
    private void Reorder()
    {
        _order = Enumerable.Range(0, _items.Count).ToList();
        if (Shuffle) for (int i = _order.Count - 1; i > 0; i--) { int j = _rnd.Next(i + 1); (_order[i], _order[j]) = (_order[j], _order[i]); }
    }
    public void EnqueueAfterCurrent(string path) { lock (_g) _after.Enqueue(path); }
    public string? Peek()
    {
        lock (_g)
        {
            if (_after.Count > 0) return _after.Peek();
            if (_items.Count == 0) return null;
            int np = _pos + 1;
            if (np >= _order.Count) { if (!Repeat) return null; np = 0; }
            return _items[_order[np]];
        }
    }
    public string? Take()
    {
        lock (_g)
        {
            if (_after.Count > 0) return _after.Dequeue();
            if (_items.Count == 0) return null;
            _pos++;
            if (_pos >= _order.Count) { if (!Repeat) { _pos = _order.Count; return null; } if (Shuffle) Reorder(); _pos = 0; }
            return _items[_order[_pos]];
        }
    }
    /// <summary>Make the given path the current position (used when the user double-clicks a track).</summary>
    public void SetCurrent(string path) { lock (_g) { int idx = _items.IndexOf(path); if (idx >= 0) _pos = _order.IndexOf(idx); } }
}

// ------------------------------------------------------------------------------------------ notifications
public enum NotifyKind { Disconnected, Reconnected, AuthFailure, RecordingCompleted, ScheduleStarting, MissingFile, MicClipping, DeviceProblem, Info }
public sealed record NotificationItem(DateTime Time, NotifyKind Kind, string Title, string Message);

public sealed class NotificationHub
{
    private readonly AppSettings _s;
    public event Action<NotificationItem>? Raised;
    /// <summary>Raised for every notification regardless of the user's toast preferences (used by alert delivery).</summary>
    public event Action<NotificationItem>? Notified;
    public ObservableCollection<NotificationItem> History { get; } = new();
    public NotificationHub(AppSettings s) { _s = s; }
    public void Notify(NotifyKind kind, string title, string message)
    {
        bool enabled = kind switch
        {
            NotifyKind.Disconnected => _s.NotifyDisconnect, NotifyKind.Reconnected => _s.NotifyReconnect, NotifyKind.AuthFailure => _s.NotifyAuth,
            NotifyKind.RecordingCompleted => _s.NotifyRecording, NotifyKind.ScheduleStarting => _s.NotifySchedule, NotifyKind.MissingFile => _s.NotifyMissingFile,
            NotifyKind.MicClipping => _s.NotifyClipping, NotifyKind.DeviceProblem => _s.NotifyDevice, _ => true
        };
        var n = new NotificationItem(DateTime.Now, kind, title, Redactor.Redact(message));
        Log.Info("Notify", $"{title}: {n.Message}");
        try { Notified?.Invoke(n); } catch { }
        if (enabled) try { Raised?.Invoke(n); } catch { }
    }
}

// ------------------------------------------------------------------------------------------ statistics
public sealed class StatsService : IDisposable
{
    private readonly StreamManager _sm; private readonly Database _db; private readonly Timer _t;
    private readonly Dictionary<string, (StreamState st, DateTime since)> _last = new();
    public List<StatSample> Recent { get; } = new();
    public DateTime Started { get; } = DateTime.Now;
    public StatsService(StreamManager sm, Database db, AppSettings s)
    {
        _sm = sm; _db = db;
        try { Recent.AddRange(db.GetStats(DateTime.Now.AddHours(-6))); } catch { }
        int sec = Math.Max(2, s.StatsSampleSec);
        _t = new Timer(_ => Sample(), null, sec * 1000, sec * 1000);
    }
    private void Sample()
    {
        foreach (var d in _sm.Destinations.ToList())
        {
            var s = new StatSample(DateTime.Now, d.Profile.Id, d.State.ToString(), d.BitrateKbps, d.Listeners ?? -1);
            lock (Recent) { Recent.Add(s); if (Recent.Count > 20000) Recent.RemoveRange(0, 2000); }
            try { _db.AddStat(s); } catch { }
        }
    }
    public List<StatSample> Snapshot(string? serverId = null, TimeSpan? window = null)
    {
        var from = DateTime.Now - (window ?? TimeSpan.FromHours(1));
        lock (Recent) return Recent.Where(x => x.Time >= from && (serverId == null || x.ServerId == serverId)).ToList();
    }
    /// <summary>Fraction of samples in which the destination was streaming.</summary>
    public double Availability(string serverId, TimeSpan window)
    {
        var l = Snapshot(serverId, window); if (l.Count == 0) return 0;
        return l.Count(x => x.Status == nameof(StreamState.Streaming)) / (double)l.Count;
    }
    public void ExportCsv(string path, DateTime since)
    {
        var names = _sm.Destinations.ToDictionary(d => d.Profile.Id, d => d.Profile.Name);
        var sb = new StringBuilder("time,server,status,bitrate_kbps,listeners\r\n");
        foreach (var s in _db.GetStats(since))
            sb.Append($"{s.Time:yyyy-MM-dd HH:mm:ss},\"{(names.TryGetValue(s.ServerId, out var n) ? n : s.ServerId).Replace("\"", "\"\"")}\",{s.Status},{s.BitrateKbps:F1},{(s.Listeners < 0 ? "" : s.Listeners.ToString())}\r\n");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }
    public void Dispose() => _t.Dispose();
}

// ------------------------------------------------------------------------------------------ persistence
public sealed class ProfileBundle
{
    public int Version { get; set; } = 1;
    public StationProfile Profile { get; set; } = new();
    public List<ScheduledEvent> Events { get; set; } = new();
    public Dictionary<string, List<string>> Playlists { get; set; } = new();
}

public static class ProfileStore
{
    public static AppSettings LoadSettings()
    {
        var s = SafeJson.Load<AppSettings>(AppPaths.AppSettingsFile) ?? new AppSettings();
        if (s.Shortcuts == null || s.Shortcuts.Count == 0) s.Shortcuts = AppSettings.DefaultShortcuts();
        foreach (var kv in AppSettings.DefaultShortcuts()) s.Shortcuts.TryAdd(kv.Key, kv.Value);
        Log.DebugEnabled = s.DebugLogging;
        return s;
    }
    public static void SaveSettings(AppSettings s) => SafeJson.Save(AppPaths.AppSettingsFile, s);

    private static string File_(string id) => Path.Combine(AppPaths.ProfileDir(id), "profile.json");
    public static List<StationProfile> List() =>
        Directory.GetDirectories(AppPaths.Profiles).Select(d => SafeJson.Load<StationProfile>(Path.Combine(d, "profile.json"))).Where(p => p != null).Select(p => p!).OrderBy(p => p.Name).ToList();
    public static StationProfile? Load(string id) { var p = SafeJson.Load<StationProfile>(File_(id)); p?.Soundboard.EnsureSize(); return p; }
    public static void Save(StationProfile p) => SafeJson.Save(File_(p.Id), p);
    public static StationProfile Create(string name)
    {
        var p = new StationProfile { Name = name }; p.Soundboard.EnsureSize(); Save(p); return p;
    }
    public static void Delete(string id) { try { Directory.Delete(Path.Combine(AppPaths.Profiles, id), true); } catch (Exception ex) { Log.Error("Profile", "Delete failed", ex); } }
    public static Database OpenDb(string id) => new(Path.Combine(AppPaths.ProfileDir(id), "radioforge.db"));

    /// <summary>Exports a profile. Passwords are removed — they are never written to export files.</summary>
    public static void Export(StationProfile p, Database db, string file)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(p, SafeJson.Options);
        var copy = System.Text.Json.JsonSerializer.Deserialize<StationProfile>(json, SafeJson.Options)!;
        foreach (var s in copy.Servers) s.PasswordProtected = "";
        var b = new ProfileBundle { Profile = copy, Events = db.GetEvents() };
        foreach (var pl in db.GetPlaylists().Where(x => !x.IsFolder)) b.Playlists[pl.Name] = db.GetPlaylistTracks(pl.Id).Select(t => t.Path).ToList();
        File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(b, SafeJson.Options), Encoding.UTF8);
    }

    public static StationProfile Import(string file)
    {
        var b = System.Text.Json.JsonSerializer.Deserialize<ProfileBundle>(File.ReadAllText(file), SafeJson.Options) ?? throw new InvalidDataException("Not a RadioForge profile file.");
        var p = b.Profile; p.Id = Guid.NewGuid().ToString("N");
        foreach (var s in p.Servers) { s.Id = Guid.NewGuid().ToString("N"); s.PasswordProtected = ""; }
        p.Name += " (imported)"; p.Soundboard.EnsureSize(); Save(p);
        var db = OpenDb(p.Id);
        foreach (var e in b.Events) db.SaveEvent(e);
        foreach (var kv in b.Playlists)
        {
            var id = db.CreatePlaylist(kv.Key);
            db.SavePlaylistTracks(id, kv.Value.Select(path => (db.GetTrackByPath(path) ?? db.ImportFile(path))?.Id).Where(i => i.HasValue).Select(i => i!.Value));
        }
        return p;
    }
}

public static class BackupService
{
    public static string Create(string folder)
    {
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, $"RadioForge-backup-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        using var z = ZipFile.Open(file, ZipArchiveMode.Create);
        if (File.Exists(AppPaths.AppSettingsFile)) z.CreateEntryFromFile(AppPaths.AppSettingsFile, "settings.json");
        foreach (var f in Directory.EnumerateFiles(AppPaths.Profiles, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith("-wal") && !f.EndsWith("-shm")))
        {
            try
            {
                using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var e = z.CreateEntry("profiles/" + Path.GetRelativePath(AppPaths.Profiles, f).Replace('\\', '/'));
                using var o = e.Open(); fs.CopyTo(o);
            }
            catch (Exception ex) { Log.Warn("Backup", $"Skipped {Path.GetFileName(f)}: {ex.Message}"); }
        }
        Log.Info("Backup", "Backup created: " + file);
        return file;
    }

    public static void Restore(string zipFile)
    {
        // extract to staging first so a bad archive never damages the live configuration
        var stage = Path.Combine(Path.GetTempPath(), "rf-restore-" + Guid.NewGuid().ToString("N"));
        ZipFile.ExtractToDirectory(zipFile, stage);
        try
        {
            if (!File.Exists(Path.Combine(stage, "settings.json")) && !Directory.Exists(Path.Combine(stage, "profiles"))) throw new InvalidDataException("This is not a RadioForge backup.");
            foreach (var f in Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories))
            {
                var dest = Path.Combine(AppPaths.Root, Path.GetRelativePath(stage, f));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(f, dest, true);
            }
            Log.Info("Backup", "Restored from " + zipFile);
        }
        finally { try { Directory.Delete(stage, true); } catch { } }
    }
}

// ------------------------------------------------------------------------------------------ the station
public sealed class NowPlayingInfo
{
    public string Artist { get; init; } = ""; public string Title { get; init; } = ""; public string Album { get; init; } = "";
    public string Path { get; init; } = ""; public string Program { get; init; } = ""; public string Dj { get; init; } = ""; public bool IsManual { get; init; }
    public string Text => string.IsNullOrWhiteSpace(Artist) ? Title : $"{Artist} — {Title}";
    public static NowPlayingInfo Idle { get; } = new() { Title = "—" };
}

/// <summary>Facade that wires audio engine, streaming, recorder, library, scheduler and notifications for one station profile.</summary>
public sealed partial class StationController : ObservableObject, IDisposable
{
    public AppSettings Settings { get; }
    public StationProfile Profile { get; private set; }
    public Database Db { get; }
    public AudioEngine Engine { get; }
    public StreamManager Streams { get; }
    public Recorder Recorder { get; }
    public ScheduleRunner Scheduler { get; }
    public PlaybackQueue Queue { get; } = new();
    public NotificationHub Notifications { get; }
    public StatsService Stats { get; }
    public AlertService Alerts { get; }
    public NowPlayingServer Web { get; }
    public VoiceTrackRecorder Voice { get; }
    private bool _archiving;
    public ObservableCollection<ScheduledEvent> Events { get; } = new();
    public string? FfmpegPath => Ffmpeg.Locate(Settings.FfmpegPath);

    [ObservableProperty] private NowPlayingInfo _nowPlaying = NowPlayingInfo.Idle;
    [ObservableProperty] private string _nextText = "—";
    [ObservableProperty] private DateTime? _broadcastStart;
    [ObservableProperty] private string _scheduleText = "Nothing scheduled";
    public event Action<NowPlayingInfo>? NowPlayingChanged;

    private readonly Dictionary<int, int> _padVoices = new();
    private readonly Timer _housekeeping;
    private DateTime _lastClipNote = DateTime.MinValue;

    public bool OnAir => Streams.StreamingCount > 0;
    public TimeSpan Uptime => BroadcastStart is { } t ? DateTime.Now - t : TimeSpan.Zero;

    public StationController(AppSettings settings, StationProfile profile, bool useAudioDevices = true)
    {
        Settings = settings; Profile = profile;
        Profile.Soundboard.EnsureSize();
        Db = ProfileStore.OpenDb(profile.Id);
        Notifications = new NotificationHub(settings);
        Engine = new AudioEngine(profile.Audio);
        Streams = new StreamManager(Engine, () => FfmpegPath);
        Recorder = new Recorder(Engine, () => Profile, Db, () => FfmpegPath);
        Stats = new StatsService(Streams, Db, settings);
        Alerts = new AlertService(Notifications, settings, () => Profile.Name);
        Web = new NowPlayingServer(this);
        Voice = new VoiceTrackRecorder(Engine);
        foreach (var s in profile.Servers) AddDestination(s);
        foreach (var e in Db.GetEvents()) Events.Add(e);
        Queue.Repeat = profile.AutoDj.Repeat; Queue.Shuffle = profile.AutoDj.Shuffle;

        // channel ↔ settings synchronisation
        Bind(Engine.MusicStrip, v => profile.Audio.MusicVolume = v); Bind(Engine.MicStrip, v => profile.Audio.MicVolume = v);
        Bind(Engine.SfxStrip, v => profile.Audio.SfxVolume = v); Bind(Engine.AuxStrip, v => profile.Audio.AuxVolume = v); Bind(Engine.SystemStrip, v => profile.Audio.SystemVolume = v);
        Engine.MicStrip.Mute = profile.Audio.Mic.Muted;
        Engine.MicStrip.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ChannelStrip.Mute)) profile.Audio.Mic.Muted = Engine.MicStrip.Mute; };
        profile.Audio.Mic.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MicSettings.Muted)) Engine.MicStrip.Mute = profile.Audio.Mic.Muted; };

        Engine.Music.NextProvider = NextPath;
        Engine.Music.GainProvider = path =>
        {
            if (!Profile.Audio.NormalizeLoudness) return 1f;
            var t = Db.GetTrackByPath(path); if (t == null) return 1f;
            if (t.Lufs == null) { try { var v = Loudness.MeasureLufs(path); if (v.HasValue) { Db.SetLufs(t.Id, v.Value); return Loudness.GainFor(v, Profile.Audio.TargetLufs); } } catch { } return 1f; }
            return t.Lufs <= -69 ? 1f : Loudness.GainFor(t.Lufs, Profile.Audio.TargetLufs);
        };
        Engine.Music.TrackStarted += OnTrackStarted;
        Engine.Music.QueueFinished += OnQueueFinished;
        Engine.Music.TrackFailed += (p, ex) => Notifications.Notify(NotifyKind.MissingFile, "Audio file problem", $"{Path.GetFileName(p)} could not be played: {ex.Message}");
        Engine.DeviceProblem += m => Notifications.Notify(NotifyKind.DeviceProblem, "Audio device", m);
        Engine.Recovered += () => Notifications.Notify(NotifyKind.Info, "Audio engine", "Audio devices recovered.");
        Streams.StateTransition += OnStreamTransition;
        Recorder.Completed += r =>
        {
            Notifications.Notify(NotifyKind.RecordingCompleted, "Recording completed", $"{Path.GetFileName(r.Path)} ({TimeSpan.FromSeconds(r.DurationSec):hh\\:mm\\:ss})");
            if (!string.IsNullOrEmpty(r.Title)) try { PodcastFeed.Write(Profile, Db.GetRecordings(), Path.GetDirectoryName(r.Path)!); } catch (Exception ex) { Log.Error("Podcast", "Feed failed", ex); }
        };
        Recorder.Failed += m => Notifications.Notify(NotifyKind.DeviceProblem, "Recording", m);

        Scheduler = new ScheduleRunner(() => Events.ToList(), () => TimeZoneInfo.ConvertTime(DateTime.UtcNow, Profile.TimeZone));
        Scheduler.Started += OnScheduleStarted; Scheduler.Ended += OnScheduleEnded;

        Engine.Start(useAudioDevices);
        Scheduler.Start(); ApplyWebSettings();
        _housekeeping = new Timer(_ => Housekeeping(), null, 2000, 2000);
        Log.Info("Station", $"Station \"{profile.Name}\" opened ({profile.Servers.Count} servers, {Events.Count} scheduled events)");
    }

    private void Bind(ChannelStrip s, Action<double> write) => s.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ChannelStrip.Volume)) write(s.Volume); };

    // ---------------------------------------------------------------- servers
    public StreamDestination AddDestination(ServerProfile p)
    {
        var d = Streams.Add(p);
        d.CurrentMetadata = () => CurrentMetaText();
        return d;
    }
    public async Task RemoveDestinationAsync(StreamDestination d) { Profile.Servers.Remove(d.Profile); await Streams.RemoveAsync(d); SaveProfile(); }
    public void SaveProfile() { try { ProfileStore.Save(Profile); } catch (Exception ex) { Log.Error("Config", "Saving the profile failed", ex); } }

    private (string artist, string title)? CurrentMetaText()
    {
        var m = Profile.Metadata;
        if (m.ManualOverride && !string.IsNullOrWhiteSpace(m.ManualText)) return ("", m.ManualText);
        if (NowPlaying == NowPlayingInfo.Idle) return null;
        return (NowPlaying.Artist, NowPlaying.Title);
    }

    private void OnStreamTransition(StreamDestination d, StreamState from, StreamState to)
    {
        if (to == StreamState.Streaming && from != StreamState.Streaming && d.ReconnectCount > 0)
            Notifications.Notify(NotifyKind.Reconnected, "Server reconnected", $"{d.Profile.Name} is streaming again.");
        else if (from == StreamState.Streaming && to is StreamState.Reconnecting or StreamState.ServerUnavailable or StreamState.Error)
            Notifications.Notify(NotifyKind.Disconnected, "Server disconnected", $"{d.Profile.Name}: {d.StatusText}");
        if (to == StreamState.AuthFailed) Notifications.Notify(NotifyKind.AuthFailure, "Authentication failed", $"{d.Profile.Name}: {d.LastError}");
    }

    public List<string> BroadcastProblems()
    {
        var res = new List<string>();
        var enabled = Streams.Destinations.Where(d => d.Profile.Enabled).ToList();
        if (enabled.Count == 0) res.Add("No streaming server is enabled. Add and enable at least one server.");
        foreach (var d in enabled) foreach (var p in ConfigValidator.Validate(d.Profile)) res.Add($"{d.Profile.Name}: {p}");
        return res;
    }

    /// <summary>Starts every enabled server independently. Returns configuration problems (servers with problems report them in their own status).</summary>
    public List<string> StartBroadcast()
    {
        var problems = BroadcastProblems();
        Streams.StartAll();
        BroadcastStart ??= DateTime.Now;
        if (Profile.AutoDj.Enabled && Engine.Music.State == PlayerState.Stopped) StartAutoDj();
        Log.Info("Station", "Broadcast started");
        return problems;
    }

    public async Task StopBroadcastAsync()
    {
        await Streams.StopAllAsync(); BroadcastStart = null; Log.Info("Station", "Broadcast stopped");
    }

    // ---------------------------------------------------------------- music / auto DJ
    private string? NextPath()
    {
        var p = Queue.Take();
        if (p != null) return p;
        return null;
    }

    private IEnumerable<string> AutoDjPaths()
    {
        List<Track> tracks;
        if (long.TryParse(Profile.AutoDj.PlaylistId, out var pid) && pid > 0) tracks = Db.GetPlaylistTracks(pid);
        else tracks = Db.GetTracks();
        return tracks.Where(t => !t.Missing).Select(t => t.Path);
    }

    public bool StartAutoDj()
    {
        var paths = AutoDjPaths().ToList();
        if (paths.Count == 0) { Log.Warn("AutoDJ", "Auto DJ has no tracks to play"); return false; }
        Queue.Repeat = Profile.AutoDj.Repeat; Queue.Load(paths, Profile.AutoDj.Shuffle);
        var first = Queue.Take(); if (first == null) return false;
        Engine.Music.Play(first); return true;
    }

    public void PlayPlaylist(long playlistId, int startIndex = 0, bool? shuffle = null)
    {
        var tracks = Db.GetPlaylistTracks(playlistId).Where(t => !t.Missing).ToList();
        if (tracks.Count == 0) { Notifications.Notify(NotifyKind.MissingFile, "Playlist", "The playlist has no playable tracks."); return; }
        PlayTracks(tracks.Select(t => t.Path), startIndex, shuffle ?? Profile.AutoDj.Shuffle);
    }

    public void PlayTracks(IEnumerable<string> paths, int startIndex = 0, bool shuffle = false)
    {
        var list = paths.ToList(); if (list.Count == 0) return;
        Queue.Repeat = Profile.AutoDj.Repeat; Queue.Load(list, shuffle);
        string? first;
        if (shuffle || startIndex <= 0) first = Queue.Take();
        else { first = list[Math.Min(startIndex, list.Count - 1)]; Queue.SetCurrent(first); }
        if (first != null) Engine.Music.Play(first);
    }

    public void PlayPause()
    {
        if (Engine.Music.State == PlayerState.Stopped) { if (Queue.Count > 0) { var p = Queue.Take(); if (p != null) Engine.Music.Play(p); } else StartAutoDj(); }
        else Engine.Music.TogglePause();
    }
    public void NextTrack() => Engine.Music.Next();
    public void PreviousTrack() => Engine.Music.Previous();
    public void StopMusic() => Engine.Music.Stop();

    private void OnQueueFinished()
    {
        if (Profile.AutoDj.Enabled) { Log.Info("AutoDJ", "Queue finished — Auto DJ continues"); StartAutoDj(); }
        else { NowPlaying = NowPlayingInfo.Idle; NextText = "—"; }
    }

    private void OnTrackStarted(string path)
    {
        var t = Db.GetTrackByPath(path);
        var m = Profile.Metadata;
        var np = new NowPlayingInfo
        {
            Path = path, Title = t?.Title ?? Path.GetFileNameWithoutExtension(path), Artist = t?.Artist ?? "", Album = t?.Album ?? "",
            Program = m.ProgramName, Dj = m.DjName, IsManual = false
        };
        NowPlaying = np; NowPlayingChanged?.Invoke(np);
        var nx = Queue.Peek(); NextText = nx == null ? "—" : (Db.GetTrackByPath(nx)?.DisplayName ?? Path.GetFileNameWithoutExtension(nx));
        if (m.AutoUpdate && !m.ManualOverride) _ = Streams.UpdateMetadataAsync(np.Artist, np.Title);
    }

    /// <summary>Manual metadata for live shows / announcements. Empty text returns to automatic metadata.</summary>
    public async Task SetManualMetadataAsync(string? text)
    {
        var m = Profile.Metadata;
        m.ManualOverride = !string.IsNullOrWhiteSpace(text); m.ManualText = text ?? "";
        SaveProfile();
        if (m.ManualOverride) await Streams.UpdateMetadataAsync("", m.ManualText);
        else if (NowPlaying != NowPlayingInfo.Idle) await Streams.UpdateMetadataAsync(NowPlaying.Artist, NowPlaying.Title);
        NowPlayingChanged?.Invoke(NowPlaying);
    }

    // ---------------------------------------------------------------- soundboard
    public bool PlayPad(SoundPad pad)
    {
        if (!pad.HasSound) return false;
        if (!File.Exists(pad.FilePath)) { Notifications.Notify(NotifyKind.MissingFile, "Soundboard", $"File for \"{pad.Name}\" is missing: {pad.FilePath}"); return false; }
        if (_padVoices.TryGetValue(pad.Index, out var old)) { Engine.Sfx.Stop(old); if (pad.Mode == PadMode.Loop) { _padVoices.Remove(pad.Index); pad.IsPlaying = false; return true; } }
        switch (pad.Mode)
        {
            case PadMode.PlayAfterCurrentTrack:
                Queue.EnqueueAfterCurrent(pad.FilePath);
                if (Engine.Music.State == PlayerState.Stopped) { var p = Queue.Take(); if (p != null) Engine.Music.Play(p); }
                return true;
            case PadMode.ReplaceMusic:
                bool wasPlaying = Engine.Music.State == PlayerState.Playing;
                if (wasPlaying) Engine.Music.Pause();
                return StartVoice(pad, false, () => { if (wasPlaying) Engine.Music.Resume(); });
            case PadMode.Loop: return StartVoice(pad, true, null);
            default: return StartVoice(pad, false, null);
        }
    }

    private bool StartVoice(SoundPad pad, bool loop, Action? after)
    {
        int id = Engine.Sfx.Play(pad.FilePath, (float)pad.Volume, loop, () => { _padVoices.Remove(pad.Index); pad.IsPlaying = false; after?.Invoke(); });
        if (id < 0) { after?.Invoke(); Notifications.Notify(NotifyKind.MissingFile, "Soundboard", $"\"{pad.Name}\" cannot be decoded."); return false; }
        _padVoices[pad.Index] = id; pad.IsPlaying = true; return true;
    }

    public void StopPad(SoundPad pad) { if (_padVoices.TryGetValue(pad.Index, out var id)) Engine.Sfx.Stop(id); }
    public void StopAllPads() { Engine.Sfx.StopAll(); }

    // ---------------------------------------------------------------- microphone & recording
    public void ToggleMic() => Profile.Audio.Mic.Muted = !Profile.Audio.Mic.Muted;
    public string? ToggleRecording()
    {
        if (Recorder.State == RecorderState.Idle) return Recorder.Start();
        _ = Recorder.StopAsync(); return null;
    }

    // ---------------------------------------------------------------- schedule
    public void SaveEvent(ScheduledEvent e)
    {
        Db.SaveEvent(e);
        var i = Events.ToList().FindIndex(x => x.Id == e.Id);
        if (i >= 0) Events[i] = e; else Events.Add(e);
    }
    public void DeleteEvent(ScheduledEvent e) { Db.DeleteEvent(e.Id); Events.Remove(e); }

    private void OnScheduleStarted(Occurrence o)
    {
        var e = o.Event;
        Notifications.Notify(NotifyKind.ScheduleStarting, "Scheduled event starting", $"{e.Title} ({o.Start:t}–{o.End:t})");
        Log.Info("Scheduler", $"Starting \"{e.Title}\" ({e.Kind})");
        switch (e.Source)
        {
            case SourceKind.Playlist when long.TryParse(e.SourceRef, out var pid):
                Profile.Metadata.ProgramName = e.Title; PlayPlaylist(pid, 0, false); break;
            case SourceKind.AudioFile:
                if (!File.Exists(e.SourceRef)) { Notifications.Notify(NotifyKind.MissingFile, "Scheduled file missing", e.SourceRef); break; }
                if (e.IsMomentary) { int id = Engine.Sfx.Play(e.SourceRef, 1f, false); if (id < 0) Notifications.Notify(NotifyKind.MissingFile, "Scheduled file", "Cannot decode " + e.SourceRef); }
                else { Profile.Metadata.ProgramName = e.Title; Engine.Music.Play(e.SourceRef); }
                break;
            case SourceKind.SoundboardPad when int.TryParse(e.SourceRef, out var idx) && idx >= 0 && idx < Profile.Soundboard.Pads.Count:
                PlayPad(Profile.Soundboard.Pads[idx]); break;
            default: Log.Warn("Scheduler", $"\"{e.Title}\" has no valid audio source"); break;
        }
        if (!e.IsMomentary) { Profile.Metadata.ProgramName = e.Title; }
        if (e.Archive && !e.IsMomentary && Recorder.State == RecorderState.Idle)
        {
            var err = Recorder.Start(e.Title); _archiving = err == null;
            if (err != null) Notifications.Notify(NotifyKind.DeviceProblem, "Show archive", "Could not record \"" + e.Title + "\": " + err);
        }
    }

    private void OnScheduleEnded(Occurrence o)
    {
        Log.Info("Scheduler", $"Ended \"{o.Event.Title}\"");
        if (_archiving && o.Event.Archive) { _archiving = false; _ = Recorder.StopAsync(); }
        if (Profile.Metadata.ProgramName == o.Event.Title) Profile.Metadata.ProgramName = "";
        if (o.Event.Source == SourceKind.Playlist && Scheduler.ActiveOccurrence == null && Profile.AutoDj.Enabled)
        {   // transition back to the Auto DJ playlist after the current track finishes
            var paths = AutoDjPaths().ToList();
            if (paths.Count > 0) Queue.Load(paths, Profile.AutoDj.Shuffle);
        }
    }

    // ---------------------------------------------------------------- voice tracks, web page
    /// <summary>Adds a recorded voice link to the library and inserts it into a playlist (after <paramref name="afterIndex"/>, or at the end).</summary>
    public Track? AddVoiceTrack(string path, long playlistId, int afterIndex = -1)
    {
        try { using var tf = TagLib.File.Create(path); tf.Tag.Title = "Voice link " + DateTime.Now.ToString("HH:mm"); tf.Tag.Performers = new[] { Profile.Metadata.DjName.Length > 0 ? Profile.Metadata.DjName : "Voice track" }; tf.Save(); } catch { }
        var t = Db.ImportFile(path); if (t == null) return null;
        var ids = Db.GetPlaylistTracks(playlistId).Select(x => x.Id).ToList();
        int at = afterIndex < 0 || afterIndex >= ids.Count ? ids.Count : afterIndex + 1; ids.Insert(at, t.Id);
        Db.SavePlaylistTracks(playlistId, ids); return t;
    }

    public string VoiceFolder => AppPaths.Dir(Path.Combine(AppPaths.ProfileDir(Profile.Id), "voice"));

    public void ApplyWebSettings()
    {
        try { if (Settings.WebEnabled) Web.Start(Settings.WebPort, Settings.WebAllowLan); else Web.Stop(); }
        catch (Exception ex) { Log.Error("Web", "Cannot start the now-playing page", ex); Notifications.Notify(NotifyKind.Info, "Now-playing page", "Could not listen on port " + Settings.WebPort + ": " + ex.Message); }
    }

    // ---------------------------------------------------------------- housekeeping
    private void Housekeeping()
    {
        try
        {
            var next = ScheduleEngine.Next(Events, TimeZoneInfo.ConvertTime(DateTime.UtcNow, Profile.TimeZone));
            ScheduleText = next == null ? "Nothing scheduled" : $"{next.Start:ddd HH:mm} — {next.Event.Title}";
            if (Engine.MicStrip.Meter.Clipped && DateTime.Now - _lastClipNote > TimeSpan.FromSeconds(30))
            {
                _lastClipNote = DateTime.Now; Engine.MicStrip.Meter.ResetClip();
                Notifications.Notify(NotifyKind.MicClipping, "Microphone clipping", "The microphone signal is too loud. Lower the gain.");
            }
            var nx = Queue.Peek(); var prepared = Engine.Music.PreparedNextPath ?? nx;
            NextText = prepared == null ? "—" : (Db.GetTrackByPath(prepared)?.DisplayName ?? Path.GetFileNameWithoutExtension(prepared));
        }
        catch (Exception ex) { Log.Error("Station", "Housekeeping failed", ex); }
    }

    public void Dispose()
    {
        _housekeeping.Dispose(); Scheduler.Dispose(); Stats.Dispose(); Alerts.Dispose(); Web.Dispose();
        try { if (Recorder.State != RecorderState.Idle) Recorder.StopAsync().Wait(8000); } catch { }
        try { Streams.Dispose(); } catch { }
        Engine.Dispose();
        SaveProfile();
    }
}
