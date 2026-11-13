using NAudio.Wave;
using RadioForge.Core;
using RadioForge.Core.Audio;
using RadioForge.Core.Station;

namespace RadioForge.Tests;

internal static class Cap
{
    /// <summary>Collects the master mix (mono) from the engine bus until disposed.</summary>
    public sealed class Recorder : IDisposable
    {
        private readonly BusSubscription _sub; private readonly Task _t; public readonly List<float> Mono = new(); public readonly int Rate;
        public Recorder(AudioEngine e)
        {
            Rate = e.Rate; _sub = e.MasterBus.Subscribe();
            _t = Task.Run(async () => { await foreach (var b in _sub.Reader.ReadAllAsync()) lock (Mono) for (int i = 0; i + 1 < b.Count; i += 2) Mono.Add((b.Data[i] + b.Data[i + 1]) * 0.5f); });
        }
        public float[] Snapshot() { lock (Mono) return Mono.ToArray(); }
        public void Dispose() { _sub.Dispose(); try { _t.Wait(500); } catch { } }
    }

    public static double Goertzel(float[] x, int start, int len, double freq, int rate)
    {
        double w = 2 * Math.PI * freq / rate, c = 2 * Math.Cos(w), s0, s1 = 0, s2 = 0;
        for (int i = start; i < start + len && i < x.Length; i++) { s0 = x[i] + c * s1 - s2; s2 = s1; s1 = s0; }
        return Math.Sqrt(s1 * s1 + s2 * s2 - c * s1 * s2) * 2.0 / len;
    }

    public static double RmsDb(float[] x, int start, int len) { double s = 0; int n = 0; for (int i = start; i < start + len && i < x.Length; i++) { s += x[i] * x[i]; n++; } return Db.ToDb(Math.Sqrt(s / Math.Max(1, n))); }

    /// <summary>WAV made of segments (seconds, frequency, amplitude); amplitude 0 = silence.</summary>
    public static string Parts(string dir, string name, params (double sec, double hz, double amp)[] parts)
    {
        var path = Path.Combine(dir, name); using var w = new WaveFileWriter(path, new WaveFormat(44100, 16, 2)); long n = 0;
        foreach (var (sec, hz, amp) in parts)
        {
            int cnt = (int)(sec * 44100); var bytes = new byte[cnt * 4];
            for (int i = 0; i < cnt; i++) { var v = (short)(Math.Sin(2 * Math.PI * hz * (n + i) / 44100) * amp * 32767); bytes[i * 4] = bytes[i * 4 + 2] = (byte)v; bytes[i * 4 + 1] = bytes[i * 4 + 3] = (byte)(v >> 8); }
            w.Write(bytes, 0, bytes.Length); n += cnt;
        }
        return path;
    }
}

public class TransitionTests
{
    [Fact]
    public async Task Gapless_playback_has_no_silence_between_tracks_with_zero_crossfade()
    {
        using var t = new TempRoot(); using var st = Station.Open(t);
        st.Profile.Audio.CrossfadeSec = 0; st.Profile.Audio.TrimSilence = false; st.Engine.Settings.MasterVolume = 1;
        var a = t.Wav("a.wav", 3, 440, 44100, 0.4); var b = t.Wav("b.wav", 3, 660, 44100, 0.4);
        var started = new List<string>(); st.Engine.Music.TrackStarted += p => started.Add(Path.GetFileName(p));
        using var rec = new Cap.Recorder(st.Engine);
        st.PlayTracks(new[] { a, b }); st.Profile.AutoDj.Repeat = false;
        Assert.True(await Wait.For(() => started.Count >= 2 && rec.Snapshot().Length > 44100 * 5.5, 12000), string.Join(",", started));
        var x = rec.Snapshot(); int rate = rec.Rate;
        // longest run of near-silence after the first 100 ms and before the end of track 2
        int run = 0, longest = 0; for (int i = rate / 10; i < Math.Min(x.Length, (int)(rate * 5.4)); i++) { if (Math.Abs(x[i]) < 0.003f) { run++; longest = Math.Max(longest, run); } else run = 0; }
        Assert.True(longest < rate * 0.005, $"gap of {longest * 1000.0 / rate:0.0} ms between tracks"); // sine zero-crossings are far shorter than 5 ms
        // both tones are present: 440 Hz in the first part, 660 Hz in the second
        Assert.True(Cap.Goertzel(x, rate / 2, rate, 440, rate) > 0.15); Assert.True(Cap.Goertzel(x, (int)(rate * 4.2), rate, 660, rate) > 0.15);
    }

