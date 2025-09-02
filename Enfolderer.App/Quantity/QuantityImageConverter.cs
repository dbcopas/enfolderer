using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Enfolderer.App.Quantity
{
    // Converts (ImageSource, Quantity) -> ImageSource (grayscale when quantity == 0)
    public class QuantityImageConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length < 2) return Binding.DoNothing;
            if (values[0] is not ImageSource src) return Binding.DoNothing;
            try
            {
        int qty = -1;
        if (values[1] is int qi) qty = qi;
                // No longer applying grayscale; always return original source.
                return src;
            }
            catch { return values[0]; }
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => Array.Empty<object>(); // one-way only
    }
}
