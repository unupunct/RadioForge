using System.Threading.Channels;
using NAudio.Dsp;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace RadioForge.Core.Audio;

public static class Db
{
    public static float ToLinear(double db) => (float)Math.Pow(10, db / 20.0);
    public static double ToDb(double lin) => lin <= 1e-6 ? -120 : 20 * Math.Log10(lin);
}

/// <summary>Peak / RMS / peak-hold meter for interleaved stereo float audio. Written by the audio thread, read by the UI.</summary>
public sealed class LevelMeter
{
    private readonly int _rate;
    private int _holdLeftL, _holdLeftR;
    public volatile float PeakL, PeakR, RmsL, RmsR, HoldL, HoldR;
    public int ClipCount;
    public volatile bool Clipped;

    public LevelMeter(int sampleRate = 44100) { _rate = sampleRate; }

    public void Process(float[] b, int off, int count)
    {
        float pl = 0, pr = 0; double sl = 0, sr = 0;
        for (int i = off; i + 1 < off + count; i += 2)
        {
            float l = Math.Abs(b[i]), r = Math.Abs(b[i + 1]);
            if (l > pl) pl = l; if (r > pr) pr = r;
            sl += l * l; sr += r * r;
        }
        int frames = Math.Max(1, count / 2);
        float fall = (float)Math.Exp(-frames / (_rate * 0.25));
        PeakL = Math.Max(pl, PeakL * fall); PeakR = Math.Max(pr, PeakR * fall);
        float rl = (float)Math.Sqrt(sl / frames), rr = (float)Math.Sqrt(sr / frames);
        RmsL = RmsL * 0.6f + rl * 0.4f; RmsR = RmsR * 0.6f + rr * 0.4f;
        // peak hold: 1.5 s then fall
        if (pl >= HoldL) { HoldL = pl; _holdLeftL = (int)(_rate * 1.5); } else if ((_holdLeftL -= frames) < 0) HoldL *= fall;
        if (pr >= HoldR) { HoldR = pr; _holdLeftR = (int)(_rate * 1.5); } else if ((_holdLeftR -= frames) < 0) HoldR *= fall;
        if (pl >= 0.999f || pr >= 0.999f) { Clipped = true; Interlocked.Increment(ref ClipCount); }
    }
    public void ResetClip() => Clipped = false;
    public float Peak => Math.Max(PeakL, PeakR);
    public float Rms => Math.Max(RmsL, RmsR);
}

/// <summary>Stereo-linked peak limiter with instant attack and exponential release.</summary>
public sealed class Limiter
{
    private float _gain = 1f;
    public bool Active { get; private set; }
    public void Process(float[] b, int off, int count, float ceiling, int rate, double releaseMs = 120)
    {
        float rel = (float)(1 - Math.Exp(-1.0 / (rate * releaseMs / 1000.0)));
        bool limited = false;
        for (int i = off; i + 1 < off + count; i += 2)
        {
            float peak = Math.Max(Math.Abs(b[i]), Math.Abs(b[i + 1]));
            float need = peak * _gain > ceiling ? ceiling / peak : 1f;
            if (need < _gain) { _gain = need; limited = true; } else _gain += (1f - _gain) * rel;
            b[i] *= _gain; b[i + 1] *= _gain;
        }
        Active = limited;
    }
}

/// <summary>Block of interleaved stereo float samples published on a bus.</summary>
public sealed record AudioBlock(float[] Data, int Count);

public sealed class BusSubscription : IDisposable
{
    private readonly AudioBus _bus;
    internal readonly Channel<AudioBlock> Chan = Channel.CreateBounded<AudioBlock>(new BoundedChannelOptions(300) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    public ChannelReader<AudioBlock> Reader => Chan.Reader;
    internal BusSubscription(AudioBus bus) { _bus = bus; }
    public void Dispose() { _bus.Remove(this); Chan.Writer.TryComplete(); }
}

/// <summary>Fan-out of audio blocks from the audio thread to consumers (encoders, recorder). Publishing never blocks.</summary>
public sealed class AudioBus
{
    private BusSubscription[] _subs = Array.Empty<BusSubscription>();
    private readonly object _gate = new();
    public bool HasSubscribers => _subs.Length > 0;
    public int SampleRate { get; }
    public AudioBus(int rate) { SampleRate = rate; }

