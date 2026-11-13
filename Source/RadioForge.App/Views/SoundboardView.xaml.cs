using System.IO;
using System.Windows;
using System.Windows.Controls;
using NAudio.Wave;
using RadioForge.App.ViewModels;
using RadioForge.Core;

namespace RadioForge.App.Views;

public partial class SoundboardView : UserControl
{
    private int _page; private WaveOutEvent? _preview; private IDisposable? _previewReader;
    private ShellViewModel Vm => (ShellViewModel)DataContext;
    public SoundboardView() { InitializeComponent(); Loaded += (_, _) => { BuildPages(); ShowPage(_page); }; }

    private void BuildPages()
    {
        Pages.Children.Clear();
        for (int i = 0; i < SoundboardConfig.PageCount; i++)
        {
            int p = i; var cat = SoundboardConfig.Categories[i % SoundboardConfig.Categories.Length];
            var b = new RadioButton { Content = $"Page {i + 1}", GroupName = "pg", IsChecked = i == _page, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(14, 6, 14, 6), Style = (Style)FindResource(typeof(System.Windows.Controls.Primitives.ToggleButton)) };
            b.Checked += (_, _) => ShowPage(p); Pages.Children.Add(b);
        }
    }
    private void ShowPage(int p) { _page = p; PadList.ItemsSource = Vm.Station.Profile.Soundboard.Pads.Skip(p * SoundboardConfig.PadsPerPage).Take(SoundboardConfig.PadsPerPage).ToList(); }

    private static SoundPad Pad(object s) => (SoundPad)((FrameworkElement)s).Tag;

    private void OnPadClick(object s, RoutedEventArgs e)
    {
        var pad = Pad(s);
        if (!pad.HasSound) { Edit(pad); return; }
        if (PreviewMode.IsChecked == true) { Preview(pad); return; }
        Vm.Station.PlayPad(pad);
    }
    private void OnPadStop(object s, RoutedEventArgs e) { var pad = Pad(s); Vm.Station.StopPad(pad); StopPreview(); }
    private void OnStopAll(object s, RoutedEventArgs e) { Vm.Station.StopAllPads(); StopPreview(); }
    private void OnPadEdit(object s, RoutedEventArgs e) { Edit(Pad(s)); e.Handled = true; }
    private void OnPadDrop(object s, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] f && f.Length > 0 && Database.IsSupported(f[0]))
        {
            var pad = Pad(s); pad.FilePath = f[0]; if (pad.Name.StartsWith("Pad ")) pad.Name = Path.GetFileNameWithoutExtension(f[0]); Vm.Station.SaveProfile();
        }
    }

    private void Edit(SoundPad pad) { if (new PadEditor(pad) { Owner = Window.GetWindow(this) }.ShowDialog() == true) { Vm.Station.SaveProfile(); ShowPage(_page); } }

    private void Preview(SoundPad pad)
    {
        StopPreview();
        try
        {
            var r = new AudioFileReader(pad.FilePath) { Volume = (float)pad.Volume }; _previewReader = r;
            _preview = new WaveOutEvent(); _preview.Init(r); _preview.Play();
            _preview.PlaybackStopped += (_, _) => Dispatcher.BeginInvoke(StopPreview);
        }
        catch (Exception ex) { MessageBox.Show("Cannot preview: " + ex.Message); }
    }
    private void StopPreview() { try { _preview?.Dispose(); _previewReader?.Dispose(); } catch { } _preview = null; _previewReader = null; }
}

public sealed class PadEditor : Window
{
    private readonly SoundPad _pad; private readonly TextBox _name = new(), _file = new(), _color = new(), _icon = new(); private readonly ComboBox _cat = new(), _mode = new();
    private readonly Slider _vol = new() { Minimum = 0, Maximum = 1.5, Width = 220 }; private readonly Button _hot = new();
    private string _hotkey;

