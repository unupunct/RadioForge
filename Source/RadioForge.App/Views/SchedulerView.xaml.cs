using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using RadioForge.App.Controls;
using RadioForge.App.ViewModels;
using RadioForge.Core;

namespace RadioForge.App.Views;

public partial class SchedulerView : UserControl
{
    private DateTime _anchor = DateTime.Today;
    private ShellViewModel Vm => (ShellViewModel)DataContext;
    private double ColW => Math.Max(80, (WeekCanvas.ActualWidth - 52) / 7);
    private const double HourH = 40;

    private static readonly Dictionary<EventKind, string> Colors = new()
    {
        [EventKind.MusicPlaylist] = "#1F6F5C", [EventKind.RadioProgram] = "#1D4ED8", [EventKind.CommercialBreak] = "#B45309", [EventKind.StationJingle] = "#7C3AED",
        [EventKind.News] = "#B91C1C", [EventKind.PreRecordedShow] = "#0E7490", [EventKind.SpecialAnnouncement] = "#BE185D",
    };

    public SchedulerView() { InitializeComponent(); Loaded += (_, _) => { Vm.Station.Events.CollectionChanged += (_, _) => Dispatcher.BeginInvoke(Render); Render(); WeekScroll.ScrollToVerticalOffset(7 * HourH); }; }

    private DateTime WeekStart => _anchor.Date.AddDays(-(((int)_anchor.DayOfWeek + 6) % 7)); // Monday
    private void OnPrev(object s, RoutedEventArgs e) { _anchor = WeekBtn.IsChecked == true ? _anchor.AddDays(-7) : _anchor.AddMonths(-1); Render(); }
    private void OnNext(object s, RoutedEventArgs e) { _anchor = WeekBtn.IsChecked == true ? _anchor.AddDays(7) : _anchor.AddMonths(1); Render(); }
    private void OnToday(object s, RoutedEventArgs e) { _anchor = DateTime.Today; Render(); }
    private void OnView(object s, RoutedEventArgs e) { if (IsLoaded) Render(); }
    private void OnCanvasSize(object s, SizeChangedEventArgs e) { if (IsLoaded) DrawWeek(); }
    private void OnNew(object s, RoutedEventArgs e) => Edit(new ScheduledEvent { StartDate = DateOnly.FromDateTime(DateTime.Today), StartTime = new TimeOnly(DateTime.Now.Hour, 0) }, true);

    private void Render()
    {
        TzText.Text = "Station time: " + Vm.Station.Profile.TimeZone.DisplayName;
        bool week = WeekBtn.IsChecked == true;
        WeekHost.Visibility = week ? Visibility.Visible : Visibility.Collapsed; MonthGrid.Visibility = week ? Visibility.Collapsed : Visibility.Visible;
        if (week) { RangeText.Text = $"{WeekStart:d MMM} – {WeekStart.AddDays(6):d MMM yyyy}"; DrawWeek(); } else { RangeText.Text = _anchor.ToString("MMMM yyyy"); DrawMonth(); }
        ShowConflicts();
    }

    private (DateTime from, DateTime to) Range()
    {
        if (WeekBtn.IsChecked == true) return (WeekStart, WeekStart.AddDays(7));
        var first = new DateTime(_anchor.Year, _anchor.Month, 1); var start = first.AddDays(-(((int)first.DayOfWeek + 6) % 7)); return (start, start.AddDays(42));
    }

    private void ShowConflicts()
    {
        var (f, t) = Range(); var conf = ScheduleEngine.Conflicts(Vm.Station.Events, f, t);
        ConflictText.Foreground = conf.Count > 0 ? (Brush)FindResource("WarnBrush") : (Brush)FindResource("MutedBrush");
        ConflictText.Text = conf.Count == 0 ? "✔ No schedule conflicts in this range." : "⚠ Conflicts: " + string.Join("   ", conf.Take(5).Select(c => $"\"{c.A.Event.Title}\" overlaps \"{c.B.Event.Title}\" ({c.B.Start:ddd HH:mm})")) + (conf.Count > 5 ? $" … +{conf.Count - 5} more" : "") + "  (higher priority wins)";
    }

