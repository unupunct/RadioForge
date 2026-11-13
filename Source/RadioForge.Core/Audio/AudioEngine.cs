using CommunityToolkit.Mvvm.ComponentModel;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace RadioForge.Core.Audio;

public sealed record DeviceInfo(string Id, string Name, bool IsDefault);

public static class AudioDevices
{
    public static List<DeviceInfo> List(DataFlow flow)
    {
        var res = new List<DeviceInfo>();
        try
        {
            using var en = new MMDeviceEnumerator();
            string? def = null;
            try { def = en.GetDefaultAudioEndpoint(flow, Role.Console).ID; } catch { }
            foreach (var d in en.EnumerateAudioEndPoints(flow, DeviceState.Active)) res.Add(new DeviceInfo(d.ID, d.FriendlyName, d.ID == def));
        }
        catch (Exception ex) { Log.Error("Audio", "Device enumeration failed", ex); }
        return res;
    }
    public static List<DeviceInfo> Inputs() => List(DataFlow.Capture);
    public static List<DeviceInfo> Outputs() => List(DataFlow.Render);
}

/// <summary>Raises events when audio endpoints are added/removed so the engine can recover and the UI can notify.</summary>
public sealed class DeviceWatcher : IMMNotificationClient, IDisposable
{
    private readonly MMDeviceEnumerator _en = new();
    public event Action<string, string>? DeviceStateChanged; // id, state
    public DeviceWatcher() { try { _en.RegisterEndpointNotificationCallback(this); } catch { } }
    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => DeviceStateChanged?.Invoke(deviceId, newState.ToString());
    public void OnDeviceAdded(string pwstrDeviceId) => DeviceStateChanged?.Invoke(pwstrDeviceId, "Added");
    public void OnDeviceRemoved(string deviceId) => DeviceStateChanged?.Invoke(deviceId, "Removed");
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) { }
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    public void Dispose() { try { _en.UnregisterEndpointNotificationCallback(this); } catch { } _en.Dispose(); }
}

public enum ChannelId { Music, Microphone, Effects, Aux, System }

public partial class ChannelStrip : ObservableObject
{
    public ChannelId Id { get; }
    public string Name { get; }
    public LevelMeter Meter { get; }
    public AudioBus Tap { get; }
    public ISampleProvider Source { get; set; }
    [ObservableProperty] private double _volume = 0.8;
    [ObservableProperty] private bool _mute;
    [ObservableProperty] private bool _solo;
    internal float LastGain = 1f;
    public ChannelStrip(ChannelId id, string name, ISampleProvider source, int rate)
    {
        Id = id; Name = name; Source = source; Meter = new LevelMeter(rate); Tap = new AudioBus(rate);
    }
}

/// <summary>
/// The mixing console. It is an ISampleProvider that is pulled by the output device (master clock). If no output
/// device is available a software clock pulls it instead, so streaming and recording keep running.
/// </summary>
public sealed class AudioEngine : ISampleProvider, IDisposable
{
    public AudioSettings Settings { get; }
    public int Rate { get; }
    public WaveFormat WaveFormat { get; }
    public MusicPlayer Music { get; }
    public SfxChannel Sfx { get; }
    public MicProcessor Mic { get; }
    public CaptureSource MicCapture { get; }
    public CaptureSource AuxCapture { get; }
    public CaptureSource SystemCapture { get; }
    public ChannelStrip MusicStrip { get; }
    public ChannelStrip MicStrip { get; }
    public ChannelStrip SfxStrip { get; }
    public ChannelStrip AuxStrip { get; }
    public ChannelStrip SystemStrip { get; }
    public IReadOnlyList<ChannelStrip> Strips { get; }
    public LevelMeter MasterMeter { get; }
    public LevelMeter InputMeter { get; }
    public AudioBus MasterBus { get; }
    /// <summary>Processed microphone for private recording (voice tracks); never goes on air.</summary>
    public AudioBus VoiceBus { get; }
    public volatile bool MicPrivate;
    public SpectrumAnalyzer Analyzer { get; }
    public Limiter Limiter { get; } = new();
    public BroadcastProcessor Processor { get; private set; } = null!;
    public bool IsRunning { get; private set; }
    public bool UsingDevice { get; private set; }
    public string OutputDeviceName { get; private set; } = "(none)";
    public string OutputState => _out == null ? "no WASAPI output" : _out.PlaybackState.ToString();
    public float DuckReductionDb { get; private set; }
    public long FramesProduced => Interlocked.Read(ref _frames);
    public event Action<string>? DeviceProblem;     // human readable
    public event Action? Recovered;