    public BusSubscription Subscribe() { var s = new BusSubscription(this); lock (_gate) _subs = _subs.Append(s).ToArray(); return s; }
    internal void Remove(BusSubscription s) { lock (_gate) _subs = _subs.Where(x => x != s).ToArray(); }

    public void Publish(float[] src, int off, int count)
    {
        var subs = _subs; if (subs.Length == 0) return;
        var copy = new float[count]; Array.Copy(src, off, copy, 0, count);
        var blk = new AudioBlock(copy, count);
        foreach (var s in subs) s.Chan.Writer.TryWrite(blk);
    }
}

/// <summary>Converts pushed stereo blocks to another sample rate / channel count (used per encoder).</summary>
public sealed class PushConverter
{
    private readonly BufferedWaveProvider? _buf;
    private readonly ISampleProvider? _chain;
    private readonly float[] _tmp = new float[8192];
    public int OutChannels { get; }
    public PushConverter(int inRate, int outRate, int outChannels)
    {
        OutChannels = outChannels;
        if (inRate == outRate && outChannels == 2) return;
        _buf = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(inRate, 2)) { ReadFully = false, DiscardOnBufferOverflow = true, BufferDuration = TimeSpan.FromSeconds(3) };
        ISampleProvider sp = _buf.ToSampleProvider();
        if (outChannels == 1) sp = new StereoToMonoSampleProvider(sp) { LeftVolume = 0.5f, RightVolume = 0.5f };
        if (inRate != outRate) sp = new WdlResamplingSampleProvider(sp, outRate);
        _chain = sp;
    }

    public IEnumerable<(float[] data, int count)> Process(float[] block, int count)
    {
        if (_buf == null) { yield return (block, count); yield break; }
        var bytes = new byte[count * 4]; Buffer.BlockCopy(block, 0, bytes, 0, bytes.Length);
        _buf.AddSamples(bytes, 0, bytes.Length);
        int n;
        while ((n = _chain!.Read(_tmp, 0, _tmp.Length)) > 0)
        {
            var o = new float[n]; Array.Copy(_tmp, o, n);
            yield return (o, n);
            if (n < _tmp.Length) break;
        }
    }

    public static byte[] ToPcm16(float[] d, int count)
    {
        var o = new byte[count * 2];
        for (int i = 0; i < count; i++)
        {
            var v = (short)Math.Clamp((int)(d[i] * 32767f), short.MinValue, short.MaxValue);
            o[i * 2] = (byte)v; o[i * 2 + 1] = (byte)(v >> 8);
        }
        return o;
    }
}

/// <summary>Taps the program for visualisation. FFT is computed on the consumer (UI) side.</summary>
public sealed class SpectrumAnalyzer
{
    private const int Ring = 8192, Wave = 4096;
    private readonly float[] _mono = new float[Ring], _l = new float[Wave], _r = new float[Wave];
    private int _pos, _wpos;
    private readonly int _rate;
    public SpectrumAnalyzer(int rate) { _rate = rate; }

    public void Push(float[] b, int off, int count)
    {
        for (int i = off; i + 1 < off + count; i += 2)
        {
            _mono[_pos] = (b[i] + b[i + 1]) * 0.5f; _pos = (_pos + 1) & (Ring - 1);
            _l[_wpos] = b[i]; _r[_wpos] = b[i + 1]; _wpos = (_wpos + 1) % Wave;
        }
    }

