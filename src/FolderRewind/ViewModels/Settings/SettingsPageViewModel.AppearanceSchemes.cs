using CommunityToolkit.Mvvm.Input;
using FolderRewind.Models;
using FolderRewind.Services;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace FolderRewind.ViewModels;

public sealed partial class SettingsPageViewModel
{
    private bool _isAppearanceOperationRunning;
    private bool _refreshingAppearanceSchemes;

    public ObservableCollection<string> BuiltinAppearanceThemeNames { get; } = new();
    public ObservableCollection<SponsorAppearanceScheme> AppearanceSchemes { get; } = new();

    public IAsyncRelayCommand SaveAppearanceSchemeCommand { get; private set; } = null!;
    public IAsyncRelayCommand UpdateAppearanceSchemeCommand { get; private set; } = null!;
    public IAsyncRelayCommand RenameAppearanceSchemeCommand { get; private set; } = null!;
    public IAsyncRelayCommand DeleteAppearanceSchemeCommand { get; private set; } = null!;

    public bool CanEditAppearanceSchemes => IsSponsorUnlocked && !_isAppearanceOperationRunning && !_refreshingAppearanceSchemes;
    public bool HasActivePersonalAppearanceScheme => ActivePersonalAppearanceScheme is not null;
    public bool CanManageAppearanceScheme => CanEditAppearanceSchemes && HasActivePersonalAppearanceScheme;
    private SponsorAppearanceScheme? ActivePersonalAppearanceScheme => Settings.SponsorAppearanceSchemes
        .FirstOrDefault(scheme => scheme.Id == Settings.SponsorActiveSchemeId);

    public int BuiltinAppearanceThemeIndex => SponsorAppearancePolicy.Themes.ToList()
        .FindIndex(theme => SponsorAppearancePolicy.ThemeSchemeId(theme.Id) == Settings.SponsorActiveSchemeId);
    public int PersonalAppearanceSchemeIndex => AppearanceSchemes.ToList().FindIndex(scheme => scheme.Id == Settings.SponsorActiveSchemeId);
    public string SaveAppearanceSchemeButtonText => I18n.GetString(HasActivePersonalAppearanceScheme ? "Appearance_SaveAs" : "Appearance_SaveCurrent");

    public string AppearanceSchemeStatusText
    {
        get
        {
            var state = SponsorAppearanceService.Capture(Settings);
            var name = ActivePersonalAppearanceScheme?.Name;
            if (name is null && BuiltinAppearanceThemeIndex is var index && index >= 0)
                name = I18n.GetString("Appearance_Theme_" + SponsorAppearancePolicy.Themes[index].Id);
            return name is null ? I18n.GetString("Appearance_CurrentCustom")
                : I18n.Format(SponsorAppearancePolicy.IsModified(state) ? "Appearance_CurrentModified" : "Appearance_CurrentScheme", name);
        }
    }

    private void InitializeAppearanceSchemeCommands()
    {
        SaveAppearanceSchemeCommand = new AsyncRelayCommand(() => RunAppearanceOperationAsync(async () =>
        {
            var name = await AppDialogService.Default.RequestTextAsync(I18n.GetString("Appearance_SaveAs"),
                I18n.GetString("Appearance_Name"), primaryButtonText: I18n.GetString("Common_Save"));
            if (name is not null) await SponsorAppearanceService.Controller.SaveAsAsync(name);
        }), () => CanEditAppearanceSchemes);

        UpdateAppearanceSchemeCommand = new AsyncRelayCommand(() => RunAppearanceOperationAsync(async () =>
        {
            if (ActivePersonalAppearanceScheme is { } scheme) await SponsorAppearanceService.Controller.UpdateAsync(scheme.Id);
        }), () => CanManageAppearanceScheme);

        RenameAppearanceSchemeCommand = new AsyncRelayCommand(() => RunAppearanceOperationAsync(async () =>
        {
            if (ActivePersonalAppearanceScheme is not { } scheme) return;
            var name = await AppDialogService.Default.RequestTextAsync(I18n.GetString("Appearance_Rename"),
                I18n.GetString("Appearance_Name"), scheme.Name);
            if (name is not null) await SponsorAppearanceService.Controller.RenameAsync(scheme.Id, name);
        }), () => CanManageAppearanceScheme);

        DeleteAppearanceSchemeCommand = new AsyncRelayCommand(() => RunAppearanceOperationAsync(async () =>
        {
            if (ActivePersonalAppearanceScheme is not { } scheme) return;
            if (await AppDialogService.Default.ConfirmAsync(I18n.GetString("Appearance_Delete"),
                I18n.Format("Appearance_DeleteConfirm", scheme.Name), I18n.GetString("Common_Delete"), isDestructive: true))
                await SponsorAppearanceService.Controller.DeleteAsync(scheme.Id);
        }), () => CanManageAppearanceScheme);
    }

