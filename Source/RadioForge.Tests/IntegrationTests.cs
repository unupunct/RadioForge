using RadioForge.Core;
using RadioForge.Core.Audio;
using RadioForge.Core.Station;
using RadioForge.Core.Streaming;

namespace RadioForge.Tests;

/// <summary>Real TCP integration tests: actual encoders, actual protocol code, local Icecast/SHOUTcast-compatible servers.</summary>
public class StreamingIntegrationTests
{
    [Fact]
    public async Task Icecast_stream_delivers_encoded_music_with_correct_headers()
    {
        using var t = new TempRoot(); using var srv = new FakeServer(FakeServer.Kind.Icecast);
        using var st = Station.Open(t, srv.Profile("Ice", 128));
        var track = t.Wav("tone.wav", 20);
        st.PlayTracks(new[] { track });
        Assert.Empty(st.StartBroadcast());
        var d = st.Streams.Destinations[0];
        Assert.True(await Wait.For(() => d.State == StreamState.Streaming), "state=" + d.State + " " + d.StatusText);
        Assert.True(await Wait.For(() => srv.BytesReceived > 12000, 8000), "bytes=" + srv.BytesReceived);
        Assert.Contains("Authorization: Basic", srv.LastHead);
        Assert.Contains("Content-Type: audio/mpeg", srv.LastHead);
        Assert.Contains("ice-bitrate: 128", srv.LastHead);
        Assert.Equal(0xFF, srv.FirstBytes.SkipWhile(b => b != 0xFF).First());
        Assert.True(await Wait.For(() => d.BitrateKbps > 60, 6000), "live bitrate=" + d.BitrateKbps);
        Assert.True(st.OnAir);
        await st.StopBroadcastAsync();
        Assert.Equal(StreamState.Disconnected, d.State);
    }

    [Fact]
    public async Task Shoutcast_v2_stream_works_and_reports_wrong_password()
    {
        using var t = new TempRoot(); using var srv = new FakeServer(FakeServer.Kind.Shoutcast);
        var good = srv.Profile("SC"); var bad = srv.Profile("SC bad"); bad.Password = "wrong";
        using var st = Station.Open(t, good, bad);
        st.PlayTracks(new[] { t.Wav("tone.wav", 20) });
        st.StartBroadcast();
        var g = st.Streams.Destinations[0]; var b = st.Streams.Destinations[1];
        Assert.True(await Wait.For(() => g.State == StreamState.Streaming), g.StatusText);
        Assert.True(await Wait.For(() => b.State == StreamState.AuthFailed, 8000), b.State + " " + b.StatusText);
        Assert.Contains("Authentication failed", b.LastError);
        Assert.Contains("icy-name:SC", srv.LastHead);
        Assert.Contains("content-type:audio/mpeg", srv.LastHead);
        Assert.Equal(StreamState.Streaming, g.State); // one failed server does not interrupt the other
        await st.StopBroadcastAsync();
    }

