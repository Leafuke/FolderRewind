using FolderRewind.Models;
using FolderRewind.Services.Plugins;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FolderRewind.Services
{
    public static partial class BackupPresetService
    {
        public static bool TryLoadTemplateFromPackage(string sourcePath, out BackupPreset? template, out string message)
        {
            template = null;
            var (success, resultMessage, loadedTemplate) = ReadTemplateFromFile(sourcePath);
            message = resultMessage;
            if (!success || loadedTemplate == null)
            {
                return false;
            }

            template = loadedTemplate;
            return true;
        }

        public static string BuildTemplateSubmissionSummary(BackupPreset template)
        {
            var lines = new List<string>
            {
                I18n.Format("Template_Submission_SummaryName", template.Name),
                I18n.Format("Template_Submission_SummaryGame", string.IsNullOrWhiteSpace(template.GameName) ? "-" : template.GameName),
                I18n.Format("Template_Submission_SummaryAuthor", string.IsNullOrWhiteSpace(template.Author) ? I18n.GetString("Template_Submission_AuthorAnonymous") : template.Author),
                I18n.Format("Template_Submission_SummaryConfigKind", $"{template.Kind.OwnerId}/{template.Kind.KindId}"),
                I18n.Format("Template_Submission_SummaryVersion", template.Version),
                I18n.Format("Template_Submission_SummaryRuleCount", (template.PathRules?.Count ?? 0).ToString())
            };

            if (template.RequiredPluginIds != null && template.RequiredPluginIds.Count > 0)
            {
                lines.Add(I18n.Format("Template_Submission_SummaryPlugins", string.Join(", ", template.RequiredPluginIds)));
            }

            if (!string.IsNullOrWhiteSpace(template.Description))
            {
                lines.Add(string.Empty);
                lines.Add(I18n.GetString("Template_Submission_SummaryDescription"));
                lines.Add(template.Description.Trim());
            }

            return string.Join(Environment.NewLine, lines);
        }

        public static bool ExportTemplateSubmissionPackage(string templateId, string destPath, out string summary, out string message)
        {
            summary = string.Empty;
            message = string.Empty;

            var template = GetTemplateById(templateId);
            if (template == null)
            {
                message = I18n.GetString("Template_Export_TemplateNotFound");
                return false;
            }

            var validation = ValidateTemplateForOfficialSharing(template);
            if (!validation.Success)
            {
                message = validation.Message;
                return false;
            }

            if (!ExportTemplate(templateId, destPath, out message))
            {
                return false;
            }

            summary = BuildTemplateSubmissionSummary(template);
            return true;
        }

        public static bool ExportTemplate(string templateId, string destPath, out string message)
        {
            message = string.Empty;
            var template = GetTemplateById(templateId);
            if (template == null)
            {
                message = I18n.GetString("Template_Export_TemplateNotFound");
                return false;
            }

            if (string.IsNullOrWhiteSpace(destPath))
            {
                message = I18n.GetString("Template_Export_PathEmpty");
                return false;
            }

            try
            {
                // 导出前一定要重新做一次脱敏，避免本地模板在后续演化中混入运行态信息。
                var sanitizedTemplate = CloneTemplate(template);
                SanitizeTemplateForShare(sanitizedTemplate);

                sanitizedTemplate.NormalizeDiscoverySources();
                var envelope = new BackupPresetShareEnvelope
                {
                    Magic = ShareMagic,
                    SchemaVersion = ShareSchemaVersion,
                    ExportedAtUtc = DateTime.UtcNow,
                    Preset = sanitizedTemplate
                };

                AtomicFileService.Write(
                    destPath,
                    stream => JsonSerializer.Serialize(
                        stream,
                        envelope,
                        AppJsonContext.Default.BackupPresetShareEnvelope));
                message = I18n.Format("Template_Export_Success", destPath);
                LogService.Log(message);
                return true;
            }
            catch (Exception ex)
            {
                message = I18n.Format("Template_Export_Failed", ex.Message);
                LogService.Log(message, LogLevel.Error);
                return false;
            }
        }

        public static TemplateImportInspectionResult InspectImportTemplate(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                return new TemplateImportInspectionResult
                {
                    Success = false,
                    Message = I18n.GetString("Template_Import_FileNotFound")
                };
            }

            var appConfig = ConfigService.CurrentConfig;
            if (appConfig?.BackupPresets == null)
            {
                return new TemplateImportInspectionResult
                {
                    Success = false,
                    Message = I18n.GetString("Template_Import_ConfigUnavailable")
                };
            }

            try
            {
                var (success, message, template) = ReadTemplateFromFile(sourcePath);
                if (!success || template == null)
                {
                    return new TemplateImportInspectionResult
                    {
                        Success = false,
                        Message = message
                    };
                }

                var conflict = FindImportConflict(appConfig.BackupPresets, template);
                return new TemplateImportInspectionResult
                {
                    Success = true,
                    Template = template,
                    HasConflict = conflict != null,
                    ConflictTemplateId = conflict?.Id ?? string.Empty,
                    ConflictTemplateName = conflict?.Name ?? string.Empty,
                    ConflictMatchedByShareId = conflict != null
                        && !string.IsNullOrWhiteSpace(template.ShareId)
                        && string.Equals(conflict.ShareId, template.ShareId, StringComparison.OrdinalIgnoreCase)
                };
            }
            catch (Exception ex)
            {
                return new TemplateImportInspectionResult
                {
                    Success = false,
                    Message = I18n.Format("Template_Import_Failed", ex.Message)
                };
            }
        }

        public static bool ImportTemplate(string sourcePath, out string message)
        {
            return ImportTemplate(sourcePath, TemplateImportConflictStrategy.KeepBoth, out message, out _);
        }

        public static bool ImportTemplate(string sourcePath, TemplateImportConflictStrategy strategy, out string message)
        {
            return ImportTemplate(sourcePath, strategy, out message, out _);
        }

        public static bool ImportTemplate(
            string sourcePath,
            TemplateImportConflictStrategy strategy,
            out string message,
            out BackupPreset? importedTemplate)
        {
            message = string.Empty;
            importedTemplate = null;

            var inspection = InspectImportTemplate(sourcePath);
            if (!inspection.Success || inspection.Template == null)
            {
                message = inspection.Message;
                return false;
            }

            var appConfig = ConfigService.CurrentConfig;
            if (appConfig?.BackupPresets == null)
            {
                message = I18n.GetString("Template_Import_ConfigUnavailable");
                return false;
            }

            try
            {
                // 导入时先克隆一份，后面无论是覆盖还是保留两份，都不要回写 inspection 里的对象。
                var template = CloneTemplate(inspection.Template);


                var existingIndex = appConfig.BackupPresets
                    .ToList()
                    .FindIndex(t => string.Equals(t.Id, inspection.ConflictTemplateId, StringComparison.OrdinalIgnoreCase));
                if (inspection.HasConflict && strategy == TemplateImportConflictStrategy.ReplaceExisting && existingIndex >= 0)
                {
                    // 用户已确认同名模板直接覆盖。
                    template.Id = appConfig.BackupPresets[existingIndex].Id;
                    appConfig.BackupPresets[existingIndex] = template;
                    importedTemplate = appConfig.BackupPresets[existingIndex];
                    message = I18n.Format("Template_Import_Overwrite", template.Name);
                }
                else
                {
                    var originalName = template.Name;
                    if (inspection.HasConflict)
                    {
                        template.Id = Guid.NewGuid().ToString("N");
                        // “保留两份”时主动改名，避免用户导入完还分不清哪份是新来的。
                        template.Name = BuildCopyTemplateName(template.Name, appConfig.BackupPresets);
                        if (inspection.ConflictMatchedByShareId)
                        {
                            template.ShareId = Guid.NewGuid().ToString("N");
                        }
                    }

                    appConfig.BackupPresets.Add(template);
                    importedTemplate = template;
                    message = !string.Equals(originalName, template.Name, StringComparison.Ordinal)
                        ? I18n.Format("Template_Import_KeepBothRenamed", originalName, template.Name)
                        : I18n.Format("Template_Import_Success", template.Name);
                }

                ConfigService.Save();
                LogService.Log(message);
                return true;
            }
            catch (Exception ex)
            {
                message = I18n.Format("Template_Import_Failed", ex.Message);
                LogService.Log(message, LogLevel.Error);
                return false;
            }
        }


        private static (bool Success, string Message, BackupPreset? Template) ReadTemplateFromFile(string sourcePath)
        {
            using var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (!root.TryGetProperty("Magic", out var magicElement)
                || !root.TryGetProperty("SchemaVersion", out var schemaElement))
            {
                return (false, I18n.GetString("Template_Import_InvalidFile"), null);
            }

            var magic = magicElement.GetString();
            var schemaVersion = schemaElement.GetString();
            BackupPreset? template;
            if (TemplateFormatPolicy.IsBackupPresetEnvelope(magic, schemaVersion))
            {
                var envelope = root.Deserialize(AppJsonContext.Default.BackupPresetShareEnvelope);
                template = envelope?.Preset;
            }
            else if (TemplateFormatPolicy.IsLegacyEnvelope(magic, schemaVersion))
            {
                var envelope = root.Deserialize(AppJsonContext.Default.TemplateShareEnvelope);
                template = envelope?.Template;
            }
            else
            {
                return (false, I18n.GetString("Template_Import_SchemaUnsupported"), null);
            }

            if (template == null)
            {
                return (false, I18n.GetString("Template_Import_InvalidFile"), null);
            }

            NormalizeImportedTemplate(template);
            return (true, string.Empty, template);
        }

        private static BackupPreset? FindImportConflict(IEnumerable<BackupPreset> existingTemplates, BackupPreset template)
        {
            if (!string.IsNullOrWhiteSpace(template.ShareId))
            {
                var shareConflict = existingTemplates.FirstOrDefault(t =>
                    !string.IsNullOrWhiteSpace(t.ShareId)
                    && string.Equals(t.ShareId, template.ShareId, StringComparison.OrdinalIgnoreCase));
                if (shareConflict != null)
                {
                    return shareConflict;
                }
            }

            return existingTemplates.FirstOrDefault(t =>
                string.Equals(t.Name, template.Name, StringComparison.OrdinalIgnoreCase));
        }

        private static AutomationSettings CreateTemplateAutomationPreset(AutomationSettings? source)
        {
            var preset = CloneAutomation(source ?? new AutomationSettings());
            preset.AutoBackupEnabled = false;
            preset.RunOnAppStart = false;
            preset.IntervalMode = false;
            preset.ScheduledMode = false;
            preset.ConditionalModeEnabled = false;
            preset.TargetFolderPath = string.Empty;
            preset.ConditionType = AutomationConditionType.FileUnlocked;
            preset.ConditionRelativePath = string.Empty;
            preset.LastAutoBackupUtc = DateTime.MinValue;
            preset.ConsecutiveNoChangeCount = 0;
            preset.ScheduleEntries = new ObservableCollection<ScheduleEntry>();
            preset.Normalize();
            return preset;
        }

        private static CloudSettings CreateTemplateCloudPreset()
        {
            return new CloudSettings
            {
                Enabled = false,
                LastRunUtc = DateTime.MinValue,
                LastExitCode = 0,
                LastErrorMessage = string.Empty
            };
        }

        private static void SanitizeTemplateForShare(BackupPreset template)
        {
            var userName = Environment.UserName;
            var userProfileName = Path.GetFileName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            template.Automation = CreateTemplateAutomationPreset(template.Automation);
            template.Cloud = CreateTemplateCloudPreset();
            // 逐段清洗路径片段，只保留文件名，避免把本机绝对路径带出去。
            foreach (var rule in template.PathRules)
            {
                foreach (var segment in rule.Segments)
                {
                    if (segment.Type == TemplatePathSegmentType.Static)
                    {
                        var safeValue = Path.GetFileName(segment.Value ?? string.Empty);
                        if (IsSensitiveSegment(safeValue, userName, userProfileName))
                        {
                            segment.Type = TemplatePathSegmentType.EnumerateDirectory;
                            segment.Value = "UserIdCandidate";
                        }
                        else
                        {
                            segment.Value = safeValue;
                        }
                    }
                }

                foreach (var marker in rule.Markers)
                {
                    marker.Value = Path.GetFileName(marker.Value ?? string.Empty);
                }
            }
        }

        private static void NormalizeImportedTemplate(BackupPreset template)
        {
            if (string.IsNullOrWhiteSpace(template.Id))
            {
                template.Id = Guid.NewGuid().ToString("N");
            }

            if (string.IsNullOrWhiteSpace(template.ShareId))
            {
                template.ShareId = Guid.NewGuid().ToString("N");
            }

            // 导入后统一落到当前版本的安全默认值，避免旧模板把运行态字段带进来。
            template.Name = template.Name?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(template.Name))
            {
                template.Name = I18n.GetString("Template_DefaultName");
            }

            MigrateLegacyTemplateIdentity(template);
            template.Version = string.IsNullOrWhiteSpace(template.Version) ? "1.0" : template.Version;
            template.CreatedUtc = template.CreatedUtc == DateTime.MinValue ? DateTime.UtcNow : template.CreatedUtc;
            template.UpdatedUtc = DateTime.UtcNow;

            template.Archive ??= new ArchiveSettings();
            template.Automation ??= new AutomationSettings();
            template.Filters ??= new FilterSettings();
            template.BackupScope ??= new BackupScopeSettings();
            template.Automation = CreateTemplateAutomationPreset(template.Automation);
            template.Cloud = CreateTemplateCloudPreset();
            template.PathRules ??= new ObservableCollection<TemplatePathRule>();
            template.DiscoverySources ??= new ObservableCollection<BackupPresetDiscoverySource>();
            template.NormalizeDiscoverySources();
            template.RequiredPluginIds ??= new ObservableCollection<string>();
            foreach (var rule in template.PathRules)
            {
                if (string.IsNullOrWhiteSpace(rule.Id))
                {
                    rule.Id = Guid.NewGuid().ToString("N");
                }

                rule.Segments ??= new ObservableCollection<TemplatePathSegment>();
                rule.Markers ??= new ObservableCollection<TemplatePathMarker>();

                foreach (var segment in rule.Segments)
                {
                    segment.Value = Path.GetFileName(segment.Value ?? string.Empty);
                }

                foreach (var marker in rule.Markers)
                {
                    marker.Value = Path.GetFileName(marker.Value ?? string.Empty);
                }
            }
        }

        private static void MigrateLegacyTemplateIdentity(BackupPreset template)
        {
            if (template.SchemaExtensions.TryGetValue("BaseConfigType", out var legacyType)
                && legacyType.ValueKind == JsonValueKind.String)
            {
                var value = legacyType.GetString();
                if (string.Equals(value, "Minecraft Saves", StringComparison.OrdinalIgnoreCase))
                {
                    template.Kind = new ConfigKindReference
                    {
                        OwnerId = FolderRewind.Plugin.Runtime.Configuration.ConfigSchema.MineRewindPluginId,
                        KindId = FolderRewind.Plugin.Runtime.Configuration.ConfigSchema.MineRewindKindId
                    };
                }
                else if (string.Equals(value, "Encrypted", StringComparison.OrdinalIgnoreCase))
                {
                    template.IsEncrypted = true;
                }
            }
            template.SchemaExtensions.Remove("BaseConfigType");

            if (template.SchemaExtensions.TryGetValue("ExtendedProperties", out var legacyProperties)
                && legacyProperties.ValueKind == JsonValueKind.Object
                && legacyProperties.TryGetProperty("Plugin", out var plugin)
                && plugin.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(plugin.GetString())
                && !template.RequiredPluginIds.Contains(plugin.GetString()!, StringComparer.OrdinalIgnoreCase))
            {
                template.RequiredPluginIds.Add(plugin.GetString()!);
            }
            template.SchemaExtensions.Remove("ExtendedProperties");
        }

        private static string BuildCopyTemplateName(string sourceName, IEnumerable<BackupPreset> existingTemplates)
        {
            var suffix = I18n.GetString("Template_Duplicate_CopySuffix");
            if (string.IsNullOrWhiteSpace(suffix))
            {
                suffix = "Copy";
            }

            var safeSourceName = string.IsNullOrWhiteSpace(sourceName) ? I18n.GetString("Template_DefaultName") : sourceName.Trim();
            var baseName = string.Format(System.Globalization.CultureInfo.CurrentCulture, "{0} {1}", safeSourceName, suffix).Trim();
            var candidate = baseName;
            var index = 2;
            while (existingTemplates.Any(t => string.Equals(t.Name, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                candidate = string.Format(System.Globalization.CultureInfo.CurrentCulture, "{0} {1}", baseName, index.ToString(System.Globalization.CultureInfo.CurrentCulture));
                index++;
            }

            return candidate;
        }

        private static string BuildMarkerSummary(IEnumerable<TemplatePathMarker>? markers)
        {
            if (markers == null)
            {
                return I18n.GetString("Template_Preview_MarkerNone");
            }

            var required = 0;
            var optional = 0;
            foreach (var marker in markers)
            {
                if (marker == null)
                {
                    continue;
                }

                switch (marker.Type)
                {
                    case TemplatePathMarkerType.RequiredDirectory:
                    case TemplatePathMarkerType.RequiredFile:
                        required++;
                        break;
                    case TemplatePathMarkerType.OptionalDirectory:
                    case TemplatePathMarkerType.OptionalFile:
                        optional++;
                        break;
                }
            }

            if (required == 0 && optional == 0)
            {
                return I18n.GetString("Template_Preview_MarkerNone");
            }

            return I18n.Format("Template_Preview_MarkerSummary", required.ToString(), optional.ToString());
        }

        private static IReadOnlyList<string> ValidatePathRules(IEnumerable<TemplatePathRule>? rules)
        {
            var errors = new List<string>();
            if (rules == null)
            {
                return errors;
            }

            foreach (var rule in rules)
            {
                if (rule == null)
                {
                    continue;
                }

                if (rule.Segments == null || rule.Segments.Count == 0)
                {
                    errors.Add(I18n.Format("Template_Submission_RuleHasNoSegments", string.IsNullOrWhiteSpace(rule.Name) ? I18n.GetString("Template_Preview_UnnamedRule") : rule.Name));
                    continue;
                }
            }

            return errors;
        }

        private static BackupPreset CloneTemplate(BackupPreset template)
        {
            return JsonCloneService.Clone(template, AppJsonContext.Default.BackupPreset);
        }

        private static ArchiveSettings CloneArchive(ArchiveSettings source)
        {
            return JsonCloneService.Clone(source ?? new ArchiveSettings(), AppJsonContext.Default.ArchiveSettings);
        }

        private static AutomationSettings CloneAutomation(AutomationSettings source)
        {
            var cloned = JsonCloneService.Clone(source ?? new AutomationSettings(), AppJsonContext.Default.AutomationSettings);
            cloned.Normalize();
            return cloned;
        }

        private static FilterSettings CloneFilters(FilterSettings source)
        {
            return JsonCloneService.Clone(source ?? new FilterSettings(), AppJsonContext.Default.FilterSettings);
        }

        private static BackupScopeSettings CloneBackupScope(BackupScopeSettings source)
        {
            return JsonCloneService.Clone(source ?? new BackupScopeSettings(), AppJsonContext.Default.BackupScopeSettings);
        }

        private static CloudSettings CloneCloud(CloudSettings source)
        {
            return JsonCloneService.Clone(source ?? new CloudSettings(), AppJsonContext.Default.CloudSettings);
        }
    }
}
