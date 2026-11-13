using NAudio.CoreAudioApi;
using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace RadioForge.Core.Audio;

/// <summary>Opens any supported file as stereo float at the engine sample rate.</summary>
public sealed class AudioFileSource : IDisposable
{
    private readonly IDisposable _reader;
    public ISampleProvider Samples { get; }
    public double DurationSec { get; }
    public long TotalFrames { get; }

    public AudioFileSource(string path, int engineRate)
    {
        ISampleProvider sp;
        TimeSpan total;
        if (Path.GetExtension(path).Equals(".ogg", StringComparison.OrdinalIgnoreCase))
        {
            var v = new VorbisWaveReader(path); _reader = v; sp = v; total = v.TotalTime;
        }
        else
        {
            var a = new AudioFileReader(path); _reader = a; sp = a; total = a.TotalTime;
        }
        if (sp.WaveFormat.Channels == 1) sp = new MonoToStereoSampleProvider(sp);
        else if (sp.WaveFormat.Channels > 2)
        {
            var mux = new MultiplexingSampleProvider(new[] { sp }, 2);
            mux.ConnectInputToOutput(0, 0); mux.ConnectInputToOutput(1, 1); sp = mux;
        }
        if (sp.WaveFormat.SampleRate != engineRate) sp = new WdlResamplingSampleProvider(sp, engineRate);
        Samples = sp; DurationSec = total.TotalSeconds; TotalFrames = (long)(total.TotalSeconds * engineRate);
    }
    public void Dispose() => _reader.Dispose();
}

/// <summary>Microphone / aux / system (loopback) capture adapted to stereo float at engine rate. Silent when no data.</summary>
public sealed class CaptureSource : ISampleProvider, IDisposable
{
    private readonly bool _loopback;
    public string DeviceId { get; set; }
    private readonly int _rate;
    private IWaveIn? _capture;
    private BufferedWaveProvider? _buffer;
    private ISampleProvider? _chain;
    public string Name { get; }
    public WaveFormat WaveFormat { get; }
    public bool IsRunning { get; private set; }
    public string DeviceName { get; private set; } = "";
    public event Action<string, Exception?>? Stopped; // name, error (null = normal)

    public CaptureSource(string name, string deviceId, bool loopback, int rate)
    {
        Name = name; DeviceId = deviceId; _loopback = loopback; _rate = rate;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, 2);
    }

    public void Start()
    {
        Stop();
        using var en = new MMDevice_Enumerator_Holder();
        var dev = en.Find(DeviceId, _loopback ? DataFlow.Render : DataFlow.Capture)
                  ?? throw new InvalidOperationException(_loopback ? "No audio output device available for system capture." : "No input device available.");
        DeviceName = dev.FriendlyName;
        _capture = _loopback ? new WasapiLoopbackCapture(dev) : new WasapiCapture(dev);
        _buffer = new BufferedWaveProvider(_capture.WaveFormat) { DiscardOnBufferOverflow = true, ReadFully = true, BufferDuration = TimeSpan.FromSeconds(1) };
        ISampleProvider sp = _buffer.ToSampleProvider();
        if (sp.WaveFormat.Channels == 1) sp = new MonoToStereoSampleProvider(sp);
        else if (sp.WaveFormat.Channels > 2)
        {
            var mux = new MultiplexingSampleProvider(new[] { sp }, 2);
            mux.ConnectInputToOutput(0, 0); mux.ConnectInputToOutput(1, 1); sp = mux;
        }
        if (sp.WaveFormat.SampleRate != _rate) sp = new WdlResamplingSampleProvider(sp, _rate);
        _chain = sp;
        _capture.DataAvailable += (_, e) => _buffer?.AddSamples(e.Buffer, 0, e.BytesRecorded);
        _capture.RecordingStopped += (_, e) => { IsRunning = false; Stopped?.Invoke(Name, e.Exception); };
        _capture.StartRecording();
        IsRunning = true;
    }

    public void Stop()
    {
        var c = _capture; _capture = null; _chain = null;
        IsRunning = false;
        if (c == null) return;
        try { c.StopRecording(); } catch { }
        try { c.Dispose(); } catch { }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int n = 0;
        try { n = _chain?.Read(buffer, offset, count) ?? 0; } catch { }
        if (n < count) Array.Clear(buffer, offset + n, count - n);
        return count;
    }
    public void Dispose() => Stop();
}

