using System.Net;
using System.Net.Mail;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using NAudio.Wave;
using RadioForge.Core.Audio;
using RadioForge.Core.Streaming;

namespace RadioForge.Core.Station;

// ====================================================================================== loudness
/// <summary>Integrated loudness per ITU-R BS.1770-4 (K-weighting, 400 ms blocks, absolute −70 LUFS and relative −10 LU gates).</summary>
public static class Loudness
{
    private sealed class Biquad
    {
        private readonly double _b0, _b1, _b2, _a1, _a2; private double _z1, _z2;
        public Biquad(double b0, double b1, double b2, double a1, double a2) { _b0 = b0; _b1 = b1; _b2 = b2; _a1 = a1; _a2 = a2; }
        public double Process(double x) { double y = _b0 * x + _z1; _z1 = _b1 * x - _a1 * y + _z2; _z2 = _b2 * x - _a2 * y; return y; }
    }
    private static Biquad[] Chain() => new[]
    {
        new Biquad(1.53512485958697, -2.69169618940638, 1.19839281085285, -1.69065929318241, 0.73248077421585), // pre-filter (shelf)
        new Biquad(1.0, -2.0, 1.0, -1.99004745483398, 0.99007225036621),                                          // RLB high-pass
    };

    /// <summary>Returns integrated loudness in LUFS, or null when the file is silent or shorter than 400 ms.</summary>
    public static double? MeasureLufs(string path, CancellationToken ct = default)
    {
        using var src = new AudioFileSource(path, 48000);
        var sp = src.Samples; var l = Chain(); var r = Chain();
        var buf = new float[4800 * 2]; var chunks = new List<double>();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int got = 0, n;
            while (got < buf.Length && (n = sp.Read(buf, got, buf.Length - got)) > 0) got += n;
            if (got < buf.Length) break;
            double sl = 0, sr = 0;
            for (int i = 0; i < buf.Length; i += 2)
            {
                double a = buf[i], b = buf[i + 1]; foreach (var f in l) a = f.Process(a); foreach (var f in r) b = f.Process(b);
                sl += a * a; sr += b * b;
            }
            chunks.Add((sl + sr) / 4800.0); // z_left + z_right for this 100 ms hop
        }
        if (chunks.Count < 4) return null;
        var blocks = new List<double>();
        for (int i = 0; i + 4 <= chunks.Count; i++) blocks.Add((chunks[i] + chunks[i + 1] + chunks[i + 2] + chunks[i + 3]) / 4);
        static double L(double z) => -0.691 + 10 * Math.Log10(z);
        var abs = blocks.Where(z => z > 0 && L(z) > -70).ToList(); if (abs.Count == 0) return null;
        double rel = L(abs.Average()) - 10;
        var gated = abs.Where(z => L(z) > rel).ToList(); if (gated.Count == 0) return null;
        return L(gated.Average());
    }

    public static float GainFor(double? lufs, double target, double maxDb = 12)
        => lufs.HasValue ? Db.ToLinear(Math.Clamp(target - lufs.Value, -maxDb, maxDb)) : 1f;

    /// <summary>Measures every library track that has no loudness value yet.</summary>
    public static async Task<int> AnalyzeLibraryAsync(Database db, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var todo = db.GetTracks().Where(t => !t.Missing && t.Lufs == null).ToList(); int done = 0;
        foreach (var t in todo)
        {
            ct.ThrowIfCancellationRequested();
            try { var v = await Task.Run(() => MeasureLufs(t.Path, ct), ct); db.SetLufs(t.Id, v ?? -70); done++; progress?.Report($"{done}/{todo.Count}  {t.DisplayName}"); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Log.Warn("Loudness", $"{Path.GetFileName(t.Path)}: {ex.Message}"); }
        }
        return done;
    }
}

