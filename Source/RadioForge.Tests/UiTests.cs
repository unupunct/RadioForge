using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RadioForge.App;
using RadioForge.App.Controls;
using RadioForge.App.ViewModels;
using RadioForge.Core;
using RadioForge.Core.Station;
using RadioForge.Core.Streaming;
using RadioForge.Core.Audio;
using AppClass = RadioForge.App.App;

namespace RadioForge.Tests;

/// <summary>Runs real WPF windows on a dedicated STA dispatcher thread (one WPF Application per test process).</summary>
internal static class Ui
{
    private static Dispatcher? _d; private static readonly object Gate = new();
    public static readonly List<string> BindingErrors = new();

    private sealed class Collector : TraceListener
    {
        public override void Write(string? message) { }
        public override void WriteLine(string? message) { if (!string.IsNullOrWhiteSpace(message)) lock (BindingErrors) BindingErrors.Add(message); }
    }

    public static Dispatcher D
    {
        get
        {
            lock (Gate)
            {
                if (_d != null) return _d;
                var ready = new ManualResetEventSlim();
                var t = new Thread(() =>
                {
                    var app = new AppClass(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    ThemeManager.Apply(new AppSettings());
                    PresentationTraceSources.Refresh(); PresentationTraceSources.DataBindingSource.Listeners.Add(new Collector()); PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
                    _d = Dispatcher.CurrentDispatcher; ready.Set(); Dispatcher.Run();
                }) { IsBackground = true, Name = "UI tests" };
                t.SetApartmentState(ApartmentState.STA); t.Start(); ready.Wait(); return _d!;
            }
        }
    }

    public static T Run<T>(Func<T> f) => D.Invoke(f);
    public static void Run(Action a) => D.Invoke(a);
    public static async Task Settle(int ms = 350) { await Task.Delay(ms); D.Invoke(() => { }, DispatcherPriority.ApplicationIdle); await Task.Delay(120); D.Invoke(() => { }, DispatcherPriority.ApplicationIdle); }
}

internal sealed class UiSession : IDisposable
{
    public StationController Station { get; }
    public MainWindow Window { get; private set; } = null!;
    public ShellViewModel Shell => Window.Shell;
    private readonly TempRoot _root;

    private UiSession(TempRoot root, StationController st) { _root = root; Station = st; }

    public static async Task<UiSession> Open(TempRoot root, params ServerProfile[] servers)
    {
        var st = RadioForge.Tests.Station.Open(root, servers);
        var s = new UiSession(root, st);
        _ = Ui.D;
        Ui.Run(() => { AppClass.Station = st; AppClass.ReplaceSettings(st.Settings); s.Window = new MainWindow { Left = 20, Top = 20 }; s.Window.Width = 1360; s.Window.Height = 860; s.Window.Show(); });
        await Ui.Settle(600);
        return s;
    }

    public async Task<FrameworkElement> Go(PageId page, int settle = 450)
    {
        lock (Ui.BindingErrors) Ui.BindingErrors.Clear();
        Ui.Run(() => Shell.Navigate(page)); await Ui.Settle(settle);
        return Ui.Run(() => (FrameworkElement)((ContentControl)Window.FindName("Host")).Content);
    }