    public PadEditor(SoundPad pad)
    {
        _pad = pad; _hotkey = pad.Hotkey; Title = $"Edit pad {pad.Index + 1}"; SizeToContent = SizeToContent.WidthAndHeight; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)Application.Current.FindResource("PanelBrush"); Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextBrush");
        _name.Text = pad.Name; _file.Text = pad.FilePath; _color.Text = pad.Color; _icon.Text = pad.Icon; _vol.Value = pad.Volume;
        _cat.ItemsSource = SoundboardConfig.Categories; _cat.SelectedItem = pad.Category; _mode.ItemsSource = Enum.GetValues<PadMode>(); _mode.SelectedItem = pad.Mode;
        var swatches = new WrapPanel { Width = 300 };
        foreach (var c in new[] { "#1F6F5C", "#0E7490", "#7C3AED", "#BE185D", "#B45309", "#B91C1C", "#1D4ED8", "#4D7C0F", "#475569", "#C2410C" })
        { var b = new Button { Background = new System.Windows.Media.SolidColorBrush(RadioForge.App.Controls.Palette.Parse(c, System.Windows.Media.Colors.Gray)), Width = 26, Height = 22, Margin = new Thickness(0, 4, 4, 0) }; b.Click += (_, _) => _color.Text = c; swatches.Children.Add(b); }
        var browse = new Button { Content = "Browse…", Margin = new Thickness(6, 0, 0, 0) };
        browse.Click += (_, _) => { var d = new Microsoft.Win32.OpenFileDialog { Filter = "Audio|*.mp3;*.wav;*.flac;*.ogg;*.aac;*.m4a" }; if (d.ShowDialog() == true) { _file.Text = d.FileName; if (_name.Text.StartsWith("Pad ")) _name.Text = Path.GetFileNameWithoutExtension(d.FileName); } };
        _hot.Content = string.IsNullOrEmpty(_hotkey) ? "Click, then press a key…" : _hotkey; _hot.MinWidth = 180;
        _hot.Click += (_, _) => { _hot.Content = "Press a key (Esc clears)…"; MainWindow.CaptureNext = k => { _hotkey = k == "Escape" ? "" : k; _hot.Content = _hotkey == "" ? "(none)" : _hotkey; }; };
        var grid = new Grid { Margin = new Thickness(18) }; grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340) });
        int r = 0;
        void Row(string label, UIElement el) { grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); var t = new TextBlock { Text = label, Margin = new Thickness(0, 8, 14, 0), VerticalAlignment = VerticalAlignment.Center }; Grid.SetRow(t, r); grid.Children.Add(t); Grid.SetRow(el, r); Grid.SetColumn(el, 1); if (el is FrameworkElement fe) fe.Margin = new Thickness(0, 8, 0, 0); grid.Children.Add(el); r++; }
        Row("Name", _name); Row("Category", _cat); Row("Icon (emoji / text)", _icon);
        var colorPanel = new StackPanel { Children = { _color, swatches } }; Row("Color", colorPanel);
        var filePanel = new DockPanel(); DockPanel.SetDock(browse, Dock.Right); filePanel.Children.Add(browse); filePanel.Children.Add(_file); Row("Audio file", filePanel);
        Row("Volume", _vol); Row("Playback mode", _mode); Row("Hotkey", _hot);
        var ok = new Button { Content = "Save", IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 16, 8, 0) }; var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90, Margin = new Thickness(0, 16, 0, 0) };
        var clear = new Button { Content = "Clear pad", Margin = new Thickness(0, 16, 24, 0) };
        clear.Click += (_, _) => { _file.Text = ""; _name.Text = $"Pad {pad.Index + 1}"; _hotkey = ""; };
        ok.Click += (_, _) => Commit(); var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { clear, ok, cancel } };
        grid.RowDefinitions.Add(new RowDefinition()); Grid.SetRow(buttons, r); Grid.SetColumnSpan(buttons, 2); grid.Children.Add(buttons);
        Content = grid; Closed += (_, _) => MainWindow.CaptureNext = null;
    }

    private void Commit()
    {
        _pad.Name = _name.Text.Trim(); _pad.FilePath = _file.Text.Trim(); _pad.Color = _color.Text.Trim(); _pad.Icon = _icon.Text; _pad.Volume = _vol.Value;
        _pad.Category = _cat.SelectedItem as string ?? _pad.Category; _pad.Mode = (PadMode)_mode.SelectedItem; _pad.Hotkey = _hotkey;
        DialogResult = true;
    }
}
