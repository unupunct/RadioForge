using System.Diagnostics;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using NAudio.Lame;
using NAudio.Wave;
using RadioForge.Core.Audio;
using RadioForge.Core.Streaming;

namespace RadioForge.Core.Station;

public enum RecorderState { Idle, Recording, Paused }

internal interface IAudioSink : IDisposable { void Write(float[] d, int count); }

internal sealed class WavSink : IAudioSink
{
    private readonly WaveFileWriter _w;
    public WavSink(string path, int rate) { _w = new WaveFileWriter(path, new WaveFormat(rate, 16, 2)); }
    public void Write(float[] d, int count) { var b = PushConverter.ToPcm16(d, count); _w.Write(b, 0, b.Length); }
    public void Dispose() => _w.Dispose();
}

internal sealed class Mp3Sink : IAudioSink
{
    private readonly LameMP3FileWriter _w;
    public Mp3Sink(string path, int rate, int kbps) { _w = new LameMP3FileWriter(path, new WaveFormat(rate, 16, 2), kbps); }
    public void Write(float[] d, int count) { var b = PushConverter.ToPcm16(d, count); _w.Write(b, 0, b.Length); }
    public void Dispose() => _w.Dispose();
}

internal sealed class FlacSink : IAudioSink
{
    private readonly Process _p;
    public FlacSink(string exe, string path, int rate)
    {
        _p = Process.Start(new ProcessStartInfo(exe, $"-hide_banner -loglevel error -y -f f32le -ar {rate} -ac 2 -i pipe:0 -c:a flac \"{path}\"")
        { RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true })!;
        _p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log.Warn("FFmpeg", e.Data); };
        _p.BeginErrorReadLine();
    }
    public void Write(float[] d, int count)
    {
        var b = new byte[count * 4]; Buffer.BlockCopy(d, 0, b, 0, b.Length);
        _p.StandardInput.BaseStream.Write(b, 0, b.Length);
    }
    public void Dispose() { try { _p.StandardInput.Close(); if (!_p.WaitForExit(8000)) _p.Kill(true); } catch { } _p.Dispose(); }
}

/// <summary>Records the master bus or a single channel to WAV / MP3 / FLAC while streaming continues.</summary>
public sealed partial class Recorder : ObservableObject
{
    private readonly AudioEngine _engine; private readonly Func<StationProfile> _profile; private readonly Database _db; private readonly Func<string?> _ffmpeg;
    private BusSubscription? _sub; private Task? _task; private volatile bool _paused;
    private long _frames; private string _path = ""; private DateTime _started; private RecordSource _src; private RecordFormat _fmt;
    [ObservableProperty] private RecorderState _state = RecorderState.Idle;
    [ObservableProperty] private TimeSpan _elapsed;
    [ObservableProperty] private long _fileSize;
    [ObservableProperty] private string _currentFile = "";
    public event Action<RecordingInfo>? Completed;
    public event Action<string>? Failed;
    private Timer? _ui;

    public Recorder(AudioEngine engine, Func<StationProfile> profile, Database db, Func<string?> ffmpeg)
    { _engine = engine; _profile = profile; _db = db; _ffmpeg = ffmpeg; }

    private AudioBus BusFor(RecordSource s) => s switch
    {
        RecordSource.Microphone => _engine.MicStrip.Tap, RecordSource.Music => _engine.MusicStrip.Tap, RecordSource.Sfx => _engine.SfxStrip.Tap,
        RecordSource.Aux => _engine.AuxStrip.Tap, RecordSource.System => _engine.SystemStrip.Tap, _ => _engine.MasterBus
    };

