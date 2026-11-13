using NAudio.Dsp;

namespace RadioForge.Core.Audio;

public enum RadioProcessing { Off, Gentle, Radio, Loud }

/// <summary>
/// "Radio sound": rumble filter → slow automatic gain control (levels every track to the same loudness) → 3-band compressor
/// (even, dense, clean sound) → soft safety clipper. The master limiter follows. Zero latency (IIR only).
/// </summary>
public sealed class BroadcastProcessor
{
    private sealed record Preset(double AgcTargetDb, double AgcRangeDb, double[] Ratio, double[] ThresholdDb, double[] MakeupDb);
    private static readonly Dictionary<RadioProcessing, Preset> Presets = new()
    {
        [RadioProcessing.Gentle] = new(-20, 6, new[] { 1.6, 1.5, 1.6 }, new[] { -24.0, -22.0, -26.0 }, new[] { 0.5, 0.5, 0.5 }),
        [RadioProcessing.Radio] = new(-18, 12, new[] { 2.8, 2.5, 3.0 }, new[] { -22.0, -20.0, -24.0 }, new[] { 2.0, 1.5, 2.0 }),
        [RadioProcessing.Loud] = new(-15, 14, new[] { 4.0, 3.5, 4.5 }, new[] { -24.0, -22.0, -26.0 }, new[] { 3.5, 3.0, 3.5 }),
    };
    private static readonly double[] Attack = { 0.030, 0.015, 0.005 }, Release = { 0.250, 0.150, 0.100 };

    private readonly int _rate;
    private readonly BiQuadFilter[] _hpf = new BiQuadFilter[2];
    private readonly BiQuadFilter[][] _lp1, _lp2; // [channel][stage] — cascaded 2nd-order low-passes (24 dB/oct)
    private readonly float[] _env = new float[3]; private readonly float[] _grDb = new float[3];
    private double _agcPower, _agcGainDb;
    public float GainReductionDb { get; private set; }   // worst band reduction (for the UI meter)
    public float AgcGainDb => (float)_agcGainDb;          // + = boosting a quiet track, − = turning a loud one down

    public BroadcastProcessor(int rate)
    {
        _rate = rate;
        for (int c = 0; c < 2; c++) _hpf[c] = BiQuadFilter.HighPassFilter(rate, 30, 0.707f);
        _lp1 = new[] { Chain(160), Chain(160) }; _lp2 = new[] { Chain(2500), Chain(2500) };
    }

    private BiQuadFilter[] Chain(float hz) => new[] { BiQuadFilter.LowPassFilter(_rate, hz, 0.707f), BiQuadFilter.LowPassFilter(_rate, hz, 0.707f) };

    public void Process(float[] b, int off, int count, RadioProcessing mode)
    {
        if (mode == RadioProcessing.Off) { GainReductionDb = 0; return; }
        var p = Presets[mode]; int frames = count / 2;
        float agcA = (float)(1 - Math.Exp(-1.0 / (_rate * 0.4))), agcR = (float)(1 - Math.Exp(-1.0 / (_rate * 4.0)));
        float powCoef = (float)(1 - Math.Exp(-1.0 / (_rate * 0.6)));
        var cA = Attack.Select(t => (float)(1 - Math.Exp(-1.0 / (_rate * t)))).ToArray(); var cR = Release.Select(t => (float)(1 - Math.Exp(-1.0 / (_rate * t)))).ToArray();
        var thr = p.ThresholdDb; var mk = p.MakeupDb.Select(m => Db.ToLinear(m)).ToArray();
        float worst = 0;
        for (int i = off; i + 1 < off + count; i += 2)
        {
            float l = _hpf[0].Transform(b[i]), r = _hpf[1].Transform(b[i + 1]);
            // ---- AGC (slow leveler) measured on the program power; silence is left alone (no pumping up of noise)
            double inst = (l * l + r * r) * 0.5; _agcPower += (inst - _agcPower) * powCoef;
            double levelDb = Db.ToDb(Math.Sqrt(_agcPower));
            if (levelDb > -55)
            {
                double want = Math.Clamp(p.AgcTargetDb - levelDb, -p.AgcRangeDb, p.AgcRangeDb);
                _agcGainDb += (want - _agcGainDb) * (want < _agcGainDb ? agcA : agcR);
            }
            float g = Db.ToLinear(_agcGainDb); l *= g; r *= g;
            // ---- split into 3 bands (perfect reconstruction: low + mid + high == input)
            float lowL = Lp(_lp1[0], l), lowR = Lp(_lp1[1], r); float restL = l - lowL, restR = r - lowR;
            float midL = Lp(_lp2[0], restL), midR = Lp(_lp2[1], restR); float highL = restL - midL, highR = restR - midR;
            float oL = 0, oR = 0;
            for (int k = 0; k < 3; k++)
            {
                float bl = k == 0 ? lowL : k == 1 ? midL : highL, br = k == 0 ? lowR : k == 1 ? midR : highR;
                float det = Math.Max(Math.Abs(bl), Math.Abs(br));
                _env[k] += (det - _env[k]) * (det > _env[k] ? cA[k] : cR[k]);
                double ed = Db.ToDb(_env[k]); double red = ed > thr[k] ? -(ed - thr[k]) * (1 - 1 / p.Ratio[k]) : 0;
                _grDb[k] += (float)(red - _grDb[k]) * (red < _grDb[k] ? cA[k] : cR[k]);
                float gk = Db.ToLinear(_grDb[k]) * (float)mk[k]; oL += bl * gk; oR += br * gk;
                if (-_grDb[k] > worst) worst = -_grDb[k];
            }
            b[i] = Soft(oL); b[i + 1] = Soft(oR);
        }
        GainReductionDb = GainReductionDb * 0.8f + worst * 0.2f;
    }

    private static float Lp(BiQuadFilter[] chain, float x) { foreach (var f in chain) x = f.Transform(x); return x; }
    /// <summary>Transparent below 0.8; rounds peaks smoothly instead of hard-clipping.</summary>
    private static float Soft(float x) { float a = Math.Abs(x); if (a < 0.8f) return x; float s = Math.Sign(x); return s * (0.8f + 0.2f * (float)Math.Tanh((a - 0.8f) / 0.2f)); }
}
