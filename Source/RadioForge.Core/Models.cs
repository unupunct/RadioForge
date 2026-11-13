using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RadioForge.Core;

public enum ServerType { ShoutcastV1, ShoutcastV2, Icecast2 }
public enum AudioCodec { Mp3, Aac, Ogg }

public static class CodecInfo
{
    public static string ContentType(AudioCodec c) => c switch { AudioCodec.Mp3 => "audio/mpeg", AudioCodec.Aac => "audio/aac", _ => "application/ogg" };
    public static string Extension(AudioCodec c) => c switch { AudioCodec.Mp3 => "mp3", AudioCodec.Aac => "aac", _ => "ogg" };
    public static bool NeedsFfmpeg(AudioCodec c) => c != AudioCodec.Mp3;
}

/// <summary>One streaming destination with its own independent encoder configuration.</summary>
public partial class ServerProfile : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [ObservableProperty] private string _name = "New server";
    [ObservableProperty] private ServerType _type = ServerType.Icecast2;
    [ObservableProperty] private string _host = "localhost";
    /// <summary>Icecast: the server port. SHOUTcast: the listener port (sources connect on port + 1).</summary>
    [ObservableProperty] private int _port = 8000;
    [ObservableProperty] private string _username = "source";
    [ObservableProperty] private string _mount = "/stream";
    [ObservableProperty] private int _streamId = 1;
    [ObservableProperty] private bool _useTls;
    [ObservableProperty] private string _streamName = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _genre = "";
    [ObservableProperty] private string _website = "";
    [ObservableProperty] private bool _isPublic;
    [ObservableProperty] private AudioCodec _codec = AudioCodec.Mp3;
    [ObservableProperty] private int _bitrateKbps = 128;
    [ObservableProperty] private int _sampleRate = 44100;
    [ObservableProperty] private int _channels = 2;
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private bool _autoReconnect = true;
    [ObservableProperty] private int _retryIntervalSec = 5;
    [ObservableProperty] private int _maxRetryIntervalSec = 60;
    [ObservableProperty] private int _priority = 1;
    [ObservableProperty] private bool _sendMetadata = true;

    /// <summary>DPAPI-protected password (what is written to disk). Never plaintext.</summary>
    public string PasswordProtected { get; set; } = "";

    [JsonIgnore]
    public string Password
    {
        get => SecretProtector.Unprotect(PasswordProtected);
        set { PasswordProtected = SecretProtector.Protect(value ?? ""); OnPropertyChanged(); }
    }

    public ServerProfile Clone()
    {
        var c = (ServerProfile)MemberwiseClone();
        return c;
    }

    /// <summary>Key identifying encoders that can safely be shared between destinations.</summary>
    [JsonIgnore] public EncoderSettings Encoder => new(Codec, BitrateKbps, SampleRate, Channels);

    public string Describe() => $"{Type} {(UseTls ? "https" : "http")}://{Host}:{Port}{(Type == ServerType.Icecast2 ? Mount : "")} — {BitrateKbps} kbps {Codec}";
}

public readonly record struct EncoderSettings(AudioCodec Codec, int BitrateKbps, int SampleRate, int Channels);

public partial class MicSettings : ObservableObject
{
    [ObservableProperty] private string _deviceId = "";
    [ObservableProperty] private double _gainDb = 0;
    [ObservableProperty] private bool _gateEnabled = true;
    [ObservableProperty] private double _gateThresholdDb = -50;
    [ObservableProperty] private bool _compressorEnabled = true;
    [ObservableProperty] private double _compThresholdDb = -18;
    [ObservableProperty] private double _compRatio = 3;
    [ObservableProperty] private double _compMakeupDb = 3;
    [ObservableProperty] private bool _eqEnabled = true;
    [ObservableProperty] private double _eqLowDb = 0;
    [ObservableProperty] private double _eqMidDb = 0;
    [ObservableProperty] private double _eqHighDb = 0;
    [ObservableProperty] private double _reverbMix = 0;
    [ObservableProperty] private double _echoMix = 0;
    [ObservableProperty] private double _echoDelayMs = 280;
    [ObservableProperty] private bool _monitorEnabled = false; // explicit routing: off by default (prevents feedback)
    [ObservableProperty] private string _monitorDeviceId = "";
    [ObservableProperty] private bool _pushToTalk = false;
    [ObservableProperty] private bool _muted = true;
    [ObservableProperty] private bool _duckingEnabled = true;
    [ObservableProperty] private double _duckAmountDb = 14;
    [ObservableProperty] private double _duckThresholdDb = -38;
    [ObservableProperty] private double _duckAttackMs = 60;
    [ObservableProperty] private double _duckReleaseMs = 900;
}