    private void DrawWeek()
    {
        WeekCanvas.Children.Clear(); Header.Children.Clear();
        double cw = ColW, left = 52; var line = (Brush)FindResource("LineBrush"); var muted = (Brush)FindResource("MutedBrush");
        for (int h = 0; h < 24; h++)
        {
            var l = new Line { X1 = left, X2 = left + 7 * cw, Y1 = h * HourH, Y2 = h * HourH, Stroke = line, StrokeThickness = 1 }; WeekCanvas.Children.Add(l);
            var t = new TextBlock { Text = $"{h:00}:00", Foreground = muted, FontSize = 11 }; Canvas.SetLeft(t, 8); Canvas.SetTop(t, h * HourH + 2); WeekCanvas.Children.Add(t);
        }
        for (int d = 0; d <= 7; d++) WeekCanvas.Children.Add(new Line { X1 = left + d * cw, X2 = left + d * cw, Y1 = 0, Y2 = 24 * HourH, Stroke = line, StrokeThickness = 1 });
        for (int d = 0; d < 7; d++)
        {
            var day = WeekStart.AddDays(d); bool today = day == DateTime.Today;
            var tb = new TextBlock { Text = day.ToString("ddd d MMM"), FontWeight = FontWeights.SemiBold, Foreground = today ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("TextBrush"), Width = cw, TextAlignment = TextAlignment.Center };
            Canvas.SetLeft(tb, left + d * cw); Canvas.SetTop(tb, 6); Header.Children.Add(tb);
            var hit = new Border { Width = cw, Height = 24 * HourH, Background = Brushes.Transparent, Tag = day }; hit.MouseLeftButtonDown += OnSlotClick; Canvas.SetLeft(hit, left + d * cw); WeekCanvas.Children.Insert(0, hit);
        }
        var (f, t2) = Range(); var occ = ScheduleEngine.Between(Vm.Station.Events, f, t2);
        var conflicts = ScheduleEngine.Conflicts(Vm.Station.Events, f, t2).SelectMany(c => new[] { c.A.Key, c.B.Key }).ToHashSet();
        foreach (var o in occ)
        {
            var segStart = o.Start < o.Start.Date ? o.Start.Date : o.Start;
            double y = (o.Start - o.Start.Date).TotalHours * HourH, hgt = Math.Max(18, Math.Min((o.End - o.Start).TotalHours, 24 - (o.Start - o.Start.Date).TotalHours) * HourH);
            int di = (int)(o.Start.Date - WeekStart).TotalDays; if (di < 0 || di > 6) continue;
            bool bad = conflicts.Contains(o.Key);
            var b = new Border
            {
                Width = cw - 4, Height = hgt - 1, CornerRadius = new CornerRadius(5), Padding = new Thickness(5, 2, 5, 2), Cursor = Cursors.Hand, Tag = o.Event,
                Background = new SolidColorBrush(Palette.Parse(Colors[o.Event.Kind], System.Windows.Media.Colors.Gray)), BorderBrush = bad ? (Brush)FindResource("WarnBrush") : Brushes.Transparent, BorderThickness = new Thickness(2),
                ToolTip = $"{o.Event.Title}\n{o.Start:HH:mm}–{o.End:HH:mm}  ({o.Event.Kind}, priority {o.Event.Priority}){(bad ? "\n⚠ conflicts with another event" : "")}\n{o.Event.Description}", Opacity = o.Event.Enabled ? 1 : 0.4,
                Child = new TextBlock { Text = $"{o.Start:HH:mm}  {o.Event.Title}", Foreground = Brushes.White, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.Wrap }
            };
            b.MouseLeftButtonDown += (s, e) => { e.Handled = true; Edit(((ScheduledEvent)((FrameworkElement)s).Tag), false); };
            Canvas.SetLeft(b, left + di * cw + 2); Canvas.SetTop(b, y); WeekCanvas.Children.Add(b);
        }
        var now = TimeZoneInfo.ConvertTime(DateTime.UtcNow, Vm.Station.Profile.TimeZone);
        if (now >= WeekStart && now < WeekStart.AddDays(7)) { double ny = now.TimeOfDay.TotalHours * HourH; int di = (int)(now.Date - WeekStart).TotalDays; WeekCanvas.Children.Add(new Line { X1 = left + di * cw, X2 = left + (di + 1) * cw, Y1 = ny, Y2 = ny, Stroke = (Brush)FindResource("DangerBrush"), StrokeThickness = 2 }); }
    }