    [Fact]
    public async Task One_failed_destination_does_not_interrupt_the_others_and_different_encoders_run_together()
    {
        using var t = new TempRoot();
        using var ice = new FakeServer(FakeServer.Kind.Icecast); using var sc = new FakeServer(FakeServer.Kind.Shoutcast);
        var dead = ice.Profile("Dead"); dead.Port = 1; // nothing listens on port 1
        var p1 = ice.Profile("Ice 192", 192); var p2 = sc.Profile("SC 96", 96); p2.SampleRate = 22050; p2.Channels = 1;
        using var st = Station.Open(t, p1, dead, p2);
        st.PlayTracks(new[] { t.Wav("tone.wav", 30) });
        st.StartBroadcast();
        var d = st.Streams.Destinations;
        Assert.True(await Wait.For(() => d[0].State == StreamState.Streaming && d[2].State == StreamState.Streaming, 12000), $"{d[0].State} / {d[2].State}");
        Assert.True(await Wait.For(() => d[1].State is StreamState.ServerUnavailable or StreamState.Reconnecting, 12000), d[1].State + " " + d[1].StatusText);
        Assert.Contains("unavailable", d[1].LastError, StringComparison.OrdinalIgnoreCase);
        Assert.True(st.Streams.Hub.ActiveEncoders >= 2);
        Assert.True(await Wait.For(() => ice.BytesReceived > 20000 && sc.BytesReceived > 8000, 10000), $"{ice.BytesReceived} {sc.BytesReceived}");
        // 192 kbps stream must carry noticeably more data than 96 kbps mono
        await Task.Delay(1500);
        Assert.True(ice.BytesReceived > sc.BytesReceived * 1.5, $"{ice.BytesReceived} vs {sc.BytesReceived}");
        Assert.Equal(StreamState.Streaming, d[0].State); Assert.Equal(StreamState.Streaming, d[2].State);
        await st.StopBroadcastAsync();
    }

    [Fact]
    public async Task Connection_loss_is_reported_and_stream_reconnects_automatically()
    {
        using var t = new TempRoot(); using var srv = new FakeServer(FakeServer.Kind.Icecast);
        using var st = Station.Open(t, srv.Profile("Flaky"));
        st.PlayTracks(new[] { t.Wav("tone.wav", 60) });
        var notes = new List<NotifyKind>(); st.Notifications.Raised += n => notes.Add(n.Kind);
        st.StartBroadcast(); var d = st.Streams.Destinations[0];
        Assert.True(await Wait.For(() => d.State == StreamState.Streaming));
        srv.Kill();
        Assert.True(await Wait.For(() => d.State is StreamState.Reconnecting or StreamState.ServerUnavailable, 12000), d.State.ToString());
        Assert.True(d.ReconnectCount >= 1);
        srv.Restart();
        Assert.True(await Wait.For(() => d.State == StreamState.Streaming, 15000), d.State + " " + d.StatusText);
        Assert.Contains(NotifyKind.Disconnected, notes); Assert.Contains(NotifyKind.Reconnected, notes);
        Assert.NotNull(d.LastSuccess);
        await st.StopBroadcastAsync();
    }

    [Fact]
    public async Task Metadata_updates_follow_track_changes_and_manual_override()
    {
        using var t = new TempRoot(); using var srv = new FakeServer(FakeServer.Kind.Icecast);
        using var st = Station.Open(t, srv.Profile("Meta"));
        st.PlayTracks(new[] { t.Wav("first.wav", 30) });
        st.StartBroadcast(); var d = st.Streams.Destinations[0];
        Assert.True(await Wait.For(() => d.State == StreamState.Streaming));
        Assert.True(await Wait.For(() => srv.LastSong.Contains("first"), 8000), "song=" + srv.LastSong);
        await st.SetManualMetadataAsync("LIVE: Morning Show with Alex");
        Assert.Equal("LIVE: Morning Show with Alex", srv.LastSong);
        await st.SetManualMetadataAsync(null);
        Assert.True(await Wait.For(() => srv.LastSong.Contains("first"), 5000));
        await st.StopBroadcastAsync();
    }

    [Fact]
    public async Task Shoutcast_metadata_and_listener_counts()
    {
        using var t = new TempRoot(); using var srv = new FakeServer(FakeServer.Kind.Shoutcast) { Listeners = 12 };
        var p = srv.Profile("SCM");
        var m = await ServerInfoClient.UpdateMetadataAsync(p, p.Password, "Artist - Song");
        Assert.True(m.Success, m.Message); Assert.Equal("Artist - Song", srv.LastSong);
        Assert.Equal(12, await ServerInfoClient.GetListenersAsync(p));
        var ice = new FakeServer(FakeServer.Kind.Icecast) { Listeners = 5 };
        Assert.Equal(5, await ServerInfoClient.GetListenersAsync(ice.Profile())); ice.Dispose();
        var disabled = srv.Profile(); disabled.SendMetadata = false;
        Assert.False((await ServerInfoClient.UpdateMetadataAsync(disabled, "x", "s")).Supported);
    }