    [Fact]
    public async Task Crossfade_blends_both_tracks_with_constant_loudness()
    {
        using var t = new TempRoot(); using var st = Station.Open(t);
        st.Profile.Audio.CrossfadeSec = 2; st.Profile.Audio.TrimSilence = false; st.Engine.Settings.MasterVolume = 1;
        var a = t.Wav("a.wav", 6, 440, 44100, 0.4); var b = t.Wav("b.wav", 6, 1320, 44100, 0.4);
        using var rec = new Cap.Recorder(st.Engine); st.Profile.AutoDj.Repeat = false;
        st.PlayTracks(new[] { a, b });
        Assert.True(await Wait.For(() => rec.Snapshot().Length > 44100 * 10.5, 16000));
        var x = rec.Snapshot(); int rate = rec.Rate;
        // transition window = last 2 s of track A: samples 4 s .. 6 s. In its middle both tones are audible.
        int mid = (int)(rate * 4.75);
        double fa = Cap.Goertzel(x, mid, rate / 4, 440, rate), fb = Cap.Goertzel(x, mid, rate / 4, 1320, rate);
        Assert.True(fa > 0.08 && fb > 0.08, $"expected both tones mid-fade, got {fa:0.000} / {fb:0.000}");
        // equal-power: loudness stays within ±3 dB of the steady level through the whole transition
        double steady = Cap.RmsDb(x, rate, rate); double worstDip = 0;
        for (int s0 = (int)(rate * 3.8); s0 < rate * 6.4; s0 += rate / 10) worstDip = Math.Min(worstDip, Cap.RmsDb(x, s0, rate / 10) - steady);
        Assert.True(worstDip > -3.0, $"loudness dip of {worstDip:0.0} dB during the crossfade");
        Assert.True(Cap.Goertzel(x, (int)(rate * 7.5), rate / 2, 1320, rate) > 0.15);
    }

    [Fact]
    public async Task Silence_at_track_edges_is_trimmed_for_a_tight_hand_over()
    {
        using var t = new TempRoot(); using var st = Station.Open(t);
        st.Profile.Audio.CrossfadeSec = 0; st.Engine.Settings.MasterVolume = 1; st.Profile.AutoDj.Repeat = false;
        var a = Cap.Parts(t.Dir, "a.wav", (2.0, 440, 0.4), (2.5, 0, 0));      // tone, then 2.5 s of silence
        var b = Cap.Parts(t.Dir, "b.wav", (1.5, 0, 0), (3.0, 660, 0.4));     // 1.5 s of silence, then tone
        async Task<double> GapSeconds(bool trim)
        {
            st.Profile.Audio.TrimSilence = trim; using var rec = new Cap.Recorder(st.Engine); st.PlayTracks(new[] { a, b });
            Assert.True(await Wait.For(() => rec.Snapshot().Length > 44100 * (trim ? 6.0 : 9.5), 16000)); var x = rec.Snapshot(); int rate = rec.Rate;
            int lastA = -1, firstB = -1; for (int i = 0; i < x.Length; i++) { if (Math.Abs(x[i]) > 0.05f) { if (Cap.Goertzel(x, i, 441, 660, rate) > 0.15 && firstB < 0 && i > rate) firstB = i; else if (firstB < 0) lastA = i; } }
            st.Engine.Music.Stop(); await Task.Delay(300); return (firstB - lastA) / (double)rate;
        }
        double untrimmed = await GapSeconds(false), trimmed = await GapSeconds(true);
        Assert.True(untrimmed > 3.5, "untrimmed gap " + untrimmed); // 2.5 s + 1.5 s of silence
        Assert.True(trimmed < 0.9, $"trimmed gap {trimmed:0.00} s (untrimmed {untrimmed:0.00} s)");
    }
}

