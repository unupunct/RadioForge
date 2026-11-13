using System.Text.Json;
using Microsoft.Data.Sqlite;


namespace RadioForge.Core;

public sealed record RecordingInfo(long Id, string Path, DateTime Started, double DurationSec, long Size, string Format, string Source);
public sealed record StatSample(DateTime Time, string ServerId, string Status, double BitrateKbps, int Listeners);

/// <summary>Per-profile SQLite store: library, playlists, schedule, recordings, statistics.</summary>
public sealed class Database
{
    private readonly string _cs;
    public string FilePath { get; }

    public Database(string file)
    {
        FilePath = file;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        _cs = new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString();
        Init();
    }

    private SqliteConnection Open() { var c = new SqliteConnection(_cs); c.Open(); return c; }

    private static void Exec(SqliteConnection c, string sql, params (string, object?)[] p)
    {
        using var cmd = c.CreateCommand(); cmd.CommandText = sql;
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private void Init()
    {
        using var c = Open();
        Exec(c, "PRAGMA journal_mode=WAL;");
        Exec(c, @"
CREATE TABLE IF NOT EXISTS tracks(id INTEGER PRIMARY KEY, path TEXT UNIQUE NOT NULL, title TEXT, artist TEXT, album TEXT, genre TEXT, year INTEGER, duration REAL, size INTEGER, added TEXT);
CREATE TABLE IF NOT EXISTS playlists(id INTEGER PRIMARY KEY, name TEXT NOT NULL, parent_id INTEGER, is_folder INTEGER NOT NULL DEFAULT 0);
CREATE TABLE IF NOT EXISTS playlist_items(id INTEGER PRIMARY KEY, playlist_id INTEGER NOT NULL, track_id INTEGER NOT NULL, position INTEGER NOT NULL);
CREATE INDEX IF NOT EXISTS ix_pi ON playlist_items(playlist_id, position);
CREATE TABLE IF NOT EXISTS schedule(id TEXT PRIMARY KEY, json TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS recordings(id INTEGER PRIMARY KEY, path TEXT, started TEXT, duration REAL, size INTEGER, format TEXT, source TEXT);
CREATE TABLE IF NOT EXISTS stats(ts TEXT, server_id TEXT, status TEXT, bitrate REAL, listeners INTEGER);
CREATE INDEX IF NOT EXISTS ix_stats ON stats(ts);
CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT);");
        Exec(c, "INSERT OR IGNORE INTO meta(key,value) VALUES('schema','1')");
    }

    private static List<T> Query<T>(SqliteConnection c, string sql, Func<SqliteDataReader, T> map, params (string, object?)[] p)
    {
        using var cmd = c.CreateCommand(); cmd.CommandText = sql;
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        using var r = cmd.ExecuteReader(); var l = new List<T>();
        while (r.Read()) l.Add(map(r));
        return l;
    }

    // ---------- tracks ----------
    private static Track MapTrack(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0), Path = r.GetString(1), Title = r.IsDBNull(2) ? "" : r.GetString(2), Artist = r.IsDBNull(3) ? "" : r.GetString(3),
        Album = r.IsDBNull(4) ? "" : r.GetString(4), Genre = r.IsDBNull(5) ? "" : r.GetString(5), Year = (uint)(r.IsDBNull(6) ? 0 : r.GetInt64(6)),
        DurationSec = r.IsDBNull(7) ? 0 : r.GetDouble(7), FileSize = r.IsDBNull(8) ? 0 : r.GetInt64(8),
        Added = DateTime.TryParse(r.IsDBNull(9) ? null : r.GetString(9), out var d) ? d : DateTime.MinValue,
    };
    private const string TrackCols = "id,path,title,artist,album,genre,year,duration,size,added";

    public static readonly string[] SupportedExtensions = { ".mp3", ".wav", ".flac", ".ogg", ".aac", ".m4a" };
    public static bool IsSupported(string path) => SupportedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>Reads tags with TagLib and inserts/updates a track. Returns null when the file is unreadable.</summary>
    public Track? ImportFile(string path)
    {
        if (!IsSupported(path) || !System.IO.File.Exists(path)) return null;
        var t = new Track { Path = path, Title = Path.GetFileNameWithoutExtension(path), FileSize = new FileInfo(path).Length };
        try
        {
            using var f = TagLib.File.Create(path);
            if (!string.IsNullOrWhiteSpace(f.Tag.Title)) t.Title = f.Tag.Title;
            t.Artist = f.Tag.FirstPerformer ?? ""; t.Album = f.Tag.Album ?? ""; t.Genre = f.Tag.FirstGenre ?? ""; t.Year = f.Tag.Year;
            t.DurationSec = f.Properties.Duration.TotalSeconds;
        }
        catch (Exception ex) { Log.Warn("Library", $"Tags unreadable for {Path.GetFileName(path)}: {ex.Message}"); }
        using var c = Open();
        Exec(c, @"INSERT INTO tracks(path,title,artist,album,genre,year,duration,size,added) VALUES($p,$t,$a,$al,$g,$y,$d,$s,$ad)
                  ON CONFLICT(path) DO UPDATE SET title=$t,artist=$a,album=$al,genre=$g,year=$y,duration=$d,size=$s",
            ("$p", path), ("$t", t.Title), ("$a", t.Artist), ("$al", t.Album), ("$g", t.Genre), ("$y", (long)t.Year), ("$d", t.DurationSec), ("$s", t.FileSize), ("$ad", DateTime.Now.ToString("o")));
        return GetTrackByPath(path);
    }

    public int ImportFolder(string folder, IProgress<int>? progress = null)
    {
        int n = 0;
        foreach (var f in Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories))
            if (IsSupported(f) && ImportFile(f) != null) { n++; progress?.Report(n); }
        return n;
    }

