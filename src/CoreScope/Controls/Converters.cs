using System;
using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace CoreScope.Controls;

/// <summary>
/// Truthiness → Visibility. true / non-empty string / non-zero number / non-empty collection are visible.
/// Pass ConverterParameter="invert" to flip.
/// </summary>
public sealed class VisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool visible = value switch
        {
            null => false,
            bool b => b,
            string s => !string.IsNullOrWhiteSpace(s),
            int i => i != 0,
            double d => d != 0 && !double.IsNaN(d),
            ICollection c => c.Count > 0,
            _ => true,
        };
        if (parameter is string p && p.Equals("invert", StringComparison.OrdinalIgnoreCase)) visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Sidebar group headers: names starting with "_" are internal and show no header.</summary>
public sealed class GroupHeaderVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s && s.Length > 0 && !s.StartsWith('_') ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
