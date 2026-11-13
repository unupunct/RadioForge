using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using RadioForge.App.Controls;
using RadioForge.Core.Streaming;

namespace RadioForge.App.Converters;

public sealed class StateBrushConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is StreamState s ? UiBrushes.ForState(s) : Brushes.Gray;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class HexBrushConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => new SolidColorBrush(Palette.Parse(value as string ?? "#333", Colors.DimGray));
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is bool b ? !b : value;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => value is bool b ? !b : value;
}

public sealed class NullVisConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value == null || (value is string s && s.Length == 0) ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class BytesImageConverter : IValueConverter
{
    public object? Convert(object value, Type t, object p, CultureInfo c)
    {
        if (value is not byte[] b || b.Length == 0) return null;
        try { var bi = new System.Windows.Media.Imaging.BitmapImage(); using var ms = new System.IO.MemoryStream(b); bi.BeginInit(); bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; bi.DecodePixelWidth = 300; bi.StreamSource = ms; bi.EndInit(); bi.Freeze(); return bi; } catch { return null; }
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}
