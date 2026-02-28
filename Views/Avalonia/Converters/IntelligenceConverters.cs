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
        if (value is Models.ForensicLevel level)
        {
            return level switch
            {
                Models.ForensicLevel.Error => new SolidColorBrush(Color.Parse("#FF5252")),   // Fire Red
                Models.ForensicLevel.Warning => new SolidColorBrush(Color.Parse("#FFD700")), // Amber
                Models.ForensicLevel.Debug => Brushes.DimGray,
                Models.ForensicLevel.Success => Brushes.LimeGreen,
                _ => new SolidColorBrush(Color.Parse("#4EC9B0"))          // Mint Green
            };
        }
        return new SolidColorBrush(Color.Parse("#4EC9B0"));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class SyncColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool isSyncing = value is bool b && b;
        return isSyncing ? new SolidColorBrush(Color.Parse("#F59E0B")) : new SolidColorBrush(Color.Parse("#4EC9B0"));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class EnumToOpacityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null || parameter == null) return 0.3;
        return value.ToString() == parameter.ToString() ? 1.0 : 0.3;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class HealthToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Models.SystemHealth health)
        {
            return health switch
            {
                Models.SystemHealth.Excellent => new SolidColorBrush(Color.Parse("#4EC9B0")),
                Models.SystemHealth.Good => new SolidColorBrush(Color.Parse("#1DB954")),
                Models.SystemHealth.Warning => new SolidColorBrush(Color.Parse("#FFD700")),
                Models.SystemHealth.Critical => new SolidColorBrush(Color.Parse("#FF5252")),
                _ => Brushes.Gray
            };
        }
        return Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class HealthToIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Models.SystemHealth health)
        {
            return health switch
            {
                Models.SystemHealth.Excellent => "🛡️",
                Models.SystemHealth.Good => "✅",
                Models.SystemHealth.Warning => "⚠️",
                Models.SystemHealth.Critical => "🚨",
                _ => "⚪"
            };
        }
        return "⚪";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class MoreThanZeroConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int count) return count > 0;
        if (value is long lCount) return lCount > 0;
        if (value is double dCount) return dCount > 0;
        return false;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