// ====================================================================================== sample content
public static class SampleAudio
{
    /// <summary>Generates an original synthetic track (bass, arpeggio, pad, hats, kick) as 16-bit stereo WAV.</summary>
    public static void Synth(string path, double root, int bpm, int seconds)
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
            double lft = bass + arp + pad + hat + kick, rgt = bass + arp * 0.8 + pad + hat * 0.6 + kick;
            double fade = Math.Min(1, Math.Min(t / 0.5, (seconds - t) / 1.0));
            short sl = (short)(Math.Clamp(lft * fade, -1, 1) * 30000), sr = (short)(Math.Clamp(rgt * fade, -1, 1) * 30000);
            buf[idx++] = (byte)sl; buf[idx++] = (byte)(sl >> 8); buf[idx++] = (byte)sr; buf[idx++] = (byte)(sr >> 8);
            if (idx >= buf.Length) { w.Write(buf, 0, idx); idx = 0; }
        }
        if (idx > 0) w.Write(buf, 0, idx);
    }

    /// <summary>A short rising/falling station-ID style sting.</summary>
    public static void Jingle(string path, double baseHz, double seconds = 2.2)
    {
        const int rate = 44100; using var w = new WaveFileWriter(path, new WaveFormat(rate, 16, 2));
        int n = (int)(rate * seconds); var notes = new[] { 1.0, 1.26, 1.498, 2.0 }; var bytes = new byte[n * 4];
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)rate; int k = Math.Min(notes.Length - 1, (int)(t / (seconds / 5))); double nt = t - k * (seconds / 5);
            double env = Math.Exp(-nt * 3.5) * Math.Min(1, nt * 80); double f = baseHz * notes[k];
            double v = (Math.Sin(2 * Math.PI * f * t) + 0.35 * Math.Sin(4 * Math.PI * f * t) + 0.15 * Math.Sin(6 * Math.PI * f * t)) * env * 0.35;
            if (t > seconds * 0.6) v += Math.Sin(2 * Math.PI * baseHz * 2 * t) * Math.Exp(-(t - seconds * 0.6) * 2.2) * 0.2;
            short s = (short)(Math.Clamp(v, -1, 1) * 30000); bytes[i * 4] = (byte)s; bytes[i * 4 + 1] = (byte)(s >> 8); bytes[i * 4 + 2] = (byte)s; bytes[i * 4 + 3] = (byte)(s >> 8);
        }
        w.Write(bytes, 0, bytes.Length);
    }
}

public static class SampleContent
{
    public const string PlaylistName = "Sample playlist (original RadioForge music)";

    /// <summary>Creates original sample tracks + jingles, a sample playlist and pads 1–4. Returns false when already present.</summary>
    public static bool Create(StationController st)
    {
        if (st.Db.GetPlaylists().Any(p => p.Name == PlaylistName)) return false;
        var dir = AppPaths.Dir(Path.Combine(AppPaths.ProfileDir(st.Profile.Id), "samples"));
        var songs = new (string title, double root, int bpm)[] { ("Sunrise Drive", 110, 112), ("Blue Hour", 98, 96), ("Harbor Lights", 123.5, 104) };
        var ids = new List<long>();
        foreach (var (title, root, bpm) in songs)
        {
            var f = Path.Combine(dir, title + ".wav");
            if (!File.Exists(f)) { SampleAudio.Synth(f, root, bpm, 40); try { using var tf = TagLib.File.Create(f); tf.Tag.Title = title; tf.Tag.Performers = new[] { "RadioForge Samples" }; tf.Tag.Album = "Starter Pack"; tf.Tag.Genres = new[] { "Electronic" }; tf.Save(); } catch { } }
            var t = st.Db.ImportFile(f); if (t != null) ids.Add(t.Id);
        }
        var pl = st.Db.CreatePlaylist(PlaylistName); st.Db.SavePlaylistTracks(pl, ids);
        string[] names = { "Station ID", "Sting", "Lift", "Outro" }; double[] hz = { 261.6, 329.6, 392, 220 }; string[] icons = { "📻", "✨", "⬆", "🎬" };
        for (int i = 0; i < 4; i++)
        {
            var jf = Path.Combine(dir, $"Sample Jingle {i + 1}.wav"); if (!File.Exists(jf)) SampleAudio.Jingle(jf, hz[i]);
            var pad = st.Profile.Soundboard.Pads[i]; if (pad.HasSound) continue;
            pad.Name = names[i]; pad.Icon = icons[i]; pad.FilePath = jf; pad.Mode = PadMode.PlayOverMusic; pad.Category = "Station jingles";
        }
        if (string.IsNullOrEmpty(st.Profile.AutoDj.PlaylistId)) st.Profile.AutoDj.PlaylistId = pl.ToString();
        st.SaveProfile(); Log.Info("Samples", "Sample content created"); return true;
    }
}

