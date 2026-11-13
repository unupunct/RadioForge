using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RadioForge.Core;
using RadioForge.Core.Audio;
using RadioForge.Core.Streaming;

namespace RadioForge.App.Controls;

/// <summary>Single UI animation clock (~30 fps) shared by all meters and visualisers. Never drives audio.</summary>
public static class UiClock
{
    private static readonly DispatcherTimer Timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
    public static event Action? Tick;
    static UiClock() { Timer.Tick += (_, _) => Tick?.Invoke(); Timer.Start(); }
    public static void Touch() { }
}

public static class Palette
{
    public static Color Parse(string hex, Color fallback) { try { return (Color)ColorConverter.ConvertFromString(hex); } catch { return fallback; } }
    public static Color Lerp(Color a, Color b, double t) =>
        Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
}

/// <summary>Stereo level bar with peak hold and clip indicator (dB scale).</summary>
public sealed class LevelBar : FrameworkElement
{
    public static readonly DependencyProperty MeterProperty = DependencyProperty.Register(nameof(Meter), typeof(LevelMeter), typeof(LevelBar), new PropertyMetadata(null, (d, e) => ((LevelBar)d).Hook()));
    public static readonly DependencyProperty IsHorizontalProperty = DependencyProperty.Register(nameof(IsHorizontal), typeof(bool), typeof(LevelBar), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public LevelMeter? Meter { get => (LevelMeter?)GetValue(MeterProperty); set => SetValue(MeterProperty, value); }
    public bool IsHorizontal { get => (bool)GetValue(IsHorizontalProperty); set => SetValue(IsHorizontalProperty, value); }
    private bool _hooked; private float _l, _r, _hl, _hr;

    public LevelBar() { Loaded += (_, _) => Hook(); Unloaded += (_, _) => Unhook(); MouseLeftButtonDown += (_, _) => Meter?.ResetClip(); ToolTip = "Click to clear the clip indicator"; }
    private void Hook() { if (!_hooked && IsLoaded) { UiClock.Tick += OnTick; _hooked = true; } }
    private void Unhook() { if (_hooked) { UiClock.Tick -= OnTick; _hooked = false; } }
    private void OnTick() { if (Meter != null && IsVisible) { _l = Meter.PeakL; _r = Meter.PeakR; _hl = Meter.HoldL; _hr = Meter.HoldR; InvalidateVisual(); } }

    private static double Pos(float lin) => Math.Clamp((Db.ToDb(lin) + 60) / 60.0, 0, 1);
    private static Brush Grad(bool horiz)
    {
        var g = new LinearGradientBrush { StartPoint = horiz ? new Point(0, 0) : new Point(0, 1), EndPoint = horiz ? new Point(1, 0) : new Point(0, 0) };
        g.GradientStops.Add(new GradientStop(Color.FromRgb(0, 200, 120), 0)); g.GradientStops.Add(new GradientStop(Color.FromRgb(0, 229, 160), 0.7));
        g.GradientStops.Add(new GradientStop(Color.FromRgb(240, 200, 40), 0.85)); g.GradientStops.Add(new GradientStop(Color.FromRgb(240, 60, 60), 1));
        g.Freeze(); return g;
    }
    private static readonly Brush VGrad = Grad(false), HGrad = Grad(true);
    private static readonly Brush Back = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128));
    private static readonly Brush Hold = new SolidColorBrush(Colors.White), Clip = new SolidColorBrush(Color.FromRgb(240, 60, 60));

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight; if (w < 2 || h < 2) return;
        bool horiz = IsHorizontal;
        var vals = new[] { (_l, _hl), (_r, _hr) };
        double clipSize = horiz ? 0 : 6;
        for (int i = 0; i < 2; i++)
        {
            Rect r = horiz ? new Rect(0, i * (h / 2) + 1, w, h / 2 - 2) : new Rect(i * (w / 2) + 1, clipSize + 2, w / 2 - 2, h - clipSize - 2);
            dc.DrawRectangle(Back, null, r);
            double p = Pos(vals[i].Item1), hp = Pos(vals[i].Item2);
            if (horiz)
            {
                dc.PushClip(new RectangleGeometry(new Rect(r.X, r.Y, r.Width * p, r.Height))); dc.DrawRectangle(HGrad, null, r); dc.Pop();
                dc.DrawRectangle(Hold, null, new Rect(r.X + r.Width * hp - 1.5, r.Y, 2, r.Height));
            }
            else
            {
                double fh = r.Height * p;
                dc.PushClip(new RectangleGeometry(new Rect(r.X, r.Bottom - fh, r.Width, fh))); dc.DrawRectangle(VGrad, null, r); dc.Pop();
                dc.DrawRectangle(Hold, null, new Rect(r.X, r.Bottom - r.Height * hp - 1, r.Width, 2));
            }
        }
        if (!horiz && Meter?.Clipped == true) dc.DrawRectangle(Clip, null, new Rect(0, 0, w, 5));
    }
}

