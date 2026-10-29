using FolderRewind.Models;
using FolderRewind.Services;
using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.ViewModels;

public sealed class OpenListEnvironmentViewModel : ViewModelBase
{
    private GlobalSettings? _observed;
    private OpenListRuntimeSettings _baseline = new(), _draft = new();
    private bool _initialized, _busy, _conflict;
    private long _revision;
    private string _result = "";
    public string ExecutablePath { get => _draft.ExecutablePath; set => Edit(_draft.ExecutablePath, value, v => _draft.ExecutablePath = v, nameof(ExecutablePath)); }
    public string ServiceBaseUri { get => _draft.ServiceBaseUri; set => Edit(_draft.ServiceBaseUri, value, v => _draft.ServiceBaseUri = v, nameof(ServiceBaseUri)); }
    public string WorkingDirectory { get => _draft.WorkingDirectory; set => Edit(_draft.WorkingDirectory, value, v => _draft.WorkingDirectory = v, nameof(WorkingDirectory)); }
    public string DataDirectory { get => _draft.DataDirectory; set => Edit(_draft.DataDirectory, value, v => _draft.DataDirectory = v, nameof(DataDirectory)); }
    public string ConfigFilePath { get => _draft.ConfigFilePath; set => Edit(_draft.ConfigFilePath, value, v => _draft.ConfigFilePath = v, nameof(ConfigFilePath)); }
    public bool AllowStartOnDemand { get => _draft.AllowStartOnDemand; set { if (_draft.AllowStartOnDemand != value) { _draft.AllowStartOnDemand = value; Changed(nameof(AllowStartOnDemand)); } } }
    public string Result { get => _result; private set => SetProperty(ref _result, value); }
    public bool IsBusy { get => _busy; private set { if (SetProperty(ref _busy, value)) { OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(CanStop)); } } }
    public bool CanEdit => !IsBusy;
    public bool CanStop => CanEdit && OpenListRuntimeService.CanStop;
    public bool HasConflict { get => _conflict; private set => SetProperty(ref _conflict, value); }
    public bool HasEdits => !OpenListEnvironmentEdits.SameIdentity(_baseline, _draft) || _baseline.AllowStartOnDemand != _draft.AllowStartOnDemand;

    private void Edit(string before, string? value, Action<string> apply, string property)
    { value ??= ""; if (before == value) return; apply(value); Changed(property); }
    private void Changed(string property)
    { _revision++; Result = ""; OnPropertyChanged(property); OnPropertyChanged(nameof(HasEdits)); }

    public void Activate()
    {
        Deactivate();
        _observed = ConfigService.CurrentConfig.GlobalSettings;
        _observed.PropertyChanged += OnSettingsChanged;
        ConfigService.Saved += OnSaved;
        if (!_initialized) Reload(); else RefreshSaved();
    }
    public void Deactivate()
    {
        if (_observed is not null) _observed.PropertyChanged -= OnSettingsChanged;
        ConfigService.Saved -= OnSaved; _observed = null;
    }
    private void OnSaved() => UiDispatcherService.Enqueue(RefreshSaved);
    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(GlobalSettings.OpenListRuntime)) UiDispatcherService.Enqueue(RefreshSaved); }
    public void Reload()
    {
        _baseline = ConfigSnapshotMapper.Copy(ConfigService.CurrentConfig.GlobalSettings.OpenListRuntime);
        _draft = ConfigSnapshotMapper.Copy(_baseline); _initialized = true; HasConflict = false; Result = "";
        _revision++; NotifyFields();
    }
    private void RefreshSaved()
    {
        if (_observed is null) return;
        if (!ReferenceEquals(_observed, ConfigService.CurrentConfig.GlobalSettings))
        {
            _observed.PropertyChanged -= OnSettingsChanged;
            _observed = ConfigService.CurrentConfig.GlobalSettings;
            _observed.PropertyChanged += OnSettingsChanged;
        }
        var current = ConfigService.CurrentConfig.GlobalSettings.OpenListRuntime;
        var before = ConfigSnapshotMapper.Copy(_draft);
        void Field(string baseline, string draft, string latest, Action<string> setBase, Action<string> setDraft)
        {
            if (draft == baseline || draft == latest) { setBase(latest); setDraft(latest); }
        }
        Field(_baseline.ExecutablePath, _draft.ExecutablePath, current.ExecutablePath, v => _baseline.ExecutablePath = v, v => _draft.ExecutablePath = v);
        Field(_baseline.ServiceBaseUri, _draft.ServiceBaseUri, current.ServiceBaseUri, v => _baseline.ServiceBaseUri = v, v => _draft.ServiceBaseUri = v);
        Field(_baseline.WorkingDirectory, _draft.WorkingDirectory, current.WorkingDirectory, v => _baseline.WorkingDirectory = v, v => _draft.WorkingDirectory = v);
        Field(_baseline.DataDirectory, _draft.DataDirectory, current.DataDirectory, v => _baseline.DataDirectory = v, v => _draft.DataDirectory = v);
        Field(_baseline.ConfigFilePath, _draft.ConfigFilePath, current.ConfigFilePath, v => _baseline.ConfigFilePath = v, v => _draft.ConfigFilePath = v);
        if (_draft.AllowStartOnDemand == _baseline.AllowStartOnDemand || _draft.AllowStartOnDemand == current.AllowStartOnDemand)
            _baseline.AllowStartOnDemand = _draft.AllowStartOnDemand = current.AllowStartOnDemand;
        _baseline.Ownership = _draft.Ownership = current.Ownership;
        _baseline.AuthorizationMachine = _draft.AuthorizationMachine = current.AuthorizationMachine;
        _baseline.AuthorizationSignature = _draft.AuthorizationSignature = current.AuthorizationSignature;
        try { _ = OpenListEnvironmentEdits.Merge(_baseline, _draft, current); HasConflict = false; }
        catch (InvalidOperationException) { HasConflict = true; Result = I18n.GetString("OpenList_EditConflict"); }
        if (!OpenListEnvironmentEdits.SameSnapshot(before, _draft))
        { _revision++; if (!HasConflict) Result = ""; }
        NotifyFields();
    }
    private void NotifyFields()
    {
        foreach (var property in new[] { nameof(ExecutablePath), nameof(ServiceBaseUri), nameof(WorkingDirectory), nameof(DataDirectory),
            nameof(ConfigFilePath), nameof(AllowStartOnDemand), nameof(HasEdits), nameof(CanStop) }) OnPropertyChanged(property);
    }
    private (OpenListRuntimeSettings Expected, OpenListRuntimeSettings Draft) PendingSave()
    {
        var expected = ConfigSnapshotMapper.Copy(ConfigService.CurrentConfig.GlobalSettings.OpenListRuntime);
        var draft = ConfigSnapshotMapper.Copy(_draft);
        draft.ExecutablePath = draft.ExecutablePath.Trim(); draft.ServiceBaseUri = draft.ServiceBaseUri.Trim();
        draft.WorkingDirectory = draft.WorkingDirectory.Trim(); draft.DataDirectory = draft.DataDirectory.Trim(); draft.ConfigFilePath = draft.ConfigFilePath.Trim();
        return (expected, OpenListEnvironmentEdits.Merge(_baseline, draft, expected));
    }
    internal OpenListRuntimeSettings CurrentDraft => PendingSave().Draft;
    internal OpenListRuntimeSettings DisplayDraft => ConfigSnapshotMapper.Copy(_draft);
    private async Task RunAsync(Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Result = CloudCommandSecurity.Redact(ex.Message); }
        finally { IsBusy = false; }
    }
    public Task SaveAsync(Func<OpenListRuntimeSettings, Task<bool>> confirm, CancellationToken token)
        => RunAsync(async () =>
        {
            var (expected, draft) = PendingSave();
            if (draft.AllowStartOnDemand)
            {
                if (!await confirm(draft)) return;
                await OpenListRuntimeService.AuthorizeAsync(draft, token);
            }
            await OpenListRuntimeService.SaveAsync(draft, expected, token);
            Reload(); Result = I18n.GetString("OpenList_SettingsSaved");
        });
    public Task StartAsync(Func<OpenListRuntimeSettings, Task<bool>> confirm, CancellationToken token)
        => RunAsync(async () =>
        {
            var (expected, draft) = PendingSave();
            if (!await confirm(draft)) return;
            await OpenListRuntimeService.AuthorizeAsync(draft, token);
            await OpenListRuntimeService.SaveAsync(draft, expected, token);
            Reload();
            using var lease = await OpenListRuntimeService.AcquireDependencyAsync(token, manual: true);
            Result = I18n.GetString("OpenList_StartedNeedsWebDav");
        });
    public Task CheckAsync(CancellationToken token) => RunAsync(async () =>
    {
        var draft = PendingSave().Draft; var revision = _revision;
        var result = await OpenListRuntimeService.InspectAsync(draft, token);
        if (revision == _revision && !token.IsCancellationRequested) Result = result;
    });
    public Task InspectConfigurationAsync(string path, CancellationToken token) => RunAsync(async () =>
    {
        var revision = _revision;
        var result = await OpenListEnvironmentReader.ReadAsync(path, _draft, token);
        if (revision != _revision || token.IsCancellationRequested) return;
        _draft = result; _revision++; NotifyFields(); Result = I18n.GetString("OpenList_ConfigRecognized");
    });
    public Task StopAsync(CancellationToken token) => RunAsync(async () =>
    { await OpenListRuntimeService.StopAsync(token); Result = I18n.GetString("OpenList_Stopped"); });
}
