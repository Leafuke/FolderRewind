using FolderRewind.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;

namespace FolderRewind.Controls;

public sealed partial class BackupScenarioPicker : UserControl
{
    public event EventHandler<BackupSetupScenario>? ScenarioChosen;
    public BackupScenarioPicker() => InitializeComponent();
    private void OnScenarioClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse<BackupSetupScenario>(tag, out var scenario))
            ScenarioChosen?.Invoke(this, scenario);
    }
    private void OnCardsSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < 600;
        Grid.SetColumn(MinecraftCard, narrow ? 0 : 1);
        Grid.SetColumn(GamesCard, narrow ? 0 : 2);
        Grid.SetRow(MinecraftCard, narrow ? 1 : 0);
        Grid.SetRow(GamesCard, narrow ? 2 : 0);
        Cards.ColumnDefinitions[1].Width = new GridLength(narrow ? 0 : 1, GridUnitType.Star);
        Cards.ColumnDefinitions[2].Width = new GridLength(narrow ? 0 : 1, GridUnitType.Star);
    }
}