    private void OnSlotClick(object s, MouseButtonEventArgs e)
    {
        if (e.ClickCount < 2) return;
        var day = (DateTime)((FrameworkElement)s).Tag; var y = e.GetPosition((IInputElement)s).Y; int hour = (int)(y / HourH);
        Edit(new ScheduledEvent { StartDate = DateOnly.FromDateTime(day), StartTime = new TimeOnly(Math.Clamp(hour, 0, 23), 0) }, true);
    }

    private void DrawMonth()
    {
        MonthGrid.Children.Clear(); var (f, t) = Range(); var occ = ScheduleEngine.Between(Vm.Station.Events, f, t);
        for (var d = f; d < t; d = d.AddDays(1))
        {
            bool inMonth = d.Month == _anchor.Month; var day = d;
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = d.Day.ToString() + (d.Day == 1 ? d.ToString(" MMM") : ""), FontWeight = d == DateTime.Today ? FontWeights.Bold : FontWeights.Normal, Foreground = d == DateTime.Today ? (Brush)FindResource("AccentBrush") : inMonth ? (Brush)FindResource("TextBrush") : (Brush)FindResource("MutedBrush") });
            var todays = occ.Where(o => o.Start.Date == d).ToList();
            foreach (var o in todays.Take(4))
            {
                var tb = new Border { Background = new SolidColorBrush(Palette.Parse(Colors[o.Event.Kind], System.Windows.Media.Colors.Gray)), CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 1, 0, 0), Padding = new Thickness(3, 0, 3, 0), Cursor = Cursors.Hand, Tag = o.Event, Child = new TextBlock { Text = $"{o.Start:HH:mm} {o.Event.Title}", FontSize = 10.5, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis } };
                tb.MouseLeftButtonDown += (s, e) => { e.Handled = true; Edit((ScheduledEvent)((FrameworkElement)s).Tag, false); }; panel.Children.Add(tb);
            }
            if (todays.Count > 4) panel.Children.Add(new TextBlock { Text = $"+{todays.Count - 4} more", FontSize = 10, Foreground = (Brush)FindResource("MutedBrush") });
            var cell = new Border { BorderBrush = (Brush)FindResource("LineBrush"), BorderThickness = new Thickness(0, 0, 1, 1), Padding = new Thickness(4), Child = panel, Background = inMonth ? Brushes.Transparent : new SolidColorBrush(System.Windows.Media.Color.FromArgb(18, 128, 128, 128)) };
            cell.MouseLeftButtonDown += (s, e) => { if (e.ClickCount == 2) Edit(new ScheduledEvent { StartDate = DateOnly.FromDateTime(day), StartTime = new TimeOnly(12, 0) }, true); };
            MonthGrid.Children.Add(cell);
        }
    }

    private void Edit(ScheduledEvent ev, bool isNew)
    {
        var ed = new ScheduleEditor(ev, Vm.Station, isNew) { Owner = Window.GetWindow(this) };
        if (ed.ShowDialog() != true) return;
        if (ed.Deleted) Vm.Station.DeleteEvent(ev);
        Render();
    }
}

public sealed class ScheduleEditor : Window
{
    private readonly ScheduledEvent _e; private readonly Core.Station.StationController _st; public bool Deleted { get; private set; }
    private readonly TextBox _title = new(), _desc = new() { AcceptsReturn = true, Height = 50 }, _time = new(), _dur = new(), _prio = new(), _ref = new();
    private readonly ComboBox _kind = new(), _src = new(), _rep = new(), _list = new(), _pad = new(); private readonly DatePicker _date = new(), _until = new();
    private readonly CheckBox _enabled = new() { Content = "Enabled" }; private readonly CheckBox _archive = new() { Content = "Record this show and list it in the podcast feed" }; private readonly CheckBox[] _days = new CheckBox[7]; private readonly TextBlock _warn = new() { Foreground = Brushes.Orange, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 };
    private readonly StackPanel _srcPanel = new();