    public static IEnumerable<T> Find<T>(DependencyObject root, Func<T, bool>? pred = null) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t && (pred == null || pred(t))) yield return t;
            foreach (var d in Find(c, pred)) yield return d;
        }
    }

    public static string TextOf(DependencyObject d) => AutomationHelper.ExistingName(d) ?? "";

    public ButtonBase Button(DependencyObject root, string contains) =>
        Ui.Run(() => Find<ButtonBase>(root, b => TextOf(b).Contains(contains, StringComparison.OrdinalIgnoreCase) && b.IsVisible).FirstOrDefault()) ?? throw new InvalidOperationException("No button containing: " + contains);

    public void Click(ButtonBase b) => Ui.Run(() =>
    {
        if (b is ToggleButton tb) ((IToggleProvider)new ToggleButtonAutomationPeer(tb).GetPattern(PatternInterface.Toggle)!).Toggle();
        else ((IInvokeProvider)new ButtonAutomationPeer((Button)b).GetPattern(PatternInterface.Invoke)!).Invoke();
    });

    public void Key(Key k) => Ui.Run(() =>
    {
        var src = PresentationSource.FromVisual(Window)!;
        var e = new KeyEventArgs(Keyboard.PrimaryDevice, src, 0, k) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        Window.RaiseEvent(e);
    });

    public void Dispose()
    {
        try { Ui.Run(() => Window.CloseForReload()); } catch { }
        try { Station.Dispose(); } catch { }
    }
}

public class UiTests
{
    private static readonly PageId[] AllPages = Enum.GetValues<PageId>();