    [Fact]
    public async Task Test_connection_gives_precise_diagnostics()
    {
        using var t = new TempRoot(); using var srv = new FakeServer(FakeServer.Kind.Icecast);
        var ok = await ServerTester.TestAsync(srv.Profile(), null);
        Assert.True(ok.Success, ok.Summary);
        Assert.Contains(ok.Steps, s => s.Name == "Mount point" && s.Status == StepStatus.Pass);

        var badPw = srv.Profile(); badPw.Password = "nope";
        var r = await ServerTester.TestAsync(badPw, null);
        Assert.False(r.Success); Assert.Contains(r.Steps, s => s.Name == "Authentication" && s.Status == StepStatus.Fail);

        var badMount = srv.Profile(); badMount.Mount = "/other";
        r = await ServerTester.TestAsync(badMount, null);
        Assert.Contains(r.Steps, s => s.Name == "Mount point" && s.Status == StepStatus.Fail);

        srv.AllowedContentType = "application/ogg";
        r = await ServerTester.TestAsync(srv.Profile(), null);
        Assert.Contains(r.Steps, s => s.Status == StepStatus.Fail && s.Detail.Contains("content type"));

        var unreachable = srv.Profile(); unreachable.Port = 1;
        r = await ServerTester.TestAsync(unreachable, null);
        Assert.Contains(r.Steps, s => s.Name == "Server reachable" && s.Status == StepStatus.Fail);

        var invalid = srv.Profile(); invalid.Host = "http://bad";
        r = await ServerTester.TestAsync(invalid, null);
        Assert.Contains(r.Steps, s => s.Name == "Configuration" && s.Status == StepStatus.Fail);
    }

    [Fact]
    public async Task Duplicate_mount_is_rejected_and_never_two_pipelines_per_destination()
    {
        using var t = new TempRoot(); using var srv = new FakeServer(FakeServer.Kind.Icecast);
        using var st = Station.Open(t, srv.Profile("Once"));
        st.PlayTracks(new[] { t.Wav("tone.wav", 20) });
        var d = st.Streams.Destinations[0];
        Assert.True(st.Streams.Start(d)); Assert.False(st.Streams.Start(d)); // second start is a no-op
        Assert.True(await Wait.For(() => d.State == StreamState.Streaming));
        await Task.Delay(500);
        Assert.Equal(1, srv.ActiveSources);
        await st.StopBroadcastAsync();
    }

    [Fact]
    public async Task Missing_ffmpeg_is_reported_clearly_for_aac()
    {
        using var t = new TempRoot(); using var srv = new FakeServer(FakeServer.Kind.Icecast);
        var p = srv.Profile("AAC", 128, AudioCodec.Aac);
        using var st = Station.Open(t, p); st.Settings.FfmpegPath = @"C:\does\not\exist\ffmpeg.exe";
        // when FFmpeg is installed on this machine Locate finds it through PATH, so only assert the message path when absent
        if (Ffmpeg.Locate(st.Settings.FfmpegPath) != null) return;
        st.StartBroadcast(); var d = st.Streams.Destinations[0];
        Assert.True(await Wait.For(() => d.State == StreamState.Error, 8000));
        Assert.Contains("FFmpeg", d.LastError);
    }