public enum VisMode { Spectrum, Bars3D, Circular, Particles, Oscilloscope, StereoWave, Waterfall, Shapes }

public sealed record VisPreset(string Name, VisMode Mode, string ColorA, string ColorB, string Background)
{
    public override string ToString() => Name;
    public static readonly List<VisPreset> All = Build();
    private static List<VisPreset> Build()
    {
        var pal = new (string n, string a, string b)[] { ("Neon", "#00E5A0", "#00B8FF"), ("Magenta", "#FF3DCB", "#7B5CFF"), ("Fire", "#FF4B1F", "#FFD23F") };
        var bgs = new[] { "Black", "Charcoal", "Midnight" };
        var l = new List<VisPreset>();
        string[] names = { "Classic Spectrum", "3D Frequency Bars", "Circular Spectrum", "Particle Waves", "Oscilloscope", "Stereo Waveform", "Frequency Waterfall", "Geometric Reactor" };
        for (int m = 0; m < 8; m++)
            for (int p = 0; p < 3; p++) l.Add(new VisPreset($"{names[m]} — {pal[p].n}", (VisMode)m, pal[p].a, pal[p].b, bgs[(m + p) % 3]));
        return l;
    }
}

/// <summary>Real-time visualiser drawing from the engine's analyser tap. FFT work happens on the UI side only.</summary>
public sealed class VisualizerControl : FrameworkElement
{
    public static readonly DependencyProperty AnalyzerProperty = DependencyProperty.Register(nameof(Analyzer), typeof(SpectrumAnalyzer), typeof(VisualizerControl));
    public SpectrumAnalyzer? Analyzer { get => (SpectrumAnalyzer?)GetValue(AnalyzerProperty); set => SetValue(AnalyzerProperty, value); }
    public VisPreset Preset { get; set; } = VisPreset.All[0];
    public double Sensitivity { get; set; } = 1.0;
    public string? CustomA { get; set; }
    public string? CustomB { get; set; }
    public string? BackgroundOverride { get; set; }
    public string? BackgroundImage { get; set; }

    private const int Bands = 64;
    private readonly float[] _raw = new float[Bands], _sm = new float[Bands], _l = new float[1024], _r = new float[1024];
    private readonly float[] _peak = new float[Bands];
    private readonly List<float[]> _hist = new();
    private WriteableBitmap? _wf; private readonly uint[] _wfPix = Enumerable.Repeat(0xFF050514u, Bands * 64).ToArray();
    private readonly List<(double x, double y, double vx, double vy, double life, double size)> _parts = new();
    private readonly Random _rnd = new(); private double _phase; private BitmapImage? _img; private string? _imgPath;
    private bool _hooked;

    public VisualizerControl() { Loaded += (_, _) => { if (!_hooked) { UiClock.Tick += Tick; _hooked = true; } }; Unloaded += (_, _) => { UiClock.Tick -= Tick; _hooked = false; }; ClipToBounds = true; }

