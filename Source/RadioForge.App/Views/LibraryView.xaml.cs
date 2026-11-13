using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using RadioForge.App.ViewModels;
using RadioForge.Core;

namespace RadioForge.App.Views;

public partial class LibraryView : UserControl
{
    private List<Track> _all = new(); private bool _ready; private List<long>? _dupIds;
    private ShellViewModel Vm => (ShellViewModel)DataContext;
    public LibraryView() { InitializeComponent(); Loaded += (_, _) => { if (!_ready) { FilterBox.ItemsSource = new[] { "All tracks", "Missing files", "Duplicates" }; FilterBox.SelectedIndex = 0; _ready = true; } Reload(); }; }

    public void Reload()
    {
        _all = Vm.Station.Db.GetTracks(Search.Text);
        IEnumerable<Track> v = _all;
        switch (FilterBox.SelectedItem as string)
        {
            case "Missing files": v = _all.Where(t => t.Missing); break;
            case "Duplicates": _dupIds = Vm.Station.Db.FindDuplicates().SelectMany(g => g).Select(t => t.Id).ToList(); v = _all.Where(t => _dupIds.Contains(t.Id)); break;
        }
        var list = v.ToList(); Grid.ItemsSource = list;
        var total = TimeSpan.FromSeconds(list.Sum(t => t.DurationSec));
        Status.Text = $"{list.Count} tracks · {(int)total.TotalHours}h {total.Minutes}m · {list.Count(t => t.Missing)} missing";
    }

    private void OnSearch(object s, TextChangedEventArgs e) { if (_ready) Reload(); }
    private void OnFilter(object s, SelectionChangedEventArgs e) { if (_ready) Reload(); }

    private async Task Import(IEnumerable<string> paths)
    {
        var db = Vm.Station.Db; int n = 0; Status.Text = "Importing…";
        await Task.Run(() =>
        {
            foreach (var p in paths)
            {
                if (Directory.Exists(p)) n += db.ImportFolder(p);
                else if (db.ImportFile(p) != null) n++;
            }
        });
        Reload(); Status.Text += $" — imported {n} file(s)";
    }

    private async void OnImportFiles(object s, RoutedEventArgs e)
    {
        var d = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Filter = "Audio|*.mp3;*.wav;*.flac;*.ogg;*.aac;*.m4a|All files|*.*" };
        if (d.ShowDialog() == true) await Import(d.FileNames);
    }
    private async void OnImportFolder(object s, RoutedEventArgs e)
    {
        var d = new System.Windows.Forms.FolderBrowserDialog { Description = "Choose a music folder (sub-folders are included)", SelectedPath = App.Settings.MusicFolder };
        if (d.ShowDialog() == System.Windows.Forms.DialogResult.OK) await Import(new[] { d.SelectedPath });
    }
    private async void OnDrop(object s, DragEventArgs e) { if (e.Data.GetData(DataFormats.FileDrop) is string[] f) await Import(f); }

    private void OnDuplicates(object s, RoutedEventArgs e) { FilterBox.SelectedItem = "Duplicates"; }
    private void OnRemoveMissing(object s, RoutedEventArgs e)
    {
        var miss = _all.Where(t => t.Missing).ToList();
        if (miss.Count == 0) { Status.Text = "No missing files."; return; }
        if (MessageBox.Show($"Remove {miss.Count} missing track(s) from the library? (Files on disk are not touched.)", "Remove missing", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        foreach (var t in miss) Vm.Station.Db.DeleteTrack(t.Id); Reload();
    }
    private void OnRemoveSelected(object s, RoutedEventArgs e)
    {
        var sel = Grid.SelectedItems.Cast<Track>().ToList(); if (sel.Count == 0) return;
        if (MessageBox.Show($"Remove {sel.Count} track(s) from the library and from all playlists? (Files on disk are not touched.)", "Remove tracks", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        foreach (var t in sel) Vm.Station.Db.DeleteTrack(t.Id); Reload();
    }
    private void OnPlay(object s, RoutedEventArgs e)
    {
        var sel = Grid.SelectedItems.Cast<Track>().Where(t => !t.Missing).ToList();
        if (sel.Count > 0) Vm.Station.PlayTracks(sel.Select(t => t.Path), 0, false);
    }

    private void OnSelect(object s, SelectionChangedEventArgs e)
    {
        if (Grid.SelectedItem is not Track t) { Art.Source = null; Info.Text = ""; return; }
        Info.Text = $"{t.DisplayName}\n{t.Album} {(t.Year > 0 ? $"({t.Year})" : "")}\n{t.DurationText} · {t.FileSize / 1048576.0:0.0} MB";
        var data = t.Missing ? null : Database.LoadArtwork(t.Path);
        if (data == null) { Art.Source = null; return; }
        try { var bi = new BitmapImage(); using var ms = new MemoryStream(data); bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.StreamSource = ms; bi.DecodePixelWidth = 400; bi.EndInit(); Art.Source = bi; } catch { Art.Source = null; }
    }
}
