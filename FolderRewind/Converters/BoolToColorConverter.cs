using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using System;

namespace FolderRewind.Converters
{
    public class BoolToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is bool isFav && isFav)
            {
                // 金色/黄色
                return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 215, 0));
            }
            // 默认灰色
            return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 128, 128, 128));
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}