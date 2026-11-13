using System.Diagnostics;
using System.Threading.Channels;
using NAudio.Lame;
using NAudio.Wave;
using RadioForge.Core.Audio;

namespace RadioForge.Core.Streaming;

public sealed class EncoderException(string message) : Exception(message);

public interface IEncoder : IDisposable
{
    string ContentType { get; }
    event Action<byte[]> Output;
    /// <summary>Interleaved float PCM already in the encoder's sample rate / channel layout.</summary>
    void Encode(float[] pcm, int count);
}

public static class Ffmpeg
{
    /// <summary>Where the in-app downloader puts ffmpeg.exe (per user, no admin rights needed).</summary>
    public static string DownloadedPath => Path.Combine(AppPaths.Root, "ffmpeg", "ffmpeg.exe");
    public const string DownloadUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-lgpl.zip";

    /// <summary>Downloads the LGPL FFmpeg build from the BtbN project on GitHub (only when the user asks) and extracts ffmpeg.exe. Returns its path.</summary>
    public static async Task<string> DownloadAsync(IProgress<double>? progress = null, CancellationToken ct = default, string? url = null)
    {
        var dest = DownloadedPath; Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var zip = dest + ".zip.part";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
            using var resp = await http.GetAsync(url ?? DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? 0;
            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(zip))
            {
                var buf = new byte[81920]; long read = 0; int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0) { await dst.WriteAsync(buf.AsMemory(0, n), ct); read += n; if (total > 0) progress?.Report(read / (double)total * 0.9); }
            }
            using (var za = System.IO.Compression.ZipFile.OpenRead(zip))
            {
                var entry = za.Entries.FirstOrDefault(e => e.FullName.EndsWith("/bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException("ffmpeg.exe not found in the downloaded archive.");
                System.IO.Compression.ZipFileExtensions.ExtractToFile(entry, dest + ".new", true);
            }
            File.Move(dest + ".new", dest, true);
            progress?.Report(1);
            string check = "";
            try { check = await RunAsync(dest, "-hide_banner -version"); } catch (Exception) { }
            if (!check.Contains("ffmpeg version")) { try { File.Delete(dest); } catch { } throw new InvalidDataException("The downloaded ffmpeg.exe does not run on this PC."); }
            Log.Info("FFmpeg", "Downloaded FFmpeg to " + dest);
            return dest;
        }
        finally { try { File.Delete(zip); } catch { } }
    }

    public static string? Locate(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        var local = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
        if (File.Exists(local)) return local;
        if (File.Exists(DownloadedPath)) return DownloadedPath;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try { var p = Path.Combine(dir.Trim('"'), "ffmpeg.exe"); if (File.Exists(p)) return p; } catch { }
        }
        try
        {
            var winget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(winget))
                foreach (var f in Directory.EnumerateFiles(winget, "ffmpeg.exe", SearchOption.AllDirectories).Take(1)) return f;
        }
        catch { }
        return null;
    }

    public static async Task<string> RunAsync(string exe, string args, int timeoutMs = 8000)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true })!;
        var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } }
        return await o + await e;
    }

    public static async Task<bool> HasEncoderAsync(string exe, string name)
    {
        try { return (await RunAsync(exe, "-hide_banner -encoders")).Contains(" " + name + " "); } catch { return false; }
    }

    public static string EncoderFor(AudioCodec c) => c switch { AudioCodec.Aac => "aac", AudioCodec.Ogg => "libvorbis", _ => "libmp3lame" };
}

internal sealed class PushStream : Stream
{
    public event Action<byte[]>? Data;
    public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) { var c = new byte[count]; Array.Copy(buffer, offset, c, 0, count); Data?.Invoke(c); }
}

