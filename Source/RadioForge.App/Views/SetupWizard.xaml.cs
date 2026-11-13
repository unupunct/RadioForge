using System.Globalization;
using System.Windows;
using RadioForge.Core;
using RadioForge.Core.Audio;
using RadioForge.Core.Streaming;

namespace RadioForge.App.Views;

public partial class SetupWizard : Window
{
    private readonly StationProfile _p; private int _step;
    private ServerProfile? _server;
    public bool GoOnAir { get; private set; }

    public SetupWizard(StationProfile p)
    {
        _p = p; InitializeComponent();
        NameBox.Text = p.Name; Desc.Text = p.Description; Genre.Text = p.Genre; Web.Text = p.Website; Logo.Text = p.LogoPath;
        Country.ItemsSource = CultureInfo.GetCultures(CultureTypes.SpecificCultures).Select(c => { try { return new RegionInfo(c.Name).EnglishName; } catch { return null; } }).Where(x => x != null).Distinct().OrderBy(x => x).ToList();
        Country.Text = string.IsNullOrWhiteSpace(p.Country) ? System.Globalization.RegionInfo.CurrentRegion.EnglishName : p.Country; Tz.ItemsSource = TimeZoneInfo.GetSystemTimeZones(); Tz.SelectedValue = p.TimeZoneId;
        RbIce.Checked += (_, _) => UpdateServerHelp(); RbSc.Checked += (_, _) => UpdateServerHelp(); UpdateServerHelp();
        SkipServer.Checked += (_, _) => ServerGrid.IsEnabled = false; SkipServer.Unchecked += (_, _) => ServerGrid.IsEnabled = true;
        var ex = p.Servers.FirstOrDefault(); if (ex != null) { Host.Text = ex.Host; Port.Text = ex.Port.ToString(); Mount.Text = ex.Mount; Pw.Password = ex.Password; (ex.Type == ServerType.Icecast2 ? RbIce : RbSc).IsChecked = true; }
        Detect(); Show(0);
    }

    private void UpdateServerHelp()
    {
        bool ice = RbIce.IsChecked == true; Mount.IsEnabled = ice; MountLabel.Opacity = ice ? 1 : 0.4;
        Help.Text = ice
            ? "Where to find your Icecast details: log in to your hosting provider's control panel (or the welcome e-mail) and look for “Icecast”, “Server address / host”, “Port” and “Source password”. The mount point is usually /stream or /live. Use the SOURCE password — not the admin or relay password."
            : "Where to find your SHOUTcast details: open your hosting control panel (or the welcome e-mail) and look for “Server IP / host”, “Port” and the “Source / DJ / Broadcaster password”. Enter the listener port shown there — RadioForge connects to port + 1 for sources, as SHOUTcast requires. Not the administrator password.";
    }