    private WasapiOut? _out;
    private Thread? _clockThread;
    private volatile bool _clockRun;
    private readonly Limiter _limiter;
    private float[][] _scratch = { new float[4096], new float[4096] };
    private float _duckGain = 1f, _balL = 1, _balR = 1;
    private long _frames;
    private bool _wantRun, _useDevice;
    private readonly object _ctl = new();
    private MonitorOutput? _monitor;
    private DeviceWatcher? _watcher;
    private int _recovering;

    public AudioEngine(AudioSettings settings)
    {
        Settings = settings; Rate = settings.SampleRate;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2);
        _limiter = Limiter; Processor = new BroadcastProcessor(Rate);
        Music = new MusicPlayer(Rate, () => Settings.CrossfadeSec) { TrimSilence = () => Settings.TrimSilence };
        Sfx = new SfxChannel(Rate);
        MicCapture = new CaptureSource("Microphone", settings.Mic.DeviceId, false, Rate);
        AuxCapture = new CaptureSource("Aux", settings.AuxDeviceId, false, Rate);
        SystemCapture = new CaptureSource("System", settings.SystemAudioDeviceId, true, Rate);
        Mic = new MicProcessor(MicCapture, settings.Mic, Rate);
        MusicStrip = new ChannelStrip(ChannelId.Music, "Music", Music, Rate) { Volume = settings.MusicVolume };
        MicStrip = new ChannelStrip(ChannelId.Microphone, "Microphone", Mic, Rate) { Volume = settings.MicVolume };
        SfxStrip = new ChannelStrip(ChannelId.Effects, "Effects", Sfx, Rate) { Volume = settings.SfxVolume };
        AuxStrip = new ChannelStrip(ChannelId.Aux, "Auxiliary", AuxCapture, Rate) { Volume = settings.AuxVolume };
        SystemStrip = new ChannelStrip(ChannelId.System, "System", SystemCapture, Rate) { Volume = settings.SystemVolume };
        Strips = new[] { MusicStrip, MicStrip, SfxStrip, AuxStrip, SystemStrip };
        MasterMeter = new LevelMeter(Rate); InputMeter = new LevelMeter(Rate);
        MasterBus = new AudioBus(Rate); VoiceBus = new AudioBus(Rate); Analyzer = new SpectrumAnalyzer(Rate);
        foreach (var c in new[] { MicCapture, AuxCapture, SystemCapture }) c.Stopped += OnCaptureStopped;
        _watcher = new DeviceWatcher();
        _watcher.DeviceStateChanged += OnDeviceState;
    }

    public bool IsStripEnabled(ChannelStrip s) =>
        s == AuxStrip ? Settings.AuxEnabled : s == SystemStrip ? Settings.SystemAudioEnabled : true;

    // ------------------------------------------------------------------ lifecycle
    /// <param name="useDevice">false = headless (software clock only), used by tests and when no hardware exists.</param>
    public void Start(bool useDevice = true)
    {
        lock (_ctl)
        {
            if (IsRunning) return;
            _wantRun = true; _useDevice = useDevice;
            IsRunning = true;
            OpenOutput();
            TryStartCaptures();
        }
    }

    private void OpenOutput()
    {
        UsingDevice = false;
        if (_useDevice)
        {
            try
            {
                using var en = new MMDeviceEnumerator();
                MMDevice? dev = null;
                if (!string.IsNullOrEmpty(Settings.OutputDeviceId))
                    try { var d = en.GetDevice(Settings.OutputDeviceId); if (d.State == DeviceState.Active) dev = d; } catch { }
                dev ??= en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
                OutputDeviceName = dev.FriendlyName;
                _out = new WasapiOut(dev, AudioClientShareMode.Shared, true, Math.Clamp(Settings.LatencyMs, 20, 500));
                _out.PlaybackStopped += OnOutputStopped;
                _out.Init(new EngineWaveProvider(this));
                _out.Play();
                UsingDevice = true;
                Log.Info("Audio", $"Output: {OutputDeviceName} @ {Rate} Hz");
                return;
            }
            catch (Exception ex)
            {
                Log.Error("Audio", "Output device unavailable — falling back to software clock", ex);
                DeviceProblem?.Invoke("Audio output unavailable: " + ex.Message);
                try { _out?.Dispose(); } catch { }
                _out = null;
            }
        }
        OutputDeviceName = "(software clock — no output device)";
        StartSoftwareClock();
    }

    private void StartSoftwareClock()
    {
        _clockRun = true;
        _clockThread = new Thread(() =>
        {
            var buf = new float[Rate / 50 * 2]; // 20 ms
            var sw = System.Diagnostics.Stopwatch.StartNew(); long produced = 0;
            while (_clockRun)
            {
                long due = (long)(sw.Elapsed.TotalSeconds * Rate) - produced;
                if (due < buf.Length / 2) { Thread.Sleep(2); continue; }
                int frames = (int)Math.Min(due, buf.Length / 2);
                try { Read(buf, 0, frames * 2); } catch (Exception ex) { Log.Error("Audio", "Mix failed", ex); }
                produced += frames;
            }
        }) { IsBackground = true, Name = "RadioForge software clock", Priority = ThreadPriority.AboveNormal };
        _clockThread.Start();
    }

    private void StopOutput()
    {
        _clockRun = false; _clockThread?.Join(500); _clockThread = null;
        var o = _out; _out = null;
        if (o != null) { o.PlaybackStopped -= OnOutputStopped; try { o.Stop(); } catch { } try { o.Dispose(); } catch { } }
        UsingDevice = false;
    }

    public void Stop()
    {
        lock (_ctl)
        {
            _wantRun = false; IsRunning = false;
            StopOutput();
            MicCapture.Stop(); AuxCapture.Stop(); SystemCapture.Stop();
            _monitor?.Dispose(); _monitor = null;
        }
    }

    /// <summary>Switch output device at runtime.</summary>
    public void RestartOutput()
    {
        lock (_ctl) { if (!IsRunning) return; StopOutput(); OpenOutput(); }
    }

    public void RestartCaptures() { lock (_ctl) { MicCapture.Stop(); AuxCapture.Stop(); SystemCapture.Stop(); if (IsRunning) TryStartCaptures(); } }

    private void TryStartCaptures()
    {
        if (!_useDevice) return;
        // Microphone is always opened (meter, test, PTT); muted state silences it in the chain.
        Try(MicCapture, Settings.Mic.DeviceId);
        if (Settings.AuxEnabled) Try(AuxCapture, Settings.AuxDeviceId);
        if (Settings.SystemAudioEnabled) Try(SystemCapture, Settings.SystemAudioDeviceId);
    }

    private void Try(CaptureSource src, string id)
    {
        try { src.DeviceId = id; src.Start(); Log.Info("Audio", $"{src.Name} input: {src.DeviceName}"); }
        catch (Exception ex) { Log.Warn("Audio", $"{src.Name} input unavailable: {ex.Message}"); DeviceProblem?.Invoke($"{src.Name} input unavailable: {ex.Message}"); }
    }


    // ------------------------------------------------------------------ recovery
    private void OnOutputStopped(object? s, StoppedEventArgs e)
    {
        if (e.Exception == null || !_wantRun) return;
        Log.Error("Audio", "Output device stopped unexpectedly", e.Exception);
        DeviceProblem?.Invoke("Audio output stopped: " + e.Exception.Message);
        ScheduleRecovery();
    }

    private void OnCaptureStopped(string name, Exception? ex)
    {
        if (ex == null || !_wantRun) return;
        Log.Error("Audio", $"{name} capture stopped", ex);
        DeviceProblem?.Invoke($"{name} device disconnected");
        ScheduleRecovery();
    }

    private void OnDeviceState(string id, string state)
    {
        if (state is "Unplugged" or "Removed" or "NotPresent" or "Disabled")
        {
            var name = id.Length > 12 ? "audio device" : id;
            Log.Warn("Audio", $"Device {state}: {id}");
            DeviceProblem?.Invoke($"An audio device was {state.ToLowerInvariant()}");
            ScheduleRecovery();
        }
    }

    /// <summary>Re-opens output and capture devices (falling back to defaults) with back-off until it works.</summary>
    private void ScheduleRecovery()
    {
        if (Interlocked.Exchange(ref _recovering, 1) == 1) return;
        Task.Run(async () =>
        {
            try
            {
                for (int attempt = 0; attempt < 30 && _wantRun; attempt++)
                {
                    await Task.Delay(Math.Min(1000 * (attempt + 1), 8000));
                    lock (_ctl)
                    {
                        if (!_wantRun) return;
                        if (_useDevice && (_out == null || _out.PlaybackState != PlaybackState.Playing)) { StopOutput(); OpenOutput(); }
                        if (_useDevice && !MicCapture.IsRunning) Try(MicCapture, Settings.Mic.DeviceId);
                        if (_useDevice && Settings.AuxEnabled && !AuxCapture.IsRunning) Try(AuxCapture, Settings.AuxDeviceId);
                        if (_useDevice && Settings.SystemAudioEnabled && !SystemCapture.IsRunning) Try(SystemCapture, Settings.SystemAudioDeviceId);
                        if (UsingDevice && MicCapture.IsRunning) { Log.Info("Audio", "Audio engine recovered"); Recovered?.Invoke(); return; }
                    }
                }
            }
            finally { Interlocked.Exchange(ref _recovering, 0); }
        });
    }

    // ------------------------------------------------------------------ mixing (audio thread)
    public int Read(float[] buffer, int offset, int count)
    {
        if (_scratch[0].Length < count) _scratch = new[] { new float[count], new float[count] };
        var master = _scratch[0]; var tmp = _scratch[1];
        Array.Clear(master, 0, count);
        int frames = count / 2;
        bool anySolo = Strips.Any(s => s.Solo);
        var ms = Settings.Mic;

        foreach (var s in Strips)
        {
            bool enabled = IsStripEnabled(s);
            if (!enabled) { s.Meter.Process(master, 0, 0); continue; }
            int n = s.Source.Read(tmp, 0, count);
            if (n < count) Array.Clear(tmp, n, count - n);
            if (s == MicStrip && MicPrivate) { VoiceBus.Publish(tmp, 0, count); s.Meter.Process(tmp, 0, count); InputMeter.Process(tmp, 0, count); continue; }

            // update duck gain right after the mic has been processed (mic is strip #2 but we need it before music → handled below)
            float target = s.Mute ? 0f : (float)s.Volume;
            if (s == MusicStrip) target *= _duckGain;
            float g0 = s.LastGain, g1 = target;
            for (int i = 0; i < frames; i++)
            {
                float g = g0 + (g1 - g0) * (i + 1) / frames;
                tmp[2 * i] *= g; tmp[2 * i + 1] *= g;
            }
            s.LastGain = g1;
            s.Meter.Process(tmp, 0, count);
            if (s.Tap.HasSubscribers) s.Tap.Publish(tmp, 0, count);
            if (s == MicStrip) { InputMeter.Process(tmp, 0, count); _monitor?.Feed(tmp, count); }
            if (!s.Mute && (!anySolo || s.Solo)) for (int i = 0; i < count; i++) master[i] += tmp[i];
        }
        UpdateDuck(frames);

        float mv = (float)Settings.MasterVolume;
        float bal = (float)Settings.Balance;
        _balL = bal > 0 ? 1 - bal : 1; _balR = bal < 0 ? 1 + bal : 1;
        for (int i = 0; i + 1 < count; i += 2) { master[i] *= mv * _balL; master[i + 1] *= mv * _balR; }
        Processor.Process(master, 0, count, Settings.RadioProcessing);
        if (Settings.LimiterEnabled) _limiter.Process(master, 0, count, Db.ToLinear(Settings.LimiterCeilingDb), Rate);
        else for (int i = 0; i < count; i++) master[i] = Math.Clamp(master[i], -1.5f, 1.5f);
        MasterMeter.Process(master, 0, count);
        Analyzer.Push(master, 0, count);
        if (MasterBus.HasSubscribers) MasterBus.Publish(master, 0, count);
        Interlocked.Add(ref _frames, frames);

        if (Settings.SpeakersEnabled && UsingDevice) Array.Copy(master, 0, buffer, offset, count);
        else Array.Clear(buffer, offset, count);
        return count;
    }

    private void UpdateDuck(int frames)
    {
        var m = Settings.Mic;
        bool speaking = m.DuckingEnabled && !MicPrivate && Mic.Open && Db.ToDb(Mic.Level) > m.DuckThresholdDb;
        float target = speaking ? Db.ToLinear(-m.DuckAmountDb) : 1f;
        double ms = target < _duckGain ? m.DuckAttackMs : m.DuckReleaseMs;
        float coef = (float)(1 - Math.Exp(-frames / (Rate * Math.Max(ms, 1) / 1000.0)));
        _duckGain += (target - _duckGain) * coef;
        DuckReductionDb = (float)-Db.ToDb(_duckGain);
    }

    // ------------------------------------------------------------------ monitoring / test
    /// <summary>Routes the processed microphone to a headphone device (explicit; off by default).</summary>
    public string? SetMicMonitor(bool enabled)
    {
        lock (_ctl)
        {
            Settings.Mic.MonitorEnabled = enabled;
            _monitor?.Dispose(); _monitor = null;
            if (!enabled) return null;
            var id = Settings.Mic.MonitorDeviceId;
            if (string.IsNullOrEmpty(id)) { Settings.Mic.MonitorEnabled = false; return "Choose a monitor (headphone) device first."; }
            if (id == Settings.OutputDeviceId || (string.IsNullOrEmpty(Settings.OutputDeviceId) && AudioDevices.Outputs().FirstOrDefault(d => d.IsDefault)?.Id == id))
            { Settings.Mic.MonitorEnabled = false; return "Monitor device must differ from the program output (it would cause feedback)."; }
            try { _monitor = new MonitorOutput(id, Rate); return null; }
            catch (Exception ex) { Settings.Mic.MonitorEnabled = false; return "Cannot open monitor device: " + ex.Message; }
        }
    }

    public void Dispose()
    {
        Stop();
        _watcher?.Dispose(); Music.Dispose();
        MicCapture.Dispose(); AuxCapture.Dispose(); SystemCapture.Dispose();
    }
}

