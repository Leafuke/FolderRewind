using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using System;
using Windows.UI;

namespace FolderRewind.Converters
{
    public class BoolToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            // 这里简单返回颜色画笔
            // 选中(True): AccentColor, 未选中(False): Default Text Color
            if ((bool)value)
                return Application.Current.Resources["SystemControlForegroundAccentBrush"] as SolidColorBrush;
            else
                return Application.Current.Resources["TextFillColorSecondaryBrush"] as SolidColorBrush;
        }
        public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotImplementedException();
    }
}