internal sealed class MMDevice_Enumerator_Holder : IDisposable
{
    private readonly MMDeviceEnumerator _e = new();
    public MMDevice? Find(string id, DataFlow flow)
    {
        try
        {
            if (!string.IsNullOrEmpty(id))
            {
                var d = _e.GetDevice(id);
                if (d.State == DeviceState.Active) return d;
            }
        }
        catch { }
        try { return _e.GetDefaultAudioEndpoint(flow, Role.Console); } catch { return null; }
    }
    public void Dispose() => _e.Dispose();
}

public enum PlayerState { Stopped, Playing, Paused }

/// <summary>
/// Two-deck music player with equal-power crossfade and gapless hand-over. The audio thread only reads decks;
/// opening files and raising events happen on a control thread, so the UI never touches the audio path.
/// </summary>
public sealed class MusicPlayer : ISampleProvider, IDisposable
{
    private sealed class Deck : IDisposable
    {
        public AudioFileSource Src = null!; public string Path = ""; public long Pos; public float Gain = 1f;
        public float[]? Lead; public int LeadLen, LeadPos; public long SilentRun;
        public void Dispose() => Src.Dispose();

        /// <summary>Reads from the decoder, serving any buffered lead-in first.</summary>
        public int Read(float[] buf, int off, int count)
        {
            int n = 0;
            if (Lead != null && LeadPos < LeadLen) { n = Math.Min(count, LeadLen - LeadPos); Array.Copy(Lead, LeadPos, buf, off, n); LeadPos += n; if (LeadPos >= LeadLen) Lead = null; }
            if (n < count) n += Src.Samples.Read(buf, off + n, count - n);
            return n;
        }

        /// <summary>Discards leading silence (below -55 dBFS, at most 4 s) so tracks start instantly. Control thread only.</summary>
        public void SkipLeadingSilence(int rate)
        {
            const float thr = 0.0018f; var tmp = new float[2048]; long skipped = 0, max = rate * 4L;
            while (skipped < max)
            {
                int n = Src.Samples.Read(tmp, 0, tmp.Length); if (n == 0) break;
                int first = -1; for (int i = 0; i < n; i++) if (Math.Abs(tmp[i]) > thr) { first = i & ~1; break; }
                if (first < 0) { skipped += n / 2; continue; }
                Lead = new float[n - first]; Array.Copy(tmp, first, Lead, 0, n - first); LeadLen = n - first; LeadPos = 0; skipped += first / 2; break;
            }
            Pos += skipped;
        }
    }

    private readonly object _gate = new();
    private readonly int _rate;
    private readonly Func<double> _crossfadeSec;
    private readonly Queue<Action> _events = new();
    private readonly List<Deck> _trash = new();
    private readonly Timer _control;
    private Deck? _cur, _next;
    private bool _fading, _noNext, _preparing; private long _fadePos, _fadeLen;
    private float[] _t1 = new float[8192], _t2 = new float[8192];
    private readonly Stack<string> _history = new();
    public WaveFormat WaveFormat { get; }
    public PlayerState State { get; private set; } = PlayerState.Stopped;
    public Func<string?>? NextProvider { get; set; }
    /// <summary>Per-track playback gain (loudness normalisation). Called when a file is opened, never on the audio thread.</summary>
    public Func<string, float>? GainProvider { get; set; }
    public Func<bool>? TrimSilence { get; set; }
    public event Action<string>? TrackStarted;
    public event Action? QueueFinished;
    public event Action<string, Exception>? TrackFailed;
    public event Action? StateChanged;

    public string? CurrentPath => _cur?.Path;
    public double PositionSec => _cur == null ? 0 : _cur.Pos / (double)_rate;
    public double DurationSec => _cur?.Src.DurationSec ?? 0;
    public string? PreparedNextPath => _next?.Path;