    public static string BuildName(string pattern, string station, RecordSource src, DateTime now)
    {
        var n = pattern.Replace("{station}", station).Replace("{source}", src.ToString()).Replace("{date}", now.ToString("yyyy-MM-dd")).Replace("{time}", now.ToString("HH-mm-ss"));
        foreach (var c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
        return n.Trim();
    }

    /// <summary>Returns an error message or null on success.</summary>
    public string? Start()
    {
        if (State != RecorderState.Idle) return "Already recording.";
        var prof = _profile(); var rs = prof.Recording;
        var folder = string.IsNullOrWhiteSpace(rs.Folder) ? AppPaths.DefaultRecordings : rs.Folder;
        try { Directory.CreateDirectory(folder); } catch (Exception ex) { return $"Cannot use recordings folder: {ex.Message}"; }
        _src = rs.Source; _fmt = rs.Format; _started = DateTime.Now;
        var ext = _fmt switch { RecordFormat.Wav => "wav", RecordFormat.Mp3 => "mp3", _ => "flac" };
        var baseName = BuildName(rs.NamePattern, prof.Name, _src, _started);
        _path = Path.Combine(folder, baseName + "." + ext);
        for (int i = 2; File.Exists(_path); i++) _path = Path.Combine(folder, $"{baseName} ({i}).{ext}");
        IAudioSink sink;
        try
        {
            sink = _fmt switch
            {
                RecordFormat.Wav => new WavSink(_path, _engine.Rate),
                RecordFormat.Mp3 => new Mp3Sink(_path, _engine.Rate, rs.Mp3Kbps),
                _ => new FlacSink(Ffmpeg.Locate(_ffmpeg()) ?? throw new InvalidOperationException("FLAC recording needs FFmpeg (not found). Choose WAV or MP3, or set the FFmpeg path in Settings."), _path, _engine.Rate)
            };
        }
        catch (Exception ex) { return "Cannot start recording: " + ex.Message; }
        _frames = 0; _paused = false; _sub = BusFor(_src).Subscribe();
        var sub = _sub;
        _task = Task.Run(async () =>
        {
            try { await foreach (var b in sub.Reader.ReadAllAsync()) { if (_paused) continue; sink.Write(b.Data, b.Count); Interlocked.Add(ref _frames, b.Count / 2); } }
            catch (Exception ex) { Log.Error("Recorder", "Recording failed", ex); Failed?.Invoke("Recording stopped: " + ex.Message); }
            finally { try { sink.Dispose(); } catch { } }
        });
        CurrentFile = _path; State = RecorderState.Recording; Elapsed = TimeSpan.Zero; FileSize = 0;
        _ui = new Timer(_ =>
        {
            Elapsed = TimeSpan.FromSeconds(Interlocked.Read(ref _frames) / (double)_engine.Rate);
            try { FileSize = File.Exists(_path) ? new FileInfo(_path).Length : 0; } catch { }
        }, null, 500, 500);
        Log.Info("Recorder", $"Recording {_src} → {_path}");
        return null;
    }

    public void TogglePause()
    {
        if (State == RecorderState.Recording) { _paused = true; State = RecorderState.Paused; }
        else if (State == RecorderState.Paused) { _paused = false; State = RecorderState.Recording; }
    }

    public async Task<RecordingInfo?> StopAsync()
    {
        if (State == RecorderState.Idle) return null;
        _ui?.Dispose(); _ui = null;
        _sub?.Dispose(); _sub = null;
        if (_task != null) await Task.WhenAny(_task, Task.Delay(10000));
        State = RecorderState.Idle;
        double dur = Interlocked.Read(ref _frames) / (double)_engine.Rate;
        long size = 0; try { size = File.Exists(_path) ? new FileInfo(_path).Length : 0; } catch { }
        Elapsed = TimeSpan.FromSeconds(dur); FileSize = size;
        if (size == 0) { Log.Warn("Recorder", "Recording was empty; discarded"); return null; }
        var info = new RecordingInfo(0, _path, _started, dur, size, _fmt.ToString().ToUpperInvariant(), _src.ToString());
        try { _db.AddRecording(info); } catch (Exception ex) { Log.Error("Recorder", "Could not save history", ex); }
        Log.Info("Recorder", $"Recording saved: {_path} ({dur:F0}s, {size / 1024} KB)");
        Completed?.Invoke(info);
        return info;
    }
}