    private void Tick()
    {
        if (!IsVisible || Analyzer == null) return;
        Analyzer.GetBands(_raw); Analyzer.GetWave(_l, _r);
        for (int i = 0; i < Bands; i++)
        {
            float v = Math.Clamp(_raw[i] * (float)Sensitivity, 0, 1);
            _sm[i] = v > _sm[i] ? _sm[i] + (v - _sm[i]) * 0.6f : _sm[i] * 0.88f;
            _peak[i] = Math.Max(_sm[i], _peak[i] - 0.012f);
        }
        _phase += 0.02;
        InvalidateVisual();
    }

    private Color A => Palette.Parse(CustomA ?? Preset.ColorA, Colors.LimeGreen);
    private Color B => Palette.Parse(CustomB ?? Preset.ColorB, Colors.Cyan);
    private float Bass => _sm.Take(8).Average();
    private float Level => _sm.Average();

    private Brush Bg(Size s)
    {
        var bg = BackgroundOverride ?? Preset.Background;
        return bg switch
        {
            "Black" => Brushes.Black,
            "Midnight" => new LinearGradientBrush(Color.FromRgb(6, 10, 30), Color.FromRgb(14, 4, 40), 90),
            "Transparent" => Brushes.Transparent,
            _ => new LinearGradientBrush(Color.FromRgb(26, 29, 35), Color.FromRgb(8, 9, 12), 90),
        };
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = new Size(ActualWidth, ActualHeight); if (size.Width < 4 || size.Height < 4) return;
        dc.DrawRectangle(Bg(size), null, new Rect(size));
        if (!string.IsNullOrEmpty(BackgroundImage) && System.IO.File.Exists(BackgroundImage))
        {
            if (_imgPath != BackgroundImage) { try { _img = new BitmapImage(new Uri(BackgroundImage)); _imgPath = BackgroundImage; } catch { _img = null; } }
            if (_img != null) { dc.PushOpacity(0.45); dc.DrawImage(_img, new Rect(size)); dc.Pop(); }
        }
        switch (Preset.Mode)
        {
            case VisMode.Spectrum: DrawSpectrum(dc, size); break;
            case VisMode.Bars3D: Draw3D(dc, size); break;
            case VisMode.Circular: DrawCircular(dc, size); break;
            case VisMode.Particles: DrawParticles(dc, size); break;
            case VisMode.Oscilloscope: DrawScope(dc, size); break;
            case VisMode.StereoWave: DrawStereo(dc, size); break;
            case VisMode.Waterfall: DrawWaterfall(dc, size); break;
            case VisMode.Shapes: DrawShapes(dc, size); break;
        }
    }

    private Brush VBrush() { var g = new LinearGradientBrush(A, B, 90) { StartPoint = new Point(0, 1), EndPoint = new Point(0, 0) }; return g; }

    private void DrawSpectrum(DrawingContext dc, Size s)
    {
        double bw = s.Width / Bands; var br = VBrush(); var pk = new SolidColorBrush(Colors.White);
        for (int i = 0; i < Bands; i++)
        {
            double h = _sm[i] * (s.Height - 6);
            dc.DrawRoundedRectangle(br, null, new Rect(i * bw + 1, s.Height - h, Math.Max(1, bw - 2), h), 2, 2);
            dc.DrawRectangle(pk, null, new Rect(i * bw + 1, s.Height - _peak[i] * (s.Height - 6) - 3, Math.Max(1, bw - 2), 2));
        }
    }

