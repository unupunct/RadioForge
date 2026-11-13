using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using RadioForge.Core;

namespace RadioForge.App.Views;

public partial class LogsView : UserControl
{
    private readonly DispatcherTimer _t = new() { Interval = TimeSpan.FromSeconds(1) }; private int _lastCount = -1; private List<LogEntry> _shown = new();
    public LogsView()
    {
        InitializeComponent(); LevelBox.ItemsSource = new[] { "All", "Info+", "Warnings+", "Errors" }; LevelBox.SelectedIndex = 0;
        Loaded += (_, _) => { _t.Start(); Reload(true); }; Unloaded += (_, _) => _t.Stop(); _t.Tick += (_, _) => Reload(false);
    }
    private void OnChange(object s, RoutedEventArgs e) { if (IsLoaded) Reload(true); }
    private void Reload(bool force)
    {
        var all = Log.Snapshot(); if (!force && all.Count == _lastCount) return; _lastCount = all.Count;
        var min = LevelBox.SelectedIndex switch { 1 => LogLevel.Info, 2 => LogLevel.Warning, 3 => LogLevel.Error, _ => LogLevel.Debug };
        var f = Filter.Text.Trim();
        _shown = all.Where(l => l.Level >= min && (f.Length == 0 || l.Category.Contains(f, StringComparison.OrdinalIgnoreCase) || l.Message.Contains(f, StringComparison.OrdinalIgnoreCase))).ToList();
        Grid.ItemsSource = _shown;
        if (Follow.IsChecked == true && _shown.Count > 0) Grid.ScrollIntoView(_shown[^1]);
    }
    private void OnCopy(object s, RoutedEventArgs e) { var sel = Grid.SelectedItems.Count > 0 ? Grid.SelectedItems.Cast<LogEntry>() : _shown; Clipboard.SetText(string.Join(Environment.NewLine, sel.Select(l => l.ToString()))); }
    private void OnExport(object s, RoutedEventArgs e)
    {
        var d = new Microsoft.Win32.SaveFileDialog { Filter = "Text|*.txt", FileName = $"radioforge-log-{DateTime.Now:yyyyMMdd-HHmm}.txt" };
        if (d.ShowDialog() == true) System.IO.File.WriteAllLines(d.FileName, _shown.Select(l => l.ToString()));
    }
    private void OnOpen(object s, RoutedEventArgs e) => Process.Start("explorer.exe", AppPaths.Logs);
}
