using System.Net;
using System.Net.Sockets;
using System.Text;
using RadioForge.Core;
using RadioForge.Core.Audio;
using RadioForge.Core.Station;

namespace RadioForge.Tests;

internal sealed class CaptureServer : IDisposable
{
    private readonly TcpListener _l = new(IPAddress.Loopback, 0); public int Port => ((IPEndPoint)_l.LocalEndpoint).Port;
    public volatile string LastBody = ""; public volatile int Hits;
    public CaptureServer()
    {
        _l.Start();
        _ = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient c; try { c = await _l.AcceptTcpClientAsync(); } catch { return; }
                _ = Task.Run(async () =>
                {
                    using var s = c.GetStream(); var sb = new StringBuilder(); var b = new byte[8192];
                    try { while (true) { int n = await s.ReadAsync(b); if (n == 0) break; sb.Append(Encoding.UTF8.GetString(b, 0, n)); var t = sb.ToString(); int i = t.IndexOf("\r\n\r\n"); if (i >= 0) { var m = System.Text.RegularExpressions.Regex.Match(t, @"Content-Length:\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase); int len = m.Success ? int.Parse(m.Groups[1].Value) : 0; if (t.Length - (i + 4) >= len) { LastBody = t[(i + 4)..]; break; } } } } catch { }
                    Interlocked.Increment(ref Hits);
                    await s.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
                });
            }
        });
    }
    public void Dispose() { try { _l.Stop(); } catch { } }
}

internal sealed class FakeSmtp : IDisposable
{
    private readonly TcpListener _l = new(IPAddress.Loopback, 0); public int Port => ((IPEndPoint)_l.LocalEndpoint).Port;
    public volatile string Rcpt = "", Data = "";
    public FakeSmtp()
    {
        _l.Start();
        _ = Task.Run(async () =>
        {
          while (true)
          {
            TcpClient c; try { c = await _l.AcceptTcpClientAsync(); } catch { return; }
            using var s = c.GetStream(); using var r = new StreamReader(s); using var w = new StreamWriter(s) { AutoFlush = true, NewLine = "\r\n" };
            await w.WriteLineAsync("220 fake ESMTP");
            try
            {
                while (await r.ReadLineAsync() is { } line)
                {
                    if (line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase) || line.StartsWith("HELO", StringComparison.OrdinalIgnoreCase)) await w.WriteLineAsync("250 ok");
                    else if (line.StartsWith("MAIL")) await w.WriteLineAsync("250 ok");
                    else if (line.StartsWith("RCPT")) { Rcpt = line; await w.WriteLineAsync("250 ok"); }
                    else if (line.StartsWith("DATA")) { await w.WriteLineAsync("354 go"); var sb = new StringBuilder(); while (await r.ReadLineAsync() is { } d && d != ".") sb.AppendLine(d); Data = sb.ToString(); await w.WriteLineAsync("250 queued"); }
                    else if (line.StartsWith("QUIT")) { await w.WriteLineAsync("221 bye"); break; }
                    else await w.WriteLineAsync("250 ok");
                }
            }
            catch { }
          }
        });
    }
    public void Dispose() { try { _l.Stop(); } catch { } }
}

public class LoudnessTests
{
    [Fact]
    public void Louder_track_measures_louder_by_the_right_amount_and_silence_is_null()
    {
        using var t = new TempRoot();
        var a = Loudness.MeasureLufs(t.Wav("a.wav", 6, 1000, 44100, 0.5)); var b = Loudness.MeasureLufs(t.Wav("b.wav", 6, 1000, 44100, 0.25));
        Assert.NotNull(a); Assert.NotNull(b);
        Assert.InRange(a!.Value - b!.Value, 5.8, 6.25); // amplitude ×2 = +6.02 dB
        Assert.InRange(a.Value, -12, -4);                 // sanity: a −6 dBFS stereo sine ≈ −6 LUFS
        Assert.Null(Loudness.MeasureLufs(t.Wav("s.wav", 3, 440, 44100, 0.0)));
        Assert.Null(Loudness.MeasureLufs(t.Wav("short.wav", 0.2, 440)));
        Assert.Equal(1f, Loudness.GainFor(null, -16));
        Assert.InRange(Loudness.GainFor(-30, -16), 3.95, 4.0); // +14 dB requested, capped at +12 dB (×3.98)
    }