    public MusicPlayer(int rate, Func<double> crossfadeSec)
    {
        _rate = rate; _crossfadeSec = crossfadeSec;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, 2);
        _control = new Timer(_ => ControlTick(), null, 100, 50);
    }

    private Deck Open(string path)
    {
        float g = 1f; try { g = GainProvider?.Invoke(path) ?? 1f; } catch { }
        var d = new Deck { Path = path, Src = new AudioFileSource(path, _rate), Gain = g };
        if (TrimSilence?.Invoke() == true) { try { d.SkipLeadingSilence(_rate); } catch { } }
        return d;
    }

    public void Play(string path)
    {
        Deck d;
        try { d = Open(path); }
        catch (Exception ex) { Log.Error("Music", $"Cannot open {Path.GetFileName(path)}", ex); TrackFailed?.Invoke(path, ex); return; }
        lock (_gate)
        {
            if (_cur != null) { _history.Push(_cur.Path); _trash.Add(_cur); }
            if (_next != null) { _trash.Add(_next); _next = null; }
            _cur = d; _fading = false; _noNext = false; State = PlayerState.Playing;
        }
        Log.Info("Music", "Playing " + Path.GetFileName(path));
        TrackStarted?.Invoke(path); StateChanged?.Invoke();
    }

    public void Pause() { lock (_gate) if (State == PlayerState.Playing) State = PlayerState.Paused; StateChanged?.Invoke(); }
    public void Resume() { lock (_gate) if (State == PlayerState.Paused) State = PlayerState.Playing; StateChanged?.Invoke(); }
    public void TogglePause() { if (State == PlayerState.Playing) Pause(); else if (State == PlayerState.Paused) Resume(); }

    public void Stop()
    {
        lock (_gate)
        {
            if (_cur != null) _trash.Add(_cur); if (_next != null) _trash.Add(_next);
            _cur = _next = null; _fading = false; State = PlayerState.Stopped;
        }
        StateChanged?.Invoke();
    }

    /// <summary>Skip to the next track with a short crossfade.</summary>
    public void Next()
    {
        Deck? n;
        lock (_gate) { n = _next; _next = null; }
        if (n == null)
        {
            string? p = null; try { p = NextProvider?.Invoke(); } catch (Exception ex) { Log.Error("Music", "Next provider failed", ex); }
            if (p == null) { Stop(); QueueFinished?.Invoke(); return; }
            try { n = Open(p); } catch (Exception ex) { Log.Error("Music", $"Cannot open {Path.GetFileName(p)}", ex); TrackFailed?.Invoke(p, ex); return; }
        }
        lock (_gate)
        {
            if (_cur == null || State != PlayerState.Playing) { if (_cur != null) _trash.Add(_cur); _cur = n; State = PlayerState.Playing; _fading = false; }
            else
            {
                if (_next != null) _trash.Add(_next);
                _next = n; _fadeLen = (long)(Math.Min(Math.Max(_crossfadeSec(), 0.05), 1.5) * _rate); _fadePos = 0; _fading = true;
            }
        }
        StateChanged?.Invoke();
    }

    public void Previous()
    {
        string? p = null; lock (_gate) if (_history.Count > 0) p = _history.Pop();
        if (PositionSec > 5 && CurrentPath != null) p = CurrentPath;
        if (p != null) { Play(p); lock (_gate) if (_history.Count > 0 && _history.Peek() == p) _history.Pop(); }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        Array.Clear(buffer, offset, count);
        lock (_gate)
        {
            if (State != PlayerState.Playing || _cur == null) return count;
            if (_t1.Length < count) { _t1 = new float[count]; _t2 = new float[count]; }
            int done = 0, guard = 0;
            while (done < count && _cur != null && guard++ < 8)
            {
                int want = count - done;
                if (!_fading && _next != null)
                {
                    long fl = (long)(_crossfadeSec() * _rate), remaining = _cur.Src.TotalFrames - _cur.Pos;
                    if (fl > 0 && _cur.Src.TotalFrames > 0 && remaining <= fl) { _fading = true; _fadePos = 0; _fadeLen = Math.Max(1, remaining); }
                }
                if (_fading && _next != null)
                {
                    int n = (int)Math.Min(want, (_fadeLen - _fadePos) * 2) & ~1;
                    if (n <= 0) { Promote(); continue; }
                    int r1 = _cur.Read(_t1, 0, n), r2 = _next.Read(_t2, 0, n);
                    for (int k = 0; k + 1 < n; k += 2)
                    {
                        double t = (_fadePos + k / 2) / (double)_fadeLen;
                        float gc = (float)Math.Cos(t * Math.PI / 2) * _cur.Gain, gn = (float)Math.Sin(t * Math.PI / 2) * _next.Gain;
                        buffer[offset + done + k] = (k < r1 ? _t1[k] * gc : 0) + (k < r2 ? _t2[k] * gn : 0);
                        buffer[offset + done + k + 1] = (k + 1 < r1 ? _t1[k + 1] * gc : 0) + (k + 1 < r2 ? _t2[k + 1] * gn : 0);
                    }
                    _cur.Pos += r1 / 2; _next.Pos += r2 / 2; _fadePos += n / 2; done += n;
                    if (_fadePos >= _fadeLen || r1 == 0) Promote();
                    continue;
                }
                int r = _cur.Read(buffer, offset + done, want);
                if (_cur.Gain != 1f) for (int q = 0; q < r; q++) buffer[offset + done + q] *= _cur.Gain;
                if (_next != null && TrimSilence?.Invoke() == true && r > 0)
                {
                    bool silent = true; for (int q = 0; q < r; q++) if (Math.Abs(buffer[offset + done + q]) > 0.0018f * Math.Max(_cur.Gain, 0.01f)) { silent = false; break; }
                    _cur.SilentRun = silent ? _cur.SilentRun + r / 2 : 0;
                    if (_cur.SilentRun > _rate / 3) { done += r; Promote(); continue; } // track ended in silence → next track starts now
                }
                _cur.Pos += r / 2; done += r;
                if (r < want)
                {
                    if (_next != null) Promote();
                    else
                    {
                        var ended = _cur; _trash.Add(ended); _cur = null; State = PlayerState.Stopped;
                        _events.Enqueue(() => { StateChanged?.Invoke(); QueueFinished?.Invoke(); });
                        break;
                    }
                }
            }
        }
        return count;
    }

    // call under _gate
    private void Promote()
    {
        _history.Push(_cur!.Path); _trash.Add(_cur!);
        _cur = _next; _next = null; _fading = false; _noNext = false; _preparing = false;
        var p = _cur!.Path;
        _events.Enqueue(() => { TrackStarted?.Invoke(p); StateChanged?.Invoke(); });
    }

    private int _ticking;
    private void ControlTick()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
        try
        {
            List<Deck> trash; List<Action> evs;
            lock (_gate) { trash = _trash.ToList(); _trash.Clear(); evs = _events.ToList(); _events.Clear(); }
            foreach (var d in trash) try { d.Dispose(); } catch { }
            foreach (var a in evs) try { a(); } catch (Exception ex) { Log.Error("Music", "Event handler failed", ex); }

            bool prepare;
            lock (_gate)
            {
                prepare = State == PlayerState.Playing && _cur != null && _next == null && !_noNext && !_preparing
                          && (_cur.Src.TotalFrames <= 0 || _cur.Src.TotalFrames - _cur.Pos <= (long)((_crossfadeSec() + 10) * _rate));
                if (prepare) _preparing = true;
            }
            if (prepare)
            {
                string? p = null; try { p = NextProvider?.Invoke(); } catch (Exception ex) { Log.Error("Music", "Next provider failed", ex); }
                if (p == null) { lock (_gate) { _noNext = true; _preparing = false; } return; }
                Deck? d = null;
                try { d = Open(p); }
                catch (Exception ex) { Log.Error("Music", $"Cannot open {Path.GetFileName(p)} (skipped)", ex); TrackFailed?.Invoke(p, ex); lock (_gate) _preparing = false; return; }
                lock (_gate)
                {
                    if (_cur == null || _next != null) _trash.Add(d); else _next = d;
                    _preparing = false;
                }
            }
        }
        catch (Exception ex) { Log.Error("Music", "Control tick failed", ex); }
        finally { Interlocked.Exchange(ref _ticking, 0); }
    }

    public void Dispose()
    {
        _control.Dispose();
        lock (_gate) { _cur?.Dispose(); _next?.Dispose(); foreach (var t in _trash) t.Dispose(); _cur = _next = null; _trash.Clear(); }
    }
}