    public Track? GetTrackByPath(string path) { using var c = Open(); return Query(c, $"SELECT {TrackCols} FROM tracks WHERE path=$p", MapTrack, ("$p", path)).FirstOrDefault(); }

    public List<Track> GetTracks(string? search = null)
    {
        using var c = Open();
        var list = string.IsNullOrWhiteSpace(search)
            ? Query(c, $"SELECT {TrackCols} FROM tracks ORDER BY artist,title", MapTrack)
            : Query(c, $"SELECT {TrackCols} FROM tracks WHERE title LIKE $q OR artist LIKE $q OR album LIKE $q OR genre LIKE $q ORDER BY artist,title", MapTrack, ("$q", "%" + search.Trim() + "%"));
        foreach (var t in list) t.Missing = !System.IO.File.Exists(t.Path);
        return list;
    }

    public void DeleteTrack(long id)
    {
        using var c = Open();
        Exec(c, "DELETE FROM playlist_items WHERE track_id=$i", ("$i", id));
        Exec(c, "DELETE FROM tracks WHERE id=$i", ("$i", id));
    }

    /// <summary>Groups of tracks with identical artist+title and (almost) identical duration.</summary>
    public List<List<Track>> FindDuplicates() =>
        GetTracks().GroupBy(t => (t.Title.Trim().ToLowerInvariant(), t.Artist.Trim().ToLowerInvariant(), (int)Math.Round(t.DurationSec / 2)))
                   .Where(g => g.Count() > 1).Select(g => g.ToList()).ToList();

    public static byte[]? LoadArtwork(string path)
    {
        try { using var f = TagLib.File.Create(path); return f.Tag.Pictures.Length > 0 ? f.Tag.Pictures[0].Data.Data : null; }
        catch { return null; }
    }

    // ---------- playlists ----------
    public List<Playlist> GetPlaylists()
    {
        using var c = Open();
        return Query(c, "SELECT id,name,parent_id,is_folder FROM playlists ORDER BY is_folder DESC, name",
            r => new Playlist { Id = r.GetInt64(0), Name = r.GetString(1), ParentId = r.IsDBNull(2) ? null : r.GetInt64(2), IsFolder = r.GetInt64(3) == 1 });
    }

    public long CreatePlaylist(string name, long? parent = null, bool folder = false)
    {
        using var c = Open();
        Exec(c, "INSERT INTO playlists(name,parent_id,is_folder) VALUES($n,$p,$f)", ("$n", name), ("$p", parent), ("$f", folder ? 1 : 0));
        return Query(c, "SELECT last_insert_rowid()", r => r.GetInt64(0)).First();
    }

    public void RenamePlaylist(long id, string name) { using var c = Open(); Exec(c, "UPDATE playlists SET name=$n WHERE id=$i", ("$n", name), ("$i", id)); }

    public void DeletePlaylist(long id)
    {
        using var c = Open();
        foreach (var child in Query(c, "SELECT id FROM playlists WHERE parent_id=$i", r => r.GetInt64(0), ("$i", id))) DeletePlaylist(child);
        Exec(c, "DELETE FROM playlist_items WHERE playlist_id=$i", ("$i", id));
        Exec(c, "DELETE FROM playlists WHERE id=$i", ("$i", id));
    }

    public List<Track> GetPlaylistTracks(long playlistId)
    {
        using var c = Open();
        var l = Query(c, $"SELECT t.id,t.path,t.title,t.artist,t.album,t.genre,t.year,t.duration,t.size,t.added FROM playlist_items i JOIN tracks t ON t.id=i.track_id WHERE i.playlist_id=$p ORDER BY i.position", MapTrack, ("$p", playlistId));
        foreach (var t in l) t.Missing = !System.IO.File.Exists(t.Path);
        return l;
    }

