using System.Windows;
using System.Windows.Controls;

namespace RadioForge.App.Controls;

public partial class LabeledSlider : UserControl
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(LabeledSlider), new PropertyMetadata(""));
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(LabeledSlider), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((LabeledSlider)d).UpdateText()));
    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(LabeledSlider), new PropertyMetadata(0.0));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(LabeledSlider), new PropertyMetadata(1.0));
    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(nameof(Step), typeof(double), typeof(LabeledSlider), new PropertyMetadata(0.01));
    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(nameof(Unit), typeof(string), typeof(LabeledSlider), new PropertyMetadata("", (d, e) => ((LabeledSlider)d).UpdateText()));
    public static readonly DependencyProperty FormatProperty = DependencyProperty.Register(nameof(Format), typeof(string), typeof(LabeledSlider), new PropertyMetadata("0.##", (d, e) => ((LabeledSlider)d).UpdateText()));
    public static readonly DependencyProperty LabelWidthProperty = DependencyProperty.Register(nameof(LabelWidth), typeof(GridLength), typeof(LabeledSlider), new PropertyMetadata(new GridLength(120)));
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Step { get => (double)GetValue(StepProperty); set => SetValue(StepProperty, value); }
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public string Format { get => (string)GetValue(FormatProperty); set => SetValue(FormatProperty, value); }
    public GridLength LabelWidth { get => (GridLength)GetValue(LabelWidthProperty); set => SetValue(LabelWidthProperty, value); }
    public LabeledSlider() { InitializeComponent(); Loaded += (_, _) => UpdateText(); }
    private void UpdateText() { if (Txt != null) Txt.Text = Value.ToString(Format) + (Unit.Length > 0 ? " " + Unit : ""); }
}
