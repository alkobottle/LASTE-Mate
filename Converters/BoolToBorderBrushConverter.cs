using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace LASTE_Mate.Converters;

public class BoolToBorderBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
        {
            // Return red border when false (not matched), gray when true (matched)
            return boolValue ? new SolidColorBrush(Colors.Gray) : new SolidColorBrush(Colors.Red);
        }
        return new SolidColorBrush(Colors.Gray);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