    [Fact]
    public async Task Normalisation_evens_out_quiet_and_loud_tracks_during_playback()
    {
        using var t = new TempRoot(); using var st = Station.Open(t);
        st.Engine.Settings.MasterVolume = 1.0; st.Profile.Audio.CrossfadeSec = 0;
        var quiet = t.Wav("quiet.wav", 12, 440, 44100, 0.08); var loud = t.Wav("loud.wav", 12, 440, 44100, 0.6);
        st.Db.ImportFile(quiet); st.Db.ImportFile(loud);
        Assert.Equal(2, await Loudness.AnalyzeLibraryAsync(st.Db));
        async Task<double> Level(string path, bool normalise)
        {
            st.Profile.Audio.NormalizeLoudness = normalise; st.Profile.Audio.TargetLufs = -18;
            st.Engine.Music.Play(path); await Task.Delay(1500); var v = new List<double>();
            for (int i = 0; i < 8; i++) { v.Add(st.Engine.MasterMeter.Rms); await Task.Delay(100); }
            return Db.ToDb(v.Average());
        }
        double rawDiff = await Level(loud, false) - await Level(quiet, false);
        double normDiff = Math.Abs(await Level(loud, true) - await Level(quiet, true));
        Assert.True(rawDiff > 12, "raw difference " + rawDiff);
        Assert.True(normDiff < 3, $"normalised difference {normDiff:0.0} dB (raw {rawDiff:0.0})");
    }
}

public class ContentTests
{
    [Fact]
    public void Sample_content_creates_playlist_jingles_and_is_idempotent()
    {
        using var t = new TempRoot(); using var st = Station.Open(t);
        Assert.True(SampleContent.Create(st));
        var pl = st.Db.GetPlaylists().Single(p => p.Name == SampleContent.PlaylistName);
        var tracks = st.Db.GetPlaylistTracks(pl.Id); Assert.Equal(3, tracks.Count);
        Assert.All(tracks, x => { Assert.True(x.DurationSec > 30); Assert.Equal("RadioForge Samples", x.Artist); });
        Assert.All(st.Profile.Soundboard.Pads.Take(4), p => { Assert.True(p.HasSound); Assert.True(File.Exists(p.FilePath)); });
        Assert.False(SampleContent.Create(st)); Assert.Single(st.Db.GetPlaylists());
        Assert.Equal(pl.Id.ToString(), st.Profile.AutoDj.PlaylistId);
        Assert.NotNull(Loudness.MeasureLufs(st.Profile.Soundboard.Pads[0].FilePath) is null ? null : "ok");
    }
}

public class AlertTests
{
    [Fact]
    public async Task Webhook_and_email_alerts_are_sent_deduplicated_and_redacted()
    {
        using var t = new TempRoot(); using var hook = new CaptureServer(); using var smtp = new FakeSmtp();
        var settings = new AppSettings(); settings.Alerts.Enabled = true; settings.Alerts.WebhookUrl = $"http://127.0.0.1:{hook.Port}/api/webhooks/123/secret-token";
        settings.Alerts.SmtpHost = "127.0.0.1"; settings.Alerts.SmtpPort = smtp.Port; settings.Alerts.SmtpSsl = false; settings.Alerts.MailFrom = "radio@example.com"; settings.Alerts.MailTo = "dj@example.com";
        var hub = new NotificationHub(settings); using var svc = new AlertService(hub, settings, () => "Radio Nova");
        Redactor.AddSecret("hunter2-alert");
        hub.Notify(NotifyKind.Disconnected, "Server disconnected", "Icecast 1: connection lost, password hunter2-alert");
        Assert.True(await Wait.For(() => hook.Hits == 1 && smtp.Data.Length > 0, 8000), $"hits={hook.Hits} smtp={smtp.Data.Length}");
        Assert.Contains("Radio Nova", hook.LastBody); Assert.Contains("Server disconnected", hook.LastBody); Assert.DoesNotContain("hunter2-alert", hook.LastBody);
        Assert.Contains("dj@example.com", smtp.Rcpt); Assert.DoesNotContain("hunter2-alert", smtp.Data);
        hub.Notify(NotifyKind.Disconnected, "Server disconnected", "again"); await Task.Delay(800);
        Assert.Equal(1, hook.Hits); // same alert within a minute is suppressed
        hub.Notify(NotifyKind.RecordingCompleted, "Recording completed", "x"); await Task.Delay(500);
        Assert.Equal(1, hook.Hits); // not an alert kind
        Assert.DoesNotContain("secret-token", System.Text.Json.JsonSerializer.Serialize(settings.Alerts, SafeJson.Options)); // URL stored protected
        var res = await svc.SendAsync("Test", "hello"); Assert.Contains("webhook: sent", res); Assert.Contains("e-mail: sent", res);
    }

    [Fact]
    public async Task Failing_webhook_reports_an_error_without_throwing()
    {
        var settings = new AppSettings(); settings.Alerts.Enabled = true; settings.Alerts.WebhookUrl = "http://127.0.0.1:1/x";
        var hub = new NotificationHub(settings); using var svc = new AlertService(hub, settings, () => "R");
        Assert.Contains("failed", await svc.SendAsync("t", "m"));
    }
}