    public ScheduleEditor(ScheduledEvent e, Core.Station.StationController st, bool isNew)
    {
        _e = e; _st = st; Title = isNew ? "New scheduled event" : "Edit scheduled event"; SizeToContent = SizeToContent.WidthAndHeight; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.FindResource("PanelBrush"); Foreground = (Brush)Application.Current.FindResource("TextBrush");
        _title.Text = e.Title; _desc.Text = e.Description; _time.Text = e.StartTime.ToString("HH:mm"); _dur.Text = e.DurationMinutes.ToString(); _prio.Text = e.Priority.ToString(); _enabled.IsChecked = e.Enabled; _archive.IsChecked = e.Archive;
        _kind.ItemsSource = Enum.GetValues<EventKind>(); _kind.SelectedItem = e.Kind; _src.ItemsSource = Enum.GetValues<SourceKind>(); _src.SelectedItem = e.Source; _rep.ItemsSource = Enum.GetValues<RepeatMode>(); _rep.SelectedItem = e.Repeat;
        _date.SelectedDate = e.StartDate.ToDateTime(TimeOnly.MinValue); _until.SelectedDate = e.RepeatUntil?.ToDateTime(TimeOnly.MinValue);
        var pls = st.Db.GetPlaylists().Where(p => !p.IsFolder).ToList(); _list.ItemsSource = pls; _list.DisplayMemberPath = "Name"; _list.SelectedItem = pls.FirstOrDefault(p => p.Id.ToString() == e.SourceRef);
        _pad.ItemsSource = st.Profile.Soundboard.Pads.Where(p => p.HasSound).ToList(); _pad.DisplayMemberPath = "Name"; _pad.SelectedItem = st.Profile.Soundboard.Pads.FirstOrDefault(p => p.Index.ToString() == e.SourceRef);
        _ref.Text = e.Source == SourceKind.AudioFile ? e.SourceRef : "";
        var daysPanel = new WrapPanel(); var names = new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
        for (int i = 0; i < 7; i++) { _days[i] = new CheckBox { Content = names[i], Margin = new Thickness(0, 0, 8, 0), IsChecked = e.DaysMask == 0 ? i == (int)e.StartDate.DayOfWeek : (e.DaysMask & (1 << i)) != 0 }; daysPanel.Children.Add(_days[i]); }
        var browse = new Button { Content = "Browse…", Margin = new Thickness(6, 0, 0, 0) }; browse.Click += (_, _) => { var d = new Microsoft.Win32.OpenFileDialog { Filter = "Audio|*.mp3;*.wav;*.flac;*.ogg;*.aac;*.m4a" }; if (d.ShowDialog() == true) _ref.Text = d.FileName; };
        var filePanel = new DockPanel(); DockPanel.SetDock(browse, Dock.Right); filePanel.Children.Add(browse); filePanel.Children.Add(_ref);
        _srcPanel.Children.Add(_list); _srcPanel.Children.Add(filePanel); _srcPanel.Children.Add(_pad);
        _src.SelectionChanged += (_, _) => { _list.Visibility = (SourceKind?)_src.SelectedItem == SourceKind.Playlist ? Visibility.Visible : Visibility.Collapsed; filePanel.Visibility = (SourceKind?)_src.SelectedItem == SourceKind.AudioFile ? Visibility.Visible : Visibility.Collapsed; _pad.Visibility = (SourceKind?)_src.SelectedItem == SourceKind.SoundboardPad ? Visibility.Visible : Visibility.Collapsed; };
        _src.RaiseEvent(new SelectionChangedEventArgs(System.Windows.Controls.Primitives.Selector.SelectionChangedEvent, Array.Empty<object>(), Array.Empty<object>()));
        var grid = new Grid { Margin = new Thickness(18), Width = 560 }; grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
        int r = 0; void Row(string l, UIElement el) { grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); var t = new TextBlock { Text = l, Margin = new Thickness(0, 8, 8, 0), VerticalAlignment = VerticalAlignment.Center }; Grid.SetRow(t, r); grid.Children.Add(t); Grid.SetRow(el, r); Grid.SetColumn(el, 1); if (el is FrameworkElement fe) fe.Margin = new Thickness(0, 8, 0, 0); grid.Children.Add(el); r++; }
        Row("Title", _title); Row("Description", _desc); Row("Type", _kind); Row("Start date", _date); Row("Start time (HH:mm)", _time); Row("Duration (minutes)", _dur);
        Row("Audio source", _src); Row("", _srcPanel); Row("Repeat", _rep); Row("Weekly days", daysPanel); Row("Repeat until", _until); Row("Priority (higher wins)", _prio); Row("", _enabled); Row("Archive", _archive);
        var ok = new Button { Content = "Save", IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 14, 8, 0) }; var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90, Margin = new Thickness(0, 14, 0, 0) }; var del = new Button { Content = "Delete", Margin = new Thickness(0, 14, 24, 0), Visibility = isNew ? Visibility.Collapsed : Visibility.Visible };
        ok.Click += (_, _) => Commit(); del.Click += (_, _) => { Deleted = true; DialogResult = true; };
        var bp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { del, ok, cancel } };
        grid.RowDefinitions.Add(new RowDefinition()); Grid.SetRow(_warn, r); Grid.SetColumnSpan(_warn, 2); grid.Children.Add(_warn); r++;
        grid.RowDefinitions.Add(new RowDefinition()); Grid.SetRow(bp, r); Grid.SetColumnSpan(bp, 2); grid.Children.Add(bp);
        Content = grid;
    }

    private void Commit()
    {
        if (!TimeOnly.TryParse(_time.Text, out var time)) { _warn.Text = "Enter the start time as HH:mm."; return; }
        if (!int.TryParse(_dur.Text, out var dur) || dur < 1 || dur > 24 * 60) { _warn.Text = "Duration must be 1–1440 minutes."; return; }
        if (_date.SelectedDate == null) { _warn.Text = "Choose a start date."; return; }
        var c = new ScheduledEvent
        {
            Id = _e.Id, Title = _title.Text.Trim().Length == 0 ? "Untitled" : _title.Text.Trim(), Description = _desc.Text, Kind = (EventKind)_kind.SelectedItem, StartDate = DateOnly.FromDateTime(_date.SelectedDate.Value), StartTime = time, DurationMinutes = dur,
            Source = (SourceKind)_src.SelectedItem, Repeat = (RepeatMode)_rep.SelectedItem, RepeatUntil = _until.SelectedDate.HasValue ? DateOnly.FromDateTime(_until.SelectedDate.Value) : null, Priority = int.TryParse(_prio.Text, out var p) ? p : 1, Enabled = _enabled.IsChecked == true, Archive = _archive.IsChecked == true,
            DaysMask = Enumerable.Range(0, 7).Where(i => _days[i].IsChecked == true).Sum(i => 1 << i)
        };
        c.SourceRef = c.Source switch { SourceKind.Playlist => (_list.SelectedItem as Playlist)?.Id.ToString() ?? "", SourceKind.AudioFile => _ref.Text.Trim(), _ => (_pad.SelectedItem as SoundPad)?.Index.ToString() ?? "" };
        if (c.SourceRef == "") { _warn.Text = "Choose the playlist, file or pad to play."; return; }
        var from = c.StartDate.ToDateTime(TimeOnly.MinValue); var conflicts = ScheduleEngine.ConflictsFor(c, _st.Events, from, from.AddDays(c.Repeat == RepeatMode.None ? 2 : 60));
        if (conflicts.Count > 0 && !_warnedOnce)
        {
            _warn.Text = $"⚠ Conflicts with \"{conflicts[0].Event.Title}\" ({conflicts[0].Start:ddd d MMM HH:mm}){(conflicts.Count > 1 ? $" and {conflicts.Count - 1} more" : "")}. The higher priority event wins. Press Save again to keep it anyway.";
            _warnedOnce = true; return;
        }
        _st.SaveEvent(c); DialogResult = true;
    }
    private bool _warnedOnce;
}
