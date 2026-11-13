using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RadioForge.Core.Streaming;

public enum StreamState { Disconnected, Connecting, Connected, Streaming, Reconnecting, AuthFailed, ServerUnavailable, Error }

/// <summary>One live destination: its own task, encoder subscription, connection, logs and statistics.</summary>
public sealed partial class StreamDestination : ObservableObject
{
    private readonly EncoderHub _hub;
    private CancellationTokenSource? _cts; private Task? _task;
    public ServerProfile Profile { get; }
    [ObservableProperty] private StreamState _state = StreamState.Disconnected;
    [ObservableProperty] private string _statusText = "Disconnected";
    [ObservableProperty] private string _lastError = "";
    [ObservableProperty] private DateTime? _connectedSince;
    [ObservableProperty] private DateTime? _lastSuccess;
    [ObservableProperty] private int _reconnectCount;
    [ObservableProperty] private double _bitrateKbps;
    [ObservableProperty] private int? _listeners;
    [ObservableProperty] private long _bytesSent;
    [ObservableProperty] private string _metadataStatus = "";
    public string Category => "Stream:" + Profile.Name;
    public bool IsRunning => _task is { IsCompleted: false };
    public TimeSpan Uptime => ConnectedSince is { } t ? DateTime.Now - t : TimeSpan.Zero;
    public event Action<StreamDestination, StreamState, StreamState>? StateTransition;

    public StreamDestination(ServerProfile p, EncoderHub hub) { Profile = p; _hub = hub; }

    public void SetState(StreamState s, string text)
    {
        var old = State; State = s; StatusText = text;
        Log.Info(Category, $"{s}: {text}");
        if (old != s) try { StateTransition?.Invoke(this, old, s); } catch { }
    }

    /// <summary>Starts the connection loop. Safe to call twice — a destination never runs two pipelines.</summary>
    public bool Start(Func<string?> ffmpeg)
    {
        if (IsRunning) return false;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _task = Task.Run(() => RunAsync(ct));
        return true;
    }

    public async Task StopAsync()
    {
        var cts = _cts; var t = _task;
        if (cts == null || t == null) return;
        cts.Cancel();
        try { await Task.WhenAny(t, Task.Delay(4000)); } catch { }
        _cts = null;
        BitrateKbps = 0; ConnectedSince = null;
        if (State != StreamState.AuthFailed && State != StreamState.Error) SetState(StreamState.Disconnected, "Stopped");
        else StatusText = "Stopped (" + StatusText + ")";
    }

    private async Task RunAsync(CancellationToken ct)
    {
        int attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            EncoderSubscription? sub = null; StreamConnection? conn = null;
            string failText; StreamState failState; bool retry = true;
            try
            {
                var problems = ConfigValidator.Validate(Profile);
                if (problems.Count > 0) { LastError = string.Join(" ", problems); SetState(StreamState.Error, "Invalid configuration: " + LastError); return; }
                SetState(attempt == 0 ? StreamState.Connecting : StreamState.Connecting, attempt == 0 ? "Connecting…" : $"Reconnecting (attempt {attempt})…");
                sub = _hub.Acquire(Profile.Encoder, Profile.Id);
                conn = await ServerConnector.OpenAsync(Profile, Profile.Password, ct);
                SetState(StreamState.Connected, "Connected to " + (conn.ServerInfo != "" ? conn.ServerInfo : Profile.Host));
                sub.Drain(); // never send audio that queued up while connecting
                ConnectedSince = LastSuccess = DateTime.Now; attempt = 0; LastError = "";
                SetState(StreamState.Streaming, $"Streaming {Profile.BitrateKbps} kbps {Profile.Codec}");
                _ = Task.Run(() => PushMetadataSoon(ct));
                await PumpAsync(conn, sub, ct);
                failState = StreamState.Reconnecting; failText = "Connection closed";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (StreamException ex)
            {
                LastError = ex.Message; failText = ex.Message;
                failState = ex.Kind switch
                {
                    StreamFailure.AuthFailed => StreamState.AuthFailed,
                    StreamFailure.Encoder or StreamFailure.Config => StreamState.Error,
                    StreamFailure.Rejected or StreamFailure.Protocol => StreamState.Error,
                    _ => StreamState.ServerUnavailable
                };
            }
            catch (EncoderException ex) { LastError = failText = ex.Message; failState = StreamState.Error; }
            catch (Exception ex) { LastError = failText = "Connection lost: " + ex.Message; failState = StreamState.Reconnecting; Log.Error(Category, "Stream failed", ex); }
            finally
            {
                sub?.Dispose(); conn?.Dispose(); ConnectedSince = null; BitrateKbps = 0;
            }
            if (ct.IsCancellationRequested) return;
            if (!Profile.AutoReconnect || !retry) { SetState(failState, failText); return; }
            attempt++; ReconnectCount++;
            int baseDelay = Math.Max(1, Profile.RetryIntervalSec);
            int delay = failState == StreamState.AuthFailed ? Math.Max(Profile.MaxRetryIntervalSec, 30)
                      : (int)Math.Min(Profile.MaxRetryIntervalSec, baseDelay * Math.Pow(1.6, Math.Min(attempt - 1, 8)));
            delay = Math.Max(delay, baseDelay);
            for (int s = delay; s > 0 && !ct.IsCancellationRequested; s--)
            {
                var text = $"{failText} — retrying in {s}s";
                if (State != failState || s == delay) SetState(failState == StreamState.Reconnecting ? StreamState.Reconnecting : failState, text); else StatusText = text;
                try { await Task.Delay(1000, ct); } catch { return; }
            }
        }
    }

