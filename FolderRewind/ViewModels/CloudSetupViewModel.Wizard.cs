using FolderRewind.Models;
using FolderRewind.Services;
using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.ViewModels;

public sealed partial class CloudSetupViewModel
{
    private readonly CloudWizardFlow _flow = new();
    public int WizardStep { get => _flow.Step; set => _flow.Step = value; }
    public int ConnectionMethod { get => _flow.Method; set => _flow.Method = value; }
    public bool RecoveryMode { get => _flow.ReadOnly; set => _flow.ReadOnly = value; }
    public string WebDavUrl { get; set; } = "";
    public string WebDavUser { get; set; } = "";
    public bool RequestAutomaticUpload { get; set; }
    public bool RequestFirstUpload { get; set; }
    public string? ManualJobId { get; private set; }
    public string ManualJobStatus => Jobs.FirstOrDefault(j => j.JobId == ManualJobId)?.Summary ?? "";
    private readonly Dictionary<string, string> _manualJobs = [];
    private OpenListRuntimeSettings? _initializedOpenList;
    public bool IsConnectionManagement => _manageConnection;
    public string ProjectId => _config?.Id ?? "";
    public bool CanEditProfile => _saveSession.Intent != CloudConnectionEditIntent.Reuse;
    public string LocationSummary => RemoteRoot;
    public string CurrentConnectionSummary => _config is null ? ConnectionName :
        ConfigService.CurrentConfig.CloudConnections.FirstOrDefault(p => p.Id == _config.Cloud.ConnectionProfileId)?.DisplayName ?? I18n.GetString("CloudWizard_NoConnection");
    public string AutomaticSummary => I18n.GetString(_config?.Cloud.Enabled == true ? "CloudWizard_AutoOn" : "CloudWizard_AutoOff");
    public string CopySummary { get; private set; } = I18n.GetString("CloudWizard_CopyUnknown");
    public bool HasMaintenanceIssue { get; private set; }
    public bool CanInitializeMissingJournal { get; private set; }
    public void SuspendChecks() => _request?.Cancel();
    public void StartMethod(int method)
    {
        if (IsBusy) return;
        if (_saveSession.PersistenceUncertain) { WizardStep = 4; Message = I18n.GetString("CloudWizard_SaveUncertain"); return; }
        if (method == 3) { _flow.Begin(method, File.Exists(Executable)); return; }
        BeginNewConnection(); ConfigPath = ""; RemoteRoot = ""; DependsOnOpenList = false;
        _flow.Begin(method, File.Exists(Executable));
    }
    public async Task PrepareToolsAsync(bool openList, CancellationToken token)
    {
        await CloudOnboardingService.PrepareRuntimeToolsAsync(openList, new Progress<string>(value => Message = value), token);
        Executable = ConfigService.CurrentConfig.GlobalSettings.RcloneExecutablePath;
    }
    public async Task InitializeOpenListAsync(string password, int port, CancellationToken token)
    {
        var current = ConfigService.CurrentConfig.GlobalSettings.OpenListRuntime;
        if (File.Exists(current.ConfigFilePath)) throw new InvalidOperationException(I18n.GetString("CloudWizard_OpenListExists"));
        var protectedPaths = ConfigService.CurrentConfig.BackupConfigs.SelectMany(c => c.SourceFolders.Select(f => f.Path).Append(c.DestinationPath))
            .Where(p => !string.IsNullOrWhiteSpace(p)).Append(ConfigService.ConfigDirectory).ToArray();
        _initializedOpenList = await OpenListInitializer.InitializeAsync(current.ExecutablePath,
            Path.Combine(AppRuntimeInfo.WritableAppDataBaseDirectory, "FolderRewind-OpenList"), password, port, protectedPaths, token);
        WebDavUrl = _initializedOpenList.ServiceBaseUri.TrimEnd('/') + "/dav/";
        Message = I18n.GetString("CloudWizard_OpenListInitialized");
    }
    public OpenListRuntimeSettings LocalOpenListDraft => ConfigSnapshotMapper.Copy(_initializedOpenList ?? ConfigService.CurrentConfig.GlobalSettings.OpenListRuntime);
    public string LocalOpenListSummary
    {
        get
        {
            var draft = LocalOpenListDraft;
            return string.IsNullOrWhiteSpace(draft.ExecutablePath) ? I18n.GetString("CloudWizard_OpenListNotReady")
                : I18n.Format("CloudWizard_LocalEnvironment", draft.ExecutablePath, draft.ServiceBaseUri, draft.DataDirectory);
        }
    }
    public async Task AuthorizeAndStartOpenListAsync(CancellationToken token)
    {
        var draft = LocalOpenListDraft;
        await OpenListRuntimeService.AuthorizeAndStartAsync(draft, _initializedOpenList is not null, token);
        _initializedOpenList = null; DependsOnOpenList = true;
        WebDavUrl = draft.ServiceBaseUri.TrimEnd('/') + "/dav/";
        Message = I18n.GetString("OpenList_StartedNeedsWebDav");
    }
    public void UseLocalOpenList()
    {
        var local = ConfigService.CurrentConfig.GlobalSettings.OpenListRuntime;
        if (string.IsNullOrWhiteSpace(local.ServiceBaseUri)) throw new InvalidOperationException(I18n.GetString("CloudWizard_OpenListNotReady"));
        DependsOnOpenList = true; WebDavUrl = local.ServiceBaseUri.TrimEnd('/') + "/dav/";
    }
    public void UseExternalOpenList() { _initializedOpenList = null; DependsOnOpenList = false; WebDavUrl = ""; }
    public void RequireEnvironmentReady()
    {
        if (!File.Exists(Executable)) throw new InvalidOperationException(I18n.GetString("CloudWizard_ToolRequired"));
        if (ConnectionMethod == 0 && _initializedOpenList is not null)
            throw new InvalidOperationException(I18n.GetString("CloudWizard_OpenListNotReady"));
    }
    public async Task DetectConfigurationAsync(CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(Executable) || !File.Exists(Executable)) throw new InvalidOperationException(I18n.GetString("CloudWizard_ToolRequired"));
        var start = new ProcessStartInfo(Executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("config"); start.ArgumentList.Add("file");
        var output = await RcloneConnectionService.RunAsync(start, TimeSpan.FromSeconds(15), token);
        var file = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).LastOrDefault(p => Path.IsPathFullyQualified(p) && File.Exists(p));
        if (file is null) throw new InvalidOperationException(I18n.GetString("CloudWizard_ConfigNotFound"));
        ConfigPath = file; LoadRemotes();
    }
    public void RequireAccessConfiguration()
    {
        var catalog = RcloneConnectionService.InspectRemotes(ConfigPath);
        if (!catalog.Remotes.Any(r => r.Name == CloudRemotePath.Split(RemoteRoot).Remote))
            throw new InvalidOperationException(I18n.GetString("CloudSetup_RemoteMismatch"));
        var auth = RcloneCredentialRefresh.AuthenticationSections(RcloneExecutionContext.ReadConfigurationBytes(ConfigPath), CloudRemotePath.Split(RemoteRoot).Remote);
        if (auth.Length != 0 && !CloudConnectionCatalog.IsManaged(ConfigPath))
            throw new InvalidOperationException(I18n.GetString("CloudWizard_ManagedRequired"));
    }
    public bool HasCurrentWriteEvidence => _writableEvidence is not null;
    public bool AcceptLocation() => _flow.AcceptLocation(_readableEvidence is not null, _writableEvidence is not null);
    public void GoBackWizard() => _flow.Back();
    public async Task SetAutomaticUploadAsync(bool enabled, CancellationToken token)
    {
        if (_config is null) return;
        var current = ConfigService.CurrentConfig.BackupConfigs.Single(p => p.Id == _config.Id);
        if (enabled)
        {
            using var context = CreateContext();
            if (current.Cloud.WritableEvidence != context.TargetRevisionEvidence + "|" + context.RemoteRoot || current.Cloud.LegacyConversionRequired
                || MigrationSetupStateStore.IsCloudPending(ConfigService.ConfigDirectory, current.Id))
                throw new InvalidOperationException(I18n.GetString("CloudJob_WriteVerificationRequired"));
        }
        var draft = new ConfigSettingsDraftService(current); draft.Draft.Cloud.Enabled = enabled;
        if (draft.IsDirty)
        {
            var result = await draft.CommitAsync(token);
            if (!result.Success) throw new InvalidOperationException(result.ErrorMessage, result.Exception);
        }
        _config = ConfigService.CurrentConfig.BackupConfigs.Single(p => p.Id == current.Id);
        OnPropertyChanged(nameof(AutomaticSummary));
    }
}
