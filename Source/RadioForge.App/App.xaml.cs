using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using RadioForge.App.Controls;
using RadioForge.App.Views;
using RadioForge.Core;
using RadioForge.Core.Station;

namespace RadioForge.App;

public static class ThemeManager
{
    public static void Apply(AppSettings s)
    {
        var app = Application.Current; if (app == null) return;
        bool dark = !string.Equals(s.Theme, "Light", StringComparison.OrdinalIgnoreCase);
        app.ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light;
        void Set(string k, string hex) => app.Resources[k] = new SolidColorBrush(Palette.Parse(hex, Colors.Gray));
        if (dark) { Set("AppBgBrush", "#0E1013"); Set("PanelBrush", "#171A1F"); Set("Panel2Brush", "#1F2329"); Set("LineBrush", "#2A2F37"); Set("TextBrush", "#E8ECEF"); Set("MutedBrush", "#8A939E"); }
        else { Set("AppBgBrush", "#EEF0F3"); Set("PanelBrush", "#FFFFFF"); Set("Panel2Brush", "#E3E7EC"); Set("LineBrush", "#CBD1D9"); Set("TextBrush", "#15181C"); Set("MutedBrush", "#5B6572"); }
        var accent = Palette.Parse(s.AccentColor, Color.FromRgb(0, 229, 160));
        app.Resources["AccentBrush"] = new SolidColorBrush(dark ? accent : Palette.Lerp(accent, Colors.Black, 0.35));
        double lum = (0.299 * accent.R + 0.587 * accent.G + 0.114 * accent.B) / 255;
        app.Resources["AccentFgBrush"] = new SolidColorBrush(dark ? (lum > 0.5 ? Color.FromRgb(4, 20, 14) : Colors.White) : Colors.White);
        var ac = ((SolidColorBrush)app.Resources["AccentBrush"]).Color;
        void SetB(string k, Color c) => app.Resources[k] = new SolidColorBrush(c);
        SetB("AccentFillColorDefaultBrush", ac); SetB("AccentFillColorSecondaryBrush", Color.FromArgb(230, ac.R, ac.G, ac.B)); SetB("AccentFillColorTertiaryBrush", Color.FromArgb(180, ac.R, ac.G, ac.B));
        SetB("AccentTextFillColorPrimaryBrush", ac); SetB("AccentTextFillColorSecondaryBrush", ac); SetB("AccentTextFillColorTertiaryBrush", ac);
        var onAc = ((SolidColorBrush)app.Resources["AccentFgBrush"]).Color; SetB("TextOnAccentFillColorPrimaryBrush", onAc); SetB("TextOnAccentFillColorSecondaryBrush", onAc);
        foreach (var k in new[] { "SystemAccentColor", "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3", "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3", "SystemAccentColorSecondary" }) app.Resources[k] = ac;
        SetB("SystemAccentColorBrush", ac);
        foreach (Window w in app.Windows) { w.FontSize = 13 * s.FontScale; }
    }
}

public partial class App : Application
{
    public static StationController? Station { get; set; }
    public static AppSettings Settings { get; private set; } = new();
    public static string[] Args { get; private set; } = Array.Empty<string>();
    private static Mutex? _single;
    public static bool ExitRequested { get; set; }
    public static bool AutoStartBroadcast { get; set; }
    public static void ReplaceSettings(AppSettings s) => Settings = s;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Args = e.Args;
        bool shots = Args.Contains("--screenshots");
        if (Args.Contains("--data-dir")) AppPaths.Root = Args[Array.IndexOf(Args, "--data-dir") + 1];
        _single = new Mutex(true, "RadioForge.SingleInstance", out bool first);
        if (!first && !shots) { MessageBox.Show("RadioForge is already running. Look for its icon in the system tray.", "RadioForge", MessageBoxButton.OK, MessageBoxImage.Information); Shutdown(); return; }

        DispatcherUnhandledException += (_, ex) => { CrashReport("UI thread", ex.Exception); ex.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => CrashReport("Unhandled", ex.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ex) => { Log.Error("Crash", "Unobserved task exception", ex.Exception); ex.SetObserved(); };

        try
        {
            Settings = ProfileStore.LoadSettings();
            ThemeManager.Apply(Settings);
            Log.Info("App", $"RadioForge {typeof(App).Assembly.GetName().Version} starting on {Environment.OSVersion}");
            var profiles = ProfileStore.List();
            StationProfile? prof = profiles.FirstOrDefault(p => p.Id == Settings.ActiveProfileId) ?? profiles.FirstOrDefault();
            bool firstRun = prof == null || !prof.SetupCompleted;
            if (prof == null) { prof = ProfileStore.Create("My Radio"); }
            Settings.ActiveProfileId = prof.Id;
            if (string.IsNullOrEmpty(Settings.MusicFolder)) Settings.MusicFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            ProfileStore.SaveSettings(Settings);

            if (firstRun && !shots)
            {
                var wiz = new SetupWizard(prof);
                if (wiz.ShowDialog() != true) { Log.Info("App", "Setup cancelled — saving defaults"); prof.SetupCompleted = false; }
                ProfileStore.Save(prof);
            }
            OpenStation(prof, !shots);
            var main = new MainWindow();
            MainWindow = main; main.Show();
            if (shots) _ = Screenshots.RunAsync(main, Args);
        }
        catch (Exception ex) { CrashReport("Startup", ex); Shutdown(1); }
    }

    public static void OpenStation(StationProfile prof, bool useAudio = true)
    {
        Station?.Dispose();
        Station = new StationController(Settings, prof, useAudio);
        Settings.ActiveProfileId = prof.Id; ProfileStore.SaveSettings(Settings);
    }

    public static void CrashReport(string where, Exception? ex)
    {
        try
        {
            Log.Error("Crash", $"{where}: {ex}", null);
            var file = Path.Combine(AppPaths.Logs, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(file, Redactor.Redact($"RadioForge crash report\r\nTime: {DateTime.Now}\r\nWhere: {where}\r\nOS: {Environment.OSVersion}\r\n.NET: {Environment.Version}\r\n\r\n{ex}"));
        }
        catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { ProfileStore.SaveSettings(Settings); Station?.Dispose(); } catch { }
        Log.Shutdown();
        base.OnExit(e);
    }
}
