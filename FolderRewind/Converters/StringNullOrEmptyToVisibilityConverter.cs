using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;

namespace FolderRewind.Converters
{
    /// <summary>
    /// 字符串判空到 Visibility：
    /// - null/空/全空白 => Collapsed
    /// - 否则 => Visible
    /// </summary>
    public sealed class StringNullOrEmptyToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var s = value as string;
            return string.IsNullOrWhiteSpace(s) ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotSupportedException();
        }
    }
}
