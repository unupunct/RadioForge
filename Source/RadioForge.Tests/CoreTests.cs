using System.Text.Json;
using RadioForge.Core;
using RadioForge.Core.Audio;
using RadioForge.Core.Station;
using RadioForge.Core.Streaming;

namespace RadioForge.Tests;

public class ScheduleTests
{
    private static ScheduledEvent Ev(string title, string date, string time, int mins, RepeatMode r = RepeatMode.None, int prio = 1) =>
        new() { Title = title, StartDate = DateOnly.Parse(date), StartTime = TimeOnly.Parse(time), DurationMinutes = mins, Repeat = r, Priority = prio };

    [Fact]
    public void Daily_event_occurs_every_day()
    {
        var e = Ev("Morning", "2026-01-01", "08:00", 120, RepeatMode.Daily);
        var occ = ScheduleEngine.Expand(e, new DateTime(2026, 3, 1), new DateTime(2026, 3, 4)).ToList();
        Assert.Equal(3, occ.Count);
        Assert.All(occ, o => Assert.Equal(8, o.Start.Hour));
    }

    [Fact]
    public void Weekly_uses_day_mask()
    {
        var e = Ev("Show", "2026-01-05", "18:00", 60, RepeatMode.Weekly); e.DaysMask = (1 << (int)DayOfWeek.Monday) | (1 << (int)DayOfWeek.Friday);
        var occ = ScheduleEngine.Expand(e, new DateTime(2026, 1, 5), new DateTime(2026, 1, 13)).ToList();
        Assert.Equal(new[] { DayOfWeek.Monday, DayOfWeek.Friday, DayOfWeek.Monday }, occ.Select(o => o.Start.DayOfWeek).ToArray());
    }

    [Fact]
    public void Specific_date_and_disabled()
    {
        var e = Ev("Once", "2026-06-01", "12:00", 15);
        Assert.Single(ScheduleEngine.Expand(e, new DateTime(2026, 5, 31), new DateTime(2026, 6, 3)));
        Assert.Empty(ScheduleEngine.Expand(e, new DateTime(2026, 6, 2), new DateTime(2026, 6, 9)));
        e.Enabled = false; Assert.Empty(ScheduleEngine.Expand(e, new DateTime(2026, 5, 31), new DateTime(2026, 6, 3)));
    }

    [Fact]
    public void Repeat_until_and_midnight_crossing()
    {
        var e = Ev("Late", "2026-01-01", "23:30", 90, RepeatMode.Daily); e.RepeatUntil = DateOnly.Parse("2026-01-02");
        Assert.Equal(2, ScheduleEngine.Expand(e, new DateTime(2026, 1, 1), new DateTime(2026, 1, 10)).Count());
        Assert.NotNull(ScheduleEngine.Active(new[] { e }, new DateTime(2026, 1, 2, 0, 30, 0))); // crosses midnight
    }

    [Fact]
    public void Conflicts_are_detected_and_priority_picks_active()
    {
        var a = Ev("Music", "2026-02-02", "10:00", 120); var b = Ev("News", "2026-02-02", "11:00", 30, prio: 5);
        var c = ScheduleEngine.Conflicts(new[] { a, b }, new DateTime(2026, 2, 2), new DateTime(2026, 2, 3));
        Assert.Single(c);
        Assert.Equal("News", ScheduleEngine.Active(new[] { a, b }, new DateTime(2026, 2, 2, 11, 10, 0))!.Event.Title);
        Assert.Equal("Music", ScheduleEngine.Active(new[] { a, b }, new DateTime(2026, 2, 2, 10, 10, 0))!.Event.Title);
        var cand = Ev("X", "2026-02-02", "11:15", 30);
        Assert.NotEmpty(ScheduleEngine.ConflictsFor(cand, new[] { a, b }, new DateTime(2026, 2, 2), new DateTime(2026, 2, 3)));
    }

    [Fact]
    public void Runner_fires_start_and_end_even_without_ui()
    {
        var now = new DateTime(2026, 4, 1, 7, 59, 59);
        var e = Ev("Morning Show", "2026-04-01", "08:00", 1);
        var started = new List<string>(); var ended = new List<string>();
        using var r = new ScheduleRunner(() => new[] { e }, () => now);
        r.Started += o => started.Add(o.Event.Title); r.Ended += o => ended.Add(o.Event.Title);
        r.Tick(); Assert.Empty(started);
        now = new DateTime(2026, 4, 1, 8, 0, 1); r.Tick(); r.Tick(); Assert.Single(started);
        now = new DateTime(2026, 4, 1, 8, 1, 2); r.Tick(); Assert.Single(ended);
    }

    [Fact]
    public void Next_event()
    {
        var e = Ev("Noon", "2026-01-01", "12:00", 15, RepeatMode.Daily);
        Assert.Equal(new DateTime(2026, 5, 5, 12, 0, 0), ScheduleEngine.Next(new[] { e }, new DateTime(2026, 5, 5, 9, 0, 0))!.Start);
    }
}

