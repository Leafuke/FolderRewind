using FolderRewind.Models;
using FolderRewind.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FolderRewind.Controls;

public sealed partial class LogLevelPresenter : UserControl
{
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(LogLevel), typeof(LogLevelPresenter),
        new PropertyMetadata(LogLevel.Info, OnLevelChanged));

    public LogLevelPresenter()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdatePresentation();
    }

    public LogLevel Level
    {
        get => (LogLevel)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    private static void OnLevelChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        => ((LogLevelPresenter)sender).UpdatePresentation();

    private void UpdatePresentation()
    {
        if (LevelText is null) return;
        var level = System.Enum.IsDefined(Level) ? Level : LogLevel.Info;
        LevelText.Text = I18n.GetString($"LogLevel_{level}");
        VisualStateManager.GoToState(this, level.ToString(), useTransitions: false);
    }
}
