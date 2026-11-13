using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using RadioForge.App.ViewModels;
using RadioForge.Core.Station;

namespace RadioForge.App.Views;

public partial class OnAirView : UserControl
{
    private ShellViewModel Vm => (ShellViewModel)DataContext;
    public OnAirView() { InitializeComponent(); Loaded += (_, _) => BuildPads(); }

    private void BuildPads()
    {
        Pads.Children.Clear();
        foreach (var pad in Vm.Station.Profile.Soundboard.Pads.Take(4))
        {
            var p = pad;
            var b = new Button { Height = 70, Margin = new Thickness(0, 0, 8, 0), FontSize = 15, HorizontalAlignment = HorizontalAlignment.Stretch };
            b.Content = p.Icon + "  " + p.Name; AutomationProperties.SetName(b, "Play jingle " + p.Name);
            b.Background = (System.Windows.Media.Brush)new Converters.HexBrushConverter().Convert(p.Color, typeof(System.Windows.Media.Brush), null!, System.Globalization.CultureInfo.InvariantCulture);
            b.Foreground = System.Windows.Media.Brushes.White;
            b.Click += (_, _) => { if (!Vm.Station.PlayPad(p)) Vm.Navigate(PageId.Soundboard); };
            Pads.Children.Add(b);
        }
    }

    private void OnGoServers(object s, RoutedEventArgs e) => Vm.Navigate(PageId.Servers);
    private void OnSamples(object s, RoutedEventArgs e) { SampleContent.Create(Vm.Station); BuildPads(); }
}
