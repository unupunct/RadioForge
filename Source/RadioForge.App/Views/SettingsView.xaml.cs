using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using RadioForge.App.Controls;
using RadioForge.App.ViewModels;
using RadioForge.Core;
using RadioForge.Core.Audio;
using RadioForge.Core.Station;
using RadioForge.Core.Streaming;

namespace RadioForge.App.Views;

public static class ProfileSwitcher
{
    /// <summary>Stops the current station, opens <paramref name="profile"/> and replaces the main window.</summary>
    public static async Task ReloadAsync(StationProfile profile)
    {
        var old = Application.Current.MainWindow as MainWindow;
        if (old != null) { try { await old.Shell.Station.StopBroadcastAsync(); } catch { } }
        ProfileStore.Save(App.Station!.Profile);
        old?.CloseForReload();
        App.OpenStation(ProfileStore.Load(profile.Id) ?? profile);
        var w = new MainWindow(); Application.Current.MainWindow = w; w.Show();
    }
}

public partial class SettingsView : UserControl
{
    private ShellViewModel Vm => (ShellViewModel)DataContext;
    public SettingsView()
    {
        InitializeComponent();
        ThemeBox.ItemsSource = new[] { "Dark", "Light" }; CloseBox.ItemsSource = new[] { "Ask", "Background", "Exit" };
        TzBox.ItemsSource = TimeZoneInfo.GetSystemTimeZones();
        foreach (var c in new[] { "#00E5A0", "#00B8FF", "#FF3DCB", "#FFD23F", "#FF6B3D", "#9B7BFF" })
        { var b = new Button { Width = 22, Height = 22, Margin = new Thickness(0, 0, 4, 0), Background = new System.Windows.Media.SolidColorBrush(Palette.Parse(c, System.Windows.Media.Colors.Gray)) }; b.Click += (_, _) => { App.Settings.AccentColor = c; ThemeManager.Apply(App.Settings); }; Swatches.Children.Add(b); }
        Loaded += (_, _) => { LoadProfiles(); BuildShortcuts(); LogsPath.Text = AppPaths.Logs; UpdateFfmpeg(); AboutText.Text = $"RadioForge {typeof(App).Assembly.GetName().Version?.ToString(3)} · .NET {Environment.Version} · NAudio / LAME (LGPL) · data folder: {AppPaths.Root}"; };
    }

    private void OnTheme(object s, RoutedEventArgs e) { if (!IsLoaded) return; ThemeManager.Apply(App.Settings); foreach (Window w in Application.Current.Windows) w.FontSize = 13 * App.Settings.FontScale; ProfileStore.SaveSettings(App.Settings); }