    /// <summary>Log-spaced band magnitudes 0..1 (normalised dB from -80 to 0).</summary>
    public void GetBands(float[] bands)
    {
        const int N = 2048;
        var c = new Complex[N];
        int start = (_pos - N + Ring) & (Ring - 1);
        for (int i = 0; i < N; i++)
        {
            float w = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (N - 1)));
            c[i].X = _mono[(start + i) & (Ring - 1)] * w; c[i].Y = 0;
        }
        FastFourierTransform.FFT(true, 11, c);
        double binHz = _rate / (double)N;
        for (int b = 0; b < bands.Length; b++)
        {
            double f0 = 30 * Math.Pow(16000.0 / 30, b / (double)bands.Length), f1 = 30 * Math.Pow(16000.0 / 30, (b + 1) / (double)bands.Length);
            int i0 = Math.Max(1, (int)(f0 / binHz)), i1 = Math.Min(N / 2 - 1, Math.Max(i0, (int)(f1 / binHz)));
            double m = 0;
            for (int i = i0; i <= i1; i++) m = Math.Max(m, Math.Sqrt(c[i].X * c[i].X + c[i].Y * c[i].Y));
            double db = Db.ToDb(m * 2.0);
            bands[b] = (float)Math.Clamp((db + 80) / 80, 0, 1);
        }
    }

    public void GetWave(float[] l, float[] r)
    {
        int n = Math.Min(l.Length, Wave);
        int start = (_wpos - n + Wave) % Wave;
        for (int i = 0; i < n; i++) { l[i] = _l[(start + i) % Wave]; r[i] = _r[(start + i) % Wave]; }
    }
}

/// <summary>Lightweight Freeverb-style reverb (mono in, mono out).</summary>
public sealed class Reverb
{
    private sealed class Comb
    {
        private readonly float[] _b; private int _i; private float _store;
        public Comb(int size) { _b = new float[size]; }
        public float Process(float x, float feedback, float damp)
        {
            float o = _b[_i]; _store = o * (1 - damp) + _store * damp; _b[_i] = x + _store * feedback;
            if (++_i >= _b.Length) _i = 0; return o;
        }
    }
    private sealed class AllPass
    {
        private readonly float[] _b; private int _i;
        public AllPass(int size) { _b = new float[size]; }
        public float Process(float x) { float bo = _b[_i]; float o = -x + bo; _b[_i] = x + bo * 0.5f; if (++_i >= _b.Length) _i = 0; return o; }
    }
    private readonly Comb[] _combs; private readonly AllPass[] _aps;
    public Reverb(int rate)
    {
        double s = rate / 44100.0;
        _combs = new[] { 1116, 1188, 1277, 1356, 1422, 1491 }.Select(n => new Comb((int)(n * s))).ToArray();
        _aps = new[] { 556, 441, 341 }.Select(n => new AllPass((int)(n * s))).ToArray();
    }
    public float Process(float x)
    {
        float inp = x * 0.03f, sum = 0;
        foreach (var c in _combs) sum += c.Process(inp, 0.84f, 0.2f);
        foreach (var a in _aps) sum = a.Process(sum);
        return sum * 6f;
    }
}

/// <summary>Microphone chain: gain → gate → EQ → compressor → echo → reverb → mute / push-to-talk.</summary>
public sealed class MicProcessor : ISampleProvider
{
    private ISampleProvider _src;
    private readonly MicSettings _s;
    /// <summary>Input of the chain (the capture device; replaceable for tests or file-based sources).</summary>
    public ISampleProvider Source { get => _src; set => _src = value; }
    private readonly int _rate;
    private float _gateEnv, _gateGain, _compEnv, _compGainDb;
    private readonly BiQuadFilter[] _low = new BiQuadFilter[2], _mid = new BiQuadFilter[2], _high = new BiQuadFilter[2];
    private double _cLow = double.NaN, _cMid = double.NaN, _cHigh = double.NaN;
    private readonly float[][] _echo; private int _echoPos;
    private readonly Reverb _reverb;
    public volatile bool PushToTalkDown;
    private volatile float _level;
    public WaveFormat WaveFormat { get; }

    /// <summary>Post-chain RMS level (linear). Zero when muted. Drives ducking and the mic meter.</summary>
    public float Level => _level;
    public bool Gated { get; private set; }
    public bool Open => !_s.Muted && (!_s.PushToTalk || PushToTalkDown);

