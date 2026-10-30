using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;

namespace FolderRewind.Converters
{
    public sealed class BoolToVisibilityInvertConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var flag = false;
            if (value is bool b) flag = b;
            return flag ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            if (value is Visibility v) return v != Visibility.Visible;
            return true;
        }
    }
}
