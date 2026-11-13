using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RadioForge.Core.Streaming;

public enum StreamFailure { Unavailable, AuthFailed, Rejected, Protocol, Encoder, Timeout, Config }

public sealed class StreamException(StreamFailure kind, string message, Exception? inner = null) : Exception(message, inner)
{
    public StreamFailure Kind { get; } = kind;
}

public sealed class StreamConnection : IDisposable
{
    public TcpClient Tcp { get; init; } = null!;
    public Stream Stream { get; init; } = null!;
    public string ServerInfo { get; init; } = "";
    public string Protocol { get; init; } = "";
    public void Dispose() { try { Stream.Dispose(); } catch { } try { Tcp.Dispose(); } catch { } }
}

public static class ConfigValidator
{
    private static readonly int[] Rates = { 22050, 24000, 32000, 44100, 48000 };
    public static List<string> Validate(ServerProfile p)
    {
        var e = new List<string>();
        if (string.IsNullOrWhiteSpace(p.Name)) e.Add("Server name is required.");
        if (string.IsNullOrWhiteSpace(p.Host) || p.Host.Any(char.IsWhiteSpace) || p.Host.Contains('/') || p.Host.Contains("://")) e.Add("Host must be a host name or IP address only (no http:// or path).");
        if (p.Port < 1 || p.Port > 65535) e.Add("Port must be between 1 and 65535.");
        if (p.Type != ServerType.Icecast2 && p.Port > 65534) e.Add("SHOUTcast sources connect on port + 1, so the port must be below 65535.");
        if (string.IsNullOrEmpty(p.Password)) e.Add("Source password is required.");
        if (p.Type == ServerType.Icecast2)
        {
            if (!p.Mount.StartsWith('/') || p.Mount.Length < 2 || p.Mount.Any(char.IsWhiteSpace)) e.Add("Mount point must start with '/' and contain no spaces (e.g. /stream).");
            if (string.IsNullOrWhiteSpace(p.Username)) e.Add("Icecast source username is required (usually 'source').");
        }
        if (p.Type == ServerType.ShoutcastV2 && p.StreamId < 1) e.Add("Stream ID must be 1 or higher.");
        if (p.BitrateKbps < 16 || p.BitrateKbps > 320) e.Add("Bitrate must be between 16 and 320 kbps.");
        if (!Rates.Contains(p.SampleRate)) e.Add("Sample rate must be one of 22050, 24000, 32000, 44100, 48000 Hz.");
        if (p.Channels is not (1 or 2)) e.Add("Channels must be 1 (mono) or 2 (stereo).");
        if (p.Codec == AudioCodec.Mp3 && p.SampleRate == 24000) e.Add("MP3 does not support 24000 Hz; use 22050, 32000, 44100 or 48000.");
        if (p.RetryIntervalSec < 1) e.Add("Retry interval must be at least 1 second.");
        return e;
    }
}

public static class ServerConnector
{
    private static string Clean(string s) => Regex.Replace(s ?? "", @"[\r\n]+", " ").Trim();
    public static int SourcePort(ServerProfile p) => p.Type == ServerType.Icecast2 ? p.Port : p.Port + 1;