    public MicProcessor(ISampleProvider source, MicSettings settings, int rate)
    {
        _src = source; _s = settings; _rate = rate; _gateGain = 0;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, 2);
        _echo = new[] { new float[rate * 2], new float[rate * 2] };
        _reverb = new Reverb(rate);
    }

    private void UpdateEq()
    {
        if (_cLow == _s.EqLowDb && _cMid == _s.EqMidDb && _cHigh == _s.EqHighDb) return;
        _cLow = _s.EqLowDb; _cMid = _s.EqMidDb; _cHigh = _s.EqHighDb;
        for (int c = 0; c < 2; c++)
        {
            _low[c] = BiQuadFilter.LowShelf(_rate, 120, 1f, (float)_cLow);
            _mid[c] = BiQuadFilter.PeakingEQ(_rate, 1500, 0.8f, (float)_cMid);
            _high[c] = BiQuadFilter.HighShelf(_rate, 6000, 1f, (float)_cHigh);
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int n = _src.Read(buffer, offset, count);
        if (n < count) Array.Clear(buffer, offset + n, count - n);
        UpdateEq();
        bool open = Open;
        float gain = Db.ToLinear(_s.GainDb);
        float gThr = Db.ToLinear(_s.GateThresholdDb);
        float gUp = (float)(1 - Math.Exp(-1.0 / (_rate * 0.005))), gDn = (float)(1 - Math.Exp(-1.0 / (_rate * 0.12))), envRel = (float)(1 - Math.Exp(-1.0 / (_rate * 0.05)));
        float cAtt = (float)(1 - Math.Exp(-1.0 / (_rate * 0.010))), cRel = (float)(1 - Math.Exp(-1.0 / (_rate * 0.15)));
        float echoMix = (float)_s.EchoMix, revMix = (float)_s.ReverbMix;
        int echoLen = Math.Clamp((int)(_s.EchoDelayMs / 1000.0 * _rate), 100, _rate * 2 - 1);
        double sumSq = 0; int frames = 0;
        for (int i = offset; i + 1 < offset + count; i += 2)
        {
            if (!open) { buffer[i] = buffer[i + 1] = 0; _gateGain = 0; frames++; continue; }
            float l = buffer[i] * gain, r = buffer[i + 1] * gain;
            float det = Math.Max(Math.Abs(l), Math.Abs(r));
            // gate
            _gateEnv = det > _gateEnv ? det : _gateEnv + (det - _gateEnv) * envRel;
            if (_s.GateEnabled)
            {
                float target = _gateEnv > gThr ? 1f : 0f;
                _gateGain += (target - _gateGain) * (target > _gateGain ? gUp : gDn);
                l *= _gateGain; r *= _gateGain;
            }
            else _gateGain = 1;
            if (_s.EqEnabled) { l = _high[0].Transform(_mid[0].Transform(_low[0].Transform(l))); r = _high[1].Transform(_mid[1].Transform(_low[1].Transform(r))); }
            // compressor
            if (_s.CompressorEnabled)
            {
                float d2 = Math.Max(Math.Abs(l), Math.Abs(r));
                _compEnv = d2 > _compEnv ? _compEnv + (d2 - _compEnv) * cAtt : _compEnv + (d2 - _compEnv) * cRel;
                double envDb = Db.ToDb(_compEnv);
                double targetDb = envDb > _s.CompThresholdDb ? -(envDb - _s.CompThresholdDb) * (1 - 1 / Math.Max(1, _s.CompRatio)) : 0;
                _compGainDb += (float)(targetDb - _compGainDb) * (targetDb < _compGainDb ? cAtt : cRel);
                float g = Db.ToLinear(_compGainDb + _s.CompMakeupDb);
                l *= g; r *= g;
            }
            if (echoMix > 0.001f)
            {
                int rp = (_echoPos - echoLen + _echo[0].Length) % _echo[0].Length;
                float el = _echo[0][rp], er = _echo[1][rp];
                _echo[0][_echoPos] = l + el * 0.4f; _echo[1][_echoPos] = r + er * 0.4f;
                l += el * echoMix; r += er * echoMix;
                _echoPos = (_echoPos + 1) % _echo[0].Length;
            }
            if (revMix > 0.001f) { float rv = _reverb.Process((l + r) * 0.5f) * revMix; l += rv; r += rv; }
            buffer[i] = l; buffer[i + 1] = r;
            sumSq += (l * l + r * r) * 0.5; frames++;
        }
        float rms = frames > 0 ? (float)Math.Sqrt(sumSq / frames) : 0;
        _level = open ? _level * 0.5f + rms * 0.5f : 0;
        Gated = _s.GateEnabled && _gateGain < 0.5f;
        return count;
    }
}
