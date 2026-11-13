using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RadioForge.Core;

/// <summary>Well-known folders. Root can be redirected (tests, portable use).</summary>
public static class AppPaths
{
    private static string? _root;
    public static string Root
    {
        get => _root ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RadioForge");
        set => _root = value;
    }
    public static string Profiles => Dir(Path.Combine(Root, "profiles"));
    public static string Backups => Dir(Path.Combine(Root, "backups"));
    public static string Logs => Dir(Path.Combine(Root, "logs"));
    public static string AppSettingsFile => Path.Combine(Root, "settings.json");
    public static string DefaultRecordings => Dir(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "RadioForge Recordings"));
    public static string ProfileDir(string id) => Dir(Path.Combine(Profiles, id));
    public static string Dir(string p) { Directory.CreateDirectory(p); return p; }
}

/// <summary>Removes credentials from any text before it is logged or exported.</summary>
public static class Redactor
{
    private static readonly ConcurrentDictionary<string, byte> Secrets = new();
    private static readonly Regex[] Patterns =
    {
        new(@"(?i)(authorization:\s*basic\s+)\S+", RegexOptions.Compiled),
        new(@"(?i)((?:pass|password|pwd)=)[^&\s]+", RegexOptions.Compiled),
    };
    public static void AddSecret(string? s) { if (!string.IsNullOrEmpty(s) && s.Length >= 3) Secrets[s] = 0; }
    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        foreach (var s in Secrets.Keys) text = text.Replace(s, "***", StringComparison.Ordinal);
        foreach (var r in Patterns) text = r.Replace(text, "$1***");
        return text;
    }
}

/// <summary>Windows DPAPI (current user) protection for stored passwords.</summary>
public static class SecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RadioForge.v1");
    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        Redactor.AddSecret(plain);
        return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));
    }
    public static string Unprotect(string? protectedB64)
    {
        if (string.IsNullOrEmpty(protectedB64)) return "";
        try
        {
            var s = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedB64), Entropy, DataProtectionScope.CurrentUser));
            Redactor.AddSecret(s);
            return s;
        }
        catch (Exception) { return ""; } // different user/machine: password must be re-entered
    }
}

public enum LogLevel { Debug, Info, Warning, Error }

public sealed record LogEntry(DateTime Time, LogLevel Level, string Category, string Message)
{
    public override string ToString() => $"{Time:yyyy-MM-dd HH:mm:ss.fff} [{Level}] [{Category}] {Message}";
}

/// <summary>Structured, redacting logger with an in-memory ring buffer and daily rolling files.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly List<LogEntry> Ring = new();
    private static StreamWriter? _writer;
    private static string? _writerDay;
    public static bool DebugEnabled { get; set; }
    public static event Action<LogEntry>? Written;

    public static IReadOnlyList<LogEntry> Snapshot(string? categoryContains = null)
    {
        lock (Gate)
            return (categoryContains == null ? Ring : Ring.Where(e => e.Category.Contains(categoryContains, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    public static void Debug(string cat, string msg) { if (DebugEnabled) Write(LogLevel.Debug, cat, msg); }
    public static void Info(string cat, string msg) => Write(LogLevel.Info, cat, msg);
    public static void Warn(string cat, string msg) => Write(LogLevel.Warning, cat, msg);
    public static void Error(string cat, string msg, Exception? ex = null) => Write(LogLevel.Error, cat, ex == null ? msg : $"{msg}: {ex.GetType().Name}: {ex.Message}");

    public static void Write(LogLevel level, string cat, string msg)
    {
        var e = new LogEntry(DateTime.Now, level, cat, Redactor.Redact(msg));
        lock (Gate)
        {
            Ring.Add(e);
            if (Ring.Count > 5000) Ring.RemoveRange(0, 500);
            try
            {
                var day = e.Time.ToString("yyyyMMdd");
                if (_writer == null || _writerDay != day)
                {
                    _writer?.Dispose();
                    _writer = new StreamWriter(new FileStream(Path.Combine(AppPaths.Logs, $"radioforge-{day}.log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
                    _writerDay = day;
                }
                _writer.WriteLine(e.ToString());
            }
            catch { /* logging must never crash the app */ }
        }
        try { Written?.Invoke(e); } catch { }
    }

    public static void Shutdown() { lock (Gate) { _writer?.Dispose(); _writer = null; } }
}

/// <summary>Atomic JSON persistence with .bak fallback and rolling timestamped backups.</summary>
public static class SafeJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNameCaseInsensitive = true,
    };

    public static void Save<T>(string path, T value, bool keepBackup = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options), Encoding.UTF8);
        if (File.Exists(path))
        {
            if (keepBackup)
            {
                File.Copy(path, path + ".bak", true);
                RollingBackup(path);
            }
            File.Replace(tmp, path, null);
        }
        else File.Move(tmp, path);
    }

    private static void RollingBackup(string path)
    {
        try
        {
            var dir = AppPaths.Dir(Path.Combine(AppPaths.Backups, Path.GetFileNameWithoutExtension(path)));
            var last = Directory.GetFiles(dir).OrderByDescending(f => f).FirstOrDefault();
            if (last != null && (DateTime.Now - File.GetCreationTime(last)).TotalMinutes < 30) return;
            File.Copy(path, Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}.json"), true);
            foreach (var old in Directory.GetFiles(dir).OrderByDescending(f => f).Skip(15)) File.Delete(old);
        }
        catch (Exception ex) { Log.Warn("Config", "Backup failed: " + ex.Message); }
    }

    public static T? Load<T>(string path) where T : class
    {
        foreach (var p in new[] { path, path + ".bak" })
        {
            if (!File.Exists(p)) continue;
            try { return JsonSerializer.Deserialize<T>(File.ReadAllText(p), Options); }
            catch (Exception ex) { Log.Error("Config", $"Cannot read {Path.GetFileName(p)}, trying backup", ex); }
        }
        return null;
    }
}
