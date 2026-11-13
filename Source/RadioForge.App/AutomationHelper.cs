using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace RadioForge.App;

/// <summary>
/// Accessibility: gives every interactive control an accessible name so screen readers announce something meaningful.
/// Uses the control's own name, its text, its tooltip, or the label sitting to its left / above it.
/// </summary>
public static class AutomationHelper
{
    public static bool NeedsName(DependencyObject d) => d is ButtonBase or Slider or ComboBox or TextBox or PasswordBox or DatePicker or ListBox or DataGrid or ProgressBar or TabControl && d is not ScrollBar && d is not RepeatButton && d is not Thumb && d is not GridViewColumnHeader && d is not DataGridColumnHeader
        && !(d is TextBox tb && tb.TemplatedParent is ComboBox) && !(d is ButtonBase bb && bb.TemplatedParent != null) && !(d is ListBox lb && lb.TemplatedParent != null);

    public static string? ExistingName(DependencyObject d)
    {
        var n = AutomationProperties.GetName(d); if (!string.IsNullOrWhiteSpace(n)) return n;
        if (AutomationProperties.GetLabeledBy(d) is TextBlock lt && !string.IsNullOrWhiteSpace(lt.Text)) return lt.Text;
        if (d is ContentControl cc)
        {
            if (cc.Content is string s && s.Any(char.IsLetterOrDigit)) return s;
            if (cc.Content is TextBlock t && t.Text.Any(char.IsLetterOrDigit)) return t.Text;
            if (cc.Content is DependencyObject inner && FindText(inner) is { } found) return found;
        }
        if (d is FrameworkElement fe && fe.ToolTip is string tip && !string.IsNullOrWhiteSpace(tip)) return tip;
        if (d is HeaderedContentControl hc && hc.Header is string h) return h;
        return null;
    }

    private static string? FindText(DependencyObject root)
    {
        if (root is TextBlock t && !string.IsNullOrWhiteSpace(t.Text)) return t.Text;
        if (root is System.Windows.Documents.Run r && !string.IsNullOrWhiteSpace(r.Text)) return r.Text;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var f = FindText(VisualTreeHelper.GetChild(root, i)); if (f != null) return f; }
        return null;
    }

    public static void Apply(DependencyObject root)
    {
        if (root is FrameworkElement fe0 && !fe0.IsLoaded && VisualTreeHelper.GetChildrenCount(root) == 0) return;
        Walk(root);
    }

    private static void Walk(DependencyObject d)
    {
        if (NeedsName(d) && ExistingName(d) == null)
        {
            var label = FindLabel(d);
            if (label != null) AutomationProperties.SetName(d, label);
        }
        int n = VisualTreeHelper.GetChildrenCount(d);
        for (int i = 0; i < n; i++) Walk(VisualTreeHelper.GetChild(d, i));
    }

    /// <summary>The text of the nearest TextBlock to the left in the same Grid row, or the preceding TextBlock sibling.</summary>
    private static string? FindLabel(DependencyObject d)
    {
        if (d is not FrameworkElement el || VisualTreeHelper.GetParent(d) is not Panel parent) return null;
        if (parent is Grid g)
        {
            int row = Grid.GetRow(el), col = Grid.GetColumn(el);
            var left = g.Children.OfType<TextBlock>().Where(t => Grid.GetRow(t) == row && Grid.GetColumn(t) < col && !string.IsNullOrWhiteSpace(t.Text)).OrderByDescending(t => Grid.GetColumn(t)).FirstOrDefault();
            if (left != null) return left.Text.Trim();
        }
        var idx = parent.Children.IndexOf(el);
        for (int i = idx - 1; i >= 0; i--) if (parent.Children[i] is TextBlock t && !string.IsNullOrWhiteSpace(t.Text)) return t.Text.Trim();
        // label inside the same stack as the control's wrapper (label above a combo in a StackPanel, DockPanel label, etc.)
        if (parent is DockPanel dp) { var lab = dp.Children.OfType<TextBlock>().FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.Text)); if (lab != null) return lab.Text.Trim(); }
        if (VisualTreeHelper.GetParent(parent) is FrameworkElement up && up is not Window && FindLabel(parent) is { } outer) return outer;
        return null;
    }

    /// <summary>All interactive controls under <paramref name="root"/> that still have no accessible name.</summary>
    public static List<string> Unnamed(DependencyObject root)
    {
        var res = new List<string>();
        void W(DependencyObject d)
        {
            if (d is UIElement { Visibility: Visibility.Collapsed }) return;
            if (NeedsName(d) && ExistingName(d) == null) res.Add($"{d.GetType().Name}{(d is FrameworkElement f && !string.IsNullOrEmpty(f.Name) ? " '" + f.Name + "'" : "")} in {VisualTreeHelper.GetParent(d)?.GetType().Name}{(d is FrameworkElement g ? $"@{Grid.GetRow(g)},{Grid.GetColumn(g)}" : "")}");
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) W(VisualTreeHelper.GetChild(d, i));
        }
        W(root); return res;
    }
}
