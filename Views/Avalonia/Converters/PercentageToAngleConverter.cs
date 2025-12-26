using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace SLSKDONET.Views.Avalonia.Converters
{
    public class PercentageToAngleConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is double percentage)
            {
                // Convert percentage (0-100 or 0.0-1.0) to angle (0-360)
                // Assuming input is 0-100 based on typical progress bars
                return percentage * 3.6;
            }
            return 0;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
