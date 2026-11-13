namespace RadioForge.Core;

public enum EventKind { MusicPlaylist, RadioProgram, CommercialBreak, StationJingle, News, PreRecordedShow, SpecialAnnouncement }
public enum RepeatMode { None, Daily, Weekly, Monthly }
public enum SourceKind { Playlist, AudioFile, SoundboardPad }

public class ScheduledEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "New event";
    public string Description { get; set; } = "";
    public EventKind Kind { get; set; } = EventKind.MusicPlaylist;
    public DateOnly StartDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public TimeOnly StartTime { get; set; } = new(12, 0);
    public int DurationMinutes { get; set; } = 60;
    public SourceKind Source { get; set; } = SourceKind.Playlist;
    /// <summary>Playlist id, audio file path or soundboard pad index depending on <see cref="Source"/>.</summary>
    public string SourceRef { get; set; } = "";
    public RepeatMode Repeat { get; set; } = RepeatMode.None;
    /// <summary>Bit mask, bit 0 = Sunday ... bit 6 = Saturday. Used by weekly repeat (0 = same weekday as start date).</summary>
    public int DaysMask { get; set; }
    public DateOnly? RepeatUntil { get; set; }
    public int Priority { get; set; } = 1; // higher wins
    public bool Enabled { get; set; } = true;
    public bool IsMomentary => Kind is EventKind.StationJingle or EventKind.SpecialAnnouncement or EventKind.CommercialBreak && Source != SourceKind.Playlist;
}

public sealed record Occurrence(ScheduledEvent Event, DateTime Start, DateTime End)
{
    public string Key => $"{Event.Id}@{Start:yyyyMMddHHmm}";
}

/// <summary>Pure scheduling logic: recurrence expansion and conflict detection (unit-tested).</summary>
public static class ScheduleEngine
{
    public static IEnumerable<Occurrence> Expand(ScheduledEvent e, DateTime from, DateTime to)
    {
        if (!e.Enabled) yield break;
        var dur = TimeSpan.FromMinutes(Math.Max(1, e.DurationMinutes));
        // search start days one day earlier so events that cross midnight are found
        for (var day = DateOnly.FromDateTime(from.Date.AddDays(-1)); day <= DateOnly.FromDateTime(to.Date); day = day.AddDays(1))
        {
            if (!Occurs(e, day)) continue;
            var start = day.ToDateTime(e.StartTime);
            var end = start + dur;
            if (end > from && start < to) yield return new Occurrence(e, start, end);
        }
    }

    private static bool Occurs(ScheduledEvent e, DateOnly day)
    {
        if (day < e.StartDate) return false;
        if (e.RepeatUntil is { } until && day > until) return false;
        switch (e.Repeat)
        {
            case RepeatMode.None: return day == e.StartDate;
            case RepeatMode.Daily: return true;
            case RepeatMode.Weekly:
                var mask = e.DaysMask == 0 ? 1 << (int)e.StartDate.DayOfWeek : e.DaysMask;
                return (mask & (1 << (int)day.DayOfWeek)) != 0;
            case RepeatMode.Monthly:
                return day.Day == Math.Min(e.StartDate.Day, DateTime.DaysInMonth(day.Year, day.Month));
        }
        return false;
    }

    public static List<Occurrence> Between(IEnumerable<ScheduledEvent> events, DateTime from, DateTime to) =>
        events.SelectMany(e => Expand(e, from, to)).OrderBy(o => o.Start).ThenByDescending(o => o.Event.Priority).ToList();

    /// <summary>Pairs of occurrences that overlap in time (zero-length jingles never conflict).</summary>
    public static List<(Occurrence A, Occurrence B)> Conflicts(IEnumerable<ScheduledEvent> events, DateTime from, DateTime to)
    {
        var list = Between(events, from, to).Where(o => !o.Event.IsMomentary).ToList();
        var res = new List<(Occurrence, Occurrence)>();
        for (int i = 0; i < list.Count; i++)
            for (int j = i + 1; j < list.Count && list[j].Start < list[i].End; j++)
                res.Add((list[i], list[j]));
        return res;
    }

    public static List<Occurrence> ConflictsFor(ScheduledEvent candidate, IEnumerable<ScheduledEvent> others, DateTime from, DateTime to)
    {
        var mine = Expand(candidate, from, to).ToList();
        var res = new List<Occurrence>();
        foreach (var o in Between(others.Where(x => x.Id != candidate.Id), from, to).Where(o => !o.Event.IsMomentary))
            if (!candidate.IsMomentary && mine.Any(m => m.Start < o.End && o.Start < m.End)) res.Add(o);
        return res;
    }

    /// <summary>The highest-priority occurrence that is active at <paramref name="now"/>.</summary>
    public static Occurrence? Active(IEnumerable<ScheduledEvent> events, DateTime now) =>
        Between(events, now.AddMinutes(-1), now.AddMinutes(1)).Where(o => !o.Event.IsMomentary && o.Start <= now && now < o.End)
            .OrderByDescending(o => o.Event.Priority).ThenByDescending(o => o.Start).FirstOrDefault();

    public static Occurrence? Next(IEnumerable<ScheduledEvent> events, DateTime now) =>
        Between(events, now, now.AddDays(8)).FirstOrDefault(o => o.Start > now);
}

/// <summary>
/// Runs in the background (independent of the main window) and raises events when occurrences start/end.
/// Time is evaluated in the station time zone.
/// </summary>
public sealed class ScheduleRunner : IDisposable
{
    private readonly Func<IReadOnlyList<ScheduledEvent>> _events;
    private readonly Func<DateTime> _now;
    private readonly HashSet<string> _fired = new();
    private Occurrence? _active;
    private Timer? _timer;
    public event Action<Occurrence>? Started;
    public event Action<Occurrence>? Ended;
    public Occurrence? ActiveOccurrence => _active;

    public ScheduleRunner(Func<IReadOnlyList<ScheduledEvent>> events, Func<DateTime>? now = null)
    {
        _events = events; _now = now ?? (() => DateTime.Now);
    }

    public void Start() => _timer ??= new Timer(_ => Tick(), null, 500, 1000);
    public void Stop() { _timer?.Dispose(); _timer = null; }
    public void Dispose() => Stop();

    private int _busy;
    public void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var now = _now();
            var evs = _events();
            // momentary items (jingles/announcements with a concrete source) fire in a 20 s window after their start
            foreach (var o in ScheduleEngine.Between(evs, now.AddMinutes(-1), now.AddSeconds(1)))
            {
                if (!o.Event.IsMomentary || o.Start > now || now - o.Start > TimeSpan.FromSeconds(20)) continue;
                if (_fired.Add(o.Key)) Safe(() => Started?.Invoke(o));
            }
            var act = ScheduleEngine.Active(evs, now);
            if (act?.Key != _active?.Key)
            {
                var old = _active; _active = act;
                if (old != null) Safe(() => Ended?.Invoke(old));
                if (act != null && _fired.Add(act.Key)) Safe(() => Started?.Invoke(act)); // also catches up when the app starts mid-event
            }
            if (_fired.Count > 500) _fired.Clear();
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    private static void Safe(Action a) { try { a(); } catch (Exception ex) { Log.Error("Scheduler", "Handler failed", ex); } }
}