    private void Draw3D(DrawingContext dc, Size s)
    {
        _hist.Insert(0, (float[])_sm.Clone()); if (_hist.Count > 10) _hist.RemoveAt(_hist.Count - 1);
        int rows = _hist.Count; double dx = 6, dy = 5, depth = 14;
        for (int row = rows - 1; row >= 0; row--)
        {
            double fade = 1 - row / (double)rows * 0.75; var data = _hist[row];
            double ox = row * dx, oy = row * dy; double bw = (s.Width - 12 * dx) / 32.0;
            for (int i = 0; i < 32; i++)
            {
                float v = (data[i * 2] + data[i * 2 + 1]) / 2; double h = v * (s.Height - 12 * dy - 20);
                double x = 10 + i * bw + ox, y = s.Height - 10 - oy - h; double w = bw * 0.75;
                var c = Palette.Lerp(A, B, i / 32.0); c.A = (byte)(255 * fade);
                var front = new SolidColorBrush(c); var side = new SolidColorBrush(Color.FromArgb(c.A, (byte)(c.R * .55), (byte)(c.G * .55), (byte)(c.B * .55))); var top = new SolidColorBrush(Color.FromArgb(c.A, (byte)Math.Min(255, c.R * 1.25), (byte)Math.Min(255, c.G * 1.25), (byte)Math.Min(255, c.B * 1.25)));
                dc.DrawRectangle(front, null, new Rect(x, y, w, Math.Max(1, h)));
                dc.DrawGeometry(side, null, Poly(new Point(x + w, y), new Point(x + w + depth / 2.5, y - depth / 2.5), new Point(x + w + depth / 2.5, y + h - depth / 2.5), new Point(x + w, y + h)));
                dc.DrawGeometry(top, null, Poly(new Point(x, y), new Point(x + depth / 2.5, y - depth / 2.5), new Point(x + w + depth / 2.5, y - depth / 2.5), new Point(x + w, y)));
            }
        }
    }

    private static Geometry Poly(params Point[] pts)
    {
        var g = new StreamGeometry(); using (var c = g.Open()) { c.BeginFigure(pts[0], true, true); c.PolyLineTo(pts.Skip(1).ToList(), true, false); } g.Freeze(); return g;
    }