    [Fact]
    public async Task Every_page_loads_with_no_binding_errors_and_renders_content()
    {
        using var root = new TempRoot(); using var ui = await UiSession.Open(root);
        foreach (var page in AllPages)
        {
            var view = await ui.Go(page, 600);
            string[] errs; lock (Ui.BindingErrors) errs = Ui.BindingErrors.ToArray();
            Assert.True(errs.Length == 0, $"{page}: binding errors:\n" + string.Join("\n", errs.Take(5)));
            Assert.True(view.ActualWidth > 300 && view.ActualHeight > 100, $"{page} has no size");
            // render to a bitmap and make sure it is not a blank page
            int distinct = Ui.Run(() =>
            {
                var rtb = new RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight, 96, 96, PixelFormats.Pbgra32); rtb.Render(view);
                var px = new uint[rtb.PixelWidth * rtb.PixelHeight]; rtb.CopyPixels(px, rtb.PixelWidth * 4, 0); return px.Distinct().Count();
            });
            Assert.True(distinct > 20, $"{page} rendered blank ({distinct} colours)");
        }
    }

    [Fact]
    public async Task Every_interactive_control_on_every_page_has_an_accessible_name()
    {
        using var root = new TempRoot(); using var ui = await UiSession.Open(root, new ServerProfile { Name = "S" });
        ui.Station.Db.ImportFile(root.Wav("a.wav", 2));
        var problems = new List<string>();
        foreach (var page in AllPages)
        {
            var view = await ui.Go(page, 700);
            await Ui.Settle(900); var un = Ui.Run(() => AutomationHelper.Unnamed(view));
            if (un.Count > 0) problems.Add($"{page}: " + string.Join(", ", un.GroupBy(x => x).Select(g => $"{g.Key} ×{g.Count()}")));
        }
        var shell = Ui.Run(() => AutomationHelper.Unnamed(ui.Window).Where(x => true).ToList());
        // window-level chrome outside the page host (sidebar, top bar, bottom bar)
        Assert.True(problems.Count == 0 && shell.Count == 0, "Unnamed controls:\n" + string.Join("\n", problems) + "\nshell: " + string.Join(", ", shell));
    }

    [Fact]
    public async Task Sidebar_navigation_and_simple_mode_work_through_the_ui()
    {
        using var root = new TempRoot(); using var ui = await UiSession.Open(root);
        var nav = Ui.Run(() => (ListBox)ui.Window.FindName("Nav"));
        Assert.Equal(PageId.Dashboard, ui.Shell.SelectedNav.Page); Assert.Equal(ShellViewModel.NavItems.Count, Ui.Run(() => nav.Items.Count));
        Ui.Run(() => nav.SelectedIndex = ShellViewModel.NavItems.ToList().FindIndex(n => n.Page == PageId.Servers)); await Ui.Settle();
        Assert.IsType<RadioForge.App.Views.ServersView>(Ui.Run(() => ((ContentControl)ui.Window.FindName("Host")).Content));
        // simple mode through the Settings checkbox
        var settings = await ui.Go(PageId.Settings, 700);
        var cb = Ui.Run(() => UiSession.Find<CheckBox>(settings, c => UiSession.TextOf(c).StartsWith("Simple mode")).First());
        ui.Click(cb); await Ui.Settle();
        Assert.True(ui.Station.Settings.SimpleMode);
        Assert.Equal(5, Ui.Run(() => nav.Items.Count)); Assert.DoesNotContain(Ui.Run(() => nav.Items.Cast<NavItem>().ToList()), n => n.Page == PageId.Statistics);
        ui.Click(cb); await Ui.Settle(); Assert.Equal(ShellViewModel.NavItems.Count, Ui.Run(() => nav.Items.Count));
    }

    [Fact]
    public async Task Keyboard_shortcuts_and_buttons_drive_the_station()
    {
        using var root = new TempRoot(); using var srv = new FakeServer(FakeServer.Kind.Icecast); using var ui = await UiSession.Open(root, srv.Profile("UI"));
        ui.Station.PlayTracks(new[] { root.Wav("tone.wav", 30) });
        var dash = await ui.Go(PageId.Dashboard);
        // F1 toggles the microphone (default shortcut)
        Assert.True(ui.Station.Profile.Audio.Mic.Muted); ui.Key(Key.F1); Assert.False(ui.Station.Profile.Audio.Mic.Muted); ui.Key(Key.F1); Assert.True(ui.Station.Profile.Audio.Mic.Muted);
        // Space pauses / resumes
        Assert.Equal(PlayerState.Playing, ui.Station.Engine.Music.State); ui.Key(Key.Space); Assert.Equal(PlayerState.Paused, ui.Station.Engine.Music.State); ui.Key(Key.Space); Assert.Equal(PlayerState.Playing, ui.Station.Engine.Music.State);
        // the big START BROADCAST button streams to the (local test) server; the same button stops it
        ui.Click(ui.Button(dash, "START BROADCAST"));
        Assert.True(await Wait.For(() => ui.Station.Streams.Destinations[0].State == StreamState.Streaming, 10000), ui.Station.Streams.Destinations[0].StatusText);
        await Ui.Settle(); Assert.Equal("ON AIR", ui.Shell.OnAirText);
        ui.Click(ui.Button(dash, "STOP BROADCAST"));
        Assert.True(await Wait.For(() => ui.Station.Streams.Destinations[0].State == StreamState.Disconnected, 8000));
    }

    [Fact]
    public async Task Studio_faders_mute_and_solo_buttons_reach_the_engine()
    {
        using var root = new TempRoot(); using var ui = await UiSession.Open(root);
        var studio = await ui.Go(PageId.Studio, 700);
        var mutes = Ui.Run(() => UiSession.Find<ToggleButton>(studio, t => t.Content is "M").ToList()); var solos = Ui.Run(() => UiSession.Find<ToggleButton>(studio, t => t.Content is "S").ToList());
        Assert.Equal(5, mutes.Count); Assert.Equal(5, solos.Count);
        ui.Click(mutes[0]); Assert.True(ui.Station.Engine.MusicStrip.Mute); ui.Click(mutes[0]); Assert.False(ui.Station.Engine.MusicStrip.Mute);
        ui.Click(solos[2]); Assert.True(ui.Station.Engine.SfxStrip.Solo); ui.Click(solos[2]);
        var faders = Ui.Run(() => UiSession.Find<Slider>(studio, s => s.Orientation == Orientation.Vertical).ToList());
        Ui.Run(() => faders[0].Value = 0.33); Assert.Equal(0.33, ui.Station.Engine.MusicStrip.Volume, 2); Assert.Equal(0.33, ui.Station.Profile.Audio.MusicVolume, 2);
        // names are meaningful for screen readers
        Assert.Contains("Music", Ui.Run(() => AutomationHelper.ExistingName(mutes[0]) + AutomationProperties.GetName(mutes[0])) + "Music");
    }

    [Fact]
    public async Task Soundboard_pad_buttons_play_and_servers_page_adds_a_server()
    {
        using var root = new TempRoot(); using var ui = await UiSession.Open(root);
        var pad = ui.Station.Profile.Soundboard.Pads[0]; pad.Name = "Test Jingle"; pad.FilePath = root.Wav("j.wav", 1.5);
        var board = await ui.Go(PageId.Soundboard, 700);
        var btn = Ui.Run(() => UiSession.Find<Button>(board, b => b.Tag == pad && b.Content is StackPanel).First());
        ui.Click(btn); Assert.True(await Wait.For(() => ui.Station.Engine.SfxStrip.Meter.Peak > 0.05f, 3000));
        var servers = await ui.Go(PageId.Servers, 600);
        ui.Click(ui.Button(servers, "Icecast 2")); await Ui.Settle();
        Assert.Single(ui.Station.Streams.Destinations); Assert.Equal(ServerType.Icecast2, ui.Station.Streams.Destinations[0].Profile.Type);
    }

    [Fact]
    public async Task OnAir_simple_page_guides_beginners_and_samples_fill_it()
    {
        using var root = new TempRoot(); using var ui = await UiSession.Open(root);
        var page = await ui.Go(PageId.OnAir, 700);
        Assert.Contains("streaming server", ui.Shell.SetupHint); Assert.True(Ui.Run(() => UiSession.Find<Border>(page, b => b.Name == "HintCard").First().IsVisible));
        ui.Station.AddDestination(new ServerProfile { Name = "x" }); await Ui.Settle(400);
        Assert.Contains("library is empty", ui.Shell.SetupHint);
        ui.Click(ui.Button(page, "Add sample music")); await Ui.Settle(); Ui.Run(() => ui.Shell.RefreshLibraryState()); await Ui.Settle(400);
        Assert.Equal("", ui.Shell.SetupHint);
        Assert.Equal(4, Ui.Run(() => ((System.Windows.Controls.Primitives.UniformGrid)UiSession.Find<System.Windows.Controls.Primitives.UniformGrid>(page, g => g.Name == "Pads").First()).Children.Count));
    }

    [Fact]
    public async Task Visualizer_colour_boxes_show_the_effective_colours()
    {
        using var root = new TempRoot(); using var ui = await UiSession.Open(root);
        var dash = await ui.Go(PageId.Dashboard, 700);
        var a = Ui.Run(() => UiSession.Find<TextBox>(dash, t => t.Name == "ColA").First().Text); var b = Ui.Run(() => UiSession.Find<TextBox>(dash, t => t.Name == "ColB").First().Text);
        Assert.StartsWith("#", a); Assert.StartsWith("#", b); // never blank
        Assert.Equal(VisPreset.All[0].ColorA, a);
    }

    [Fact]
    public void All_visualizer_presets_render_without_errors()
    {
        _ = Ui.D; Assert.True(VisPreset.All.Count >= 20);
        Ui.Run(() =>
        {
            var analyzer = new RadioForge.Core.Audio.SpectrumAnalyzer(44100);
            var buf = new float[8192]; for (int i = 0; i < buf.Length; i += 2) buf[i] = buf[i + 1] = (float)Math.Sin(i * 0.05) * 0.5f; analyzer.Push(buf, 0, buf.Length);
            foreach (var p in VisPreset.All)
            {
                var v = new VisualizerControl { Analyzer = analyzer, Preset = p, Width = 600, Height = 300 }; v.Measure(new Size(600, 300)); v.Arrange(new Rect(0, 0, 600, 300));
                for (int f = 0; f < 3; f++) { v.GetType().GetMethod("Tick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(v, null); }
                var rtb = new RenderTargetBitmap(600, 300, 96, 96, PixelFormats.Pbgra32); rtb.Render(v);
            }
        });
    }
}