    public Task SelectBuiltinAppearanceThemeAsync(int index)
    {
        if (!CanEditAppearanceSchemes || index < 0 || index >= SponsorAppearancePolicy.Themes.Count || index == BuiltinAppearanceThemeIndex)
            return Task.CompletedTask;
        return RunAppearanceOperationAsync(() => SponsorAppearanceService.Controller.ApplyThemeAsync(SponsorAppearancePolicy.Themes[index].Id));
    }

    public Task SelectPersonalAppearanceSchemeAsync(int index)
    {
        if (!CanEditAppearanceSchemes || index < 0 || index >= AppearanceSchemes.Count || index == PersonalAppearanceSchemeIndex)
            return Task.CompletedTask;
        var id = AppearanceSchemes[index].Id;
        return RunAppearanceOperationAsync(() => SponsorAppearanceService.Controller.ApplySchemeAsync(id));
    }

    private async Task RunAppearanceOperationAsync(Func<Task> operation)
    {
        if (!CanEditAppearanceSchemes) return;
        _isAppearanceOperationRunning = true;
        RefreshAppearanceSchemeState();
        try { await operation(); }
        catch (SponsorAppearanceException ex)
        {
            NotificationService.ShowError(I18n.GetString("Appearance_Error_" + ex.Error), I18n.GetString("Sponsor_Title"));
        }
        catch (Exception ex)
        {
            LogService.LogError(ex.Message, nameof(SponsorAppearanceService), ex);
            NotificationService.ShowError(I18n.Format("Appearance_OperationFailed", ex.Message), I18n.GetString("Sponsor_Title"));
        }
        finally
        {
            MainWindowService.ApplySponsorVisuals();
            _isAppearanceOperationRunning = false;
            RefreshAppearanceSchemeState();
        }
    }

    private void RefreshAppearanceSchemeState()
    {
        _refreshingAppearanceSchemes = true;
        try
        {
            if (!AppearanceSchemes.SequenceEqual(Settings.SponsorAppearanceSchemes))
            {
                AppearanceSchemes.Clear();
                foreach (var scheme in Settings.SponsorAppearanceSchemes) AppearanceSchemes.Add(scheme);
            }
            OnPropertyChanged(nameof(BuiltinAppearanceThemeIndex));
            OnPropertyChanged(nameof(PersonalAppearanceSchemeIndex));
            OnPropertyChanged(nameof(SponsorNavigationAnimationIndex));
        }
        finally { _refreshingAppearanceSchemes = false; }
        OnPropertyChanged(nameof(CanEditAppearanceSchemes));
        OnPropertyChanged(nameof(CanManageAppearanceScheme));
        OnPropertyChanged(nameof(HasActivePersonalAppearanceScheme));
        OnPropertyChanged(nameof(SaveAppearanceSchemeButtonText));
        OnPropertyChanged(nameof(AppearanceSchemeStatusText));
        SaveAppearanceSchemeCommand?.NotifyCanExecuteChanged();
        UpdateAppearanceSchemeCommand?.NotifyCanExecuteChanged();
        RenameAppearanceSchemeCommand?.NotifyCanExecuteChanged();
        DeleteAppearanceSchemeCommand?.NotifyCanExecuteChanged();
    }
}
