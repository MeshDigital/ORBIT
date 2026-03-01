using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using SLSKDONET.Services.Export;

namespace SLSKDONET.Converters;

public class CamelotToColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string key)
        {
            var hex = CamelotColorMapper.GetHexColor(key);
            if (Color.TryParse(hex, out var color))
            {
                return new SolidColorBrush(color);
            }
        }
        return Brushes.Gray;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
