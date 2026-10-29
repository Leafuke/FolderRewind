using Microsoft.UI.Xaml.Data;
using System;
using System.Globalization;

namespace FolderRewind.Converters
{
    public sealed class CountToStringConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value == null) return "0";

            try
            {
                var count = System.Convert.ToInt32(value, CultureInfo.InvariantCulture);
                return count.ToString(CultureInfo.CurrentCulture);
            }
            catch
            {
                return value?.ToString() ?? "0";
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotSupportedException();
        }
    }
}
