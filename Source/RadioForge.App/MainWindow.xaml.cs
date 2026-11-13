using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RadioForge.App.ViewModels;
using RadioForge.App.Views;
using RadioForge.Core;
using RadioForge.Core.Station;

namespace RadioForge.App;

public partial class MainWindow : Window
{
    private readonly ShellViewModel _vm;
    private readonly Dictionary<PageId, UserControl> _pages = new();
    private System.Windows.Forms.NotifyIcon? _tray;
    private System.Drawing.Icon? _iconOff, _iconOn;
    private bool _closingConfirmed;

    public ShellViewModel Shell => _vm;

    public MainWindow()
    {
        InitializeComponent();
        var station = App.Station!;
        _vm = new ShellViewModel(station); DataContext = _vm;
        FontSize = 13 * App.Settings.FontScale; Loaded += (_, _) => Dispatcher.BeginInvoke(() => AutomationHelper.Apply(this), System.Windows.Threading.DispatcherPriority.Loaded);
        _vm.NavigateRequested += Show; Show(PageId.Dashboard);
        BuildTray();
        SourceInitialized += (_, _) => { _hotkeys = new GlobalHotkeys(this, a => Dispatcher.BeginInvoke(() => RunAction(a))); _hotkeys.Refresh(); };
        _vm.Balloon += (t, m) => { try { _tray?.ShowBalloonTip(5000, t, m, System.Windows.Forms.ToolTipIcon.Info); } catch { } };
        PreviewKeyDown += OnKey; PreviewKeyUp += OnKeyUp;
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized && App.Settings.MinimizeToTray) Hide(); };
        Closing += OnClosing;
        System.Windows.Threading.DispatcherTimer t = new() { Interval = TimeSpan.FromSeconds(1) };
        t.Tick += (_, _) => UpdateTray(); t.Start();
        Loaded += (_, _) => { if (App.AutoStartBroadcast) { App.AutoStartBroadcast = false; _vm.StartBroadcastCommand.Execute(null); } };
    }

    public void Show(PageId id)
    {
        if (!_pages.TryGetValue(id, out var v))
        {
            var s = _vm.Station;
            v = id switch
            {
                PageId.Dashboard => new DashboardView(),
                PageId.OnAir => new OnAirView(),
                PageId.Studio => new StudioView(),
                PageId.Playlists => new PlaylistsView(),
                PageId.Soundboard => new SoundboardView(),
                PageId.Servers => new ServersView(),
                PageId.Scheduler => new SchedulerView(),
                PageId.Library => new LibraryView(),
                PageId.Recordings => new RecordingsView(),
                PageId.AudioSettings => new AudioSettingsView(),
                PageId.Statistics => new StatisticsView(),
                PageId.Logs => new LogsView(),
                _ => new SettingsView(),
            };
            v.DataContext = _vm; _pages[id] = v;
            v.Loaded += (_, _) => NameLater(v);
        }
        Host.Content = v; NameLater(v);
    }

    /// <summary>Accessible names are applied after load and again once lazily-created parts exist.</summary>
    private void NameLater(UserControl v)
    {
        Dispatcher.BeginInvoke(() => AutomationHelper.Apply(v), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) }; t.Tick += (_, _) => { t.Stop(); AutomationHelper.Apply(v); AutomationHelper.Apply(this); }; t.Start();
    }

    // ------------------------------------------------------------------ tray
    private void BuildTray()
    {
        using (var s = Application.GetResourceStream(new Uri("pack://application:,,,/RadioForge;component/Assets/radioforge.ico")).Stream) _iconOff = new System.Drawing.Icon(s);
        _iconOn = MakeLive(_iconOff);
        _tray = new System.Windows.Forms.NotifyIcon { Icon = _iconOff, Visible = true, Text = "RadioForge" };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open RadioForge", null, (_, _) => Dispatcher.Invoke(Restore));
        var toggle = menu.Items.Add("Start broadcast", null, (_, _) => Dispatcher.Invoke(() => _vm.ToggleBroadcastCommand.Execute(null)));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(() => ExitApp()));
        menu.Opening += (_, _) => toggle.Text = _vm.Station.Streams.AnyActive ? "Stop broadcast" : "Start broadcast";
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(Restore);
    }

    private static System.Drawing.Icon MakeLive(System.Drawing.Icon src)
    {
        using var bmp = src.ToBitmap(); using var g = System.Drawing.Graphics.FromImage(bmp);
        g.FillEllipse(System.Drawing.Brushes.Red, bmp.Width - 11, bmp.Height - 11, 10, 10);
        return System.Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    private void UpdateTray()
    {
        if (_tray == null) return;
        _tray.Icon = _vm.IsOnAir ? _iconOn : _iconOff;
        var t = $"RadioForge — {_vm.OnAirText}: {_vm.NowPlayingText}";
        _tray.Text = t.Length > 62 ? t[..62] : t;
    }

    /// <summary>Closes this window without prompting so a new station/window can take over (profile switch, restore).</summary>
    public void CloseForReload()
    {
        _closingConfirmed = true; _hotkeys?.Dispose();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
        Close();
    }

    public void Restore() { Show(); WindowState = WindowState.Normal; Activate(); }

    public async void ExitApp()
    {
        _closingConfirmed = true;
        try { await _vm.Station.StopBroadcastAsync(); } catch { }
        _hotkeys?.Dispose(); _tray!.Visible = false; _tray.Dispose();
        App.ExitRequested = true;
        Application.Current.Shutdown();
    }

    // ------------------------------------------------------------------ closing: never end a broadcast unexpectedly
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closingConfirmed || App.ExitRequested) return;
        var st = _vm.Station;
        bool active = st.Streams.AnyActive || st.Recorder.State != RecorderState.Idle;
        if (!active) { e.Cancel = true; ExitApp(); return; }
        string behavior = App.Settings.CloseBehavior;
        if (behavior == "Background") { e.Cancel = true; Hide(); return; }
        if (behavior == "Exit") { e.Cancel = true; ExitApp(); return; }
        var r = MessageBox.Show(this, "A broadcast or recording is running.\n\nYes — keep broadcasting in the background (system tray)\nNo — stop everything and exit\nCancel — return to RadioForge",
            "RadioForge is on air", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        e.Cancel = true;
        if (r == MessageBoxResult.Yes) { Hide(); _tray?.ShowBalloonTip(3000, "RadioForge", "Still broadcasting in the background.", System.Windows.Forms.ToolTipIcon.Info); }
        else if (r == MessageBoxResult.No) ExitApp();
    }

    // ------------------------------------------------------------------ shortcuts (in-app; avoids clashing with other applications)
    public static string Describe(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.None) return "";
        var parts = new List<string>();
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        var name = key switch { >= Key.D0 and <= Key.D9 => ((int)key - (int)Key.D0).ToString(), _ => key.ToString() };
        parts.Add(name); return string.Join("+", parts);
    }

    private void OnKey(object s, KeyEventArgs e)
    {
        if (CaptureNext != null) { var d = Describe(e); if (d != "") { CaptureNext(d); CaptureNext = null; e.Handled = true; } return; }
        var desc = Describe(e); if (desc == "") return;
        bool typing = Keyboard.FocusedElement is TextBox or ComboBox or PasswordBox or System.Windows.Controls.Primitives.TextBoxBase || (Keyboard.FocusedElement is Button && desc == "Space");
        if (typing && !desc.Contains('+') && !desc.StartsWith('F')) return;
        if (typing && desc.StartsWith("Ctrl+") && desc is not ("Ctrl+N" or "Ctrl+R") ) { }
        var st = _vm.Station; var map = App.Settings.Shortcuts;
        string? action = map.FirstOrDefault(kv => string.Equals(kv.Value, desc, StringComparison.OrdinalIgnoreCase)).Key;
        if (action != null) { RunAction(action); e.Handled = true; return; }
        var pad = st.Profile.Soundboard.Pads.FirstOrDefault(p => p.HotkeyMatches(desc));
        if (pad != null) { st.PlayPad(pad); e.Handled = true; }
    }

    /// <summary>Runs a named shortcut action (used by in-app and system-wide hotkeys).</summary>
    public void RunAction(string action)
    {
        var st = _vm.Station;
        switch (action)
        {
            case "PlayPause": st.PlayPause(); break;
            case "NextTrack": st.NextTrack(); break;
            case "ToggleRecording": _vm.ToggleRecordingCommand.Execute(null); break;
            case "ToggleMic": st.ToggleMic(); break;
            case "StartBroadcast": _vm.StartBroadcastCommand.Execute(null); break;
            case "StopBroadcast": _vm.StopBroadcastCommand.Execute(null); break;
            default:
                if (action.StartsWith("Pad") && int.TryParse(action[3..], out var n) && n >= 1 && n <= st.Profile.Soundboard.Pads.Count) st.PlayPad(st.Profile.Soundboard.Pads[n - 1]);
                break;
        }
    }

    private GlobalHotkeys? _hotkeys;
    public GlobalHotkeys Hotkeys => _hotkeys!;
    public static Action<string>? CaptureNext;
    private void OnKeyUp(object s, KeyEventArgs e) { }
}

public static class PadExtensions
{
    public static bool HotkeyMatches(this SoundPad p, string desc) => !string.IsNullOrEmpty(p.Hotkey) && string.Equals(p.Hotkey, desc, StringComparison.OrdinalIgnoreCase);
}