public class SecurityTests
{
    [Fact]
    public void Password_is_dpapi_protected_and_not_in_json()
    {
        var p = new ServerProfile { Name = "x" }; p.Password = "hunter2-secret";
        var json = JsonSerializer.Serialize(p, SafeJson.Options);
        Assert.DoesNotContain("hunter2-secret", json);
        Assert.NotEmpty(p.PasswordProtected);
        var back = JsonSerializer.Deserialize<ServerProfile>(json, SafeJson.Options)!;
        Assert.Equal("hunter2-secret", back.Password);
    }

    [Fact]
    public void Redactor_removes_secrets_and_basic_auth()
    {
        Redactor.AddSecret("topsecretvalue");
        Assert.DoesNotContain("topsecretvalue", Redactor.Redact("login with topsecretvalue now"));
        Assert.DoesNotContain("abc123==", Redactor.Redact("Authorization: Basic abc123=="));
        Assert.DoesNotContain("pw99", Redactor.Redact("GET /admin.cgi?pass=pw99&mode=updinfo"));
    }

    [Fact]
    public void Log_never_contains_password()
    {
        using var t = new TempRoot();
        var p = new ServerProfile(); p.Password = "very-private-pw";
        Log.Info("Test", "connecting with very-private-pw to server");
        Assert.DoesNotContain(Log.Snapshot("Test"), e => e.Message.Contains("very-private-pw"));
        Log.Shutdown();
        foreach (var f in Directory.GetFiles(AppPaths.Logs)) Assert.DoesNotContain("very-private-pw", File.ReadAllText(f));
    }

    [Fact]
    public void Profile_export_has_no_passwords()
    {
        using var t = new TempRoot();
        var prof = ProfileStore.Create("Export Me");
        var sp = new ServerProfile { Name = "S" }; sp.Password = "exported-pw-123"; prof.Servers.Add(sp); ProfileStore.Save(prof);
        var db = ProfileStore.OpenDb(prof.Id);
        var file = Path.Combine(t.Dir, "p.rfprofile");
        ProfileStore.Export(prof, db, file);
        Assert.DoesNotContain("exported-pw-123", File.ReadAllText(file));
        var imported = ProfileStore.Import(file);
        Assert.Equal("", imported.Servers[0].Password);
        Assert.NotEqual(prof.Id, imported.Id);
    }

    [Fact]
    public void Config_save_is_atomic_with_backup_and_recovers_from_corruption()
    {
        using var t = new TempRoot();
        var path = Path.Combine(AppPaths.Root, "x.json");
        SafeJson.Save(path, new AppSettings { Theme = "A" }); SafeJson.Save(path, new AppSettings { Theme = "B" });
        File.WriteAllText(path, "{ corrupt");
        var s = SafeJson.Load<AppSettings>(path);
        Assert.Equal("A", s!.Theme); // recovered from .bak
    }
}

public class ValidationTests
{
    [Fact]
    public void Valid_profile_passes() { var p = new ServerProfile { Host = "a.b", Port = 8000 }; p.Password = "pw"; Assert.Empty(ConfigValidator.Validate(p)); }

    [Theory]
    [InlineData("http://host", 8000, "/s", 128, 44100)]
    [InlineData("host", 0, "/s", 128, 44100)]
    [InlineData("host", 8000, "stream", 128, 44100)]
    [InlineData("host", 8000, "/s", 5, 44100)]
    [InlineData("host", 8000, "/s", 128, 12345)]
    public void Invalid_settings_are_rejected(string host, int port, string mount, int kbps, int rate)
    {
        var p = new ServerProfile { Host = host, Port = port, Mount = mount, BitrateKbps = kbps, SampleRate = rate }; p.Password = "pw";
        Assert.NotEmpty(ConfigValidator.Validate(p));
    }

    [Fact] public void Missing_password_is_rejected() => Assert.Contains(ConfigValidator.Validate(new ServerProfile { Host = "h" }), e => e.Contains("password"));
}

public class LibraryTests
{
    [Fact]
    public void Import_playlists_duplicates_m3u_and_missing()
    {
        using var t = new TempRoot();
        var db = new Database(Path.Combine(AppPaths.Root, "t.db"));
        var a = t.Wav("a.wav", 2); var b = t.Wav("b.wav", 3, 330);
        var dir = Path.Combine(t.Dir, "copy"); Directory.CreateDirectory(dir); File.Copy(a, Path.Combine(dir, "a.wav"));
        Assert.Equal(3, db.ImportFolder(t.Dir));
        var tracks = db.GetTracks();
        Assert.Equal(3, tracks.Count);
        Assert.All(tracks, x => Assert.True(x.DurationSec > 1.5));
        Assert.Single(db.FindDuplicates()); // a.wav twice
        var id = db.CreatePlaylist("Mix");
        db.SavePlaylistTracks(id, new[] { tracks[1].Id, tracks[0].Id });
        Assert.Equal(new[] { tracks[1].Id, tracks[0].Id }, db.GetPlaylistTracks(id).Select(x => x.Id).ToArray());
        var m3u = Path.Combine(t.Dir, "mix.m3u"); db.ExportM3u(id, m3u);
        var imported = db.ImportM3u(m3u, "Imported");
        Assert.Equal(2, db.GetPlaylistTracks(imported).Count);
        File.Delete(b);
        Assert.Contains(db.GetTracks(), x => x.Missing);
        Assert.Single(db.GetTracks("b"));
        var folder = db.CreatePlaylist("F", null, true); var child = db.CreatePlaylist("C", folder);
        db.DeletePlaylist(folder); Assert.DoesNotContain(db.GetPlaylists(), p => p.Id == child);
    }

