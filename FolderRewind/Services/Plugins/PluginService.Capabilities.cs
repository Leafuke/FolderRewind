using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Services.Plugins.V3;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services.Plugins;

public static partial class PluginService
{
    public static IReadOnlyList<PluginBackupScopeDefinition> GetBackupScopeDefinitions(BackupConfig config)
    {
        if (config is null || !IsPluginSystemEnabled() || IsCore(config))
            return Array.Empty<PluginBackupScopeDefinition>();

        PluginId pluginId;
        try { pluginId = new PluginId(config.Kind.OwnerId); }
        catch { return Array.Empty<PluginBackupScopeDefinition>(); }

        var kind = PluginV3ModelMapper.ToKind(config);
        using var lease = PluginV3RuntimeService.Runtime.TryAcquire<IBackupScopeCapability>(
            pluginId,
            capability => capability.Kind == kind);
        if (lease is null)
            return Array.Empty<PluginBackupScopeDefinition>();

        return lease.Capability.Scopes
            .Where(scope => string.Equals(scope.Id.OwnerId.Value, pluginId.Value, StringComparison.Ordinal))
            .Select(scope => new PluginBackupScopeDefinition
            {
                OwnerId = scope.Id.OwnerId.Value,
                Id = scope.Id.ScopeId,
                DisplayName = scope.DisplayName,
                Description = ReadSchemaDescription(scope.FormSchema),
                Parameters = ReadFormFields(scope.FormSchema)
            })
            .ToArray();
    }

    public static async Task<IReadOnlyList<FolderDetailsSection>> GetFolderDetailsSectionsAsync(
        BackupConfig config,
        ManagedFolder folder,
        CancellationToken cancellationToken)
    {
        if (config is null || folder is null || !IsPluginSystemEnabled() || IsCore(config))
            return Array.Empty<FolderDetailsSection>();

        PluginId pluginId;
        try { pluginId = new PluginId(config.Kind.OwnerId); }
        catch { return Array.Empty<FolderDetailsSection>(); }

        var kind = PluginV3ModelMapper.ToKind(config);
        using var lease = PluginV3RuntimeService.Runtime.TryAcquire<IFolderMetadataCapability>(
            pluginId,
            capability => capability.Kind == kind,
            cancellationToken);
        if (lease is null)
            return Array.Empty<FolderDetailsSection>();

        try
        {
            var configSnapshot = PluginV3ModelMapper.ToSnapshot(config);
            var folderSnapshot = PluginV3ModelMapper.ToSnapshot(config.Id, folder);
            var result = await lease.Capability.ReadAsync(
                new FolderMetadataRequest(configSnapshot, folderSnapshot),
                lease.Context).ConfigureAwait(false);
            if (result.Fields.Count == 0) return Array.Empty<FolderDetailsSection>();

            var fieldKeys = new HashSet<string>(StringComparer.Ordinal);
            var items = new List<FolderDetailsItem>(result.Fields.Count);
            foreach (var field in result.Fields)
            {
                if (string.IsNullOrWhiteSpace(field.Key) || !fieldKeys.Add(field.Key))
                {
                    LogService.LogWarning(
                        $"v3 folder metadata ignored an empty or duplicate field key '{field.Key}' from '{pluginId.Value}'.",
                        nameof(PluginService));
                    continue;
                }

                items.Add(new FolderDetailsItem
                {
                    Label = ResolveLocalizedText(field.DisplayName, field.Key),
                    Value = ResolveLocalizedText(field.Value, string.Empty)
                });
            }
            if (items.Count == 0) return Array.Empty<FolderDetailsSection>();

            var title = ResolveConfigKindOption(config).DisplayName;
            return
            [
                new FolderDetailsSection
                {
                    Title = title,
                    Items = new ObservableCollection<FolderDetailsItem>(items)
                }
            ];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogService.LogError(
                $"v3 folder metadata failed for '{pluginId.Value}': {ex.Message}",
                nameof(PluginService),
                ex);
            return Array.Empty<FolderDetailsSection>();
        }
    }