    private void Detect()
    {
        var ins = AudioDevices.Inputs(); var outs = AudioDevices.Outputs();
        ins.Insert(0, new DeviceInfo("", "Windows default microphone", false)); outs.Insert(0, new DeviceInfo("", "Windows default speakers", false));
        MicBox.ItemsSource = ins; OutBox.ItemsSource = outs; MicBox.SelectedValue = _p.Audio.Mic.DeviceId; OutBox.SelectedValue = _p.Audio.OutputDeviceId;
        if (MicBox.SelectedItem == null) MicBox.SelectedIndex = 0; if (OutBox.SelectedItem == null) OutBox.SelectedIndex = 0;
        DevInfo.Text = $"Found {ins.Count - 1} input device(s) and {outs.Count - 1} output device(s). Microphone starts muted; you can change everything later under Audio Settings.";
    }
    private void OnDetect(object s, RoutedEventArgs e) => Detect();
    private void OnLogo(object s, RoutedEventArgs e) { var d = new Microsoft.Win32.OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp" }; if (d.ShowDialog() == true) Logo.Text = d.FileName; }

    private void Show(int step)
    {
        _step = step; var panels = new FrameworkElement[] { S1, S2, S3, S4, S5, S6 };
        for (int i = 0; i < panels.Length; i++) panels[i].Visibility = i == step ? Visibility.Visible : Visibility.Collapsed;
        StepText.Text = $"Step {step + 1} of 6"; Prog.Value = step + 1; BackBtn.IsEnabled = step > 0; NextBtn.Visibility = step == 5 ? Visibility.Collapsed : Visibility.Visible;
        if (step == 4) { TestSkip.Text = SkipServer.IsChecked == true ? "You skipped the server step, so there is nothing to test. You can add a server later under Streaming Servers." : ""; TestBtn.IsEnabled = SkipServer.IsChecked != true; }
        if (step == 5) BuildSummary();
    }

    private int Kbps => Q64.IsChecked == true ? 64 : Q192.IsChecked == true ? 192 : Q256.IsChecked == true ? 256 : QC.IsChecked == true && int.TryParse(QCv.Text, out var k) ? Math.Clamp(k, 16, 320) : 128;

    private ServerProfile? BuildServer(out string? error)
    {
        error = null; if (SkipServer.IsChecked == true) return null;
        bool ice = RbIce.IsChecked == true;
        var sp = new ServerProfile { Id = _p.Servers.FirstOrDefault()?.Id ?? Guid.NewGuid().ToString("N"), Name = ice ? "Icecast server" : "SHOUTcast server", Type = ice ? ServerType.Icecast2 : ServerType.ShoutcastV2, Host = Host.Text.Trim(), Mount = Mount.Text.Trim(), BitrateKbps = Kbps, StreamName = NameBox.Text, Genre = Genre.Text, Website = Web.Text, Description = Desc.Text };
        if (int.TryParse(Port.Text, out var port)) sp.Port = port; else { error = "Port must be a number."; return null; }
        sp.Password = Pw.Password; var errs = ConfigValidator.Validate(sp); if (errs.Count > 0) { error = errs[0]; return null; }
        return sp;
    }

    private void OnNext(object s, RoutedEventArgs e)
    {
        if (_step == 0 && NameBox.Text.Trim().Length == 0) { MessageBox.Show(this, "Please enter a station name."); return; }
        if (_step == 1 && SkipServer.IsChecked != true) { BuildServer(out var err); if (err != null) { MessageBox.Show(this, err, "Check the server details"); return; } }
        Show(_step + 1);
    }
    private void OnBack(object s, RoutedEventArgs e) => Show(_step - 1);

    private async void OnTest(object s, RoutedEventArgs e)
    {
        var sp = BuildServer(out var err); if (sp == null) { TestSummary.Text = err ?? ""; return; }
        TestBtn.IsEnabled = false; TestSummary.Text = "Testing…";
        var r = await ServerTester.TestAsync(sp, Ffmpeg.Locate(App.Settings.FfmpegPath)); TestSteps.ItemsSource = r.Steps; TestSummary.Text = r.Summary; TestBtn.IsEnabled = true;
    }

    private void Commit()
    {
        _p.Name = NameBox.Text.Trim(); _p.Description = Desc.Text; _p.Genre = Genre.Text; _p.Website = Web.Text; _p.LogoPath = Logo.Text; _p.Country = Country.Text; if (Tz.SelectedValue is string tz) _p.TimeZoneId = tz;
        _p.Audio.Mic.DeviceId = MicBox.SelectedValue as string ?? ""; _p.Audio.OutputDeviceId = OutBox.SelectedValue as string ?? "";
        var sp = BuildServer(out _);
        if (sp != null) { _p.Servers.RemoveAll(x => x.Id == sp.Id); _p.Servers.Insert(0, sp); }
        _p.SetupCompleted = true;
    }

    private void BuildSummary()
    {
        var sp = BuildServer(out _);
        Summary.Text = $"Station:  {NameBox.Text}\nGenre:  {(Genre.Text == "" ? "—" : Genre.Text)}\nServer:  {(sp == null ? "none yet (add one under Streaming Servers)" : sp.Describe())}\nMicrophone:  {(MicBox.SelectedItem as DeviceInfo)?.Name}\nSpeakers:  {(OutBox.SelectedItem as DeviceInfo)?.Name}\n\nYour music library is empty — add songs under Audio Library or Playlist Manager. Auto DJ plays them automatically once you are on air.";
    }

    private void OnFinish(object s, RoutedEventArgs e) { Commit(); App.AddSamplesOnStart = SampleBox.IsChecked == true; GoOnAir = _p.Servers.Count > 0; App.AutoStartBroadcast = GoOnAir; DialogResult = true; }
    private void OnSaveOnly(object s, RoutedEventArgs e) { Commit(); App.AddSamplesOnStart = SampleBox.IsChecked == true; DialogResult = true; }
    private void OnSkip(object s, RoutedEventArgs e) { _p.Name = NameBox.Text.Trim().Length > 0 ? NameBox.Text.Trim() : _p.Name; _p.SetupCompleted = true; DialogResult = true; }
}
