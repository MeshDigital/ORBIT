using System;
using System.Globalization;
using System.Linq;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace SLSKDONET.Views.Avalonia.Converters;

public class EnumToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null || parameter == null) return false;
        return value.ToString() == parameter.ToString();
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class EnumToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null || parameter == null) return Brushes.Transparent;
        
        // Parameter format: "EnumValue;ColorIfTrue;ColorIfFalse"
        var parts = parameter.ToString()?.Split(';');
        if (parts == null || parts.Length < 2) return Brushes.Transparent;

        bool isMatch = value.ToString() == parts[0];
        string colorStr = isMatch ? parts[1] : (parts.Length > 2 ? parts[2] : "Transparent");

        if (Color.TryParse(colorStr, out var color))
        {
            return new SolidColorBrush(color);
        }

        return Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class PassThroughConverter : IMultiValueConverter
{
    public object Convert(System.Collections.Generic.IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        return values.ToArray(); // Return as array for command parameter
    }
}

public class ForensicLevelToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string level = value?.ToString() ?? "Info";
        return level switch
        {
            "Error" => new SolidColorBrush(Color.Parse("#FF5252")),  // Fire Red
            "Warning" => new SolidColorBrush(Color.Parse("#FFD700")), // Amber
            "Debug" => Brushes.DimGray,
            _ => new SolidColorBrush(Color.Parse("#4EC9B0"))         // Mint Green
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
