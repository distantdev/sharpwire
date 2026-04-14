using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Sharpwire.Converters;

public class StringEqualsConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null || parameter == null) return false;

        var valString = value.ToString();
        var paramString = parameter.ToString();

        // Handle ComboBoxItem content if value is a ComboBoxItem
        if (value is Avalonia.Controls.ComboBoxItem item)
        {
            valString = item.Content?.ToString();
        }

        return string.Equals(valString, paramString, StringComparison.OrdinalIgnoreCase);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