/// <summary>In-process MP3 encoder (LAME, CBR).</summary>
public sealed class Mp3Encoder : IEncoder
{
    private readonly LameMP3FileWriter _w;
    public string ContentType => "audio/mpeg";
    public event Action<byte[]>? Output;
    public Mp3Encoder(int bitrateKbps, int rate, int channels)
    {
        var ps = new PushStream(); ps.Data += d => Output?.Invoke(d);
        try { _w = new LameMP3FileWriter(ps, new WaveFormat(rate, 16, channels), bitrateKbps); }
        catch (Exception ex) { throw new EncoderException($"MP3 encoder cannot start ({bitrateKbps} kbps, {rate} Hz, {channels} ch): {ex.Message}"); }
    }
    public void Encode(float[] pcm, int count) { var b = PushConverter.ToPcm16(pcm, count); _w.Write(b, 0, b.Length); }
    public void Dispose() { try { _w.Dispose(); } catch { } }
}

/// <summary>AAC / Ogg Vorbis encoder through an FFmpeg child process (stdin PCM → stdout stream).</summary>
public sealed class FfmpegEncoder : IEncoder
{
    private readonly Process _p;
    private readonly Thread _reader;
    private readonly AudioCodec _codec;
    public string ContentType => CodecInfo.ContentType(_codec);
    public event Action<byte[]>? Output;
    public bool Exited => _p.HasExited;

    public FfmpegEncoder(string exe, AudioCodec codec, int kbps, int rate, int channels)
    {
        _codec = codec;
        var fmt = codec == AudioCodec.Aac ? "-c:a aac -f adts" : "-c:a libvorbis -f ogg";
        var args = $"-hide_banner -loglevel error -f f32le -ar {rate} -ac {channels} -i pipe:0 {fmt} -b:a {kbps}k pipe:1";
        try
        {
            _p = Process.Start(new ProcessStartInfo(exe, args) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true })!;
        }
        catch (Exception ex) { throw new EncoderException("FFmpeg could not be started: " + ex.Message); }
        _p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log.Warn("FFmpeg", e.Data); };
        _p.BeginErrorReadLine();
        _reader = new Thread(() =>
        {
            var buf = new byte[8192]; var s = _p.StandardOutput.BaseStream;
            try { int n; while ((n = s.Read(buf, 0, buf.Length)) > 0) { var c = new byte[n]; Array.Copy(buf, c, n); Output?.Invoke(c); } } catch { }
        }) { IsBackground = true, Name = "ffmpeg-out" };
        _reader.Start();
    }

    public void Encode(float[] pcm, int count)
    {
        if (_p.HasExited) throw new EncoderException("FFmpeg encoder exited unexpectedly (check the encoder/bitrate settings; see the log).");
        var b = new byte[count * 4]; Buffer.BlockCopy(pcm, 0, b, 0, b.Length);
        try { _p.StandardInput.BaseStream.Write(b, 0, b.Length); _p.StandardInput.BaseStream.Flush(); }
        catch (IOException ex) { throw new EncoderException("FFmpeg encoder pipe closed: " + ex.Message); }
    }

    public void Dispose()
    {
        try { _p.StandardInput.Close(); } catch { }
        try { if (!_p.WaitForExit(1500)) _p.Kill(true); } catch { }
        _p.Dispose();
    }
}

public static class EncoderFactory
{
    public static IEncoder Create(EncoderSettings s, string? ffmpegPath)
    {
        if (s.Codec == AudioCodec.Mp3) return new Mp3Encoder(s.BitrateKbps, s.SampleRate, s.Channels);
        var exe = Ffmpeg.Locate(ffmpegPath) ?? throw new EncoderException($"{s.Codec} streaming requires FFmpeg, which was not found. Install FFmpeg or set its path in Settings → Advanced.");
        return new FfmpegEncoder(exe, s.Codec, s.BitrateKbps, s.SampleRate, s.Channels);
    }
}

/// <summary>A consumer of an encoded stream. Slow consumers lose old data instead of blocking the encoder.</summary>
public sealed class EncoderSubscription : IDisposable
{
    internal readonly Channel<byte[]> Chan = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly Action _release;
    public ChannelReader<byte[]> Reader => Chan.Reader;
    public string ContentType { get; }
    public string? Error { get; internal set; }
    internal EncoderSubscription(string contentType, Action release) { ContentType = contentType; _release = release; }
    public void Dispose() { _release(); Chan.Writer.TryComplete(); }
    public void Drain() { while (Chan.Reader.TryRead(out _)) { } }
}