    private static async Task<(TcpClient, Stream)> DialAsync(string host, int port, bool tls, CancellationToken ct, int timeoutMs)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(timeoutMs);
            await tcp.ConnectAsync(host, port, cts.Token);
            Stream s = tcp.GetStream();
            if (tls)
            {
                var ssl = new SslStream(s);
                try { await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, cts.Token); }
                catch (Exception ex) when (ex is not OperationCanceledException) { throw new StreamException(StreamFailure.Protocol, $"TLS handshake with {host}:{port} failed: {ex.Message}", ex); }
                s = ssl;
            }
            return (tcp, s);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { tcp.Dispose(); throw new StreamException(StreamFailure.Timeout, $"Connection to {host}:{port} timed out. Check host name, port and firewall."); }
        catch (SocketException ex) { tcp.Dispose(); throw new StreamException(StreamFailure.Unavailable, $"Server unavailable at {host}:{port}: {ex.Message}", ex); }
        catch (StreamException) { tcp.Dispose(); throw; }
        catch (Exception ex) when (ex is not OperationCanceledException) { tcp.Dispose(); throw new StreamException(StreamFailure.Unavailable, $"Cannot reach {host}:{port}: {ex.Message}", ex); }
    }

    private static async Task<string> ReadHeadAsync(Stream s, CancellationToken ct, int timeoutMs)
    {
        var sb = new StringBuilder(); var one = new byte[1];
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(timeoutMs);
        try
        {
            while (sb.Length < 16384)
            {
                int n = await s.ReadAsync(one, cts.Token);
                if (n == 0) break;
                sb.Append((char)one[0]);
                if (sb.Length >= 4 && sb.ToString(sb.Length - 4, 4) == "\r\n\r\n") break;
                if (sb.Length >= 2 && sb.ToString(sb.Length - 2, 2) == "\n\n") break;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new StreamException(StreamFailure.Timeout, "The server did not answer in time."); }
        return sb.ToString();
    }

    public static async Task<StreamConnection> OpenAsync(ServerProfile p, string password, CancellationToken ct, int timeoutMs = 8000)
    {
        return p.Type == ServerType.Icecast2 ? await OpenIcecast(p, password, ct, timeoutMs) : await OpenShoutcast(p, password, ct, timeoutMs);
    }

    // ---------------------------------------------------------------- Icecast 2
    private static async Task<StreamConnection> OpenIcecast(ServerProfile p, string password, CancellationToken ct, int timeoutMs)
    {
        foreach (var method in new[] { "PUT", "SOURCE" })
        {
            var (tcp, stream) = await DialAsync(p.Host, p.Port, p.UseTls, ct, timeoutMs);
            try
            {
                var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{p.Username}:{password}"));
                var sb = new StringBuilder();
                sb.Append($"{method} {p.Mount} HTTP/1.1\r\nHost: {p.Host}:{p.Port}\r\nAuthorization: Basic {auth}\r\nUser-Agent: RadioForge/1.0\r\n");
                sb.Append($"Content-Type: {CodecInfo.ContentType(p.Codec)}\r\n");
                sb.Append($"ice-name: {Clean(string.IsNullOrWhiteSpace(p.StreamName) ? p.Name : p.StreamName)}\r\n");
                if (p.Description != "") sb.Append($"ice-description: {Clean(p.Description)}\r\n");
                if (p.Genre != "") sb.Append($"ice-genre: {Clean(p.Genre)}\r\n");
                if (p.Website != "") sb.Append($"ice-url: {Clean(p.Website)}\r\n");
                sb.Append($"ice-public: {(p.IsPublic ? 1 : 0)}\r\nice-bitrate: {p.BitrateKbps}\r\n");
                sb.Append($"ice-audio-info: ice-samplerate={p.SampleRate};ice-bitrate={p.BitrateKbps};ice-channels={p.Channels}\r\n");
                if (method == "PUT") sb.Append("Expect: 100-continue\r\n");
                sb.Append("\r\n");
                var bytes = Encoding.UTF8.GetBytes(sb.ToString());
                await stream.WriteAsync(bytes, ct);
                await stream.FlushAsync(ct);
                var head = await ReadHeadAsync(stream, ct, timeoutMs);
                if (head.Length == 0) throw new StreamException(StreamFailure.Protocol, "The server closed the connection without answering. Is this an Icecast server and the right port?");
                var line = head.Split('\n')[0].Trim();
                var m = Regex.Match(line, @"^(?:HTTP/\d\.\d|ICY)\s+(\d{3})\s*(.*)$");
                if (!m.Success) throw new StreamException(StreamFailure.Protocol, $"Unexpected answer from server: \"{Redactor.Redact(Clean(line))}\". The selected type/port may not be an Icecast server.");
                int code = int.Parse(m.Groups[1].Value); var reason = m.Groups[2].Value.Trim();
                var server = Regex.Match(head, @"(?im)^Server:\s*(.+)$");
                switch (code)
                {
                    case 100 or 200:
                        return new StreamConnection { Tcp = tcp, Stream = stream, ServerInfo = server.Success ? server.Groups[1].Value.Trim() : "", Protocol = method + " (HTTP)" };
                    case 401:
                        throw new StreamException(StreamFailure.AuthFailed, "Authentication failed: the source username or password was rejected (401).");
                    case 403:
                        throw new StreamException(StreamFailure.Rejected, $"The server refused the source (403 {reason}). Common causes: mount point already in use, or content type {CodecInfo.ContentType(p.Codec)} not allowed on this mount.");
                    case 404:
                        throw new StreamException(StreamFailure.Rejected, $"Mount point {p.Mount} was not found / is not permitted (404).");
                    case 400 or 405 or 501 when method == "PUT":
                        stream.Dispose(); tcp.Dispose(); stream = null!; tcp = null!;
                        continue; // old Icecast: retry with SOURCE
                    default:
                        throw new StreamException(StreamFailure.Protocol, $"Server answered {code} {reason}.");
                }
            }
            catch { try { stream?.Dispose(); tcp?.Dispose(); } catch { } throw; }
        }
        throw new StreamException(StreamFailure.Protocol, "The server rejected both PUT and SOURCE source requests.");
    }

    // ---------------------------------------------------------------- SHOUTcast v1 / v2 (legacy source protocol)
    private static async Task<StreamConnection> OpenShoutcast(ServerProfile p, string password, CancellationToken ct, int timeoutMs)
    {
        int port = SourcePort(p);
        var (tcp, stream) = await DialAsync(p.Host, port, p.UseTls, ct, timeoutMs);
        try
        {
            var pw = p.Type == ServerType.ShoutcastV2 && p.StreamId != 1 ? $"{password}:#{p.StreamId}" : password;
            await stream.WriteAsync(Encoding.UTF8.GetBytes(pw + "\r\n"), ct);
            await stream.FlushAsync(ct);
            // answer: "OK2\r\nicy-caps:11\r\n\r\n" (or "OK\r\n"); failures: "invalid password" or immediate close
            var sb = new StringBuilder(); var one = new byte[1];
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(timeoutMs);
                try
                {
                    while (sb.Length < 2048)
                    {
                        int n = await stream.ReadAsync(one, cts.Token);
                        if (n == 0) break;
                        sb.Append((char)one[0]);
                        if (one[0] == '\n') break;
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new StreamException(StreamFailure.Timeout, $"SHOUTcast server on port {port} did not answer. Source connections use the listener port + 1."); }
            }
            var first = sb.ToString().Trim();
            if (first.Length == 0) throw new StreamException(StreamFailure.AuthFailed, $"The server on port {port} closed the connection without answering — usually a wrong source password (or wrong port).");
            if (!first.StartsWith("OK", StringComparison.OrdinalIgnoreCase))
            {
                var low = first.ToLowerInvariant();
                if (low.Contains("invalid") || low.Contains("password") || low.Contains("bad")) throw new StreamException(StreamFailure.AuthFailed, $"Authentication failed: the server says \"{Clean(first)}\".");
                throw new StreamException(StreamFailure.Rejected, $"The server refused the source: \"{Clean(first)}\".");
            }
            // drain remaining reply lines (icy-caps etc.) until blank line, best effort
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(600);
                try
                {
                    var tail = new StringBuilder();
                    while (true)
                    {
                        int n = await stream.ReadAsync(one, cts.Token); if (n == 0) break;
                        tail.Append((char)one[0]);
                        if (tail.ToString().EndsWith("\r\n\r\n") || tail.ToString().EndsWith("\n\n")) break;
                    }
                }
                catch (OperationCanceledException) { }
            }
            var h = new StringBuilder();
            h.Append($"content-type:{CodecInfo.ContentType(p.Codec)}\r\n");
            h.Append($"icy-name:{Clean(string.IsNullOrWhiteSpace(p.StreamName) ? p.Name : p.StreamName)}\r\n");
            h.Append($"icy-genre:{Clean(p.Genre)}\r\nicy-url:{Clean(p.Website)}\r\nicy-pub:{(p.IsPublic ? 1 : 0)}\r\nicy-br:{p.BitrateKbps}\r\n");
            if (p.Description != "") h.Append($"icy-description:{Clean(p.Description)}\r\n");
            h.Append("\r\n");
            await stream.WriteAsync(Encoding.UTF8.GetBytes(h.ToString()), ct);
            await stream.FlushAsync(ct);
            return new StreamConnection { Tcp = tcp, Stream = stream, ServerInfo = first, Protocol = first.StartsWith("OK2") ? "SHOUTcast v2 (legacy source)" : "SHOUTcast v1 source" };
        }
        catch { try { stream.Dispose(); tcp.Dispose(); } catch { } throw; }
    }
}