public partial class AudioSettings : ObservableObject
{
    [ObservableProperty] private string _outputDeviceId = "";   // "" = Windows default
    [ObservableProperty] private string _auxDeviceId = "";
    [ObservableProperty] private string _systemAudioDeviceId = ""; // render device to loop back, "" = default
    [ObservableProperty] private int _sampleRate = 44100;
    [ObservableProperty] private int _latencyMs = 100;
    [ObservableProperty] private bool _speakersEnabled = true;  // monitor routing of the program bus
    [ObservableProperty] private double _masterVolume = 0.85;
    [ObservableProperty] private double _balance = 0; // -1..1
    [ObservableProperty] private bool _limiterEnabled = true;
    [ObservableProperty] private double _limiterCeilingDb = -1;
    [ObservableProperty] private double _musicVolume = 0.8;
    [ObservableProperty] private double _micVolume = 0.9;
    [ObservableProperty] private double _sfxVolume = 0.9;
    [ObservableProperty] private double _auxVolume = 0.7;
    [ObservableProperty] private double _systemVolume = 0.7;
    [ObservableProperty] private double _crossfadeSec = 3;
    [ObservableProperty] private bool _systemAudioEnabled = false;
    [ObservableProperty] private bool _auxEnabled = false;
    public MicSettings Mic { get; set; } = new();
}

public enum RecordSource { Master, Microphone, Music, Sfx, Aux, System }
public enum RecordFormat { Wav, Mp3, Flac }

public partial class RecordingSettings : ObservableObject
{
    [ObservableProperty] private string _folder = "";
    [ObservableProperty] private RecordFormat _format = RecordFormat.Mp3;
    [ObservableProperty] private RecordSource _source = RecordSource.Master;
    [ObservableProperty] private int _mp3Kbps = 192;
    [ObservableProperty] private string _namePattern = "{station}_{source}_{date}_{time}";
}

public enum PadMode { PlayOnce, Loop, PlayOverMusic, ReplaceMusic, PlayAfterCurrentTrack }

public partial class SoundPad : ObservableObject
{
    public int Index { get; set; }
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _category = "Custom effects";
    [ObservableProperty] private string _color = "#1F6F5C";
    [ObservableProperty] private string _icon = "♪";
    [ObservableProperty] private string _filePath = "";
    [ObservableProperty] private double _volume = 1.0;
    [ObservableProperty] private PadMode _mode = PadMode.PlayOverMusic;
    [ObservableProperty] private string _hotkey = "";
    [JsonIgnore] [ObservableProperty] private bool _isPlaying;
    [JsonIgnore] public bool HasSound => !string.IsNullOrEmpty(FilePath);
}

public class SoundboardConfig
{
    public const int PadsPerPage = 12, PageCount = 4; // 48 pads
    public static readonly string[] Categories =
    {
        "Station jingles","Intro","Outro","News","Commercials","Applause","Laughter","Transitions","Emergency announcements","Custom effects"
    };
    public List<SoundPad> Pads { get; set; } = new();
    public void EnsureSize()
    {
        while (Pads.Count < PadsPerPage * PageCount)
        {
            int i = Pads.Count;
            Pads.Add(new SoundPad { Index = i, Name = $"Pad {i + 1}", Category = Categories[(i / PadsPerPage) % Categories.Length] });
        }
    }
}

public class MetadataSettings
{
    public bool AutoUpdate { get; set; } = true;
    public string DjName { get; set; } = "";
    public string ProgramName { get; set; } = "";
    public bool ManualOverride { get; set; }
    public string ManualText { get; set; } = "";
}

public class AutoDjSettings
{
    public bool Enabled { get; set; } = true;
    public bool Shuffle { get; set; }
    public bool Repeat { get; set; } = true;
    public string PlaylistId { get; set; } = "";
}