/// <summary>
/// Owns the running encoders. Destinations with identical (frame-based) encoder settings share one encoder;
/// Ogg destinations always get a private encoder because late joiners would miss the stream headers.
/// </summary>
public sealed class EncoderHub : IDisposable
{
    private sealed class Shared
    {
        public string Key = ""; public IEncoder Encoder = null!; public BusSubscription Bus = null!; public Task Task = null!;
        public CancellationTokenSource Cts = new(); public List<EncoderSubscription> Subs = new(); public long Bytes;
    }

    private readonly AudioBus _bus; private readonly Func<string?> _ffmpeg;
    private readonly Dictionary<string, Shared> _enc = new(); private readonly object _gate = new();
    public EncoderHub(AudioBus masterBus, Func<string?> ffmpegPath) { _bus = masterBus; _ffmpeg = ffmpegPath; }
    public int ActiveEncoders { get { lock (_gate) return _enc.Count; } }

    public EncoderSubscription Acquire(EncoderSettings s, string ownerId)
    {
        var key = s.Codec == AudioCodec.Ogg ? $"{s}|{ownerId}" : s.ToString();
        lock (_gate)
        {
            if (!_enc.TryGetValue(key, out var sh))
            {
                sh = new Shared { Key = key, Encoder = EncoderFactory.Create(s, _ffmpeg()) };
                var conv = new PushConverter(_bus.SampleRate, s.SampleRate, s.Channels);
                sh.Bus = _bus.Subscribe();
                var shared = sh;
                sh.Encoder.Output += data =>
                {
                    Interlocked.Add(ref shared.Bytes, data.Length);
                    EncoderSubscription[] subs; lock (_gate) subs = shared.Subs.ToArray();
                    foreach (var sub in subs) sub.Chan.Writer.TryWrite(data);
                };
                sh.Task = Task.Run(() => Loop(shared, conv));
                _enc[key] = sh;
                Log.Info("Encoder", $"Started {s.Codec} {s.BitrateKbps} kbps {s.SampleRate} Hz {s.Channels}ch ({(s.Codec == AudioCodec.Ogg ? "dedicated" : "shared")})");
            }
            EncoderSubscription? subscription = null;
            subscription = new EncoderSubscription(sh.Encoder.ContentType, () => Release(key, subscription!));
            sh.Subs.Add(subscription);
            return subscription;
        }
    }

    private async Task Loop(Shared sh, PushConverter conv)
    {
        try
        {
            await foreach (var blk in sh.Bus.Reader.ReadAllAsync(sh.Cts.Token))
                foreach (var (data, n) in conv.Process(blk.Data, blk.Count))
                    sh.Encoder.Encode(data, n);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error("Encoder", $"Encoder {sh.Key} failed", ex);
            lock (_gate) { _enc.Remove(sh.Key); foreach (var s in sh.Subs) { s.Error = ex.Message; s.Chan.Writer.TryComplete(); } }
        }
    }

    private void Release(string key, EncoderSubscription sub)
    {
        Shared? dead = null;
        lock (_gate)
        {
            if (!_enc.TryGetValue(key, out var sh)) return;
            sh.Subs.Remove(sub);
            if (sh.Subs.Count == 0) { _enc.Remove(key); dead = sh; }
        }
        if (dead != null) Task.Run(() => { dead.Cts.Cancel(); dead.Bus.Dispose(); try { dead.Task.Wait(1000); } catch { } dead.Encoder.Dispose(); Log.Info("Encoder", "Stopped " + dead.Key); });
    }

    public void Dispose()
    {
        List<Shared> all; lock (_gate) { all = _enc.Values.ToList(); _enc.Clear(); }
        foreach (var d in all) { d.Cts.Cancel(); d.Bus.Dispose(); try { d.Task.Wait(1000); } catch { } d.Encoder.Dispose(); }
    }
}
