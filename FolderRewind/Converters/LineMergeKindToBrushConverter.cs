using FolderRewind.History.Merge;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using System;
using Windows.UI;

namespace FolderRewind.Converters
{
    /// <summary>
    /// 合并区段的类型 → 底色：冲突红、本地绿、对方蓝、未变化不着色。
    /// <para>
    /// 取系统填充色而不是写死颜色：这些资源在两套主题里各有一份，深色下不会出现「浅绿底配白字」那种读不清的组合。
    /// 取不到时退回半透明色 —— 它是叠加在卡片底上的，两种主题下都还能看清文字，比不着色更容易发现问题。
    /// </para>
    /// </summary>
    public class LineMergeKindToBrushConverter : IValueConverter
    {
        public object? Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is not LineMergeKind kind) return null;
            var (key, fallback) = kind switch
            {
                LineMergeKind.Conflict => ("SystemFillColorCriticalBackgroundBrush", Color.FromArgb(48, 196, 43, 28)),
                LineMergeKind.OursOnly => ("SystemFillColorSuccessBackgroundBrush", Color.FromArgb(40, 16, 124, 16)),
                LineMergeKind.BothSame => ("SystemFillColorSuccessBackgroundBrush", Color.FromArgb(40, 16, 124, 16)),
                LineMergeKind.TheirsOnly => ("SystemFillColorAttentionBackgroundBrush", Color.FromArgb(40, 0, 95, 184)),
                // 未变化的区段是多数，底色会变成噪声；它的信息量在「它不是冲突」这一件事上，已经由别的对比给足了。
                _ => (string.Empty, Colors.Transparent)
            };

            return string.IsNullOrEmpty(key) ? null : TryGetThemeBrush(key, fallback);
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotImplementedException();

        private static Brush TryGetThemeBrush(string key, Color fallback)
        {
            try
            {
                if (Application.Current?.Resources != null
                    && Application.Current.Resources.TryGetValue(key, out var value)
                    && value is Brush brush)
                {
                    return brush;
                }
            }
            catch
            {
            }

            return new SolidColorBrush(fallback);
        }
    }
}