/// <summary>A complete station: servers, audio, soundboard, metadata, recording. Playlists and schedule live in the profile's SQLite DB.</summary>
public class StationProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "My Radio";
    public string Description { get; set; } = "";
    public string Genre { get; set; } = "";
    public string Website { get; set; } = "";
    public string LogoPath { get; set; } = "";
    public string Country { get; set; } = "";
    public string TimeZoneId { get; set; } = TimeZoneInfo.Local.Id;
    public bool SetupCompleted { get; set; }
    public List<ServerProfile> Servers { get; set; } = new();
    public AudioSettings Audio { get; set; } = new();
    public RecordingSettings Recording { get; set; } = new();
    public SoundboardConfig Soundboard { get; set; } = new();
    public MetadataSettings Metadata { get; set; } = new();
    public AutoDjSettings AutoDj { get; set; } = new();

    [JsonIgnore] public TimeZoneInfo TimeZone
    {
        get { try { return TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId); } catch { return TimeZoneInfo.Local; } }
    }
}

public partial class AppSettings : ObservableObject
{
    public string ActiveProfileId { get; set; } = "";
    [ObservableProperty] private string _theme = "Dark";            // Dark | Light
    [ObservableProperty] private string _accentColor = "#00E5A0";
    [ObservableProperty] private double _fontScale = 1.0;
    [ObservableProperty] private bool _minimizeToTray = true;
    [ObservableProperty] private string _closeBehavior = "Ask";     // Ask | Background | Exit
    [ObservableProperty] private string _ffmpegPath = "";
    [ObservableProperty] private bool _debugLogging;
    [ObservableProperty] private string _musicFolder = "";
    [ObservableProperty] private string _backupFolder = "";
    [ObservableProperty] private bool _notifyDisconnect = true;
    [ObservableProperty] private bool _notifyReconnect = true;
    [ObservableProperty] private bool _notifyAuth = true;
    [ObservableProperty] private bool _notifyRecording = true;
    [ObservableProperty] private bool _notifySchedule = true;
    [ObservableProperty] private bool _notifyMissingFile = true;
    [ObservableProperty] private bool _notifyClipping = true;
    [ObservableProperty] private bool _notifyDevice = true;
    [ObservableProperty] private int _statsSampleSec = 5;
    [ObservableProperty] private int _visPreset = 0;
    [ObservableProperty] private double _visSensitivity = 1.0;
    [ObservableProperty] private string _visColorA = "";
    [ObservableProperty] private string _visColorB = "";
    [ObservableProperty] private string _visBackground = "";
    [ObservableProperty] private string _visImage = "";
    public Dictionary<string, string> Shortcuts { get; set; } = DefaultShortcuts();
    /// <summary>Actions whose shortcut is registered system-wide (works while another app has focus). Only combos with a modifier or an F-key are accepted.</summary>
    public HashSet<string> GlobalShortcuts { get; set; } = new() { "StartBroadcast", "StopBroadcast" };
    public Dictionary<string, double> Layout { get; set; } = new();

    public static Dictionary<string, string> DefaultShortcuts() => new()
    {
        ["PlayPause"] = "Space", ["NextTrack"] = "Ctrl+N", ["ToggleRecording"] = "Ctrl+R", ["ToggleMic"] = "F1",
        ["Pad1"] = "F2", ["Pad2"] = "F3", ["Pad3"] = "F4", ["Pad4"] = "F5",
        ["StartBroadcast"] = "Ctrl+Shift+B", ["StopBroadcast"] = "Ctrl+Shift+S",
    };
}

public enum TrackKind { Music, Jingle, Program }

public class Track
{
    public long Id { get; set; }
    public string Path { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public string Genre { get; set; } = "";
    public uint Year { get; set; }
    public double DurationSec { get; set; }
    public long FileSize { get; set; }
    public DateTime Added { get; set; } = DateTime.Now;
    public bool Missing { get; set; }
    public TimeSpan Duration => TimeSpan.FromSeconds(DurationSec);
    public string DisplayName => string.IsNullOrWhiteSpace(Artist) ? Title : $"{Artist} — {Title}";
    public string DurationText => Duration.TotalHours >= 1 ? Duration.ToString(@"h\:mm\:ss") : Duration.ToString(@"m\:ss");
}

public class Playlist
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public long? ParentId { get; set; }
    public bool IsFolder { get; set; }
}
