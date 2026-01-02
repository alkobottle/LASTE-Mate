using System;
using System.Globalization;
using Avalonia.Data.Converters;
using LASTE_Mate.ViewModels;

namespace LASTE_Mate.Converters;

public class EnumToBoolConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is ConnectionMode mode && parameter is string paramStr)
        {
            if (Enum.TryParse<ConnectionMode>(paramStr, out var paramMode))
            {
                return mode == paramMode;
            }
        }
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isChecked && isChecked && parameter is string paramStr)
        {
            if (Enum.TryParse<ConnectionMode>(paramStr, out var paramMode))
            {
                return paramMode;
            }
        }
        return ConnectionMode.FileBased;
    }
}