    [Fact]
    public void Schedule_and_recordings_persist()
    {
        using var t = new TempRoot();
        var db = new Database(Path.Combine(AppPaths.Root, "t.db"));
        db.SaveEvent(new ScheduledEvent { Title = "Persisted", Repeat = RepeatMode.Weekly });
        Assert.Equal("Persisted", new Database(db.FilePath).GetEvents().Single().Title);
        db.AddRecording(new RecordingInfo(0, "x.wav", DateTime.Now, 5, 100, "WAV", "Master"));
        Assert.Single(db.GetRecordings());
    }

    [Fact]
    public void Backup_and_restore_roundtrip()
    {
        using var t = new TempRoot();
        var p = ProfileStore.Create("Backed up"); ProfileStore.SaveSettings(new AppSettings { Theme = "Light" });
        var zip = BackupService.Create(Path.Combine(t.Dir, "bk"));
        ProfileStore.Delete(p.Id); Assert.Null(ProfileStore.Load(p.Id));
        BackupService.Restore(zip);
        Assert.Equal("Backed up", ProfileStore.Load(p.Id)!.Name);
    }
}

public class EncoderTests
{
    [Fact]
    public void Mp3_encoder_emits_mpeg_frames()
    {
        var enc = new Mp3Encoder(128, 44100, 2); var bytes = new List<byte>();
        enc.Output += d => bytes.AddRange(d);
        var pcm = new float[44100 * 2];
        for (int i = 0; i < 44100; i++) pcm[2 * i] = pcm[2 * i + 1] = (float)Math.Sin(2 * Math.PI * 440 * i / 44100) * 0.5f;
        enc.Encode(pcm, pcm.Length); enc.Encode(pcm, pcm.Length); enc.Dispose();
        Assert.True(bytes.Count > 20000, $"only {bytes.Count} bytes");
        int sync = bytes.FindIndex(b => b == 0xFF);
        Assert.True((bytes[sync + 1] & 0xE0) == 0xE0);
        // 128 kbps ≈ 16000 bytes/s → 2 s ≈ 32000
        Assert.InRange(bytes.Count, 25000, 40000);
    }

    [Fact]
    public async Task Ffmpeg_aac_and_ogg_encode_when_ffmpeg_is_available()
    {
        var exe = Ffmpeg.Locate(null);
        if (exe == null) return; // documented: skipped without FFmpeg
        foreach (var codec in new[] { AudioCodec.Aac, AudioCodec.Ogg })
        {
            using var enc = new FfmpegEncoder(exe, codec, 96, 44100, 2); var bytes = new List<byte>();
            enc.Output += d => { lock (bytes) bytes.AddRange(d); };
            var pcm = new float[44100 * 2];
            for (int i = 0; i < 44100; i++) pcm[2 * i] = pcm[2 * i + 1] = (float)Math.Sin(2 * Math.PI * 440 * i / 44100) * 0.5f;
            for (int k = 0; k < 4; k++) enc.Encode(pcm, pcm.Length);
            Assert.True(await Wait.For(() => { lock (bytes) return bytes.Count > 2000; }, 8000), codec + " produced no data");
            if (codec == AudioCodec.Ogg) { lock (bytes) Assert.Equal("OggS", System.Text.Encoding.ASCII.GetString(bytes.Take(4).ToArray())); }
        }
    }

    [Fact]
    public void Push_converter_resamples_and_downmixes()
    {
        var c = new PushConverter(48000, 22050, 1); int total = 0;
        var pcm = new float[48000 * 2];
        for (int k = 0; k < 2; k++) foreach (var (_, n) in c.Process(pcm, pcm.Length)) total += n;
        Assert.InRange(total, 40000, 46000); // ≈ 2 s of mono at 22.05 kHz
    }

    [Fact]
    public async Task Compatible_destinations_share_one_encoder_incompatible_do_not()
    {
        using var t = new TempRoot();
        using var st = Station.Open(t);
        var hub = st.Streams.Hub;
        using var a = hub.Acquire(new EncoderSettings(AudioCodec.Mp3, 128, 44100, 2), "1");
        using var b = hub.Acquire(new EncoderSettings(AudioCodec.Mp3, 128, 44100, 2), "2");
        Assert.Equal(1, hub.ActiveEncoders);
        using var c = hub.Acquire(new EncoderSettings(AudioCodec.Mp3, 192, 44100, 2), "3");
        Assert.Equal(2, hub.ActiveEncoders);
        a.Dispose(); b.Dispose(); await Task.Delay(100);
        Assert.Equal(1, hub.ActiveEncoders);
    }
}