// ====================================================================================== alerts (Discord / webhook / e-mail)
public partial class AlertSettings : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private bool _enabled;
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private bool _onDisconnect = true;
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private bool _onAuthFailure = true;
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private bool _onReconnect = true;
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private bool _onDevice = true;
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private string _smtpHost = "";
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private int _smtpPort = 587;
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private bool _smtpSsl = true;
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private string _smtpUser = "";
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private string _mailFrom = "";
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private string _mailTo = "";
    /// <summary>DPAPI-protected (webhook URLs contain a secret token).</summary>
    public string WebhookProtected { get; set; } = "";
    public string SmtpPasswordProtected { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore] public string WebhookUrl { get => SecretProtector.Unprotect(WebhookProtected); set { WebhookProtected = SecretProtector.Protect(value ?? ""); OnPropertyChanged(); } }
    [System.Text.Json.Serialization.JsonIgnore] public string SmtpPassword { get => SecretProtector.Unprotect(SmtpPasswordProtected); set { SmtpPasswordProtected = SecretProtector.Protect(value ?? ""); OnPropertyChanged(); } }
}

/// <summary>Sends stream-health alerts to a Discord/Slack-compatible webhook and/or by e-mail. De-duplicated and never throws.</summary>
public sealed class AlertService : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly AppSettings _s; private readonly Func<string> _station; private readonly NotificationHub _hub;
    private readonly Dictionary<string, DateTime> _last = new();
    public List<string> Sent { get; } = new(); // last results, for the UI / tests
    public AlertService(NotificationHub hub, AppSettings s, Func<string> stationName) { _hub = hub; _s = s; _station = stationName; hub.Notified += OnNotified; }
    public void Dispose() => _hub.Notified -= OnNotified;

    private bool Wanted(NotifyKind k) => k switch
    {
        NotifyKind.Disconnected => _s.Alerts.OnDisconnect, NotifyKind.AuthFailure => _s.Alerts.OnAuthFailure,
        NotifyKind.Reconnected => _s.Alerts.OnReconnect, NotifyKind.DeviceProblem => _s.Alerts.OnDevice, _ => false
    };

    private void OnNotified(NotificationItem n)
    {
        var a = _s.Alerts; if (!a.Enabled || !Wanted(n.Kind)) return;
        var key = n.Kind + "|" + n.Title;
        lock (_last) { if (_last.TryGetValue(key, out var t) && DateTime.UtcNow - t < TimeSpan.FromSeconds(60)) return; _last[key] = DateTime.UtcNow; }
        _ = Task.Run(() => SendAsync($"[{_station()}] {n.Title}", n.Message));
    }

    public async Task<string> SendAsync(string subject, string message)
    {
        var a = _s.Alerts; var results = new List<string>();
        var text = Redactor.Redact($"{subject}\n{message}\n({DateTime.Now:yyyy-MM-dd HH:mm:ss})");
        if (!string.IsNullOrWhiteSpace(a.WebhookUrl))
        {
            try
            {
                var body = JsonSerializer.Serialize(new { content = text, text, username = "RadioForge" });
                using var resp = await Http.PostAsync(a.WebhookUrl, new StringContent(body, Encoding.UTF8, "application/json"));
                results.Add(resp.IsSuccessStatusCode ? "webhook: sent" : $"webhook: HTTP {(int)resp.StatusCode}");
            }
            catch (Exception ex) { results.Add("webhook failed: " + Redactor.Redact(ex.Message)); }
        }
        if (!string.IsNullOrWhiteSpace(a.SmtpHost) && !string.IsNullOrWhiteSpace(a.MailTo))
        {
            try
            {
                using var smtp = new SmtpClient(a.SmtpHost, a.SmtpPort) { EnableSsl = a.SmtpSsl, Timeout = 15000 };
                if (!string.IsNullOrEmpty(a.SmtpUser)) smtp.Credentials = new NetworkCredential(a.SmtpUser, a.SmtpPassword);
                var from = string.IsNullOrWhiteSpace(a.MailFrom) ? a.SmtpUser : a.MailFrom;
                using var mail = new MailMessage(from, a.MailTo, subject, message); await smtp.SendMailAsync(mail);
                results.Add("e-mail: sent");
            }
            catch (Exception ex) { results.Add("e-mail failed: " + Redactor.Redact(ex.Message)); }
        }
        if (results.Count == 0) results.Add("nothing configured");
        var r = string.Join("; ", results); lock (Sent) { Sent.Add(r); if (Sent.Count > 50) Sent.RemoveAt(0); }
        Log.Info("Alerts", r); return r;
    }
}

// ====================================================================================== now-playing web page
/// <summary>Tiny built-in HTTP server: a mobile-friendly now-playing page and a JSON endpoint. Off by default; loopback unless LAN access is enabled.</summary>
public sealed class NowPlayingServer : IDisposable
{
    private readonly StationController _st; private TcpListener? _l; private CancellationTokenSource? _cts;
    public int Port { get; private set; }
    public NowPlayingServer(StationController st) { _st = st; }

