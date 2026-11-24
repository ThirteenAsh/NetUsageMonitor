using System.Globalization;
using System.Windows;
using System.Windows.Data;
using NetUsageMonitor.Common;

namespace NetUsageMonitor.Ui;

/// <summary>Formats a byte count (long/double) as e.g. "1.50 MB". Sorting still uses the numeric source.</summary>
public sealed class BytesConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        => ByteFormatter.Bytes(System.Convert.ToInt64(value));

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Formats a bytes/second rate as e.g. "1.50 MB/s" (or "—" when idle).</summary>
public sealed class RateConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        => ByteFormatter.Rate(System.Convert.ToDouble(value));

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>True -> Visible, False -> Collapsed. Pass "invert" to reverse.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool b = value is bool v && v;
        if (string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase)) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Null/empty -> Collapsed, otherwise Visible. Pass "invert" to reverse (show when null).</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool hasValue = !(value is null || (value is string s && string.IsNullOrWhiteSpace(s)));
        if (string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase)) hasValue = !hasValue;
        return hasValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