    /// <summary>Replaces the whole track order of a playlist (used for add/remove/reorder).</summary>
    public void SavePlaylistTracks(long playlistId, IEnumerable<long> trackIds)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        Exec(c, "DELETE FROM playlist_items WHERE playlist_id=$p", ("$p", playlistId));
        int pos = 0;
        foreach (var id in trackIds) Exec(c, "INSERT INTO playlist_items(playlist_id,track_id,position) VALUES($p,$t,$o)", ("$p", playlistId), ("$t", id), ("$o", pos++));
        tx.Commit();
    }

    public void ExportM3u(long playlistId, string file)
    {
        var lines = new List<string> { "#EXTM3U" };
        foreach (var t in GetPlaylistTracks(playlistId)) { lines.Add($"#EXTINF:{(int)t.DurationSec},{t.DisplayName}"); lines.Add(t.Path); }
        System.IO.File.WriteAllLines(file, lines);
    }

    public long ImportM3u(string file, string? name = null)
    {
        var dir = Path.GetDirectoryName(file)!;
        var ids = new List<long>();
        foreach (var raw in System.IO.File.ReadAllLines(file))
        {
            var l = raw.Trim(); if (l.Length == 0 || l.StartsWith('#')) continue;
            var p = Path.IsPathRooted(l) ? l : Path.GetFullPath(Path.Combine(dir, l));
            var t = GetTrackByPath(p) ?? ImportFile(p);
            if (t != null) ids.Add(t.Id);
        }
        var id = CreatePlaylist(name ?? Path.GetFileNameWithoutExtension(file));
        SavePlaylistTracks(id, ids);
        return id;
    }

    // ---------- schedule ----------
    public List<ScheduledEvent> GetEvents()
    {
        using var c = Open();
        return Query(c, "SELECT json FROM schedule", r => JsonSerializer.Deserialize<ScheduledEvent>(r.GetString(0), SafeJson.Options)!).Where(e => e != null).ToList();
    }
    public void SaveEvent(ScheduledEvent e) { using var c = Open(); Exec(c, "INSERT INTO schedule(id,json) VALUES($i,$j) ON CONFLICT(id) DO UPDATE SET json=$j", ("$i", e.Id), ("$j", JsonSerializer.Serialize(e, SafeJson.Options))); }
    public void DeleteEvent(string id) { using var c = Open(); Exec(c, "DELETE FROM schedule WHERE id=$i", ("$i", id)); }

    // ---------- recordings ----------
    public void AddRecording(RecordingInfo r)
    {
        using var c = Open();
        Exec(c, "INSERT INTO recordings(path,started,duration,size,format,source) VALUES($p,$s,$d,$z,$f,$o)",
            ("$p", r.Path), ("$s", r.Started.ToString("o")), ("$d", r.DurationSec), ("$z", r.Size), ("$f", r.Format), ("$o", r.Source));
    }
    public List<RecordingInfo> GetRecordings()
    {
        using var c = Open();
        return Query(c, "SELECT id,path,started,duration,size,format,source FROM recordings ORDER BY started DESC",
            r => new RecordingInfo(r.GetInt64(0), r.GetString(1), DateTime.Parse(r.GetString(2)), r.GetDouble(3), r.GetInt64(4), r.GetString(5), r.GetString(6)));
    }
    public void DeleteRecording(long id) { using var c = Open(); Exec(c, "DELETE FROM recordings WHERE id=$i", ("$i", id)); }

    // ---------- statistics ----------
    public void AddStat(StatSample s)
    {
        using var c = Open();
        Exec(c, "INSERT INTO stats(ts,server_id,status,bitrate,listeners) VALUES($t,$s,$st,$b,$l)", ("$t", s.Time.ToString("o")), ("$s", s.ServerId), ("$st", s.Status), ("$b", s.BitrateKbps), ("$l", s.Listeners));
        Exec(c, "DELETE FROM stats WHERE ts < $cut", ("$cut", DateTime.Now.AddDays(-30).ToString("o")));
    }
    public List<StatSample> GetStats(DateTime since)
    {
        using var c = Open();
        return Query(c, "SELECT ts,server_id,status,bitrate,listeners FROM stats WHERE ts>=$s ORDER BY ts",
            r => new StatSample(DateTime.Parse(r.GetString(0)), r.GetString(1), r.GetString(2), r.GetDouble(3), (int)r.GetInt64(4)), ("$s", since.ToString("o")));
    }
}