public sealed record MetadataResult(bool Success, bool Supported, string Message);

/// <summary>Out-of-band metadata and listener statistics (HTTP admin interfaces).</summary>
public static class ServerInfoClient
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(6) }) { Timeout = TimeSpan.FromSeconds(10) };

    private static string Base(ServerProfile p) => $"{(p.UseTls ? "https" : "http")}://{p.Host}:{p.Port}";

    public static async Task<MetadataResult> UpdateMetadataAsync(ServerProfile p, string password, string song, CancellationToken ct = default)
    {
        if (!p.SendMetadata) return new(false, false, "Metadata updates are disabled for this server.");
        try
        {
            HttpRequestMessage req;
            var q = Uri.EscapeDataString(song);
            if (p.Type == ServerType.Icecast2)
            {
                req = new HttpRequestMessage(HttpMethod.Get, $"{Base(p)}/admin/metadata?mount={Uri.EscapeDataString(p.Mount)}&mode=updinfo&song={q}&charset=UTF-8");
                req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{p.Username}:{password}")));
            }
            else
            {
                var sid = p.Type == ServerType.ShoutcastV2 ? $"sid={p.StreamId}&" : "";
                req = new HttpRequestMessage(HttpMethod.Get, $"{Base(p)}/admin.cgi?{sid}pass={Uri.EscapeDataString(password)}&mode=updinfo&song={q}");
            }
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (RadioForge)");
            using var resp = await Http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode) return new(true, true, "Metadata updated.");
            if ((int)resp.StatusCode == 401 || (int)resp.StatusCode == 403) return new(false, true, "Metadata rejected: authentication failed.");
            return new(false, true, $"Metadata update failed: HTTP {(int)resp.StatusCode}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return new(false, true, "Metadata update failed: " + ex.Message); }
    }

    /// <summary>Returns null when the server does not expose listener counts to us.</summary>
    public static async Task<int?> GetListenersAsync(ServerProfile p, CancellationToken ct = default)
    {
        try
        {
            if (p.Type == ServerType.Icecast2)
            {
                var json = await Http.GetStringAsync($"{Base(p)}/status-json.xsl", ct);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("icestats", out var st) || !st.TryGetProperty("source", out var src)) return 0;
                IEnumerable<JsonElement> sources = src.ValueKind == JsonValueKind.Array ? src.EnumerateArray() : new[] { src };
                foreach (var s in sources)
                    if (s.TryGetProperty("listenurl", out var u) && (u.GetString() ?? "").EndsWith(p.Mount, StringComparison.OrdinalIgnoreCase) && s.TryGetProperty("listeners", out var l)) return l.GetInt32();
                return 0;
            }
            var sid = p.Type == ServerType.ShoutcastV2 ? p.StreamId : 1;
            try
            {
                var json = await Http.GetStringAsync($"{Base(p)}/stats?sid={sid}&json=1", ct);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("currentlisteners", out var cl)) return cl.GetInt32();
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
            var req = new HttpRequestMessage(HttpMethod.Get, $"{Base(p)}/7.html"); req.Headers.UserAgent.ParseAdd("Mozilla/5.0");
            using var r = await Http.SendAsync(req, ct);
            var body = Regex.Match(await r.Content.ReadAsStringAsync(ct), @"<body>(\d+),");
            return body.Success ? int.Parse(body.Groups[1].Value) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }
}

