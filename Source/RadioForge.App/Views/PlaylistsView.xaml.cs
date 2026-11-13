using System.Windows;
using System.Windows.Controls;
using RadioForge.App.ViewModels;
using RadioForge.Core;

namespace RadioForge.App.Views;

public sealed class PlaylistNode
{
    public Playlist P { get; init; } = null!; public int Depth { get; init; }
    public string Display => new string(' ', Depth * 3) + (P.IsFolder ? "📁 " : "♫ ") + P.Name;
}
public sealed class PlaylistRow
{
    public int Index { get; set; } public Track Track { get; init; } = null!;
    public string Status => Track.Missing ? "missing" : "";
}

public partial class PlaylistsView : UserControl
{
    private List<Track> _items = new(); private long _currentId; private bool _loading;
    private ShellViewModel Vm => (ShellViewModel)DataContext;
    private Database Db => Vm.Station.Db;
    public PlaylistsView() { InitializeComponent(); Loaded += (_, _) => { LoadLists(); OnFind(null!, null!); UpdateInfo(); }; }

    private void LoadLists()
    {
        var all = Db.GetPlaylists(); var nodes = new List<PlaylistNode>();
        void Add(long? parent, int depth) { foreach (var p in all.Where(x => x.ParentId == parent)) { nodes.Add(new PlaylistNode { P = p, Depth = depth }); Add(p.Id, depth + 1); } }
        Add(null, 0); _loading = true; Lists.ItemsSource = nodes;
        var sel = nodes.FirstOrDefault(n => n.P.Id == _currentId) ?? nodes.FirstOrDefault(n => n.P.Id.ToString() == Vm.Station.Profile.AutoDj.PlaylistId) ?? nodes.FirstOrDefault(n => !n.P.IsFolder);
        _loading = false; if (sel != null) { Lists.SelectedItem = sel; }
    }
    private PlaylistNode? Sel => Lists.SelectedItem as PlaylistNode;
    private string? Ask(string title, string def = "")
    {
        var tb = new TextBox { Text = def, Margin = new Thickness(0, 10, 0, 10), MinWidth = 280 };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80, HorizontalAlignment = HorizontalAlignment.Right };
        var w = new Window { Title = title, Content = new StackPanel { Margin = new Thickness(16), Children = { new TextBlock { Text = title }, tb, ok } }, SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Window.GetWindow(this), ResizeMode = ResizeMode.NoResize, Background = (System.Windows.Media.Brush)FindResource("PanelBrush"), Foreground = (System.Windows.Media.Brush)FindResource("TextBrush") };
        ok.Click += (_, _) => w.DialogResult = true; tb.Loaded += (_, _) => { tb.Focus(); tb.SelectAll(); };
        return w.ShowDialog() == true && tb.Text.Trim().Length > 0 ? tb.Text.Trim() : null;
    }

    private void OnNew(object s, RoutedEventArgs e) { var n = Ask("Playlist name", "New playlist"); if (n == null) return; _currentId = Db.CreatePlaylist(n, Sel?.P.IsFolder == true ? Sel.P.Id : Sel?.P.ParentId); LoadLists(); }
    private void OnNewFolder(object s, RoutedEventArgs e) { var n = Ask("Folder name", "New folder"); if (n == null) return; _currentId = Db.CreatePlaylist(n, Sel?.P.IsFolder == true ? Sel.P.Id : Sel?.P.ParentId, true); LoadLists(); }
    private void OnRename(object s, RoutedEventArgs e) { if (Sel == null) return; var n = Ask("Rename", Sel.P.Name); if (n == null) return; _currentId = Sel.P.Id; Db.RenamePlaylist(_currentId, n); LoadLists(); }
    private void OnDelete(object s, RoutedEventArgs e)
    {
        if (Sel == null) return;
        if (MessageBox.Show($"Delete \"{Sel.P.Name}\"{(Sel.P.IsFolder ? " and everything inside it" : "")}? Audio files are not deleted.", "Delete", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        Db.DeletePlaylist(Sel.P.Id); _currentId = 0; LoadLists(); Items.ItemsSource = null; ListTitle.Text = "Select or create a playlist";
    }
    private void OnImportM3u(object s, RoutedEventArgs e)
    {
        var d = new Microsoft.Win32.OpenFileDialog { Filter = "Playlists|*.m3u;*.m3u8" };
        if (d.ShowDialog() == true) { try { _currentId = Db.ImportM3u(d.FileName); LoadLists(); } catch (Exception ex) { MessageBox.Show("Import failed: " + ex.Message); } }
    }
    private void OnExportM3u(object s, RoutedEventArgs e)
    {
        if (Sel == null || Sel.P.IsFolder) return;
        var d = new Microsoft.Win32.SaveFileDialog { Filter = "M3U playlist|*.m3u", FileName = Sel.P.Name };
        if (d.ShowDialog() == true) Db.ExportM3u(Sel.P.Id, d.FileName);
    }

    private void OnListSelect(object s, SelectionChangedEventArgs e)
    {
        if (_loading || Sel == null) return; _currentId = Sel.P.Id;
        if (Sel.P.IsFolder) { Items.ItemsSource = null; ListTitle.Text = Sel.P.Name + " (folder)"; Total.Text = ""; return; }
        ListTitle.Text = Sel.P.Name; Reload();
    }

    private void Reload() { _items = Db.GetPlaylistTracks(_currentId); Render(); }
    private void Render()
    {
        Items.ItemsSource = _items.Select((t, i) => new PlaylistRow { Index = i + 1, Track = t }).ToList();
        var tot = TimeSpan.FromSeconds(_items.Sum(t => t.DurationSec));
        Total.Text = $"{_items.Count} tracks · {(int)tot.TotalHours}:{tot.Minutes:00}:{tot.Seconds:00}";
    }
    private void Save() { if (_currentId > 0) Db.SavePlaylistTracks(_currentId, _items.Select(t => t.Id)); Render(); }
    private bool EditableList => Sel != null && !Sel.P.IsFolder;

    private void OnFind(object s, TextChangedEventArgs e) { Lib.ItemsSource = Db.GetTracks(Find.Text); }
    private void OnAddSelected(object s, RoutedEventArgs e)
    {
        if (!EditableList) { MessageBox.Show("Select or create a playlist first."); return; }
        foreach (var t in Lib.SelectedItems.Cast<Track>()) _items.Add(t); Save();
    }
    private async void OnDropFiles(object s, DragEventArgs e)
    {
        if (!EditableList || e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        var added = await Task.Run(() =>
        {
            var l = new List<Track>();
            foreach (var f in files)
            {
                IEnumerable<string> paths = System.IO.Directory.Exists(f) ? System.IO.Directory.EnumerateFiles(f, "*.*", System.IO.SearchOption.AllDirectories) : new[] { f };
                foreach (var p in paths.Where(Database.IsSupported)) { var t = Db.GetTrackByPath(p) ?? Db.ImportFile(p); if (t != null) l.Add(t); }
            }
            return l;
        });
        _items.AddRange(added); Save(); OnFind(null!, null!);
    }
    private void OnRemove(object s, RoutedEventArgs e)
    {
        var idx = Items.SelectedItems.Cast<PlaylistRow>().Select(r => r.Index - 1).OrderByDescending(i => i).ToList();
        foreach (var i in idx) _items.RemoveAt(i); Save();
    }
    private void Move(int delta)
    {
        if (Items.SelectedItem is not PlaylistRow r) return; int i = r.Index - 1, j = i + delta;
        if (j < 0 || j >= _items.Count) return; (_items[i], _items[j]) = (_items[j], _items[i]); Save(); Items.SelectedIndex = j;
    }
    private void OnUp(object s, RoutedEventArgs e) => Move(-1);
    private void OnDown(object s, RoutedEventArgs e) => Move(1);
    private void OnPlayItem(object s, RoutedEventArgs e) { if (Items.SelectedItem is PlaylistRow r) Vm.Station.PlayPlaylist(_currentId, r.Index - 1, false); }
    private void OnPlayAll(object s, RoutedEventArgs e) { if (EditableList) Vm.Station.PlayPlaylist(_currentId, 0, Vm.Station.Profile.AutoDj.Shuffle); }
    private void OnUseAutoDj(object s, RoutedEventArgs e) { if (!EditableList) return; Vm.Station.Profile.AutoDj.PlaylistId = _currentId.ToString(); Vm.Station.SaveProfile(); UpdateInfo(); }
    private void OnModes(object s, RoutedEventArgs e) { Vm.Station.Queue.Shuffle = Vm.Station.Profile.AutoDj.Shuffle; Vm.Station.Queue.Repeat = Vm.Station.Profile.AutoDj.Repeat; Vm.Station.SaveProfile(); }
    private void UpdateInfo()
    {
        var id = Vm.Station.Profile.AutoDj.PlaylistId;
        var name = long.TryParse(id, out var l) ? Db.GetPlaylists().FirstOrDefault(p => p.Id == l)?.Name : null;
        AutoDjInfo.Text = "Auto DJ source: " + (name ?? "entire library");
    }
}
