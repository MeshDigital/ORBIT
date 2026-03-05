using Avalonia.Data.Converters;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SLSKDONET.Converters
{
    /// <summary>
    /// Phase 7: Surgical UI Helper.
    /// Calculates pixel size/offset based on [CurrentValue, TotalValue, AvailablePixels].
    /// </summary>
    public class ProportionalValueConverter : IMultiValueConverter
    {
        public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        {
            if (values.Count >= 3 &&
                values[0] is float val &&
                values[1] is double total &&
                values[2] is double maxWidth)
            {
                if (total <= 0) return 0.0;
                double ratio = (double)val / total;
                return ratio * maxWidth;
            }
            
            // Fallback for double inputs
            if (values.Count >= 3 &&
                values[0] is double valD &&
                values[1] is double totalD &&
                values[2] is double maxWidthD)
            {
                if (totalD <= 0) return 0.0;
                double ratio = valD / totalD;
                return ratio * maxWidthD;
            }

            return 0.0;
        }
    }
}
