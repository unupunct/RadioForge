using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NAudio.Wave;
using RadioForge.App.ViewModels;
using RadioForge.App.Views;
using RadioForge.Core;
using RadioForge.Core.Station;

namespace RadioForge.App;

/// <summary>
/// Developer tooling: `RadioForge.exe --screenshots --out DIR --data-dir TMP [--demo-port N]` renders every page to PNG
/// using generated demo audio and (optionally) a local test Icecast server. Never used in normal operation.
/// </summary>
public static class Screenshots
{
    private static string Arg(string[] a, string n, string def) { var i = Array.IndexOf(a, n); return i >= 0 && i + 1 < a.Length ? a[i + 1] : def; }

    public static async Task RunAsync(MainWindow win, string[] args)
    {
        try
        {
            var outDir = Arg(args, "--out", Path.Combine(Environment.CurrentDirectory, "screenshots")); Directory.CreateDirectory(outDir);
            int port = int.Parse(Arg(args, "--demo-port", "0"));
            var st = win.Shell.Station;
            await Prepare(st, port); await Task.Delay(1200); win.Shell.DismissToastCommand.Execute(null);
            win.Width = 1400; win.Height = 880; win.UpdateLayout();
            await Task.Delay(3500);
            var pages = new (PageId id, string file)[] { (PageId.Dashboard, "dashboard"), (PageId.Studio, "studio"), (PageId.Playlists, "playlists"), (PageId.Soundboard, "soundboard"), (PageId.Servers, "servers"), (PageId.Scheduler, "scheduler"), (PageId.Library, "library"), (PageId.Recordings, "recordings"), (PageId.AudioSettings, "audio-settings"), (PageId.Statistics, "statistics"), (PageId.Logs, "logs"), (PageId.Settings, "settings") };
            foreach (var (id, file) in pages)
            {
                win.Shell.Navigate(id); await Task.Delay(id == PageId.Dashboard ? 2500 : 1500);
                Save(win, Path.Combine(outDir, file + ".png"));
            }
            var wiz = new SetupWizard(new StationProfile { Name = "Radio Nova", Genre = "Electronic", Website = "https://radionova.example" }) { WindowStartupLocation = WindowStartupLocation.Manual, Left = 100, Top = 60 };
            wiz.Show(); await Task.Delay(1200); ScreenGrab(wiz, Path.Combine(outDir, "wizard.png")); wiz.Close();
            Log.Info("Screenshots", "Done: " + outDir);
        }
        catch (Exception ex) { App.CrashReport("Screenshots", ex); }
        finally { App.ExitRequested = true; Application.Current.Shutdown(); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }

    /// <summary>Captures only this window's own pixels (PrintWindow) — never the screen, so other windows can't leak in.</summary>
    private static void ScreenGrab(Window w, string file)
    {
        var h = new System.Windows.Interop.WindowInteropHelper(w).Handle; GetWindowRect(h, out var r);
        int pw = r.R - r.L, ph = r.B - r.T;
        using var bmp = new System.Drawing.Bitmap(pw, ph); using (var g = System.Drawing.Graphics.FromImage(bmp)) { var hdc = g.GetHdc(); PrintWindow(h, hdc, 2); g.ReleaseHdc(hdc); }
        bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static void Save(Window w, string file)
    {
        var el = (FrameworkElement)w.Content; var dpi = VisualTreeHelper.GetDpi(el);
        int pw = (int)(el.ActualWidth * dpi.DpiScaleX), ph = (int)(el.ActualHeight * dpi.DpiScaleY);
        var rtb = new RenderTargetBitmap(pw, ph, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var dv = new DrawingVisual(); using (var dc = dv.RenderOpen()) dc.DrawRectangle((Brush)Application.Current.FindResource("AppBgBrush"), null, new Rect(0, 0, el.ActualWidth, el.ActualHeight));
        rtb.Render(dv); rtb.Render(el);
        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(rtb)); using var fs = File.Create(file); enc.Save(fs);
    }

    private static async Task Prepare(StationController st, int port)
    {
        var dir = Path.Combine(AppPaths.Root, "demo-music"); Directory.CreateDirectory(dir);
        st.Profile.Name = "Radio Nova"; st.Profile.Genre = "Electronic / Chill"; st.Profile.Description = "Demo station"; st.Profile.Website = "https://radionova.example";
        st.Profile.Metadata.DjName = "DJ Aria"; st.Profile.SetupCompleted = true;
        var songs = new (string title, string artist, string album, double root, int bpm)[]
        {
            ("Neon Drive", "Synthwave Collective", "Night Lines", 110, 118), ("Midnight Signal", "Aurora Fields", "Transmissions", 98, 100), ("Afterglow", "Kite & Compass", "Golden Hour", 123.5, 92),
            ("Satellite Hearts", "Aurora Fields", "Transmissions", 87.3, 124), ("Low Tide", "Paper Lanterns", "Harbor", 130.8, 80), ("Static Bloom", "Synthwave Collective", "Night Lines", 116.5, 108),
        };
        var tracks = new List<Track>();
        foreach (var s in songs)
        {
            var f = Path.Combine(dir, s.title + ".wav");
            if (!File.Exists(f)) { Synth(f, s.root, s.bpm, 75); try { using var tf = TagLib.File.Create(f); tf.Tag.Title = s.title; tf.Tag.Performers = new[] { s.artist }; tf.Tag.Album = s.album; tf.Tag.Genres = new[] { "Electronic" }; tf.Tag.Year = 2026; tf.Save(); } catch { } }
            var t = st.Db.ImportFile(f); if (t != null) tracks.Add(t);
        }
        var pl = st.Db.CreatePlaylist("Evening Chill"); st.Db.SavePlaylistTracks(pl, tracks.Take(4).Select(t => t.Id));
        var pl2 = st.Db.CreatePlaylist("Night Drive"); st.Db.SavePlaylistTracks(pl2, tracks.Skip(2).Select(t => t.Id));
        var folder = st.Db.CreatePlaylist("Weekend Shows", null, true); st.Db.CreatePlaylist("Saturday Mix", folder);
        st.Profile.AutoDj.PlaylistId = pl.ToString();
        // soundboard
        string[] names = { "Station ID", "Intro", "Outro", "News Sting", "Ad Break", "Applause", "Laugh", "Whoosh", "Alert", "Drop", "Rewind", "Bell" };
        string[] icons = { "📻", "🎬", "🎞", "📰", "💰", "👏", "😄", "💨", "🚨", "💥", "⏪", "🔔" }; string[] colors = { "#1F6F5C", "#0E7490", "#7C3AED", "#B91C1C", "#B45309", "#BE185D", "#1D4ED8", "#4D7C0F", "#C2410C", "#475569", "#0E7490", "#7C3AED" };
        var jf = Path.Combine(dir, "jingle.wav"); if (!File.Exists(jf)) Synth(jf, 220, 140, 3);
        for (int i = 0; i < 12; i++) { var p = st.Profile.Soundboard.Pads[i]; p.Name = names[i]; p.Icon = icons[i]; p.Color = colors[i]; p.FilePath = jf; p.Hotkey = i < 4 ? "F" + (i + 2) : ""; }
        // schedule (this week)
        var mon = DateTime.Today.AddDays(-(((int)DateTime.Today.DayOfWeek + 6) % 7));
        void Ev(string title, EventKind k, int h, int m, int dur, RepeatMode r, int prio = 1, long pid = 0) => st.SaveEvent(new ScheduledEvent { Title = title, Kind = k, StartDate = DateOnly.FromDateTime(mon), StartTime = new TimeOnly(h, m), DurationMinutes = dur, Repeat = r, DaysMask = r == RepeatMode.Weekly ? 0b0111110 : 0, Source = SourceKind.Playlist, SourceRef = pid.ToString(), Priority = prio });
        Ev("Morning Show", EventKind.RadioProgram, 8, 0, 120, RepeatMode.Weekly, 2, pl); Ev("Music Playlist", EventKind.MusicPlaylist, 10, 0, 120, RepeatMode.Weekly, 1, pl2); Ev("News", EventKind.News, 12, 0, 15, RepeatMode.Weekly, 3, pl);
        Ev("Music Playlist", EventKind.MusicPlaylist, 12, 15, 105, RepeatMode.Weekly, 1, pl); Ev("Afternoon Show", EventKind.RadioProgram, 14, 0, 240, RepeatMode.Weekly, 2, pl2); Ev("Evening Music", EventKind.MusicPlaylist, 18, 0, 180, RepeatMode.Daily, 1, pl);
        // streaming + recording
        if (port > 0)
        {
            var sp = new ServerProfile { Name = "Icecast 128k (local test)", Type = ServerType.Icecast2, Host = "127.0.0.1", Port = port, Mount = "/live", BitrateKbps = 128, StreamName = "Radio Nova", Genre = "Electronic" }; sp.Password = "secret";
            var sp2 = new ServerProfile { Name = "Icecast 192k (local test)", Type = ServerType.Icecast2, Host = "127.0.0.1", Port = port, Mount = "/hq", BitrateKbps = 192, StreamName = "Radio Nova HQ" }; sp2.Password = "secret";
            var sp3 = new ServerProfile { Name = "SHOUTcast (no server here)", Type = ServerType.ShoutcastV2, Host = "127.0.0.1", Port = 1, BitrateKbps = 96, Channels = 1, SampleRate = 22050, RetryIntervalSec = 5 }; sp3.Password = "x";
            foreach (var s in new[] { sp, sp2, sp3 }) { st.Profile.Servers.Add(s); st.AddDestination(s); }
        }
        st.Profile.Recording.Folder = Path.Combine(AppPaths.Root, "recordings"); st.Profile.Audio.Mic.Muted = true;
        st.StartBroadcast(); await Task.Delay(1500);
        st.Profile.Recording.Format = RecordFormat.Mp3; st.Recorder.Start(); await Task.Delay(4500); await st.Recorder.StopAsync();
        st.SaveProfile();
    }

    /// <summary>Generates a short synthetic track (bass, arpeggio, pad, hats) so meters and visualisers have rich content.</summary>
    private static void Synth(string path, double root, int bpm, int seconds)
    {
        const int rate = 44100; using var w = new WaveFileWriter(path, new WaveFormat(rate, 16, 2));
        double beat = 60.0 / bpm; var rnd = new Random(root.GetHashCode()); var scale = new[] { 1.0, 1.122, 1.26, 1.498, 1.682, 2.0, 1.498, 1.26 };
        var buf = new byte[rate * 4 / 10]; int n = rate * seconds; int idx = 0;
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)rate; double bt = t / beat; int step = (int)(bt * 2); double ph = bt * 2 - step;
            double bass = Math.Sin(2 * Math.PI * root / 2 * t) * Math.Exp(-ph * 1.5) * 0.45;
            double f = root * 2 * scale[step % scale.Length] * (1 + (step / 32 % 2) * 0.5); double arp = (Math.Sin(2 * Math.PI * f * t) + 0.4 * Math.Sin(4 * Math.PI * f * t)) * Math.Exp(-ph * 4) * 0.22;
            double pad = (Math.Sin(2 * Math.PI * root * t) + Math.Sin(2 * Math.PI * root * 1.498 * t) + Math.Sin(2 * Math.PI * root * 1.26 * t)) * 0.06 * (0.6 + 0.4 * Math.Sin(t * 0.5));
            double hat = (rnd.NextDouble() * 2 - 1) * Math.Exp(-((bt * 2) % 1) * 18) * 0.12 * (step % 2);
            double kick = Math.Sin(2 * Math.PI * (45 + 90 * Math.Exp(-(bt % 1) * 25)) * (bt % 1) * beat) * Math.Exp(-(bt % 1) * 8) * 0.5;
            double l = bass + arp + pad + hat + kick, r = bass + arp * 0.8 + pad + hat * 0.6 + kick;
            double fade = Math.Min(1, Math.Min(t / 0.5, (seconds - t) / 1.0));
            short sl = (short)(Math.Clamp(l * fade, -1, 1) * 30000), sr = (short)(Math.Clamp(r * fade, -1, 1) * 30000);
            buf[idx++] = (byte)sl; buf[idx++] = (byte)(sl >> 8); buf[idx++] = (byte)sr; buf[idx++] = (byte)(sr >> 8);
            if (idx >= buf.Length) { w.Write(buf, 0, idx); idx = 0; }
        }
        if (idx > 0) w.Write(buf, 0, idx);
    }
}
