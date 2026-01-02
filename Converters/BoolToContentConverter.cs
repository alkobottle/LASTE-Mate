using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace LASTE_Mate.Converters;

public class BoolToContentConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool boolValue && parameter is string paramStr)
        {
            // Parameter format: "TrueValue|FalseValue"
            var parts = paramStr.Split('|');
            if (parts.Length == 2)
            {
                return boolValue ? parts[0] : parts[1];
            }
        }
        return parameter?.ToString() ?? value?.ToString();
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

