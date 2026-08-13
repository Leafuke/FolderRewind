using System.Collections.ObjectModel;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.Services.Plugins.V3;

internal static class PluginV3ModelMapper
{
    public static ConfigKindRef ToKind(BackupConfig config)
        => new(
            new OwnerId(config.Kind?.OwnerId ?? "folderrewind.core"),
            config.Kind?.KindId ?? "default");

    public static ConfigSnapshot ToSnapshot(BackupConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var kind = ToKind(config);
        var folders = config.SourceFolders.Select(folder => ToSnapshot(config.Id, folder)).ToArray();
        return new ConfigSnapshot(
            config.Id,
            new ConfigRevision(config.ConfigRevision),
            kind,
            config.Name,
            folders,
            ToStates(config.Id, null, config.ProviderStates));
    }

    public static FolderSnapshot ToSnapshot(string configId, ManagedFolder folder)
    {
        if (!Guid.TryParse(folder.Id, out var folderId) || folderId == Guid.Empty)
        {
            throw new InvalidDataException("ManagedFolder requires a stable GUID before entering the v3 runtime.");
        }
        return new FolderSnapshot(
            folderId,
            folder.Path,
            folder.DisplayName,
            ToStates(configId, folderId, folder.ProviderStates));
    }

    public static (BackupConfig Config, ManagedFolder Folder) CloneForOperation(
        BackupConfig config,
        ManagedFolder folder)
    {
        var json = JsonSerializer.Serialize(config, AppJsonContext.Default.BackupConfig);
        var clone = JsonSerializer.Deserialize(json, AppJsonContext.Default.BackupConfig)
            ?? throw new InvalidDataException("Could not clone the backup configuration for a v3 operation.");
        var clonedFolder = clone.SourceFolders.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, folder.Id, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("The operation folder is missing from the cloned configuration.");
        return (clone, clonedFolder);
    }

    public static OperationDiagnosticRecord ToRecord(PluginDiagnostic diagnostic)
        => new()
        {
            Code = diagnostic.Code,
            Severity = diagnostic.Severity switch
            {
                DiagnosticSeverity.Warning => PersistedDiagnosticSeverity.Warning,
                DiagnosticSeverity.Error => PersistedDiagnosticSeverity.Error,
                _ => PersistedDiagnosticSeverity.Information
            },
            Capability = diagnostic.Capability,
            Owner = diagnostic.Owner,
            Arguments = new Dictionary<string, string>(diagnostic.Arguments, StringComparer.Ordinal)
        };

    public static PersistedOperationOutcome ToPersisted(OperationOutcome outcome)
        => outcome switch
        {
            OperationOutcome.Success => PersistedOperationOutcome.Success,
            OperationOutcome.SuccessWithWarnings => PersistedOperationOutcome.SuccessWithWarnings,
            OperationOutcome.NoChanges => PersistedOperationOutcome.NoChanges,
            OperationOutcome.Canceled => PersistedOperationOutcome.Canceled,
            OperationOutcome.Blocked => PersistedOperationOutcome.Blocked,
            _ => PersistedOperationOutcome.Failed
        };

    public static CoreCaptureMode ToCaptureMode(BackupMode mode, string fileName)
        => mode switch
        {
            BackupMode.Incremental when fileName.StartsWith("[Full]", StringComparison.OrdinalIgnoreCase)
                => CoreCaptureMode.Full,
            BackupMode.Incremental => CoreCaptureMode.Smart,
            BackupMode.Overwrite => CoreCaptureMode.Overwrite,
            _ => CoreCaptureMode.Full
        };

    private static IReadOnlyDictionary<StateOwnerId, ProviderStateSnapshot> ToStates(
        string configId,
        Guid? folderId,
        IReadOnlyDictionary<string, ProviderStatePayload>? states)
    {
        var result = new Dictionary<StateOwnerId, ProviderStateSnapshot>();
        foreach (var (ownerValue, payload) in states ?? new Dictionary<string, ProviderStatePayload>())
        {
            var owner = new StateOwnerId(ownerValue);
            result.Add(owner, new ProviderStateSnapshot(
                new ProviderStateLocation(configId, folderId),
                owner,
                payload.SchemaVersion,
                payload.Data.Clone()));
        }
        return result;
    }
}