/// <summary>Plays overlapping effect voices (jingles, soundboard) on the effects channel.</summary>
public sealed class SfxChannel : ISampleProvider
{
    private sealed class Voice
    {
        public int Id; public string Path = ""; public AudioFileSource Src = null!; public float Vol; public bool Loop; public Action? OnEnd; public bool Stopped;
    }
    private readonly object _gate = new();
    private readonly List<Voice> _voices = new();
    private readonly int _rate; private int _nextId;
    private float[] _tmp = new float[8192];
    private readonly ConcurrentQueueOfActions _ended = new();
    public WaveFormat WaveFormat { get; }
    public bool HasVoices { get { lock (_gate) return _voices.Count > 0; } }
    public SfxChannel(int rate) { _rate = rate; WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, 2); }

    /// <summary>Starts a voice. Returns its id, or -1 when the file cannot be opened.</summary>
    public int Play(string path, float volume, bool loop, Action? onEnd = null)
    {
        AudioFileSource src;
        try { src = new AudioFileSource(path, _rate); }
        catch (Exception ex) { Log.Error("Sfx", $"Cannot open {Path.GetFileName(path)}", ex); return -1; }
        var v = new Voice { Id = Interlocked.Increment(ref _nextId), Path = path, Src = src, Vol = volume, Loop = loop, OnEnd = onEnd };
        lock (_gate) _voices.Add(v);
        return v.Id;
    }

    public void Stop(int id) { lock (_gate) foreach (var v in _voices.Where(x => x.Id == id)) v.Stopped = true; }
    public void StopAll() { lock (_gate) foreach (var v in _voices) v.Stopped = true; }
    public bool IsPlaying(int id) { lock (_gate) return _voices.Any(v => v.Id == id && !v.Stopped); }

    public int Read(float[] buffer, int offset, int count)
    {
        Array.Clear(buffer, offset, count);
        List<Voice>? finished = null;
        lock (_gate)
        {
            if (_voices.Count == 0) return count;
            if (_tmp.Length < count) _tmp = new float[count];
            foreach (var v in _voices)
            {
                if (v.Stopped) { (finished ??= new()).Add(v); continue; }
                int done = 0, guard = 0;
                while (done < count && guard++ < 4)
                {
                    int r = v.Src.Samples.Read(_tmp, 0, count - done);
                    for (int i = 0; i < r; i++) buffer[offset + done + i] += _tmp[i] * v.Vol;
                    done += r;
                    if (r == 0 || done < count)
                    {
                        if (v.Loop)
                        {
                            try { var path = v.Path; v.Src.Dispose(); v.Src = new AudioFileSource(path, _rate); continue; }
                            catch { v.Loop = false; }
                        }
                        (finished ??= new()).Add(v); break;
                    }
                }
            }
            if (finished != null) foreach (var v in finished) { _voices.Remove(v); if (v.OnEnd != null) _ended.Enqueue(v.OnEnd); }
        }
        if (finished != null)
        {
            var list = finished;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                foreach (var v in list) try { v.Src.Dispose(); } catch { }
                while (_ended.TryDequeue(out var a)) try { a(); } catch (Exception ex) { Log.Error("Sfx", "End handler failed", ex); }
            });
        }
        return count;
    }

    private sealed class ConcurrentQueueOfActions
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _q = new();
        public void Enqueue(Action a) => _q.Enqueue(a);
        public bool TryDequeue(out Action a) => _q.TryDequeue(out a!);
    }
}