public class RadioSoundTests
{
    private static float[] Sine(double hz, double amp, double sec, int rate = 44100) { var b = new float[(int)(sec * rate) * 2]; for (int i = 0; i < b.Length / 2; i++) b[2 * i] = b[2 * i + 1] = (float)(Math.Sin(2 * Math.PI * hz * i / rate) * amp); return b; }
    private static double Tail(float[] b) { double s = 0; int n = 0; for (int i = b.Length * 3 / 4; i < b.Length; i += 2) { s += b[i] * b[i]; n++; } return Db.ToDb(Math.Sqrt(s / n)); }
    private static float[] Run(BroadcastProcessor p, float[] x, RadioProcessing m) { var y = (float[])x.Clone(); for (int o = 0; o < y.Length; o += 4096) p.Process(y, o, Math.Min(4096, y.Length - o), m); return y; }

    [Fact]
    public void Leveler_brings_quiet_and_loud_material_to_a_similar_level()
    {
        foreach (var mode in new[] { RadioProcessing.Gentle, RadioProcessing.Radio, RadioProcessing.Loud })
        {
            var quiet = Tail(Run(new BroadcastProcessor(44100), Sine(440, 0.05, 14), mode)); var loud = Tail(Run(new BroadcastProcessor(44100), Sine(440, 0.7, 14), mode));
            double inputDiff = Db.ToDb(0.7) - Db.ToDb(0.05), outDiff = loud - quiet;
            Assert.True(outDiff < inputDiff * (mode == RadioProcessing.Gentle ? 0.8 : 0.5), $"{mode}: input differs {inputDiff:0.0} dB, output still {outDiff:0.0} dB");
        }
    }

    [Fact]
    public void Rumble_is_removed_silence_stays_silent_off_is_transparent_and_output_never_exceeds_full_scale()
    {
        var rumble = Run(new BroadcastProcessor(44100), Sine(8, 0.4, 6), RadioProcessing.Gentle);
        Assert.True(Tail(rumble) < Db.ToDb(0.4 * 0.707) - 18, "8 Hz rumble only reduced to " + Tail(rumble));
        var silence = Run(new BroadcastProcessor(44100), new float[44100 * 2 * 5], RadioProcessing.Loud); Assert.All(silence, v => Assert.True(Math.Abs(v) < 1e-6f)); // no noise boost in silence
        var src = Sine(1000, 0.3, 1); var off = Run(new BroadcastProcessor(44100), src, RadioProcessing.Off); Assert.Equal(src, off);
        var hot = Run(new BroadcastProcessor(44100), Sine(1000, 1.6, 3), RadioProcessing.Loud); Assert.True(hot.Max(Math.Abs) <= 1.0001f, "peak " + hot.Max(Math.Abs));
        var p = new BroadcastProcessor(44100); Run(p, Sine(440, 0.8, 8), RadioProcessing.Radio); Assert.True(p.AgcGainDb < -3, "AGC should be turning a hot track down: " + p.AgcGainDb);
        var q = new BroadcastProcessor(44100); Run(q, Sine(440, 0.03, 8), RadioProcessing.Radio); Assert.True(q.AgcGainDb > 3, "AGC should be lifting a quiet track: " + q.AgcGainDb);
    }

    [Fact]
    public async Task Engine_levels_tracks_on_air_when_radio_sound_is_enabled()
    {
        using var t = new TempRoot(); using var st = Station.Open(t);
        st.Engine.Settings.MasterVolume = 1; st.Profile.Audio.CrossfadeSec = 0; st.Profile.Audio.NormalizeLoudness = false;
        var quiet = t.Wav("quiet.wav", 14, 440, 44100, 0.06); var loud = t.Wav("loud.wav", 14, 440, 44100, 0.6);
        async Task<double> Level(string path)
        {
            st.Engine.Music.Play(path); await Task.Delay(9000); var v = new List<double>(); for (int i = 0; i < 8; i++) { v.Add(st.Engine.MasterMeter.Rms); await Task.Delay(100); }
            return Db.ToDb(v.Average());
        }
        st.Profile.Audio.RadioProcessing = RadioProcessing.Off; double rawDiff = await Level(loud) - await Level(quiet);
        st.Profile.Audio.RadioProcessing = RadioProcessing.Radio; double procDiff = await Level(loud) - await Level(quiet);
        Assert.True(rawDiff > 15, "raw " + rawDiff); Assert.True(procDiff < 6, $"with radio sound the tracks still differ by {procDiff:0.0} dB (raw {rawDiff:0.0})");
    }
}
