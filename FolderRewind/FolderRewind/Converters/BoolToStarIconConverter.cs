using Microsoft.UI.Xaml.Data;
using System;

namespace FolderRewind.Converters
{
    public class BoolToStarIconConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            // 实心星: \uE735, 空心星: \uE734
            return (bool)value ? "\uE735" : "\uE734";
        }
        public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotImplementedException();
    }
}