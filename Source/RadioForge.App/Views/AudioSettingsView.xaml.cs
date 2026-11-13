using System.Windows;
using System.Windows.Controls;
using RadioForge.App.ViewModels;
using RadioForge.Core.Audio;

namespace RadioForge.App.Views;

public partial class AudioSettingsView : UserControl
{
    private ShellViewModel Vm => (ShellViewModel)DataContext;
    public AudioSettingsView() { InitializeComponent(); RateBox.ItemsSource = new[] { 44100, 48000 }; Loaded += (_, _) => Fill(); }

    private void Fill()
    {
        var a = Vm.Station.Profile.Audio; var outs = AudioDevices.Outputs(); var ins = AudioDevices.Inputs();
        List<DeviceInfo> WithDefault(List<DeviceInfo> l, string label) { var r = new List<DeviceInfo> { new("", label, false) }; r.AddRange(l); return r; }
        var o = a.OutputDeviceId; var m = a.Mic.DeviceId; var x = a.AuxDeviceId; var s = a.SystemAudioDeviceId;
        OutBox.ItemsSource = WithDefault(outs, "Windows default output"); MicBox.ItemsSource = WithDefault(ins, "Windows default input");
        AuxBox.ItemsSource = WithDefault(ins, "Windows default input"); SysBox.ItemsSource = WithDefault(outs, "Windows default output");
        OutBox.SelectedValue = o; MicBox.SelectedValue = m; AuxBox.SelectedValue = x; SysBox.SelectedValue = s;
        Info.Text = $"Detected {outs.Count} output and {ins.Count} input device(s). Engine: {Vm.Engine.OutputDeviceName} @ {Vm.Engine.Rate} Hz ({(Vm.Engine.UsingDevice ? "hardware clock" : "software clock")}).";
    }
    private void OnRefresh(object s, RoutedEventArgs e) => Fill();
    private void OnApply(object s, RoutedEventArgs e)
    {
        var a = Vm.Station.Profile.Audio;
        Vm.Engine.MicCapture.DeviceId = a.Mic.DeviceId; Vm.Engine.AuxCapture.DeviceId = a.AuxDeviceId; Vm.Engine.SystemCapture.DeviceId = a.SystemAudioDeviceId;
        Vm.Engine.RestartOutput(); Vm.Engine.RestartCaptures(); Vm.Station.SaveProfile(); Fill();
        Info.Text += "  Applied.";
    }
    private async void OnRestartEngine(object s, RoutedEventArgs e)
    {
        if (Vm.Station.Streams.AnyActive && MessageBox.Show("Changing the sample rate restarts the engine and stops the current broadcast. Continue?", "Restart engine", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        Vm.Station.SaveProfile(); await ProfileSwitcher.ReloadAsync(Vm.Station.Profile);
    }
}