    [Fact]
    public async Task Aac_and_ogg_stream_through_ffmpeg_when_available()
    {
        if (Ffmpeg.Locate(null) == null) return;
        using var t = new TempRoot(); using var ice = new FakeServer(FakeServer.Kind.Icecast) { AllowedContentType = "audio/aac" };
        using var ogg = new FakeServer(FakeServer.Kind.Icecast) { AllowedContentType = "application/ogg" };
        using var st = Station.Open(t, ice.Profile("AAC", 128, AudioCodec.Aac), ogg.Profile("OGG", 96, AudioCodec.Ogg));
        st.PlayTracks(new[] { t.Wav("tone.wav", 30) }); st.StartBroadcast();
        Assert.True(await Wait.For(() => ice.BytesReceived > 8000 && ogg.BytesReceived > 6000, 15000), $"{ice.BytesReceived} {ogg.BytesReceived}");
        await st.StopBroadcastAsync();
    }
}

public class EngineIntegrationTests
{
    [Fact]
    public async Task Music_plays_through_master_with_meters_and_crossfades_to_next_track()
    {
        using var t = new TempRoot(); using var st = Station.Open(t);
        var a = t.Wav("a.wav", 4, 440); var b = t.Wav("b.wav", 4, 660);
        var started = new List<string>(); st.Engine.Music.TrackStarted += p => started.Add(Path.GetFileName(p));
        st.PlayTracks(new[] { a, b });
        Assert.True(await Wait.For(() => st.Engine.MasterMeter.Peak > 0.05f, 4000), "peak=" + st.Engine.MasterMeter.Peak);
        Assert.True(st.Engine.MusicStrip.Meter.Peak > 0.05f);
        Assert.True(await Wait.For(() => started.Count >= 2, 9000), string.Join(",", started)); // auto-advanced without silence handling by user
        Assert.Equal(new[] { "a.wav", "b.wav" }, started.Take(2).ToArray());
        Assert.True(st.NowPlaying.Title is "b");
    }

    [Fact]
    public async Task Channel_mute_solo_master_volume_and_limiter()
    {
        using var t = new TempRoot(); using var st = Station.Open(t);
        st.Engine.Settings.Mic.DuckingEnabled = false;
        st.PlayTracks(new[] { t.Wav("loud.wav", 20, 440, 44100, 0.95) });
        Assert.True(await Wait.For(() => st.Engine.MasterMeter.Peak > 0.2f));
        st.Engine.MusicStrip.Mute = true;
        await Task.Delay(700); Assert.True(st.Engine.MasterMeter.Rms < 0.005f, "muted rms " + st.Engine.MasterMeter.Rms);
        st.Engine.MusicStrip.Mute = false; st.Engine.SfxStrip.Solo = true; // solo SFX → music excluded from program
        await Task.Delay(700); Assert.True(st.Engine.MasterMeter.Rms < 0.005f);
        st.Engine.SfxStrip.Solo = false;
        st.Engine.Settings.MasterVolume = 2.0; st.Engine.MusicStrip.Volume = 1.5;
        await Task.Delay(800);
        Assert.True(st.Engine.MasterMeter.HoldL <= Db.ToLinear(-1) + 0.02f, "limiter ceiling exceeded: " + st.Engine.MasterMeter.HoldL);
    }

    [Fact]
    public async Task Soundboard_pad_modes_play_over_replace_and_after()
    {
        using var t = new TempRoot(); using var st = Station.Open(t);
        var music = t.Wav("music.wav", 30, 300); var jingle = t.Wav("jingle.wav", 1.2, 900); var next = t.Wav("after.wav", 5, 500);
        st.PlayTracks(new[] { music });
        var pads = st.Profile.Soundboard.Pads; Assert.Equal(48, pads.Count);
        pads[0].FilePath = jingle; pads[0].Mode = PadMode.PlayOverMusic;
        Assert.True(st.PlayPad(pads[0]));
        Assert.True(await Wait.For(() => st.Engine.SfxStrip.Meter.Peak > 0.05f, 3000));
        Assert.Equal(PlayerState.Playing, st.Engine.Music.State); // music continues under the jingle
        Assert.True(await Wait.For(() => !pads[0].IsPlaying, 4000));

        pads[1].FilePath = jingle; pads[1].Mode = PadMode.ReplaceMusic; st.PlayPad(pads[1]);
        Assert.Equal(PlayerState.Paused, st.Engine.Music.State);
        Assert.True(await Wait.For(() => st.Engine.Music.State == PlayerState.Playing, 4000)); // music resumes afterwards

        pads[2].FilePath = next; pads[2].Mode = PadMode.PlayAfterCurrentTrack; st.PlayPad(pads[2]);
        Assert.Equal(next, st.Queue.Peek());

        pads[3].FilePath = jingle; pads[3].Mode = PadMode.Loop; st.PlayPad(pads[3]);
        await Task.Delay(1800); Assert.True(pads[3].IsPlaying); // still looping after its natural length
        st.PlayPad(pads[3]); await Task.Delay(300); Assert.False(pads[3].IsPlaying);

        pads[4].FilePath = @"C:\nope\missing.wav"; var warned = false; st.Notifications.Raised += n => warned |= n.Kind == NotifyKind.MissingFile;
        Assert.False(st.PlayPad(pads[4])); Assert.True(warned);
    }