    private async Task PumpAsync(StreamConnection conn, EncoderSubscription sub, CancellationToken ct)
    {
        var stream = conn.Stream; var sink = new byte[512];
        Task<int> remote = stream.ReadAsync(sink, ct).AsTask(); // detects the server closing the connection
        long windowBytes = 0; var windowStart = DateTime.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            var wait = sub.Reader.WaitToReadAsync(ct).AsTask();
            var done = await Task.WhenAny(wait, remote);
            if (done == remote)
            {
                int n; try { n = await remote; } catch (Exception ex) when (ex is not OperationCanceledException) { throw new IOException("Connection lost: " + ex.Message); }
                if (n == 0) throw new IOException("The server closed the connection.");
                remote = stream.ReadAsync(sink, ct).AsTask();
                continue;
            }
            if (!await wait) throw new StreamException(StreamFailure.Encoder, sub.Error != null ? "Encoder stopped: " + sub.Error : "Encoder stopped.");
            while (sub.Reader.TryRead(out var chunk))
            {
                using var wcts = CancellationTokenSource.CreateLinkedTokenSource(ct); wcts.CancelAfter(10000);
                try { await stream.WriteAsync(chunk, wcts.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new IOException("Sending to the server timed out."); }
                BytesSent += chunk.Length; windowBytes += chunk.Length;
            }
            var el = (DateTime.UtcNow - windowStart).TotalSeconds;
            if (el >= 1) { BitrateKbps = windowBytes * 8 / 1000.0 / el; windowBytes = 0; windowStart = DateTime.UtcNow; }
        }
    }

    public Func<(string artist, string title)?>? CurrentMetadata { get; set; }
    private async Task PushMetadataSoon(CancellationToken ct)
    {
        try { await Task.Delay(1500, ct); var m = CurrentMetadata?.Invoke(); if (m != null) await SendMetadataAsync(m.Value.artist, m.Value.title); } catch { }
    }

    public async Task<MetadataResult> SendMetadataAsync(string artist, string title)
    {
        var song = string.IsNullOrWhiteSpace(artist) ? title : $"{artist} - {title}";
        var r = await ServerInfoClient.UpdateMetadataAsync(Profile, Profile.Password, song);
        MetadataStatus = r.Message; Log.Info(Category, $"Metadata \"{song}\": {r.Message}");
        return r;
    }

    public async Task RefreshListenersAsync() { if (State == StreamState.Streaming) Listeners = await ServerInfoClient.GetListenersAsync(Profile); }
}

/// <summary>Manages all destinations of the active station. A failing destination never affects the others.</summary>
public sealed class StreamManager : IDisposable
{
    private readonly Func<string?> _ffmpeg;
    private readonly Timer _poll;
    public EncoderHub Hub { get; }
    public ObservableCollection<StreamDestination> Destinations { get; } = new();
    public event Action<StreamDestination, StreamState, StreamState>? StateTransition;

    public StreamManager(Audio.AudioEngine engine, Func<string?> ffmpegPath)
    {
        _ffmpeg = ffmpegPath;
        Hub = new EncoderHub(engine.MasterBus, ffmpegPath);
        _poll = new Timer(async _ => { foreach (var d in Destinations.ToList()) try { await d.RefreshListenersAsync(); } catch { } }, null, 15000, 15000);
    }

    public StreamDestination Add(ServerProfile p)
    {
        var d = new StreamDestination(p, Hub);
        d.StateTransition += (x, a, b) => StateTransition?.Invoke(x, a, b);
        Destinations.Add(d); return d;
    }

    public async Task RemoveAsync(StreamDestination d) { await d.StopAsync(); Destinations.Remove(d); }
    public StreamDestination? Find(string id) => Destinations.FirstOrDefault(d => d.Profile.Id == id);
    public bool AnyActive => Destinations.Any(d => d.IsRunning);
    public int StreamingCount => Destinations.Count(d => d.State == StreamState.Streaming);
    public int? TotalListeners => Destinations.Any(d => d.Listeners.HasValue) ? Destinations.Sum(d => d.Listeners ?? 0) : null;

    public bool Start(StreamDestination d) => d.Start(_ffmpeg);
    public void StartAll() { foreach (var d in Destinations.Where(d => d.Profile.Enabled).OrderBy(d => d.Profile.Priority)) Start(d); }
    public Task StopAllAsync() => Task.WhenAll(Destinations.Select(d => d.StopAsync()));

    public async Task UpdateMetadataAsync(string artist, string title)
    {
        await Task.WhenAll(Destinations.Where(d => d.State == StreamState.Streaming).Select(async d => { try { await d.SendMetadataAsync(artist, title); } catch (Exception ex) { Log.Error(d.Category, "Metadata failed", ex); } }));
    }

    public void Dispose() { _poll.Dispose(); foreach (var d in Destinations) try { d.StopAsync().Wait(2000); } catch { } Hub.Dispose(); }
}