    // ---- profiles
    private void LoadProfiles() { ProfileBox.ItemsSource = ProfileStore.List(); ProfileBox.SelectedItem = ((List<StationProfile>)ProfileBox.ItemsSource).FirstOrDefault(p => p.Id == Vm.Station.Profile.Id); }
    private StationProfile? SelProfile => ProfileBox.SelectedItem as StationProfile;
    private async void OnSwitch(object s, RoutedEventArgs e)
    {
        if (SelProfile == null || SelProfile.Id == Vm.Station.Profile.Id) return;
        if (Vm.Station.Streams.AnyActive && MessageBox.Show("Switching profiles stops the current broadcast. Continue?", "Switch profile", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        await ProfileSwitcher.ReloadAsync(SelProfile);
    }
    private async void OnNewProfile(object s, RoutedEventArgs e)
    {
        var name = Prompt("New station profile name", "My Radio"); if (name == null) return;
        var p = ProfileStore.Create(name); await ProfileSwitcher.ReloadAsync(p);
    }
    private void OnRenameProfile(object s, RoutedEventArgs e) { if (SelProfile == null) return; var n = Prompt("Rename profile", SelProfile.Name); if (n == null) return; SelProfile.Name = n; ProfileStore.Save(SelProfile); if (SelProfile.Id == Vm.Station.Profile.Id) Vm.Station.Profile.Name = n; LoadProfiles(); }
    private void OnDeleteProfile(object s, RoutedEventArgs e)
    {
        if (SelProfile == null) return;
        if (SelProfile.Id == Vm.Station.Profile.Id) { MessageBox.Show("Switch to another profile before deleting this one."); return; }
        if (MessageBox.Show($"Delete profile \"{SelProfile.Name}\" with all its playlists, schedule and settings? This cannot be undone.", "Delete profile", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        ProfileStore.Delete(SelProfile.Id); LoadProfiles();
    }
    private void OnImportProfile(object s, RoutedEventArgs e)
    {
        var d = new Microsoft.Win32.OpenFileDialog { Filter = "RadioForge profile|*.rfprofile;*.json" }; if (d.ShowDialog() != true) return;
        try { ProfileStore.Import(d.FileName); LoadProfiles(); MessageBox.Show("Profile imported. Server passwords are not part of exports — enter them under Streaming Servers."); } catch (Exception ex) { MessageBox.Show("Import failed: " + ex.Message); }
    }
    private void OnExportProfile(object s, RoutedEventArgs e)
    {
        var p = SelProfile ?? Vm.Station.Profile; if (p.Id == Vm.Station.Profile.Id) Vm.Station.SaveProfile();
        var d = new Microsoft.Win32.SaveFileDialog { Filter = "RadioForge profile|*.rfprofile", FileName = p.Name }; if (d.ShowDialog() != true) return;
        var db = p.Id == Vm.Station.Profile.Id ? Vm.Station.Db : ProfileStore.OpenDb(p.Id);
        ProfileStore.Export(ProfileStore.Load(p.Id) ?? p, db, d.FileName);
    }
    private async void OnWizard(object s, RoutedEventArgs e)
    {
        var w = new SetupWizard(Vm.Station.Profile) { Owner = Window.GetWindow(this) };
        if (w.ShowDialog() == true) { ProfileStore.Save(Vm.Station.Profile); await ProfileSwitcher.ReloadAsync(Vm.Station.Profile); }
    }

    private string? Prompt(string title, string def)
    {
        var tb = new TextBox { Text = def, Margin = new Thickness(0, 10, 0, 10), MinWidth = 300 }; var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80, HorizontalAlignment = HorizontalAlignment.Right };
        var w = new Window { Title = title, SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Window.GetWindow(this), ResizeMode = ResizeMode.NoResize, Background = (System.Windows.Media.Brush)FindResource("PanelBrush"), Foreground = (System.Windows.Media.Brush)FindResource("TextBrush"), Content = new StackPanel { Margin = new Thickness(16), Children = { new TextBlock { Text = title }, tb, ok } } };
        ok.Click += (_, _) => w.DialogResult = true; tb.Loaded += (_, _) => { tb.Focus(); tb.SelectAll(); };
        return w.ShowDialog() == true && tb.Text.Trim().Length > 0 ? tb.Text.Trim() : null;
    }

    // ---- shortcuts
    private static readonly Dictionary<string, string> Names = new()
    {
        ["PlayPause"] = "Play / Pause", ["NextTrack"] = "Next track", ["ToggleRecording"] = "Start/stop recording", ["ToggleMic"] = "Microphone on/off",
        ["Pad1"] = "Jingle 1", ["Pad2"] = "Jingle 2", ["Pad3"] = "Jingle 3", ["Pad4"] = "Jingle 4", ["StartBroadcast"] = "Start broadcasting", ["StopBroadcast"] = "Stop broadcasting",
    };
    private void BuildShortcuts()
    {
        Shortcuts.Items.Clear();
        foreach (var kv in App.Settings.Shortcuts.OrderBy(k => Names.Keys.ToList().IndexOf(k.Key)))
        {
            var key = kv.Key; var btn = new Button { Content = kv.Value, MinWidth = 130, Margin = new Thickness(8, 0, 0, 0) };
            btn.Click += (_, _) =>
            {
                btn.Content = "press keys…";
                MainWindow.CaptureNext = k =>
                {
                    var clash = App.Settings.Shortcuts.FirstOrDefault(x => x.Key != key && x.Value == k).Key;
                    if (clash != null) { btn.Content = App.Settings.Shortcuts[key]; MessageBox.Show($"{k} is already used by \"{(Names.TryGetValue(clash, out var n) ? n : clash)}\"."); return; }
                    App.Settings.Shortcuts[key] = k; btn.Content = k; ProfileStore.SaveSettings(App.Settings);
                };
            };
            Shortcuts.Items.Add(new StackPanel { Orientation = Orientation.Horizontal, Width = 380, Margin = new Thickness(0, 3, 0, 3), Children = { new TextBlock { Text = Names.TryGetValue(key, out var nm) ? nm : key, Width = 160, VerticalAlignment = VerticalAlignment.Center }, btn } });
        }
    }
    private void OnResetKeys(object s, RoutedEventArgs e) { App.Settings.Shortcuts = AppSettings.DefaultShortcuts(); ProfileStore.SaveSettings(App.Settings); BuildShortcuts(); }

    // ---- storage
    private void OnMusicFolder(object s, RoutedEventArgs e) { var d = new System.Windows.Forms.FolderBrowserDialog { SelectedPath = App.Settings.MusicFolder }; if (d.ShowDialog() == System.Windows.Forms.DialogResult.OK) App.Settings.MusicFolder = d.SelectedPath; }
    private void OnBackupFolder(object s, RoutedEventArgs e) { var d = new System.Windows.Forms.FolderBrowserDialog { SelectedPath = App.Settings.BackupFolder }; if (d.ShowDialog() == System.Windows.Forms.DialogResult.OK) App.Settings.BackupFolder = d.SelectedPath; }
    private void OnOpenLogs(object s, RoutedEventArgs e) => Process.Start("explorer.exe", AppPaths.Logs);
    private string BackupDir => string.IsNullOrWhiteSpace(App.Settings.BackupFolder) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RadioForge Backups") : App.Settings.BackupFolder;
    private void OnBackup(object s, RoutedEventArgs e) { try { Vm.Station.SaveProfile(); ProfileStore.SaveSettings(App.Settings); BackupMsg.Text = "Saved " + BackupService.Create(BackupDir); } catch (Exception ex) { BackupMsg.Text = "Backup failed: " + ex.Message; } }
    private async void OnRestore(object s, RoutedEventArgs e)
    {
        var d = new Microsoft.Win32.OpenFileDialog { Filter = "RadioForge backup|RadioForge-backup-*.zip|Zip files|*.zip", InitialDirectory = Directory.Exists(BackupDir) ? BackupDir : null }; if (d.ShowDialog() != true) return;
        if (MessageBox.Show("Restoring replaces the current profiles and settings with the backup and restarts the station. Continue?", "Restore backup", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            var id = Vm.Station.Profile.Id; if (Vm.Station.Streams.AnyActive) await Vm.Station.StopBroadcastAsync();
            Vm.Station.Dispose(); BackupService.Restore(d.FileName);
            var settings = ProfileStore.LoadSettings(); var prof = ProfileStore.List().FirstOrDefault(p => p.Id == id) ?? ProfileStore.List().FirstOrDefault() ?? ProfileStore.Create("My Radio");
            MessageBox.Show("Backup restored. RadioForge will now restart its station.");
            ((MainWindow)Application.Current.MainWindow).CloseForReload(); App.ReplaceSettings(settings); App.OpenStation(prof);
            var w = new MainWindow(); Application.Current.MainWindow = w; w.Show();
        }
        catch (Exception ex) { MessageBox.Show("Restore failed: " + ex.Message); }
    }

    // ---- advanced
    private void UpdateFfmpeg() { var f = Ffmpeg.Locate(App.Settings.FfmpegPath); FfmpegInfo.Text = f == null ? "FFmpeg not found — MP3 and WAV work without it; AAC, Ogg and FLAC need it." : "Using: " + f; }
    private void OnDetectFfmpeg(object s, RoutedEventArgs e) { App.Settings.FfmpegPath = ""; UpdateFfmpeg(); }
    private void OnFfmpeg(object s, RoutedEventArgs e) { var d = new Microsoft.Win32.OpenFileDialog { Filter = "ffmpeg.exe|ffmpeg.exe|Executables|*.exe" }; if (d.ShowDialog() == true) { App.Settings.FfmpegPath = d.FileName; UpdateFfmpeg(); } }
    private void OnDebug(object s, RoutedEventArgs e) { Log.DebugEnabled = App.Settings.DebugLogging; }
    private void OnDiag(object s, RoutedEventArgs e)
    {
        var sb = new StringBuilder(); var eng = Vm.Engine;
        sb.AppendLine($"RadioForge {typeof(App).Assembly.GetName().Version} on {Environment.OSVersion}, .NET {Environment.Version}, 64-bit={Environment.Is64BitProcess}");
        sb.AppendLine($"Engine: running={eng.IsRunning}, hardware={eng.UsingDevice}, output={eng.OutputDeviceName}, rate={eng.Rate}, mic running={eng.MicCapture.IsRunning} ({eng.MicCapture.DeviceName})");
        sb.AppendLine("Outputs: " + string.Join("; ", AudioDevices.Outputs().Select(d => d.Name))); sb.AppendLine("Inputs: " + string.Join("; ", AudioDevices.Inputs().Select(d => d.Name)));
        sb.AppendLine("FFmpeg: " + (Ffmpeg.Locate(App.Settings.FfmpegPath) ?? "not found"));
        foreach (var d in Vm.Station.Streams.Destinations) sb.AppendLine($"Server {d.Profile.Name}: {d.Profile.Describe()} state={d.State} reconnects={d.ReconnectCount} lastError={d.LastError}");
        sb.AppendLine("Recent log:"); foreach (var l in Log.Snapshot().TakeLast(40)) sb.AppendLine(l.ToString());
        Clipboard.SetText(Redactor.Redact(sb.ToString())); BackupMsg.Text = "Diagnostics copied (credentials are redacted).";
    }
}
