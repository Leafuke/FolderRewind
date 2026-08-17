using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using System;

namespace FolderRewind.Converters
{
    public class IntToStretchConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is int index)
            {
                return index switch
                {
                    1 => Stretch.Uniform,
                    2 => Stretch.Fill,
                    _ => Stretch.UniformToFill
                };
            }
            return Stretch.UniformToFill;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            if (value is Stretch stretch)
            {
                return stretch switch
                {
                    Stretch.Uniform => 1,
                    Stretch.Fill => 2,
                    _ => 0
                };
            }
            return 0;
        }
    }
}