    private void DrawCircular(DrawingContext dc, Size s)
    {
        double cx = s.Width / 2, cy = s.Height / 2, r0 = Math.Min(cx, cy) * (0.32 + Bass * 0.08), rmax = Math.Min(cx, cy) * 0.62;
        for (int i = 0; i < Bands * 2; i++)
        {
            int b = i < Bands ? i : Bands * 2 - 1 - i; double ang = i / (double)(Bands * 2) * Math.PI * 2 + _phase * 0.3;
            double len = _sm[b] * rmax; var c = Palette.Lerp(A, B, b / (double)Bands);
            var pen = new Pen(new SolidColorBrush(c), Math.Max(2, s.Width / 220)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawLine(pen, new Point(cx + Math.Cos(ang) * r0, cy + Math.Sin(ang) * r0), new Point(cx + Math.Cos(ang) * (r0 + len + 2), cy + Math.Sin(ang) * (r0 + len + 2)));
        }
        dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(120, A.R, A.G, A.B)), 2), new Point(cx, cy), r0 - 4, r0 - 4);
    }

    private void DrawParticles(DrawingContext dc, Size s)
    {
        int spawn = (int)(Bass * 14 * Sensitivity) + 1;
        for (int i = 0; i < spawn && _parts.Count < 600; i++)
        {
            int band = _rnd.Next(Bands); double x = band / (double)Bands * s.Width;
            _parts.Add((x, s.Height * 0.55 + Math.Sin(_phase * 3 + band) * s.Height * 0.12, (_rnd.NextDouble() - .5) * 1.2, -(1 + _sm[band] * 6 * _rnd.NextDouble()), 1, 2 + _sm[band] * 5));
        }
        for (int i = _parts.Count - 1; i >= 0; i--)
        {
            var p = _parts[i]; p = (p.x + p.vx, p.y + p.vy, p.vx, p.vy + 0.05, p.life - 0.014, p.size);
            if (p.life <= 0 || p.y > s.Height) { _parts.RemoveAt(i); continue; }
            _parts[i] = p;
            var c = Palette.Lerp(A, B, p.x / s.Width); c.A = (byte)(255 * p.life);
            dc.DrawEllipse(new SolidColorBrush(c), null, new Point(p.x, p.y), p.size, p.size);
        }
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(180, A.R, A.G, A.B)), 2);
        var g = new StreamGeometry(); using (var ctx = g.Open())
        {
            for (int i = 0; i < Bands; i++)
            { var pt = new Point(i / (double)(Bands - 1) * s.Width, s.Height * 0.55 + Math.Sin(_phase * 3 + i) * s.Height * 0.12 - _sm[i] * s.Height * 0.25); if (i == 0) ctx.BeginFigure(pt, false, false); else ctx.LineTo(pt, true, true); }
        }
        dc.DrawGeometry(null, pen, g);
    }

    private void DrawWave(DrawingContext dc, float[] data, double y, double h, double w, Color c, int step = 2)
    {
        var g = new StreamGeometry(); using (var ctx = g.Open())
        {
            for (int i = 0; i < data.Length; i += step)
            { var pt = new Point(i / (double)(data.Length - 1) * w, y - Math.Clamp(data[i] * Sensitivity, -1, 1) * h / 2); if (i == 0) ctx.BeginFigure(pt, false, false); else ctx.LineTo(pt, true, true); }
        }
        g.Freeze(); dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(60, c.R, c.G, c.B)), 6), g); dc.DrawGeometry(null, new Pen(new SolidColorBrush(c), 1.8), g);
    }

    private void DrawScope(DrawingContext dc, Size s)
    {
        var grid = new Pen(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), 1);
        for (int i = 1; i < 8; i++) dc.DrawLine(grid, new Point(s.Width * i / 8, 0), new Point(s.Width * i / 8, s.Height));
        for (int i = 1; i < 4; i++) dc.DrawLine(grid, new Point(0, s.Height * i / 4), new Point(s.Width, s.Height * i / 4));
        var mono = new float[_l.Length]; for (int i = 0; i < mono.Length; i++) mono[i] = (_l[i] + _r[i]) / 2;
        DrawWave(dc, mono, s.Height / 2, s.Height * 0.9, s.Width, A, 1);
    }

    private void DrawStereo(DrawingContext dc, Size s)
    {
        DrawWave(dc, _l, s.Height * 0.27, s.Height * 0.46, s.Width, A, 1); DrawWave(dc, _r, s.Height * 0.73, s.Height * 0.46, s.Width, B, 1);
        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)), 1), new Point(0, s.Height / 2), new Point(s.Width, s.Height / 2));
    }

    private void DrawWaterfall(DrawingContext dc, Size s)
    {
        const int rows = 64;
        _wf ??= new WriteableBitmap(Bands, rows, 96, 96, PixelFormats.Bgra32, null);
        Array.Copy(_wfPix, 0, _wfPix, Bands, Bands * (rows - 1));
        for (int i = 0; i < Bands; i++)
        {
            double v = _sm[i]; var c = v < 0.5 ? Palette.Lerp(Color.FromRgb(5, 5, 20), A, v * 2) : Palette.Lerp(A, B, (v - .5) * 2);
            if (v > 0.85) c = Palette.Lerp(c, Colors.White, (v - .85) * 5);
            _wfPix[i] = 0xFF000000u | (uint)(c.R << 16) | (uint)(c.G << 8) | c.B;
        }
        _wf.WritePixels(new Int32Rect(0, 0, Bands, rows), _wfPix, Bands * 4, 0);
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.Linear);
        dc.DrawImage(_wf, new Rect(0, 0, s.Width, s.Height));
    }

    private void DrawShapes(DrawingContext dc, Size s)
    {
        double cx = s.Width / 2, cy = s.Height / 2, R = Math.Min(cx, cy);
        for (int k = 0; k < 6; k++)
        {
            int sides = 3 + k; double e = _sm[k * 9 + 2]; double r = R * (0.2 + k * 0.1) * (0.8 + e * 0.9); double rot = _phase * (k % 2 == 0 ? 1 : -1) * (0.6 + k * 0.15);
            var pts = Enumerable.Range(0, sides).Select(i => new Point(cx + Math.Cos(rot + i * Math.PI * 2 / sides) * r, cy + Math.Sin(rot + i * Math.PI * 2 / sides) * r)).ToArray();
            var c = Palette.Lerp(A, B, k / 5.0);
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb((byte)(25 + e * 90), c.R, c.G, c.B)), new Pen(new SolidColorBrush(c), 1.5 + e * 3), Poly(pts));
        }
    }
}

