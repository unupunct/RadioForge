using System.Net;
using System.Net.Sockets;
using System.Text;
using NAudio.Wave;
using RadioForge.Core;
using RadioForge.Core.Station;

namespace RadioForge.Tests;

public sealed class TempRoot : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "rf-test-" + Guid.NewGuid().ToString("N"));
    public TempRoot() { Directory.CreateDirectory(Dir); AppPaths.Root = Path.Combine(Dir, "appdata"); }
    public string Wav(string name, double seconds, double freq = 440, int rate = 44100, double amp = 0.4)
    {
        var path = Path.Combine(Dir, name);
        using var w = new WaveFileWriter(path, new WaveFormat(rate, 16, 2));
        var buf = new short[(int)(seconds * rate) * 2];
        for (int i = 0; i < buf.Length / 2; i++) { var v = (short)(Math.Sin(2 * Math.PI * freq * i / rate) * amp * 32767); buf[2 * i] = v; buf[2 * i + 1] = v; }
        var bytes = new byte[buf.Length * 2]; Buffer.BlockCopy(buf, 0, bytes, 0, bytes.Length);
        w.Write(bytes, 0, bytes.Length);
        return path;
    }
    public void Dispose() { Log.Shutdown(); try { Directory.Delete(Dir, true); } catch { } }
}

public static class Wait
{
    public static async Task<bool> For(Func<bool> cond, int timeoutMs = 10000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < end) { if (cond()) return true; await Task.Delay(50); }
        return cond();
    }
}

public static class Station
{
    public static StationController Open(TempRoot root, params ServerProfile[] servers)
    {
        var settings = new AppSettings();
        var prof = ProfileStore.Create("Test Radio");
        prof.Audio.Mic.Muted = true;
        prof.Audio.CrossfadeSec = 1;
        prof.Recording.Folder = Path.Combine(root.Dir, "rec");
        foreach (var s in servers) prof.Servers.Add(s);
        return new StationController(settings, prof, useAudioDevices: false);
    }
}

/// <summary>Minimal Icecast-2 / SHOUTcast source+admin server used to integration-test the real client code over TCP.</summary>
public sealed class FakeServer : IDisposable
{
    public enum Kind { Icecast, Shoutcast }
    private TcpListener _http = null!, _src = null!;
    private readonly Kind _kind; private CancellationTokenSource _cts = new();
    private readonly List<TcpClient> _clients = new();
    public int Port { get; private set; }
    public string Password = "secret";
    public string Mount = "/live";
    public string AllowedContentType = "audio/mpeg";
    public int Listeners = 7;
    public long BytesReceived; public int SourceConnections; public volatile int ActiveSources;
    public string LastHead = ""; public string LastSong = "";
    public byte[] FirstBytes = Array.Empty<byte>();
    public bool AnyMount;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _mounts = new();

    public FakeServer(Kind kind) { _kind = kind; Listen(0); }