public class WebPageTests
{
    [Fact]
    public async Task Now_playing_page_and_json_reflect_the_station_and_listen_on_loopback_by_default()
    {
        using var t = new TempRoot(); using var st = Station.Open(t);
        st.PlayTracks(new[] { t.Wav("web song.wav", 20) }); st.Profile.Metadata.DjName = "DJ Test";
        st.Settings.WebEnabled = true; st.Settings.WebPort = 0; st.ApplyWebSettings();
        Assert.True(await Wait.For(() => st.NowPlaying.Title.Contains("web song"), 5000));
        using var http = new HttpClient();
        var json = System.Text.Json.JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{st.Web.Port}/nowplaying.json")).RootElement;
        Assert.Equal("Test Radio", json.GetProperty("station").GetString()); Assert.Contains("web song", json.GetProperty("title").GetString()); Assert.Equal("DJ Test", json.GetProperty("dj").GetString());
        Assert.False(json.GetProperty("onAir").GetBoolean()); Assert.Equal(System.Text.Json.JsonValueKind.Null, json.GetProperty("listeners").ValueKind); // never fakes listeners
        var html = await http.GetStringAsync($"http://127.0.0.1:{st.Web.Port}/"); Assert.Contains("nowplaying.json", html);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"http://127.0.0.1:{st.Web.Port}/secret")).StatusCode);
        st.Settings.WebEnabled = false; st.ApplyWebSettings(); await Task.Delay(200);
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => http.GetStringAsync($"http://127.0.0.1:{st.Web.Port}/nowplaying.json"));
    }
}

public class VoiceAndArchiveTests
{
    [Fact]
    public async Task Voice_track_records_the_mic_privately_and_inserts_into_a_playlist()
    {
        using var t = new TempRoot(); using var st = Station.Open(t);
        st.Engine.Mic.Source = new ToneProvider(0.3f); st.Profile.Audio.Mic.Muted = true;
        var a = st.Db.ImportFile(t.Wav("one.wav", 3))!; var b = st.Db.ImportFile(t.Wav("two.wav", 3, 500))!;
        var pl = st.Db.CreatePlaylist("Show"); st.Db.SavePlaylistTracks(pl, new[] { a.Id, b.Id });
        Assert.Null(st.Voice.Start(st.VoiceFolder));
        await Task.Delay(1800);
        Assert.True(st.Engine.MasterMeter.Rms < 0.001f, "voice must not go on air: " + st.Engine.MasterMeter.Rms); // mic is NOT in the program
        Assert.True(st.Engine.DuckReductionDb < 1);
        var file = await st.Voice.StopAsync(); Assert.NotNull(file);
        using (var r = new NAudio.Wave.AudioFileReader(file!)) { Assert.InRange(r.TotalTime.TotalSeconds, 1.2, 3.0); var buf = new float[44100]; r.Read(buf, 0, buf.Length); Assert.True(buf.Max() > 0.2f); }
        var tr = st.AddVoiceTrack(file!, pl, 0); Assert.NotNull(tr);
        var order = st.Db.GetPlaylistTracks(pl); Assert.Equal(3, order.Count); Assert.Equal(tr!.Id, order[1].Id); Assert.StartsWith("Voice link", order[1].Title);
        Assert.False(st.Engine.MicPrivate); Assert.False(st.Engine.Mic.ForceOpen);
        Assert.True(await Wait.For(() => st.Engine.MicStrip.Meter.PeakL < 0.01f, 4000)); // back to muted (meter decays)
    }

    [Fact]
    public async Task Archived_show_is_recorded_for_its_duration_and_listed_in_the_podcast_feed()
    {
        using var t = new TempRoot(); using var st = Station.Open(t);
        st.Profile.Recording.Format = RecordFormat.Wav; st.Profile.Recording.PodcastBaseUrl = "https://example.com/shows/";
        st.Profile.Website = "https://radio.example";
        st.PlayTracks(new[] { t.Wav("bed.wav", 30) });
        var pl = st.Db.CreatePlaylist("p");
        var now = TimeZoneInfo.ConvertTime(DateTime.UtcNow, st.Profile.TimeZone); var start = now.AddSeconds(-2);
        var ev = new ScheduledEvent { Title = "Morning & Friends", Kind = EventKind.RadioProgram, Source = SourceKind.Playlist, SourceRef = pl.ToString(), StartDate = DateOnly.FromDateTime(start), StartTime = TimeOnly.FromDateTime(start), DurationMinutes = 5, Archive = true };
        st.SaveEvent(ev);
        Assert.True(await Wait.For(() => st.Recorder.State == RecorderState.Recording, 6000), "archive recording did not start");
        await Task.Delay(1500);
        st.DeleteEvent(ev); // the show ends
        Assert.True(await Wait.For(() => st.Db.GetRecordings().Count == 1, 8000), "recording not saved");
        var rec = st.Db.GetRecordings()[0]; Assert.Equal("Morning & Friends", rec.Title); Assert.Contains("Morning", Path.GetFileName(rec.Path));
        var feed = Path.Combine(Path.GetDirectoryName(rec.Path)!, "podcast.xml");
        Assert.True(await Wait.For(() => File.Exists(feed), 3000));
        var xml = System.Xml.Linq.XDocument.Load(feed); var item = xml.Descendants("item").Single();
        Assert.Equal("Morning & Friends", item.Element("title")!.Value);
        Assert.StartsWith("https://example.com/shows/", item.Element("enclosure")!.Attribute("url")!.Value);
        Assert.Equal("audio/wav", item.Element("enclosure")!.Attribute("type")!.Value);
    }
}