    private static string ResolveLocalizedText(LocalizedText text, string fallback)
        => I18n.PickBest(text.Translations, text.Default) ?? fallback;

    /// <summary>
    /// 设置页只验证静态 Scope 身份和表单必填项；真正的领域解析在备份会话中执行，
    /// 并且仍以 fail-closed 方式阻止无 Provider 或无效 Scope。
    /// </summary>
    public static PluginBackupScopeValidationResult ValidateBackupScope(BackupConfig config)
    {
        if (config?.BackupScope is null || !config.BackupScope.IsPluginScopeEnabled)
            return new PluginBackupScopeValidationResult { Success = true };

        var definition = GetBackupScopeDefinitions(config).FirstOrDefault(candidate =>
            string.Equals(candidate.OwnerId, config.BackupScope.OwnerId, StringComparison.Ordinal)
            && string.Equals(candidate.Id, config.BackupScope.ScopeId, StringComparison.Ordinal));
        if (definition is null)
        {
            return new PluginBackupScopeValidationResult
            {
                Success = false,
                ErrorCode = "scope_provider_missing",
                ErrorMessage = I18n.Format("PluginService_BackupScope_ProviderMissing", config.BackupScope.ScopeId)
            };
        }

        foreach (var parameter in definition.Parameters.Where(field => field.IsRequired))
        {
            if (!config.BackupScope.Parameters.TryGetValue(parameter.Key, out var value)
                || string.IsNullOrWhiteSpace(value))
            {
                return new PluginBackupScopeValidationResult
                {
                    Success = false,
                    ErrorCode = "scope_parameter_required",
                    ErrorMessage = $"{parameter.DisplayName}: {I18n.GetString("Validation_Required")}"
                };
            }
        }

        return new PluginBackupScopeValidationResult { Success = true };
    }

    private static bool IsCore(BackupConfig config)
        => string.Equals(config.Kind.OwnerId, "folderrewind.core", StringComparison.Ordinal);

    private static string? ReadSchemaDescription(JsonElement schema)
        => schema.ValueKind == JsonValueKind.Object
           && schema.TryGetProperty("description", out var description)
           && description.ValueKind == JsonValueKind.String
            ? description.GetString()
            : null;

    private static IReadOnlyList<PluginFormFieldDefinition> ReadFormFields(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object)
            return Array.Empty<PluginFormFieldDefinition>();

        var required = schema.TryGetProperty("required", out var requiredElement)
                       && requiredElement.ValueKind == JsonValueKind.Array
            ? requiredElement.EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString()!)
                .ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var result = new List<PluginFormFieldDefinition>();
        foreach (var property in properties.EnumerateObject())
        {
            var value = property.Value;
            var type = value.TryGetProperty("type", out var typeElement)
                ? typeElement.GetString()
                : "string";
            result.Add(new PluginFormFieldDefinition
            {
                Key = property.Name,
                DisplayName = value.TryGetProperty("title", out var title)
                    ? title.GetString() ?? property.Name
                    : property.Name,
                Description = value.TryGetProperty("description", out var description)
                    ? description.GetString()
                    : null,
                Type = type switch
                {
                    "boolean" => PluginFormFieldType.Boolean,
                    "integer" => PluginFormFieldType.Integer,
                    "string" when value.TryGetProperty("format", out var format)
                                  && string.Equals(format.GetString(), "multiline", StringComparison.OrdinalIgnoreCase)
                        => PluginFormFieldType.MultilineString,
                    _ => PluginFormFieldType.String
                },
                DefaultValue = value.TryGetProperty("default", out var defaultValue)
                    ? defaultValue.ToString()
                    : null,
                IsRequired = required.Contains(property.Name)
            });
        }

        return result;
    }
}
