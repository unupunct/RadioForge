using System.Windows;
using System.Windows.Controls;
using RadioForge.App.Controls;
using RadioForge.App.ViewModels;

namespace RadioForge.App.Views;

public partial class DashboardView : UserControl
{
    private bool _init;
    public DashboardView() { InitializeComponent(); Loaded += (_, _) => Init(); }

    private void Init()
    {
        if (_init) return; _init = true;
        var s = App.Settings;
        PresetBox.ItemsSource = VisPreset.All; PresetBox.SelectedIndex = Math.Clamp(s.VisPreset, 0, VisPreset.All.Count - 1);
        BgBox.ItemsSource = new[] { "Preset", "Black", "Charcoal", "Midnight" }; BgBox.SelectedItem = string.IsNullOrEmpty(s.VisBackground) ? "Preset" : s.VisBackground;
        SensSlider.Value = s.VisSensitivity;
        Apply(); RefreshColorBoxes();
    }

    public static void ApplyTo(VisualizerControl v)
    {
        var s = App.Settings;
        v.Preset = VisPreset.All[Math.Clamp(s.VisPreset, 0, VisPreset.All.Count - 1)];
        v.Sensitivity = s.VisSensitivity;
        v.CustomA = string.IsNullOrWhiteSpace(s.VisColorA) ? null : s.VisColorA; v.CustomB = string.IsNullOrWhiteSpace(s.VisColorB) ? null : s.VisColorB;
        v.BackgroundOverride = string.IsNullOrEmpty(s.VisBackground) ? null : s.VisBackground; v.BackgroundImage = s.VisImage;
    }
    private void Apply() => ApplyTo(Vis);

    private void OnPreset(object sender, SelectionChangedEventArgs e) { if (!_init) return; App.Settings.VisPreset = Math.Max(0, PresetBox.SelectedIndex); Apply(); RefreshColorBoxes(); }

    private static VisPreset Cur => VisPreset.All[Math.Clamp(App.Settings.VisPreset, 0, VisPreset.All.Count - 1)];
    /// <summary>Always show the colors in effect (custom or the preset's own), with swatches.</summary>
    private void RefreshColorBoxes()
    {
        var a = string.IsNullOrWhiteSpace(App.Settings.VisColorA) ? Cur.ColorA : App.Settings.VisColorA; var b = string.IsNullOrWhiteSpace(App.Settings.VisColorB) ? Cur.ColorB : App.Settings.VisColorB;
        ColA.Text = a; ColB.Text = b;
        SwA.Background = new System.Windows.Media.SolidColorBrush(Palette.Parse(a, System.Windows.Media.Colors.LimeGreen)); SwB.Background = new System.Windows.Media.SolidColorBrush(Palette.Parse(b, System.Windows.Media.Colors.Cyan));
    }
    private void OnResetColors(object sender, RoutedEventArgs e) { App.Settings.VisColorA = ""; App.Settings.VisColorB = ""; Apply(); RefreshColorBoxes(); }
    private void OnSens(object sender, RoutedPropertyChangedEventArgs<double> e) { if (!_init) return; App.Settings.VisSensitivity = SensSlider.Value; Apply(); }
    private void OnBg(object sender, SelectionChangedEventArgs e) { if (!_init) return; App.Settings.VisBackground = BgBox.SelectedItem as string is null or "Preset" ? "" : (string)BgBox.SelectedItem; Apply(); }
    private void OnColors(object sender, RoutedEventArgs e)
    {
        static bool Valid(string t) { try { System.Windows.Media.ColorConverter.ConvertFromString(t); return t.StartsWith('#'); } catch { return false; } }
        string Norm(string t, string preset) => !Valid(t.Trim()) || string.Equals(t.Trim(), preset, StringComparison.OrdinalIgnoreCase) ? "" : t.Trim();
        App.Settings.VisColorA = Norm(ColA.Text, Cur.ColorA); App.Settings.VisColorB = Norm(ColB.Text, Cur.ColorB); Apply(); RefreshColorBoxes();
    }
    private void OnImage(object sender, RoutedEventArgs e)
    {
        var d = new Microsoft.Win32.OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp|All files|*.*", Title = "Visualizer background (cancel to clear)" };
        App.Settings.VisImage = d.ShowDialog() == true ? d.FileName : ""; Apply();
    }
    private void OnFull(object sender, RoutedEventArgs e)
    {
        var w = new Window { WindowStyle = WindowStyle.None, WindowState = WindowState.Maximized, Background = System.Windows.Media.Brushes.Black, Title = "RadioForge Visualizer" };
        var v = new VisualizerControl { Analyzer = ((ShellViewModel)DataContext).Engine.Analyzer }; ApplyTo(v); w.Content = v;
        w.KeyDown += (_, k) => { if (k.Key == System.Windows.Input.Key.Escape) w.Close(); else if (k.Key is System.Windows.Input.Key.Right or System.Windows.Input.Key.Left) { App.Settings.VisPreset = (App.Settings.VisPreset + (k.Key == System.Windows.Input.Key.Right ? 1 : VisPreset.All.Count - 1)) % VisPreset.All.Count; ApplyTo(v); } };
        w.MouseDoubleClick += (_, _) => w.Close();
        w.Show();
    }
}