    [Fact]
    public async Task Missing_track_is_skipped_and_reported_without_stopping_the_queue()
    {
        using var t = new TempRoot(); using var st = Station.Open(t);
        var good1 = t.Wav("g1.wav", 3); var good2 = t.Wav("g2.wav", 3, 500);
        var warned = false; st.Notifications.Raised += n => warned |= n.Kind == NotifyKind.MissingFile;
        var started = new List<string>(); st.Engine.Music.TrackStarted += p => started.Add(Path.GetFileName(p));
        st.PlayTracks(new[] { good1, Path.Combine(t.Dir, "ghost.mp3"), good2 });
        Assert.True(await Wait.For(() => started.Contains("g2.wav"), 9000), string.Join(",", started));
        Assert.True(warned);
    }

    [Fact]
    public async Task Auto_dj_starts_with_broadcast_and_continues_from_library()
    {
        using var t = new TempRoot(); using var st = Station.Open(t);
        st.Db.ImportFile(t.Wav("x1.wav", 2)); st.Db.ImportFile(t.Wav("x2.wav", 2, 500));
        st.Profile.AutoDj.Enabled = true;
        var started = new List<string>(); st.Engine.Music.TrackStarted += p => started.Add(Path.GetFileName(p));
        st.StartBroadcast();
        Assert.True(await Wait.For(() => started.Count >= 3, 10000), string.Join(",", started)); // keeps going: repeat wraps
        Assert.Equal(PlayerState.Playing, st.Engine.Music.State);
    }

    [Fact]
    public async Task Recording_to_wav_and_mp3_while_streaming()
    {
        using var t = new TempRoot(); using var srv = new FakeServer(FakeServer.Kind.Icecast);
        using var st = Station.Open(t, srv.Profile("Rec"));
        st.PlayTracks(new[] { t.Wav("tone.wav", 30) }); st.StartBroadcast();
        Assert.True(await Wait.For(() => st.Streams.Destinations[0].State == StreamState.Streaming));
        RecordingInfo? done = null; st.Recorder.Completed += r => done = r;
        foreach (var fmt in new[] { RecordFormat.Wav, RecordFormat.Mp3 })
        {
            st.Profile.Recording.Format = fmt;
            Assert.Null(st.Recorder.Start());
            await Task.Delay(2500);
            Assert.Equal(RecorderState.Recording, st.Recorder.State);
            st.Recorder.TogglePause(); var e1 = st.Recorder.Elapsed; await Task.Delay(1200);
            Assert.True((st.Recorder.Elapsed - e1).TotalSeconds < 0.6); // paused: no progress
            st.Recorder.TogglePause(); await Task.Delay(500);
            var info = await st.Recorder.StopAsync();
            Assert.NotNull(info); Assert.True(File.Exists(info!.Path)); Assert.True(info.Size > 5000, info.Path);
            Assert.InRange(info.DurationSec, 2.5, 5);
            Assert.EndsWith(fmt == RecordFormat.Wav ? ".wav" : ".mp3", info.Path);
        }
        Assert.NotNull(done); Assert.Equal(2, st.Db.GetRecordings().Count);
        Assert.Equal(StreamState.Streaming, st.Streams.Destinations[0].State); // streaming unaffected
        await st.StopBroadcastAsync();
    }