public enum StepStatus { Pass, Fail, Warn, Info, Skipped }
public sealed record DiagStep(string Name, StepStatus Status, string Detail);
public sealed class TestResult
{
    public List<DiagStep> Steps { get; } = new();
    public bool Success => Steps.All(s => s.Status != StepStatus.Fail);
    public string Summary => Success ? "Connection test passed." : "Connection test failed: " + Steps.First(s => s.Status == StepStatus.Fail).Detail;
}

public static class ServerTester
{
    public static async Task<TestResult> TestAsync(ServerProfile p, string? ffmpegPath, CancellationToken ct = default)
    {
        var r = new TestResult();
        var problems = ConfigValidator.Validate(p);
        if (problems.Count > 0) { r.Steps.Add(new("Configuration", StepStatus.Fail, string.Join(" ", problems))); return r; }
        r.Steps.Add(new("Configuration", StepStatus.Pass, "All fields are valid."));

        // encoder availability
        if (p.Codec == AudioCodec.Mp3) r.Steps.Add(new("Encoder", StepStatus.Pass, "MP3 (built-in LAME encoder) is available."));
        else
        {
            var exe = Ffmpeg.Locate(ffmpegPath);
            if (exe == null) r.Steps.Add(new("Encoder", StepStatus.Fail, $"{p.Codec} needs FFmpeg, which was not found. Install it or set its path in Settings → Advanced."));
            else if (await Ffmpeg.HasEncoderAsync(exe, Ffmpeg.EncoderFor(p.Codec))) r.Steps.Add(new("Encoder", StepStatus.Pass, $"FFmpeg encoder '{Ffmpeg.EncoderFor(p.Codec)}' is available."));
            else r.Steps.Add(new("Encoder", StepStatus.Fail, $"This FFmpeg build has no '{Ffmpeg.EncoderFor(p.Codec)}' encoder."));
        }

        int port = ServerConnector.SourcePort(p);
        StreamConnection? conn = null;
        try
        {
            conn = await ServerConnector.OpenAsync(p, p.Password, ct, 6000);
            r.Steps.Add(new("Server reachable", StepStatus.Pass, $"{p.Host}:{port} accepted the connection."));
            r.Steps.Add(new("Authentication", StepStatus.Pass, "Source credentials were accepted."));
            r.Steps.Add(new("Protocol", StepStatus.Pass, conn.Protocol + (conn.ServerInfo != "" ? $" — server: {conn.ServerInfo}" : "")));
            if (p.Type == ServerType.Icecast2)
            {
                r.Steps.Add(new("Mount point", StepStatus.Pass, $"{p.Mount} accepted content type {CodecInfo.ContentType(p.Codec)}."));
                if (conn.ServerInfo != "" && !conn.ServerInfo.Contains("icecast", StringComparison.OrdinalIgnoreCase))
                    r.Steps.Add(new("Server type", StepStatus.Warn, $"Server identifies as \"{conn.ServerInfo}\", not Icecast."));
            }
            else
            {
                if (p.Type == ServerType.ShoutcastV2 && !conn.ServerInfo.StartsWith("OK2")) r.Steps.Add(new("Server type", StepStatus.Warn, "Server answered like SHOUTcast v1 (OK), not v2 (OK2)."));
                if (p.Type == ServerType.ShoutcastV1 && conn.ServerInfo.StartsWith("OK2")) r.Steps.Add(new("Server type", StepStatus.Info, "Server is SHOUTcast v2; it accepts v1 sources."));
                r.Steps.Add(new("Mount point", StepStatus.Skipped, "SHOUTcast has no mount points."));
            }
        }
        catch (StreamException ex)
        {
            switch (ex.Kind)
            {
                case StreamFailure.Unavailable or StreamFailure.Timeout:
                    r.Steps.Add(new("Server reachable", StepStatus.Fail, ex.Message));
                    r.Steps.Add(new("Authentication", StepStatus.Skipped, "Not tested.")); break;
                case StreamFailure.AuthFailed:
                    r.Steps.Add(new("Server reachable", StepStatus.Pass, $"{p.Host}:{port} is reachable."));
                    r.Steps.Add(new("Authentication", StepStatus.Fail, ex.Message)); break;
                case StreamFailure.Rejected:
                    r.Steps.Add(new("Server reachable", StepStatus.Pass, $"{p.Host}:{port} is reachable."));
                    r.Steps.Add(new("Authentication", StepStatus.Pass, "Credentials accepted."));
                    r.Steps.Add(new(p.Type == ServerType.Icecast2 ? "Mount point" : "Stream", StepStatus.Fail, ex.Message)); break;
                default:
                    r.Steps.Add(new("Server reachable", StepStatus.Pass, $"{p.Host}:{port} is reachable."));
                    r.Steps.Add(new("Protocol", StepStatus.Fail, ex.Message)); break;
            }
        }
        finally { conn?.Dispose(); }

        if (r.Success)
        {
            var n = await ServerInfoClient.GetListenersAsync(p, ct);
            r.Steps.Add(new("Listener statistics", n.HasValue ? StepStatus.Pass : StepStatus.Info, n.HasValue ? $"Available ({n} listeners currently)." : "Not available from this server (listener count will show n/a)."));
        }
        return r;
    }
}
