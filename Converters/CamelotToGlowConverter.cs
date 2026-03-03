using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using SLSKDONET.Services.Export;

namespace SLSKDONET.Converters;

public class CamelotToGlowConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string key)
        {
            var hex = CamelotColorMapper.GetHexColor(key);
            if (Color.TryParse(hex, out var color))
            {
                // Return a BoxShadows collection with one glow
                return new BoxShadows(new BoxShadow
                {
                    OffsetX = 0,
                    OffsetY = 0,
                    Blur = 25,
                    Spread = 2,
                    Color = new Color(120, color.R, color.G, color.B)
                });
            }
        }
        return new BoxShadows();
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