    private void Listen(int port)
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            var http = new TcpListener(IPAddress.Loopback, port); http.Start();
            int p = ((IPEndPoint)http.LocalEndpoint).Port;
            if (_kind == Kind.Icecast) { _http = http; _src = http; Port = p; break; }
            try { var s = new TcpListener(IPAddress.Loopback, p + 1); s.Start(); _http = http; _src = s; Port = p; break; }
            catch { http.Stop(); if (port != 0) throw; }
        }
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => Accept(_http, true, _cts.Token));
        if (_kind == Kind.Shoutcast) _ = Task.Run(() => Accept(_src, false, _cts.Token));
    }

    /// <summary>Simulates a server outage: stops listening and drops all connections.</summary>
    public void Kill()
    {
        _cts.Cancel(); try { _http.Stop(); } catch { } try { _src.Stop(); } catch { }
        lock (_clients) { foreach (var c in _clients) try { c.Close(); } catch { } _clients.Clear(); }
    }
    public void Restart() => Listen(Port);

    private async Task Accept(TcpListener l, bool httpPort, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient c; try { c = await l.AcceptTcpClientAsync(ct); } catch { return; }
            lock (_clients) _clients.Add(c);
            _ = Task.Run(() => Handle(c, httpPort, ct));
        }
    }

    private static async Task<string> ReadLine(Stream s)
    {
        var sb = new StringBuilder(); var b = new byte[1];
        while (await s.ReadAsync(b) > 0) { if (b[0] == '\n') break; if (b[0] != '\r') sb.Append((char)b[0]); }
        return sb.ToString();
    }
    private static async Task<string> ReadHead(Stream s)
    {
        var sb = new StringBuilder();
        while (true) { var l = await ReadLine(s); if (l.Length == 0) break; sb.Append(l).Append('\n'); }
        return sb.ToString();
    }
    private static async Task Reply(Stream s, string text) { await s.WriteAsync(Encoding.ASCII.GetBytes(text)); await s.FlushAsync(); }

    private async Task Handle(TcpClient c, bool httpPort, CancellationToken ct)
    {
        try
        {
            using var s = c.GetStream();
            if (_kind == Kind.Shoutcast && !httpPort) { await HandleShoutcastSource(s); return; }
            var first = await ReadLine(s);
            var head = await ReadHead(s);
            LastHead = first + "\n" + head;
            var parts = first.Split(' ');
            var method = parts[0]; var target = parts.Length > 1 ? parts[1] : "/";
            if (method is "PUT" or "SOURCE") { await HandleIcecastSource(s, method, target, head); return; }
            var body = "";
            if (target.StartsWith("/admin/metadata"))
            {
                if (!head.Contains("Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("source:" + Password)))) { await Reply(s, "HTTP/1.0 401 Unauthorized\r\nContent-Length: 0\r\n\r\n"); return; }
                LastSong = Uri.UnescapeDataString(System.Web.HttpUtility.ParseQueryString(target[(target.IndexOf('?') + 1)..])["song"] ?? "");
            }
            else if (target.StartsWith("/admin.cgi"))
            {
                var q = System.Web.HttpUtility.ParseQueryString(target[(target.IndexOf('?') + 1)..]);
                if (q["pass"] != Password) { await Reply(s, "HTTP/1.0 401 Unauthorized\r\nContent-Length: 0\r\n\r\n"); return; }
                LastSong = q["song"] ?? "";
            }
            else if (target.StartsWith("/status-json.xsl"))
                body = $"{{\"icestats\":{{\"source\":{{\"listenurl\":\"http://127.0.0.1:{Port}{Mount}\",\"listeners\":{Listeners}}}}}}}";
            else if (target.StartsWith("/stats")) body = $"{{\"currentlisteners\":{Listeners}}}";
            await Reply(s, $"HTTP/1.0 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\n\r\n{body}");
        }
        catch { }
        finally { try { c.Close(); } catch { } }
    }

    private async Task HandleIcecastSource(Stream s, string method, string target, string head)
    {
        Interlocked.Increment(ref SourceConnections);
        var auth = "Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("source:" + Password));
        if (!head.Contains(auth)) { await Reply(s, "HTTP/1.0 401 Authorization Required\r\n\r\n"); return; }
        if (target != Mount && !AnyMount) { await Reply(s, "HTTP/1.0 404 Not Found\r\n\r\n"); return; }
        if (!head.Contains("Content-Type: " + AllowedContentType)) { await Reply(s, "HTTP/1.0 403 Content-type not supported\r\n\r\n"); return; }
        if (AnyMount ? _mounts.GetValueOrDefault(target) > 0 : ActiveSources > 0) { await Reply(s, "HTTP/1.0 403 Mountpoint in use\r\n\r\n"); return; }
        _mounts.AddOrUpdate(target, 1, (_, v) => v + 1);
        await Reply(s, head.Contains("Expect: 100-continue") ? "HTTP/1.1 100 Continue\r\nServer: Icecast 2.4.4\r\n\r\n" : "HTTP/1.0 200 OK\r\nServer: Icecast 2.4.4\r\n\r\n");
        try { await Drain(s); } finally { _mounts.AddOrUpdate(target, 0, (_, v) => Math.Max(0, v - 1)); }
    }

    private async Task HandleShoutcastSource(Stream s)
    {
        Interlocked.Increment(ref SourceConnections);
        var pw = await ReadLine(s);
        if (pw.Split(':')[0] != Password) { await Reply(s, "invalid password\r\n"); return; }
        await Reply(s, "OK2\r\nicy-caps:11\r\n\r\n");
        LastHead = await ReadHead(s);
        await Drain(s);
    }

    private async Task Drain(Stream s)
    {
        Interlocked.Increment(ref ActiveSources);
        try
        {
            var buf = new byte[8192]; int n; bool first = true;
            while ((n = await s.ReadAsync(buf)) > 0)
            {
                if (first && FirstBytes.Length == 0) FirstBytes = buf.Take(Math.Min(n, 64)).ToArray();
                first = false; Interlocked.Add(ref BytesReceived, n);
            }
        }
        catch { }
        finally { Interlocked.Decrement(ref ActiveSources); }
    }

    public ServerProfile Profile(string name = "Test", int kbps = 128, AudioCodec codec = AudioCodec.Mp3)
    {
        var p = new ServerProfile
        {
            Name = name, Host = "127.0.0.1", Port = Port, Type = _kind == Kind.Icecast ? ServerType.Icecast2 : ServerType.ShoutcastV2,
            Mount = Mount, BitrateKbps = kbps, Codec = codec, RetryIntervalSec = 1, MaxRetryIntervalSec = 2, StreamName = name,
        };
        p.Password = Password; return p;
    }

    public void Dispose() => Kill();
}
