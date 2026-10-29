using FolderRewind.Models;
using FolderRewind.Services;
using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.ViewModels;

public sealed partial class CloudSetupViewModel
{
    public OpenListEnvironmentViewModel LocalEnvironment { get; } = new();
    private OpenListEnvironmentChoice _environmentChoice;
    private string _presetProviderId = "";
    private string _presetProviderName = "";
    private OpenListRuntimeSettings? _startedLocalEnvironment;
    private string _externalOpenListUrl = "";
    private OpenListRuntimeSettings? _lastLocalEnvironment;
    public int EnvironmentIndex { get => (int)EnvironmentChoice - 1; set { if (value is >= 0 and <= 2) EnvironmentChoice = (OpenListEnvironmentChoice)(value + 1); } }
    public OpenListEnvironmentChoice EnvironmentChoice
    {
        get => _environmentChoice;
        private set
        {
            if (_environmentChoice == OpenListEnvironmentChoice.External) _externalOpenListUrl = WebDavUrl;
            if (!SetProperty(ref _environmentChoice, value)) return;
            DependsOnOpenList = value is OpenListEnvironmentChoice.ExistingLocal or OpenListEnvironmentChoice.NewLocal;
            var local = value == OpenListEnvironmentChoice.NewLocal && _initializedOpenList is not null
                ? _initializedOpenList : LocalEnvironment.DisplayDraft;
            WebDavUrl = value == OpenListEnvironmentChoice.External ? _externalOpenListUrl
                : local.ServiceBaseUri.TrimEnd('/') is { Length: > 0 } uri ? uri + "/dav/" : "";
            Invalidate(); OnPropertyChanged(nameof(LocalOpenListSummary)); OnPropertyChanged(nameof(EnvironmentIndex));
        }
    }
    public string PresetHelp => _presetProviderId switch
    {
        "onedrive" => I18n.GetString("CloudWizard_OneDriveHelp"),
        "s3" => I18n.GetString("CloudWizard_S3Help"),
        "baidu" or "aliyun" or "openlist" => I18n.Format("CloudWizard_BridgePresetHelp", _presetProviderName),
        _ => ""
    };
    public void ApplyPreset(string? providerId)
    {
        var provider = CloudOnboardingService.GetProviderOptions().FirstOrDefault(p => p.Id == providerId);
        if (provider is null) return;
        _presetProviderId = provider.Id;
        _presetProviderName = provider.DisplayName;
        ConnectionName = provider.DisplayName;
        StartMethod(provider.RequiresOpenList ? 0 : provider.Id == "webdav" ? 1 : 2);
        OnPropertyChanged(nameof(PresetHelp));
    }
    public void UseNewLocalOpenList() => EnvironmentChoice = OpenListEnvironmentChoice.NewLocal;
    private void OnLocalEnvironmentChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OpenListEnvironmentViewModel.ExecutablePath) or nameof(OpenListEnvironmentViewModel.ServiceBaseUri)
            or nameof(OpenListEnvironmentViewModel.WorkingDirectory) or nameof(OpenListEnvironmentViewModel.DataDirectory)
            or nameof(OpenListEnvironmentViewModel.ConfigFilePath))
        {
            var current = LocalEnvironment.DisplayDraft;
            if (_lastLocalEnvironment is not null && OpenListEnvironmentEdits.SameIdentity(_lastLocalEnvironment, current)) return;
            _lastLocalEnvironment = current;
            if (DependsOnOpenList) Invalidate(cancelCurrent: !IsBusy);
            OnPropertyChanged(nameof(LocalOpenListSummary));
        }
    }
    internal void ActivateEnvironment()
    {
        LocalEnvironment.PropertyChanged -= OnLocalEnvironmentChanged;
        LocalEnvironment.PropertyChanged += OnLocalEnvironmentChanged;
        LocalEnvironment.Activate();
    }
    public async Task RequireEnvironmentReadyAsync(CancellationToken token)
    {
        await VerifyRcloneAsync(token);
        if (ConnectionMethod != 0) return;
        if (EnvironmentChoice == OpenListEnvironmentChoice.None)
            throw new CloudSetupInputException("CloudWizard_SelectEnvironment");
        if (EnvironmentChoice == OpenListEnvironmentChoice.External) { Message = ""; return; }
        if (LocalEnvironment.HasEdits)
            throw new CloudSetupInputException("CloudWizard_SaveEnvironmentFirst");
        var draft = LocalOpenListDraft;
        if (EnvironmentChoice == OpenListEnvironmentChoice.NewLocal && (_initializedOpenList is not null || _startedLocalEnvironment is null
            || !OpenListEnvironmentEdits.SameIdentity(_startedLocalEnvironment, draft)))
            throw new CloudSetupInputException("CloudWizard_OpenListNotReady");
        if (string.IsNullOrWhiteSpace(draft.ServiceBaseUri)) throw new CloudSetupInputException("OpenList_InvalidServiceUri");
        var uri = OpenListRuntimeService.Validate(draft);
        if (!uri.IsLoopback) throw new CloudSetupInputException("OpenList_LocalEndpointRequired");
        using var lease = await OpenListRuntimeService.AcquireForConnectionAsync(true, token);
        if (!await OpenListRuntimeService.EndpointObservedAsync(draft, token))
            throw new CloudSetupInputException("OpenList_EndpointUnavailable");
        if (!OpenListEnvironmentEdits.SameSnapshot(draft, LocalOpenListDraft))
            throw new CloudSetupInputException("OpenList_EditConflict");
        WebDavUrl = uri.AbsoluteUri.TrimEnd('/') + "/dav/";
        Message = I18n.GetString("CloudWizard_EnvironmentObserved");
    }
    public async Task VerifyRcloneAsync(CancellationToken token)
    {
        try { await ToolExecutableVerifier.VerifyAsync(Executable, "rclone", token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
        { throw RcloneConnectionDiagnostics.Failure("cloud.tool-unavailable", "CloudFailure_Tool", ex); }
    }
}