    [Fact]
    public async Task Recording_a_single_channel_captures_only_that_channel()
    {
        using var t = new TempRoot(); using var st = Station.Open(t);
        st.PlayTracks(new[] { t.Wav("tone.wav", 20) });
        st.Profile.Recording.Source = RecordSource.Microphone; st.Profile.Recording.Format = RecordFormat.Wav;
        Assert.Null(st.Recorder.Start()); await Task.Delay(1500);
        var info = await st.Recorder.StopAsync();
        Assert.NotNull(info);
        using var r = new NAudio.Wave.AudioFileReader(info!.Path); var buf = new float[44100]; float max = 0; int n;
        while ((n = r.Read(buf, 0, buf.Length)) > 0) for (int i = 0; i < n; i++) max = Math.Max(max, Math.Abs(buf[i]));
        Assert.True(max < 0.001f, "mic-only recording contained music: " + max); // mic is muted → silence, music absent
    }

    [Fact]
    public void Microphone_ducking_gate_and_mute_logic()
    {
        var s = new MicSettings { Muted = false, GateEnabled = true, GateThresholdDb = -40, CompressorEnabled = false };
        var src = new ToneProvider(0.3f);
        var mic = new MicProcessor(src, s, 44100); var buf = new float[4410 * 2];
        for (int i = 0; i < 20; i++) mic.Read(buf, 0, buf.Length);
        Assert.True(mic.Level > 0.1f);
        s.Muted = true; mic.Read(buf, 0, buf.Length); Assert.All(buf, v => Assert.Equal(0f, v)); Assert.Equal(0f, mic.Level);
        s.Muted = false; s.PushToTalk = true; mic.PushToTalkDown = false; mic.Read(buf, 0, buf.Length); Assert.All(buf, v => Assert.Equal(0f, v));
        mic.PushToTalkDown = true; for (int i = 0; i < 10; i++) mic.Read(buf, 0, buf.Length); Assert.True(buf.Max() > 0.1f);
        s.PushToTalk = false; s.GateThresholdDb = -3; for (int i = 0; i < 20; i++) mic.Read(buf, 0, buf.Length); Assert.True(buf.Max() < 0.01f); // 0.3 ≈ -10 dB < gate
    }

    [Fact]
    public async Task Ducking_lowers_music_while_dj_speaks_and_recovers()
    {
        using var t = new TempRoot(); using var st = Station.Open(t);
        var m = st.Profile.Audio.Mic; m.DuckingEnabled = true; m.DuckAmountDb = 18; m.DuckThresholdDb = -40; m.DuckAttackMs = 30; m.DuckReleaseMs = 200; m.Muted = false;
        // replace mic source with a speech-level tone (headless: no capture device)
        st.Engine.Mic.Source = new ToneProvider(0.25f);
        st.PlayTracks(new[] { t.Wav("loud.wav", 30, 440, 44100, 0.6) });
        Assert.True(await Wait.For(() => st.Engine.DuckReductionDb > 12, 3000), "duck=" + st.Engine.DuckReductionDb);
        m.Muted = true;
        Assert.True(await Wait.For(() => st.Engine.DuckReductionDb < 1, 4000), "released duck=" + st.Engine.DuckReductionDb);
    }

