using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace BDIT.TenantToolkit.App.Infrastructure;

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is bool b ? !b : true;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is bool b ? !b : true;
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value is not null && !(value is string s && s.Length == 0);
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Maps status words to a background brush so pass/fail/pending states are visually distinct in grids.</summary>
public sealed class StatusToBrushConverter : IValueConverter
{
    private static readonly Brush Good = new SolidColorBrush(Color.FromRgb(0xE8, 0xF5, 0xED));
    private static readonly Brush Bad = new SolidColorBrush(Color.FromRgb(0xFD, 0xE7, 0xE5));
    private static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(0xFF, 0xF5, 0xDE));
    private static readonly Brush Info = new SolidColorBrush(Color.FromRgb(0xE7, 0xF4, 0xFB));
    private static readonly Brush Neutral = Brushes.Transparent;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = value?.ToString() ?? "";
        if (text.Contains("Compliant", StringComparison.OrdinalIgnoreCase) || text is "Pass" or "Completed" or "Collected" or "NoChange" or "Create" or "Update")
            return Good;
        if (text.Contains("Missing", StringComparison.OrdinalIgnoreCase) || text is "Error" or "Fail" or "Conflict" or "Drift" or "Review required" or "Interrupted" || text.Contains("Removed", StringComparison.OrdinalIgnoreCase))
            return Bad;
        if (text.Contains("Partial", StringComparison.OrdinalIgnoreCase) || text.Contains("Manual", StringComparison.OrdinalIgnoreCase) || text.Contains("Unable", StringComparison.OrdinalIgnoreCase)
            || text.Contains("NotEnforced", StringComparison.OrdinalIgnoreCase) || text is "Pending" or "Blocked" or "Unknown" or "Stopped" || text.Contains("Licence", StringComparison.OrdinalIgnoreCase))
            return Warn;
        if (text is "NotApplicable" or "Deviation" or "In progress" or "Running") return Info;
        return Neutral;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
