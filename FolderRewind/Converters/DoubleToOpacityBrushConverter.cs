using Microsoft.UI;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using System;
using Windows.UI;

namespace FolderRewind.Converters
{
    /// <summary>
    /// 将 double 类型的不透明度值转换为带透明度的 SolidColorBrush
    /// 用于 overlay 等需要半透明效果的场景
    /// </summary>
    public class DoubleToOpacityBrushConverter : IValueConverter
    {
        /// <summary>
        /// 覆盖层的默认颜色（黑色）
        /// </summary>
        public Color BaseColor { get; set; } = Colors.Black;

        public object? Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is double opacity)
            {
                // 确保不透明度在有效范围内 (0.0 - 1.0)
                opacity = Math.Clamp(opacity, 0.0, 1.0);

                var color = BaseColor;
                // 将不透明度应用到颜色
                color.A = (byte)(opacity * 255);

                return new SolidColorBrush(color);
            }

            // 如果转换失败，返回完全透明的画刷
            return new SolidColorBrush(Colors.Transparent);
        }

        public object? ConvertBack(object value, Type targetType, object parameter, string language)
        {
            if (value is SolidColorBrush brush)
            {
                return brush.Color.A / 255.0;
            }

            return 0.0;
        }
    }
}
