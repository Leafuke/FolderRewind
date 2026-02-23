using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace FolderRewind.Services
{
    public static class AutoToolTipService
    {
        public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(AutoToolTipService),
            new PropertyMetadata(false, OnIsEnabledChanged));

        private static readonly DependencyProperty TextChangedTokenProperty = DependencyProperty.RegisterAttached(
            "TextChangedToken",
            typeof(long),
            typeof(AutoToolTipService),
            new PropertyMetadata(0L));

        public static bool GetIsEnabled(DependencyObject obj)
            => (bool)obj.GetValue(IsEnabledProperty);

        public static void SetIsEnabled(DependencyObject obj, bool value)
            => obj.SetValue(IsEnabledProperty, value);

        private static long GetTextChangedToken(DependencyObject obj)
            => (long)obj.GetValue(TextChangedTokenProperty);

        private static void SetTextChangedToken(DependencyObject obj, long value)
            => obj.SetValue(TextChangedTokenProperty, value);

        private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not TextBlock textBlock)
            {
                return;
            }

            if (e.NewValue is true)
            {
                textBlock.Loaded += OnTextBlockLoaded;
                textBlock.SizeChanged += OnTextBlockSizeChanged;

                var token = textBlock.RegisterPropertyChangedCallback(TextBlock.TextProperty, OnTextBlockTextChanged);
                SetTextChangedToken(textBlock, token);

                UpdateToolTip(textBlock);
            }
            else
            {
                textBlock.Loaded -= OnTextBlockLoaded;
                textBlock.SizeChanged -= OnTextBlockSizeChanged;

                var token = GetTextChangedToken(textBlock);
                if (token != 0)
                {
                    textBlock.UnregisterPropertyChangedCallback(TextBlock.TextProperty, token);
                    SetTextChangedToken(textBlock, 0);
                }

                ToolTipService.SetToolTip(textBlock, null);
            }
        }

        private static void OnTextBlockLoaded(object sender, RoutedEventArgs e)
            => UpdateToolTip((TextBlock)sender);

        private static void OnTextBlockSizeChanged(object sender, SizeChangedEventArgs e)
            => UpdateToolTip((TextBlock)sender);

        private static void OnTextBlockTextChanged(DependencyObject sender, DependencyProperty dp)
            => UpdateToolTip((TextBlock)sender);

        private static void UpdateToolTip(TextBlock textBlock)
        {
            if (!GetIsEnabled(textBlock))
            {
                return;
            }

            var text = textBlock.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                ToolTipService.SetToolTip(textBlock, null);
                return;
            }

            ToolTipService.SetToolTip(textBlock, IsTextTrimmed(textBlock) ? text : null);
        }

        private static bool IsTextTrimmed(TextBlock textBlock)
        {
            if (textBlock.TextTrimming == TextTrimming.None)
            {
                return false;
            }

            if (textBlock.ActualWidth <= 0 || textBlock.ActualHeight <= 0)
            {
                return false;
            }

            var probe = new TextBlock
            {
                Text = textBlock.Text,
                FontFamily = textBlock.FontFamily,
                FontSize = textBlock.FontSize,
                FontStyle = textBlock.FontStyle,
                FontWeight = textBlock.FontWeight,
                FontStretch = textBlock.FontStretch,
                CharacterSpacing = textBlock.CharacterSpacing,
                TextWrapping = textBlock.TextWrapping,
                TextTrimming = TextTrimming.None,
                MaxLines = textBlock.MaxLines
            };

            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var desired = probe.DesiredSize;

            if (textBlock.TextWrapping == TextWrapping.Wrap || textBlock.TextWrapping == TextWrapping.WrapWholeWords)
            {
                return desired.Height - textBlock.ActualHeight > 1;
            }

            return desired.Width - textBlock.ActualWidth > 1;
        }
    }
}