/// <summary>Simple multi-series line chart (history graphs).</summary>
public sealed class LineChart : FrameworkElement
{
    public List<(string name, Color color, List<(DateTime t, double v)> points)> Series { get; set; } = new();
    public double? FixedMax { get; set; }
    public string Unit { get; set; } = "";
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(30);
    public void Refresh() => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight; if (w < 40 || h < 30) return;
        var tf = new Typeface("Segoe UI"); var txt = new SolidColorBrush(Color.FromArgb(160, 160, 160, 160));
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(18, 128, 128, 128)), null, new Rect(0, 0, w, h));
        double max = FixedMax ?? Math.Max(5, Series.SelectMany(s => s.points).Select(p => p.v).DefaultIfEmpty(1).Max() * 1.15);
        var grid = new Pen(new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)), 1);
        double left = 40, bottom = h - 4, top = 14;
        for (int i = 0; i <= 4; i++)
        {
            double y = top + (bottom - top) * i / 4; dc.DrawLine(grid, new Point(left, y), new Point(w - 6, y));
            var ft = new FormattedText((max * (4 - i) / 4).ToString("0"), System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, tf, 10, txt, 1.0);
            dc.DrawText(ft, new Point(2, y - 6));
        }
        var now = DateTime.Now; var from = now - Window;
        int idx = 0;
        foreach (var (name, color, pts) in Series)
        {
            var vis = pts.Where(p => p.t >= from).ToList();
            if (vis.Count > 1)
            {
                var g = new StreamGeometry(); using (var c = g.Open())
                {
                    for (int i = 0; i < vis.Count; i++)
                    { var pt = new Point(left + (vis[i].t - from).TotalSeconds / Window.TotalSeconds * (w - left - 6), bottom - Math.Clamp(vis[i].v / max, 0, 1) * (bottom - top)); if (i == 0) c.BeginFigure(pt, false, false); else c.LineTo(pt, true, true); }
                }
                g.Freeze(); dc.DrawGeometry(null, new Pen(new SolidColorBrush(color), 1.8), g);
            }
            var lt = new FormattedText(name, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, tf, 10.5, new SolidColorBrush(color), 1.0);
            dc.DrawText(lt, new Point(left + 4 + idx * 120, 0)); idx++;
        }
        if (Series.All(s => s.points.Count < 2)) dc.DrawText(new FormattedText("Collecting data…", System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, tf, 12, txt, 1.0), new Point(w / 2 - 45, h / 2 - 8));
    }
}

public static class UiBrushes
{
    public static Brush ForState(StreamState s) => s switch
    {
        StreamState.Streaming => new SolidColorBrush(Color.FromRgb(0, 220, 130)),
        StreamState.Connected or StreamState.Connecting => new SolidColorBrush(Color.FromRgb(240, 190, 50)),
        StreamState.Reconnecting => new SolidColorBrush(Color.FromRgb(255, 140, 40)),
        StreamState.Disconnected => new SolidColorBrush(Color.FromRgb(130, 130, 130)),
        _ => new SolidColorBrush(Color.FromRgb(240, 70, 70)),
    };
}