internal sealed class MonitorOutput : IDisposable
{
    private readonly WasapiOut _out; private readonly BufferedWaveProvider _buf;
    public MonitorOutput(string deviceId, int rate)
    {
        using var en = new MMDeviceEnumerator();
        _buf = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(rate, 2)) { DiscardOnBufferOverflow = true, BufferDuration = TimeSpan.FromMilliseconds(500) };
        _out = new WasapiOut(en.GetDevice(deviceId), AudioClientShareMode.Shared, true, 60);
        _out.Init(_buf); _out.Play();
    }
    public void Feed(float[] data, int count)
    {
        var b = new byte[count * 4]; Buffer.BlockCopy(data, 0, b, 0, b.Length); _buf.AddSamples(b, 0, b.Length);
    }
    public void Dispose() { try { _out.Stop(); } catch { } _out.Dispose(); }
}

/// <summary>Pulls float samples from the engine and copies them explicitly into the device's byte buffer.</summary>
internal sealed class EngineWaveProvider : IWaveProvider
{
    private readonly AudioEngine _e; private float[] _tmp = new float[8192];
    public EngineWaveProvider(AudioEngine e) { _e = e; WaveFormat = e.WaveFormat; }
    public WaveFormat WaveFormat { get; }
    public int Read(byte[] buffer, int offset, int count)
    {
        int n = count / 4; if (_tmp.Length < n) _tmp = new float[n];
        _e.Read(_tmp, 0, n); Buffer.BlockCopy(_tmp, 0, buffer, offset, n * 4); return n * 4;
    }
}
