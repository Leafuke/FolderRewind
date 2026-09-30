using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace FolderRewind.Services
{
    /// <summary>
    /// 把窗口做成「内容延伸到标题栏、系统按钮透明悬浮在材质上」的共用写法。
    /// <para>
    /// 自建窗口（赞助者窗口、合并窗口）本来就是同一套，差别只在拿哪个元素当拖动区，
    /// 所以收在一处。另两个窗口未纳入：主窗口的拖动区在页面壳里、另有自己的尺寸逻辑；
    /// 迷你窗口要的是系统按钮<b>全透明</b>（连悬停底色都不给，见 <c>MiniWindow</c> 里那段），取值与本类不同。
    /// </para>
    /// <para>
    /// 两段都吞异常：这套 API 在不支持的宿主/系统版本上会直接抛，
    /// 而失败的表现只是退回传统标题栏 —— 窗口照样能用，不值得为它中断构造函数。
    /// </para>
    /// </summary>
    public static class WindowChromeHelper
    {
        /// <summary>应用透明标题栏。<paramref name="dragRegion"/> 是窗口内容里充当拖动区的元素。</summary>
        public static void ApplySystemTitleBar(Window window, UIElement dragRegion)
        {
            try
            {
                window.ExtendsContentIntoTitleBar = true;
                window.SetTitleBar(dragRegion);
            }
            catch
            {
            }

            try
            {
                if (window.AppWindow?.TitleBar is not { } titleBar)
                {
                    return;
                }

                titleBar.ExtendsContentIntoTitleBar = true;
                titleBar.PreferredHeightOption = TitleBarHeightOption.Standard;

                // 让系统关闭按钮悬在 Mica/Acrylic 上，窗口本身不绘制传统标题栏。
                titleBar.BackgroundColor = Colors.Transparent;
                titleBar.InactiveBackgroundColor = Colors.Transparent;
                titleBar.ButtonBackgroundColor = Colors.Transparent;
                titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
                titleBar.ButtonHoverBackgroundColor = Color.FromArgb(32, 128, 128, 128);
                titleBar.ButtonPressedBackgroundColor = Color.FromArgb(48, 128, 128, 128);
            }
            catch
            {
            }
        }
    }
}