    [Fact]
    public async Task Scheduler_executes_playlist_event_and_jingle_pad()
    {
        using var t = new TempRoot(); using var st = Station.Open(t);
        var tr = st.Db.ImportFile(t.Wav("sched.wav", 20))!; var pl = st.Db.CreatePlaylist("Scheduled"); st.Db.SavePlaylistTracks(pl, new[] { tr.Id });
        var jingle = t.Wav("j.wav", 1); st.Profile.Soundboard.Pads[5].FilePath = jingle;
        var now = TimeZoneInfo.ConvertTime(DateTime.UtcNow, st.Profile.TimeZone);
        var start = now.AddSeconds(-2);
        st.SaveEvent(new ScheduledEvent { Title = "Morning Show", Kind = EventKind.MusicPlaylist, Source = SourceKind.Playlist, SourceRef = pl.ToString(), StartDate = DateOnly.FromDateTime(start), StartTime = TimeOnly.FromDateTime(start), DurationMinutes = 5 });
        Assert.True(await Wait.For(() => st.Engine.Music.State == PlayerState.Playing && st.Profile.Metadata.ProgramName == "Morning Show", 6000), "program=" + st.Profile.Metadata.ProgramName);
        Assert.Contains("sched", st.NowPlaying.Title);
        var j = now.AddSeconds(-1);
        st.SaveEvent(new ScheduledEvent { Title = "Top of hour", Kind = EventKind.StationJingle, Source = SourceKind.AudioFile, SourceRef = jingle, StartDate = DateOnly.FromDateTime(j), StartTime = TimeOnly.FromDateTime(j), DurationMinutes = 1 });
        Assert.True(await Wait.For(() => st.Engine.SfxStrip.Meter.Peak > 0.05f, 5000), "jingle did not play");
    }

    [Fact]
    public async Task Engine_runs_headless_and_stays_alive_when_output_unavailable()
    {
        using var t = new TempRoot(); using var st = Station.Open(t);
        Assert.False(st.Engine.UsingDevice); Assert.True(st.Engine.IsRunning);
        var f0 = st.Engine.FramesProduced; await Task.Delay(1000);
        Assert.InRange(st.Engine.FramesProduced - f0, 30000, 60000); // real-time pacing from the software clock
    }
}

internal sealed class ToneProvider(float amp) : NAudio.Wave.ISampleProvider
{
    private double _p;
    public NAudio.Wave.WaveFormat WaveFormat { get; } = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
    public int Read(float[] b, int o, int c) { for (int i = 0; i + 1 < c; i += 2) { var v = (float)Math.Sin(_p) * amp; _p += 2 * Math.PI * 300 / 44100; b[o + i] = v; b[o + i + 1] = v; } return c; }
}

public class HardwareTests
{
    [Fact]
    public async Task Real_wasapi_output_and_mic_capture_start_when_devices_exist()
    {
        var outs = AudioDevices.Outputs(); if (outs.Count == 0) return; // no audio hardware (CI): documented as unverified
        using var t = new TempRoot();
        var settings = new AudioSettings { MasterVolume = 0.05 }; settings.Mic.Muted = true;
        using var engine = new AudioEngine(settings);
        var problems = new List<string>(); engine.DeviceProblem += problems.Add;
        engine.Start(true);
        Assert.True(engine.UsingDevice, "output device not opened: " + string.Join("; ", problems));
        var f0 = engine.FramesProduced; await Task.Delay(1000);
        Assert.True(engine.FramesProduced - f0 > 30000, $"device={engine.OutputDeviceName} state={engine.OutputState} frames={engine.FramesProduced - f0} problems={string.Join(";", problems)} all={string.Join(";", outs.Select(o => o.Name))}"); // device clock paces the mixer in real time
        engine.Music.Play(t.Wav("hw.wav", 3)); await Task.Delay(800);
        Assert.True(engine.MasterMeter.Peak > 0.005f);
        if (AudioDevices.Inputs().Count > 0) Assert.True(engine.MicCapture.IsRunning, "mic capture did not start: " + string.Join("; ", problems));
        engine.Stop(); Assert.False(engine.IsRunning);
    }
}
