using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace SLSKDONET.Views.Avalonia.Converters
{
    public class NavIndexToMarginConverter : IValueConverter
    {
        private readonly double[] _offsets = new double[] 
        { 
            20,  // 0: Dashboard
            72,  // 1: Search
            124, // 2: Library
            185, // 3: Studio
            237, // 4: Style Lab
            298, // 5: Projects
            350, // 6: Import
            411, // 7: Discovery
            463, // 8: Upgrade
            524  // 9: Engine
        };

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is int index && index >= 0 && index < _offsets.Length)
            {
                return new Thickness(0, _offsets[index], 0, 0);
            }
            return new Thickness(0, 20, 0, 0);
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
