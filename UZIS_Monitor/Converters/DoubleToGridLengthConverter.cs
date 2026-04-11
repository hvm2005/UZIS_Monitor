using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace UZIS_Monitor.Converters
{
    public class DoubleToGridLengthConverter : MarkupExtension, IValueConverter
    {
        public override object ProvideValue(IServiceProvider serviceProvider) => this;

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            double val = 0;
            if (value is double d) val = d;
            else if (value != null) double.TryParse(value.ToString(), out val);

            // Если передан параметр (например, "100"), вычитаем из него значение
            if (parameter != null && double.TryParse(parameter.ToString(), out var max))
            {
                val = max - val;
            }

            return new GridLength(Math.Max(0, val), GridUnitType.Star);
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