    public void Start(int port, bool allowLan)
    {
        Stop();
        _l = new TcpListener(allowLan ? IPAddress.Any : IPAddress.Loopback, port); _l.Start(); Port = ((IPEndPoint)_l.LocalEndpoint).Port;
        _cts = new CancellationTokenSource(); var ct = _cts.Token; var l = _l;
        _ = Task.Run(async () => { while (!ct.IsCancellationRequested) { TcpClient c; try { c = await l.AcceptTcpClientAsync(ct); } catch { return; } _ = Task.Run(() => Handle(c)); } });
        Log.Info("Web", $"Now-playing page on port {Port} ({(allowLan ? "network" : "this PC only")})");
    }

    public void Stop() { _cts?.Cancel(); try { _l?.Stop(); } catch { } _l = null; }
    public void Dispose() => Stop();

    public string Json()
    {
        var np = _st.NowPlaying; var m = _st.Profile.Metadata;
        bool manual = m.ManualOverride && !string.IsNullOrWhiteSpace(m.ManualText);
        return JsonSerializer.Serialize(new
        {
            station = _st.Profile.Name, onAir = _st.OnAir, title = manual ? m.ManualText : np.Title, artist = manual ? "" : np.Artist, album = np.Album,
            next = _st.NextText, program = m.ProgramName, dj = m.DjName, listeners = _st.Streams.TotalListeners, updated = DateTime.UtcNow
        });
    }

    private async Task Handle(TcpClient c)
    {
        try
        {
            c.ReceiveTimeout = c.SendTimeout = 5000; using var s = c.GetStream();
            var buf = new byte[4096]; int n = await s.ReadAsync(buf); var req = Encoding.ASCII.GetString(buf, 0, n); var line = req.Split('\n')[0].Split(' ');
            var path = line.Length > 1 ? line[1].Split('?')[0] : "/";
            string body, type = "application/json; charset=utf-8"; int code = 200;
            if (path == "/nowplaying.json") body = Json();
            else if (path is "/" or "/index.html") { body = Page; type = "text/html; charset=utf-8"; }
            else { body = "{\"error\":\"not found\"}"; code = 404; }
            var bytes = Encoding.UTF8.GetBytes(body);
            var head = $"HTTP/1.1 {code} {(code == 200 ? "OK" : "Not Found")}\r\nContent-Type: {type}\r\nContent-Length: {bytes.Length}\r\nCache-Control: no-store\r\nAccess-Control-Allow-Origin: *\r\nConnection: close\r\n\r\n";
            await s.WriteAsync(Encoding.ASCII.GetBytes(head)); await s.WriteAsync(bytes);
        }
        catch { }
        finally { try { c.Close(); } catch { } }
    }

    private const string Page = """
<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Now playing</title>
<style>:root{color-scheme:dark}body{margin:0;font-family:Segoe UI,system-ui,sans-serif;background:#0e1013;color:#e8ecef;display:flex;min-height:100vh;align-items:center;justify-content:center}
.card{width:min(92vw,520px);background:#171a1f;border:1px solid #2a2f37;border-radius:16px;padding:28px;text-align:center}
.st{font-size:14px;color:#8a939e;letter-spacing:.08em;text-transform:uppercase}.air{display:inline-block;margin:12px 0;padding:4px 14px;border-radius:6px;background:#3a3f47;font-weight:700;font-size:13px}
.air.on{background:#d8222b}.t{font-size:26px;font-weight:600;margin:10px 0 4px}.a{font-size:17px;color:#8a939e}.n{margin-top:22px;font-size:14px;color:#8a939e}</style></head>
<body><div class="card"><div class="st" id="st">Radio</div><div class="air" id="air">OFF AIR</div><div class="t" id="t">…</div><div class="a" id="a"></div><div class="n" id="n"></div></div>
<script>async function u(){try{const r=await(await fetch('nowplaying.json',{cache:'no-store'})).json();
st.textContent=r.station+(r.program?' · '+r.program:'')+(r.dj?' · '+r.dj:'');air.textContent=r.onAir?'ON AIR':'OFF AIR';air.className='air'+(r.onAir?' on':'');
t.textContent=r.title||'—';a.textContent=r.artist||'';n.textContent=(r.next&&r.next!=='—'?'Next: '+r.next:'')+(r.listeners!=null?'   ·   '+r.listeners+' listeners':'');document.title=(r.title||'Now playing')+' — '+r.station}catch(e){}}
u();setInterval(u,4000)</script></body></html>
""";
}

