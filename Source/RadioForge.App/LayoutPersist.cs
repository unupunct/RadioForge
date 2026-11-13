using System.Windows.Controls;
using RadioForge.Core.Station;

namespace RadioForge.App;

/// <summary>Remembers the widths of splitter-resizable panels between runs (stored in settings.json → Layout).</summary>
public static class LayoutPersist
{
    public static void Attach(Grid grid, string key)
    {
        var cols = grid.ColumnDefinitions; var layout = App.Settings.Layout;
        for (int i = 0; i < cols.Count; i++)
            if (cols[i].Width.IsAbsolute && cols[i].Width.Value > 12 && layout.TryGetValue($"{key}.{i}", out var w))
                cols[i].Width = new System.Windows.GridLength(Math.Max(cols[i].MinWidth, Math.Min(w, 1400)));
        foreach (var sp in grid.Children.OfType<GridSplitter>())
            sp.DragCompleted += (_, _) =>
            {
                for (int i = 0; i < cols.Count; i++) if (cols[i].Width.IsAbsolute && cols[i].Width.Value > 12) layout[$"{key}.{i}"] = Math.Round(cols[i].ActualWidth);
                ProfileStore.SaveSettings(App.Settings);
            };
    }

    public static void Reset() { App.Settings.Layout.Clear(); ProfileStore.SaveSettings(App.Settings); }
}