// ====================================================================================== podcast feed
public static class PodcastFeed
{
    /// <summary>Writes podcast.xml (RSS 2.0) next to the recordings. Only titled (archived show) recordings are listed.</summary>
    public static string? Write(StationProfile profile, IEnumerable<RecordingInfo> recordings, string folder)
    {
        var baseUrl = profile.Recording.PodcastBaseUrl?.Trim().TrimEnd('/'); if (string.IsNullOrEmpty(baseUrl)) return null;
        var items = recordings.Where(r => !string.IsNullOrEmpty(r.Title) && File.Exists(r.Path)).OrderByDescending(r => r.Started).Take(100).ToList();
        string Mime(string p) => Path.GetExtension(p).ToLowerInvariant() switch { ".mp3" => "audio/mpeg", ".wav" => "audio/wav", ".flac" => "audio/flac", _ => "application/octet-stream" };
        XNamespace itunes = "http://www.itunes.com/dtds/podcast-1.0.dtd";
        var channel = new XElement("channel", new XElement("title", profile.Name), new XElement("link", string.IsNullOrWhiteSpace(profile.Website) ? baseUrl : profile.Website),
            new XElement("description", string.IsNullOrWhiteSpace(profile.Description) ? profile.Name + " — archived shows" : profile.Description), new XElement("language", "en"));
        foreach (var r in items)
        {
            var name = Path.GetFileName(r.Path);
            channel.Add(new XElement("item", new XElement("title", r.Title), new XElement("pubDate", r.Started.ToUniversalTime().ToString("R")),
                new XElement("guid", new XAttribute("isPermaLink", "false"), Path.GetFileNameWithoutExtension(name) + "-" + r.Started.Ticks),
                new XElement("enclosure", new XAttribute("url", baseUrl + "/" + Uri.EscapeDataString(name)), new XAttribute("length", r.Size), new XAttribute("type", Mime(r.Path))),
                new XElement(itunes + "duration", (int)r.DurationSec)));
        }
        var doc = new XDocument(new XElement("rss", new XAttribute("version", "2.0"), new XAttribute(XNamespace.Xmlns + "itunes", itunes.NamespaceName), channel));
        var file = Path.Combine(folder, "podcast.xml"); doc.Save(file); Log.Info("Podcast", $"Feed written: {items.Count} episode(s)"); return file;
    }
}

// ====================================================================================== voice tracks
/// <summary>Records the processed microphone to a WAV file without putting it on air (the mic is routed privately while recording).</summary>
public sealed class VoiceTrackRecorder
{
    private readonly AudioEngine _e; private BusSubscription? _sub; private Task? _task; private string _path = "";
    private long _frames; public bool IsRecording { get; private set; }
    public double Seconds => Interlocked.Read(ref _frames) / (double)_e.Rate;
    public VoiceTrackRecorder(AudioEngine e) { _e = e; }

    /// <summary>Returns an error message or null.</summary>
    public string? Start(string folder)
    {
        if (IsRecording) return "Already recording.";
        if (!_e.MicCapture.IsRunning && !_e.Mic.HasInjectedSource) return "No microphone is available. Choose one under Studio or Audio Settings.";
        Directory.CreateDirectory(folder); _path = Path.Combine(folder, $"Voice {DateTime.Now:yyyy-MM-dd HH-mm-ss}.wav");
        _frames = 0; _e.MicPrivate = true; _e.Mic.ForceOpen = true; _sub = _e.VoiceBus.Subscribe(); var sub = _sub;
        var w = new WaveFileWriter(_path, new WaveFormat(_e.Rate, 16, 2));
        _task = Task.Run(async () => { try { await foreach (var b in sub.Reader.ReadAllAsync()) { var pcm = Streaming_ToPcm(b.Data, b.Count); w.Write(pcm, 0, pcm.Length); Interlocked.Add(ref _frames, b.Count / 2); } } finally { w.Dispose(); } });
        IsRecording = true; return null;
    }

    private static byte[] Streaming_ToPcm(float[] d, int n) => PushConverter.ToPcm16(d, n);

    /// <summary>Stops and returns the file path (null when nothing was recorded).</summary>
    public async Task<string?> StopAsync()
    {
        if (!IsRecording) return null;
        IsRecording = false; _e.Mic.ForceOpen = false; _e.MicPrivate = false; _sub?.Dispose(); if (_task != null) await Task.WhenAny(_task, Task.Delay(5000));
        return File.Exists(_path) && new FileInfo(_path).Length > 1000 ? _path : null;
    }
}
