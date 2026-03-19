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
    public static class TemplateService
    {
        private const string ShareMagic = "FolderRewindTemplate";
        private const string ShareSchemaVersion = "1.0";
        private const int ScanDepthLimit = 6;
        private const int ScanDirectoryLimit = 5000;
        public const string ShareFileExtension = ".frtemplate.json";

        private static readonly string[] KnownMarkerDirectories =
        {
            "saves",
            "save",
            "savegames",
            "profiles",
            "userdata",
            "slot"
        };

        private static readonly string[] KnownMarkerFiles =
        {
            "level.dat",
            "profile.json",
            "savegame.sav",
            "globalgamemanagers"
        };

        private static readonly Regex GuidRegex = new(
            "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
            RegexOptions.Compiled);

        private static readonly Regex SidRegex = new(
            "^S-1-5-[0-9-]+$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex LongDigitsRegex = new("^[0-9]{8,20}$", RegexOptions.Compiled);
        private static readonly Regex LongHexRegex = new("^[0-9a-fA-F]{16,32}$", RegexOptions.Compiled);

        public sealed class CreateConfigFromTemplateResult
        {
            public bool Success { get; init; }
            public string Message { get; init; } = string.Empty;
            public BackupConfig? Config { get; init; }
            public IReadOnlyList<TemplateFolderCandidate> FolderCandidates { get; init; } = Array.Empty<TemplateFolderCandidate>();
            public IReadOnlyList<string> MissingPluginIds { get; init; } = Array.Empty<string>();
        }

        public sealed class TemplatePreviewResult
        {
            public bool Success { get; init; }
            public string Message { get; init; } = string.Empty;
            public IReadOnlyList<TemplateRulePreviewItem> Items { get; init; } = Array.Empty<TemplateRulePreviewItem>();
        }

        public enum TemplateImportConflictStrategy
        {
            KeepBoth = 0,
            ReplaceExisting = 1
        }

        public sealed class TemplateFolderCandidate
        {
            public string Path { get; init; } = string.Empty;
            public string DisplayName { get; init; } = string.Empty;
            public string RuleName { get; init; } = string.Empty;
            public double Confidence { get; init; }
            public bool IsSelectedByDefault { get; init; }
        }

        public sealed class TemplateImportInspectionResult
        {
            public bool Success { get; init; }
            public string Message { get; init; } = string.Empty;
            public ConfigTemplate? Template { get; init; }
            public bool HasConflict { get; init; }
            public string ConflictTemplateId { get; init; } = string.Empty;
            public string ConflictTemplateName { get; init; } = string.Empty;
            public bool ConflictMatchedByShareId { get; init; }
        }

        public sealed class TemplateValidationResult
        {
            public bool Success { get; init; }
            public string Message { get; init; } = string.Empty;
            public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
            public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
        }

        public static IReadOnlyList<ConfigTemplate> GetTemplates()
        {
            return ConfigService.CurrentConfig?.Templates?.ToList() ?? new List<ConfigTemplate>();
        }

        public static ConfigTemplate? GetTemplateById(string? templateId)
        {
            if (string.IsNullOrWhiteSpace(templateId))
            {
                return null;
            }

            return ConfigService.CurrentConfig?.Templates?
                .FirstOrDefault(t => string.Equals(t.Id, templateId, StringComparison.OrdinalIgnoreCase));
        }

        public static bool UpdateTemplateMetadata(
            string templateId,
            string templateName,
            string? author,
            string? description,
            out string message)
        {
            message = string.Empty;
            var appConfig = ConfigService.CurrentConfig;
            if (appConfig?.Templates == null)
            {
                message = I18n.GetString("Template_Create_ConfigUnavailable");
                return false;
            }

            if (string.IsNullOrWhiteSpace(templateName))
            {
                message = I18n.GetString("Template_Update_NameRequired");
                return false;
            }

            var template = appConfig.Templates.FirstOrDefault(t => string.Equals(t.Id, templateId, StringComparison.OrdinalIgnoreCase));
            if (template == null)
            {
                message = I18n.GetString("Template_Update_TemplateNotFound");
                return false;
            }

            var finalName = templateName.Trim();
            var hasConflict = appConfig.Templates.Any(t =>
                !string.Equals(t.Id, template.Id, StringComparison.OrdinalIgnoreCase)
                && string.Equals(t.Name, finalName, StringComparison.OrdinalIgnoreCase));

            if (hasConflict)
            {
                message = I18n.Format("Template_Update_NameConflict", finalName);
                return false;
            }

            template.Name = finalName;
            template.Author = author?.Trim() ?? string.Empty;
            template.Description = description?.Trim() ?? string.Empty;
            template.UpdatedUtc = DateTime.UtcNow;

            ConfigService.Save();
            message = I18n.Format("Template_Update_Success", template.Name);
            return true;
        }

        public static (bool Success, string Message, ConfigTemplate? Template) DuplicateTemplate(string templateId)
        {
            var appConfig = ConfigService.CurrentConfig;
            if (appConfig?.Templates == null)
            {
                return (false, I18n.GetString("Template_Create_ConfigUnavailable"), null);
            }

            var source = appConfig.Templates.FirstOrDefault(t => string.Equals(t.Id, templateId, StringComparison.OrdinalIgnoreCase));
            if (source == null)
            {
                return (false, I18n.GetString("Template_Duplicate_TemplateNotFound"), null);
            }

            var clone = CloneTemplate(source);
            clone.Id = Guid.NewGuid().ToString("N");
            clone.ShareId = Guid.NewGuid().ToString("N");
            clone.CreatedUtc = DateTime.UtcNow;
            clone.UpdatedUtc = DateTime.UtcNow;
            clone.Name = BuildCopyTemplateName(source.Name, appConfig.Templates);

            appConfig.Templates.Add(clone);
            ConfigService.Save();

            return (true, I18n.Format("Template_Duplicate_Success", clone.Name), clone);
        }

        public static bool DeleteTemplate(string templateId, out string message)
        {
            message = string.Empty;
            var appConfig = ConfigService.CurrentConfig;
            if (appConfig?.Templates == null)
            {
                message = I18n.GetString("Template_Create_ConfigUnavailable");
                return false;
            }

            var template = appConfig.Templates.FirstOrDefault(t => string.Equals(t.Id, templateId, StringComparison.OrdinalIgnoreCase));
            if (template == null)
            {
                message = I18n.GetString("Template_Delete_TemplateNotFound");
                return false;
            }

            if (!appConfig.Templates.Remove(template))
            {
                message = I18n.GetString("Template_Delete_Failed");
                return false;
            }

            ConfigService.Save();
            message = I18n.Format("Template_Delete_Success", template.Name);
            return true;
        }

        public static TemplatePreviewResult PreviewTemplateRules(string templateId)
        {
            var template = GetTemplateById(templateId);
            if (template == null)
            {
                return new TemplatePreviewResult
                {
                    Success = false,
                    Message = I18n.GetString("Template_Preview_TemplateNotFound")
                };
            }

            if (template.PathRules == null || template.PathRules.Count == 0)
            {
                return new TemplatePreviewResult
                {
                    Success = true,
                    Message = I18n.GetString("Template_Preview_NoRules"),
                    Items = Array.Empty<TemplateRulePreviewItem>()
                };
            }

            var items = new List<TemplateRulePreviewItem>();
            var matchedRuleCount = 0;
            foreach (var rule in template.PathRules)
            {
                var resolvedPaths = ResolveRulePaths(rule).ToList();
                if (resolvedPaths.Count > 0)
                {
                    matchedRuleCount++;
                }

                items.Add(new TemplateRulePreviewItem
                {
                    RuleId = rule.Id,
                    RuleName = string.IsNullOrWhiteSpace(rule.Name) ? I18n.GetString("Template_Preview_UnnamedRule") : rule.Name,
                    Pattern = string.IsNullOrWhiteSpace(rule.DisplayPath) ? I18n.GetString("Template_Preview_NoPath") : rule.DisplayPath,
                    StatusText = resolvedPaths.Count > 0
                        ? I18n.GetString("Template_Preview_StatusMatched")
                        : I18n.GetString("Template_Preview_StatusNoMatch"),
                    MatchSummary = I18n.Format("Template_Preview_MatchCount", resolvedPaths.Count.ToString()),
                    SamplePath = resolvedPaths.FirstOrDefault() ?? I18n.GetString("Template_Preview_NoPath"),
                    MarkerSummary = BuildMarkerSummary(rule.Markers)
                });
            }

            var message = I18n.Format("Template_Preview_Summary", matchedRuleCount.ToString(), template.PathRules.Count.ToString());
            if (!IsConfigTypeAvailable(template.BaseConfigType, out var reason) && !string.IsNullOrWhiteSpace(reason))
            {
                message = message + " " + reason;
            }

            return new TemplatePreviewResult
            {
                Success = true,
                Message = message,
                Items = items
            };
        }

        public static (bool Success, string Message, ConfigTemplate? Template) UpsertTemplateFromConfig(
            BackupConfig sourceConfig,
            string templateName,
            string? author,
            string? description)
        {
            if (sourceConfig == null)
            {
                return (false, I18n.GetString("Template_Create_SourceConfigNull"), null);
            }

            if (string.IsNullOrWhiteSpace(templateName))
            {
                return (false, I18n.GetString("Template_Create_NameRequired"), null);
            }

            var appConfig = ConfigService.CurrentConfig;
            if (appConfig?.Templates == null)
            {
                return (false, I18n.GetString("Template_Create_ConfigUnavailable"), null);
            }

            var now = DateTime.UtcNow;
            var existing = appConfig.Templates
                .FirstOrDefault(t => string.Equals(t.Name, templateName.Trim(), StringComparison.OrdinalIgnoreCase));

            var template = existing ?? new ConfigTemplate
            {
                CreatedUtc = now
            };

            if (string.IsNullOrWhiteSpace(template.ShareId))
            {
                template.ShareId = Guid.NewGuid().ToString("N");
            }

            template.Name = templateName.Trim();
            template.Author = author?.Trim() ?? string.Empty;
            template.Description = description?.Trim() ?? string.Empty;
            template.BaseConfigType = sourceConfig.IsEncrypted
                ? "Encrypted"
                : (string.IsNullOrWhiteSpace(sourceConfig.ConfigType) ? "Default" : sourceConfig.ConfigType);
            template.IsEncrypted = sourceConfig.IsEncrypted;
            template.IconGlyph = sourceConfig.IconGlyph;
            template.DefaultConfigName = sourceConfig.Name;
            template.Version = string.IsNullOrWhiteSpace(template.Version) ? "1.0" : template.Version;
            template.UpdatedUtc = now;

            template.Archive = CloneArchive(sourceConfig.Archive);
            // 模板要复用“策略”，不要顺手把用户本机的自动任务状态也打包进去。
            template.Automation = CreateTemplateAutomationPreset(sourceConfig.Automation);
            template.Filters = CloneFilters(sourceConfig.Filters);
            // 云同步这类配置很容易带出本地路径和远端地址，分享时宁可保守一点。
            template.Cloud = CreateTemplateCloudPreset();
            // 扩展字段先走白名单，后面如果插件要分享更多内容，再做显式声明机制。
            template.ExtendedProperties = FilterTemplateExtendedProperties(sourceConfig.ExtendedProperties);

            var requiredPlugins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (template.ExtendedProperties.TryGetValue("Plugin", out var pluginId) && !string.IsNullOrWhiteSpace(pluginId))
            {
                requiredPlugins.Add(pluginId);
            }
            template.RequiredPluginIds = new ObservableCollection<string>(requiredPlugins.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));

            template.PathRules = InferPathRules(sourceConfig);
            if (template.PathRules.Count == 0)
            {
                // 没有可推断规则时不阻断创建，模板依旧可复用策略参数。
                LogService.Log(I18n.GetString("Template_Create_NoPathRules"), LogLevel.Warning);
            }

            if (existing == null)
            {
                appConfig.Templates.Add(template);
            }

            ConfigService.Save();

            var messageKey = existing == null ? "Template_Create_Success" : "Template_Create_OverwriteSuccess";
            return (true, I18n.Format(messageKey, template.Name), template);
        }

        public static CreateConfigFromTemplateResult CreateConfigFromTemplate(
            ConfigTemplate template,
            string configName,
            string? configTypeOverride = null)
        {
            if (template == null)
            {
                return new CreateConfigFromTemplateResult
                {
                    Success = false,
                    Message = I18n.GetString("Template_Apply_TemplateNull")
                };
            }

            var finalName = string.IsNullOrWhiteSpace(configName)
                ? (string.IsNullOrWhiteSpace(template.DefaultConfigName) ? template.Name : template.DefaultConfigName)
                : configName.Trim();

            if (string.IsNullOrWhiteSpace(finalName))
            {
                return new CreateConfigFromTemplateResult
                {
                    Success = false,
                    Message = I18n.GetString("Template_Apply_NameRequired")
                };
            }

            var requestedType = string.IsNullOrWhiteSpace(configTypeOverride)
                ? template.BaseConfigType
                : configTypeOverride.Trim();
            requestedType = string.IsNullOrWhiteSpace(requestedType) ? "Default" : requestedType;

            // “模板偏好加密”和“这次显式选了加密”都算数，但真正的运行时类型仍然走 Default + IsEncrypted。
            var useEncrypted = string.Equals(requestedType, "Encrypted", StringComparison.OrdinalIgnoreCase) || template.IsEncrypted;
            var effectiveType = useEncrypted ? "Default" : requestedType;
            if (!IsConfigTypeAvailable(effectiveType, out var unavailableReason))
            {
                return new CreateConfigFromTemplateResult
                {
                    Success = false,
                    Message = unavailableReason
                };
            }

            var config = new BackupConfig
            {
                Name = finalName,
                DestinationPath = ConfigService.BuildDefaultDestinationPath(finalName),
                ConfigType = effectiveType,
                IsEncrypted = useEncrypted,
                IconGlyph = string.IsNullOrWhiteSpace(template.IconGlyph) ? "\uE8B7" : template.IconGlyph,
                SummaryText = string.Empty,
                Archive = CloneArchive(template.Archive),
                Automation = CloneAutomation(template.Automation),
                Filters = CloneFilters(template.Filters),
                Cloud = CloneCloud(template.Cloud),
                ExtendedProperties = template.ExtendedProperties == null
                    ? new Dictionary<string, string>()
                    : new Dictionary<string, string>(template.ExtendedProperties, StringComparer.OrdinalIgnoreCase)
            };

            config.ExtendedProperties["TemplateId"] = template.Id;
            config.ExtendedProperties["TemplateName"] = template.Name;

            // 这里先生成候选项，不直接写进 Config.SourceFolders。
            // 游戏模板的推断再聪明，也不该替用户静默决定最终要备份哪些目录。
            var candidates = new List<TemplateFolderCandidate>();
            var folderPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rule in template.PathRules.OrderByDescending(r => r.Confidence))
            {
                // 规则按置信度从高到低展开，尽量让更像“标准存档路径”的结果排在前面。
                var resolvedPaths = ResolveRulePaths(rule).ToList();
                var canAutoSelect = rule.AutoAdd && resolvedPaths.Count == 1;
                foreach (var path in resolvedPaths)
                {
                    if (!folderPaths.Add(path))
                    {
                        continue;
                    }

                    candidates.Add(new TemplateFolderCandidate
                    {
                        Path = path,
                        DisplayName = FolderNameConflictService.ResolveDisplayName(rule.Name, path),
                        RuleName = string.IsNullOrWhiteSpace(rule.Name) ? I18n.GetString("Template_Preview_UnnamedRule") : rule.Name,
                        Confidence = rule.Confidence,
                        IsSelectedByDefault = canAutoSelect
                    });
                }
            }

            var selectedByDefaultCount = candidates.Count(c => c.IsSelectedByDefault);
            var message = candidates.Count > 0
                ? I18n.Format("Template_Apply_SuccessWithFolders", selectedByDefaultCount.ToString())
                : I18n.GetString("Template_Apply_SuccessNoFolders");
            var missingPluginIds = GetMissingRequiredPluginIds(template);

            return new CreateConfigFromTemplateResult
            {
                Success = true,
                Message = message,
                Config = config,
                FolderCandidates = candidates,
                MissingPluginIds = missingPluginIds
            };
        }

        public static bool IsConfigTypeAvailable(string? configType, out string reason)
        {
            reason = string.Empty;
            var normalized = string.IsNullOrWhiteSpace(configType) ? "Default" : configType.Trim();

            if (string.Equals(normalized, "Default", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "Encrypted", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            PluginService.Initialize();
            var supported = PluginService.GetAllSupportedConfigTypes();
            if (supported.Any(t => string.Equals(t, normalized, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            reason = I18n.Format("Template_ConfigTypeUnavailable", normalized);
            return false;
        }

        public static IReadOnlyList<string> GetMissingRequiredPluginIds(ConfigTemplate? template)
        {
            if (template?.RequiredPluginIds == null || template.RequiredPluginIds.Count == 0)
            {
                return Array.Empty<string>();
            }

            PluginService.Initialize();
            var installedIds = PluginService.InstalledPlugins
                .Select(p => p.Id)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return template.RequiredPluginIds
                .Where(id => !string.IsNullOrWhiteSpace(id) && !installedIds.Contains(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static TemplateValidationResult ValidateTemplateForOfficialSharing(ConfigTemplate? template)
        {
            if (template == null)
            {
                return new TemplateValidationResult
                {
                    Success = false,
                    Message = I18n.GetString("Template_Submission_TemplateNull"),
                    Errors = new[] { I18n.GetString("Template_Submission_TemplateNull") }
                };
            }

            var errors = new List<string>();
            var warnings = new List<string>();

            if (string.IsNullOrWhiteSpace(template.Name))
            {
                errors.Add(I18n.GetString("Template_Submission_NameRequired"));
            }

            if (string.IsNullOrWhiteSpace(template.Description))
            {
                errors.Add(I18n.GetString("Template_Submission_DescriptionRequired"));
            }

            if (template.PathRules == null || template.PathRules.Count == 0)
            {
                errors.Add(I18n.GetString("Template_Submission_PathRulesRequired"));
            }
            else
            {
                foreach (var issue in ValidatePathRules(template.PathRules))
                {
                    errors.Add(issue);
                }
            }

            if (!IsConfigTypeAvailable(template.BaseConfigType, out var unavailableReason)
                && !string.IsNullOrWhiteSpace(unavailableReason))
            {
                errors.Add(unavailableReason);
            }

            var dryRun = CreateConfigFromTemplate(template, template.DefaultConfigName);
            if (!dryRun.Success)
            {
                errors.Add(string.IsNullOrWhiteSpace(dryRun.Message)
                    ? I18n.GetString("Template_Submission_DryRunFailed")
                    : dryRun.Message);
            }

            var missingPlugins = GetMissingRequiredPluginIds(template);
            if (missingPlugins.Count > 0)
            {
                warnings.Add(I18n.Format("Template_RequiredPluginsMissing", string.Join(", ", missingPlugins)));
            }

            if (string.IsNullOrWhiteSpace(template.GameName))
            {
                warnings.Add(I18n.GetString("Template_Submission_GameNameRecommended"));
            }

            var message = errors.Count > 0
                ? I18n.Format("Template_Submission_ValidationFailed", errors.Count.ToString())
                : (warnings.Count > 0
                    ? I18n.Format("Template_Submission_ValidationWarning", warnings.Count.ToString())
                    : I18n.GetString("Template_Submission_ValidationPassed"));

            return new TemplateValidationResult
            {
                Success = errors.Count == 0,
                Message = message,
                Errors = errors,
                Warnings = warnings
            };
        }

        public static bool TryLoadTemplateFromPackage(string sourcePath, out ConfigTemplate? template, out string message)
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

        public static string BuildTemplateSubmissionSummary(ConfigTemplate template)
        {
            var lines = new List<string>
            {
                I18n.Format("Template_Submission_SummaryName", template.Name),
                I18n.Format("Template_Submission_SummaryGame", string.IsNullOrWhiteSpace(template.GameName) ? "-" : template.GameName),
                I18n.Format("Template_Submission_SummaryAuthor", string.IsNullOrWhiteSpace(template.Author) ? I18n.GetString("Template_Submission_AuthorAnonymous") : template.Author),
                I18n.Format("Template_Submission_SummaryConfigType", template.BaseConfigType),
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

                var envelope = new TemplateShareEnvelope
                {
                    Magic = ShareMagic,
                    SchemaVersion = ShareSchemaVersion,
                    ExportedAtUtc = DateTime.UtcNow,
                    Template = sanitizedTemplate
                };

                var json = JsonSerializer.Serialize(envelope, AppJsonContext.Default.TemplateShareEnvelope);
                File.WriteAllText(destPath, json);
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
            if (appConfig?.Templates == null)
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

                var conflict = FindImportConflict(appConfig.Templates, template);
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
            return ImportTemplate(sourcePath, TemplateImportConflictStrategy.KeepBoth, out message);
        }

        public static bool ImportTemplate(string sourcePath, TemplateImportConflictStrategy strategy, out string message)
        {
            message = string.Empty;

            var inspection = InspectImportTemplate(sourcePath);
            if (!inspection.Success || inspection.Template == null)
            {
                message = inspection.Message;
                return false;
            }

            var appConfig = ConfigService.CurrentConfig;
            if (appConfig?.Templates == null)
            {
                message = I18n.GetString("Template_Import_ConfigUnavailable");
                return false;
            }

            try
            {
                // 导入时先克隆一份，后面无论是覆盖还是保留两份，都不要回写 inspection 里的对象。
                var template = CloneTemplate(inspection.Template);


                // 兼容早期直存 ConfigTemplate 的格式。

                var existingIndex = appConfig.Templates
                    .ToList()
                    .FindIndex(t => string.Equals(t.Id, inspection.ConflictTemplateId, StringComparison.OrdinalIgnoreCase));
                if (inspection.HasConflict && strategy == TemplateImportConflictStrategy.ReplaceExisting && existingIndex >= 0)
                {
                    // 用户已确认同名模板直接覆盖。
                    template.Id = appConfig.Templates[existingIndex].Id;
                    appConfig.Templates[existingIndex] = template;
                    message = I18n.Format("Template_Import_Overwrite", template.Name);
                }
                else
                {
                    var originalName = template.Name;
                    if (inspection.HasConflict)
                    {
                        template.Id = Guid.NewGuid().ToString("N");
                        // “保留两份”时主动改名，避免用户导入完还分不清哪份是新来的。
                        template.Name = BuildCopyTemplateName(template.Name, appConfig.Templates);
                        if (inspection.ConflictMatchedByShareId)
                        {
                            template.ShareId = Guid.NewGuid().ToString("N");
                        }
                    }

                    appConfig.Templates.Add(template);
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

        private static ObservableCollection<TemplatePathRule> InferPathRules(BackupConfig sourceConfig)
        {
            var rules = new ObservableCollection<TemplatePathRule>();
            if (sourceConfig?.SourceFolders == null)
            {
                return rules;
            }

            var currentUserName = Environment.UserName;
            var userProfileName = Path.GetFileName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            foreach (var folder in sourceConfig.SourceFolders)
            {
                if (folder == null || string.IsNullOrWhiteSpace(folder.Path) || !Directory.Exists(folder.Path))
                {
                    continue;
                }

                var rule = BuildRule(folder, currentUserName, userProfileName);
                if (rule == null)
                {
                    continue;
                }

                rules.Add(rule);
            }

            return rules;
        }

        private static TemplatePathRule? BuildRule(ManagedFolder folder, string currentUserName, string userProfileName)
        {
            if (!TryGetSegmentsWithRootPlaceholder(folder.Path, out var segments, out var confidence))
            {
                return null;
            }

            var hasDynamicSegment = false;
            for (int i = 0; i < segments.Count; i++)
            {
                var segment = segments[i];
                if (segment.Type != TemplatePathSegmentType.Static)
                {
                    continue;
                }

                if (string.Equals(segment.Value, currentUserName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(segment.Value, userProfileName, StringComparison.OrdinalIgnoreCase))
                {
                    segment.Type = TemplatePathSegmentType.Placeholder;
                    segment.Value = "UserName";
                    confidence += 0.08;
                    continue;
                }

                if (IsDynamicCandidate(segment.Value))
                {
                    segment.Type = TemplatePathSegmentType.EnumerateDirectory;
                    segment.Value = "UserIdCandidate";
                    hasDynamicSegment = true;
                    confidence += 0.1;
                }
            }

            if (hasDynamicSegment)
            {
                confidence += 0.05;
            }

            confidence = Math.Clamp(confidence, 0.35, 0.95);

            return new TemplatePathRule
            {
                Name = FolderNameConflictService.ResolveDisplayName(folder),
                Segments = segments,
                Markers = BuildMarkers(folder.Path),
                Confidence = confidence,
                AutoAdd = confidence >= 0.85
            };
        }

        private static bool TryGetSegmentsWithRootPlaceholder(
            string path,
            out ObservableCollection<TemplatePathSegment> segments,
            out double confidence)
        {
            segments = new ObservableCollection<TemplatePathSegment>();
            confidence = 0.0;

            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            string normalized;
            try
            {
                normalized = Path.GetFullPath(path);
            }
            catch
            {
                return false;
            }

            var roots = GetKnownRoots()
                .Where(r => !string.IsNullOrWhiteSpace(r.Path))
                .OrderByDescending(r => r.Path.Length)
                .ToList();

            foreach (var root in roots)
            {
                if (!normalized.StartsWith(root.Path, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (normalized.Length > root.Path.Length)
                {
                    var boundary = normalized[root.Path.Length];
                    if (boundary != Path.DirectorySeparatorChar && boundary != Path.AltDirectorySeparatorChar)
                    {
                        continue;
                    }
                }

                segments.Add(new TemplatePathSegment
                {
                    Type = TemplatePathSegmentType.Placeholder,
                    Value = root.Token
                });

                var relative = normalized.Substring(root.Path.Length)
                    .Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!string.IsNullOrWhiteSpace(relative))
                {
                    foreach (var segment in relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        segments.Add(new TemplatePathSegment
                        {
                            Type = TemplatePathSegmentType.Static,
                            Value = segment
                        });
                    }
                }

                confidence = 0.6;
                return true;
            }

            return false;
        }

        private static ObservableCollection<TemplatePathMarker> BuildMarkers(string folderPath)
        {
            var markers = new ObservableCollection<TemplatePathMarker>();
            try
            {
                foreach (var fileName in KnownMarkerFiles)
                {
                    if (File.Exists(Path.Combine(folderPath, fileName)))
                    {
                        markers.Add(new TemplatePathMarker
                        {
                            Type = TemplatePathMarkerType.RequiredFile,
                            Value = fileName
                        });
                    }
                }

                foreach (var dirName in KnownMarkerDirectories)
                {
                    if (Directory.Exists(Path.Combine(folderPath, dirName)))
                    {
                        markers.Add(new TemplatePathMarker
                        {
                            Type = TemplatePathMarkerType.OptionalDirectory,
                            Value = dirName
                        });
                    }
                }

                if (markers.Count == 0)
                {
                    var candidateFile = Directory.EnumerateFiles(folderPath, "*", SearchOption.TopDirectoryOnly)
                        .Select(Path.GetFileName)
                        .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name) && IsLikelyStableFileName(name));

                    if (!string.IsNullOrWhiteSpace(candidateFile))
                    {
                        markers.Add(new TemplatePathMarker
                        {
                            Type = TemplatePathMarkerType.OptionalFile,
                            Value = candidateFile
                        });
                    }
                }
            }
            catch
            {
                // 标记提取失败不阻断规则创建。
            }

            return markers;
        }

        private static bool IsLikelyStableFileName(string fileName)
        {
            var extension = Path.GetExtension(fileName);
            if (string.IsNullOrWhiteSpace(extension))
            {
                return fileName.Length <= 40;
            }

            return extension.Equals(".sav", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".dat", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".json", StringComparison.OrdinalIgnoreCase);
        }

        private static IReadOnlyList<string> ResolveRulePaths(TemplatePathRule rule)
        {
            if (rule == null || rule.Segments == null || rule.Segments.Count == 0)
            {
                return Array.Empty<string>();
            }

            var current = new List<string> { string.Empty };
            var scannedDirectories = 0;

            foreach (var segment in rule.Segments)
            {
                if (segment == null)
                {
                    continue;
                }

                var next = new List<string>();

                switch (segment.Type)
                {
                    case TemplatePathSegmentType.Placeholder:
                        var placeholder = ResolvePlaceholder(segment.Value);
                        if (string.IsNullOrWhiteSpace(placeholder))
                        {
                            return Array.Empty<string>();
                        }

                        foreach (var basePath in current)
                        {
                            var combined = CombinePathSegment(basePath, placeholder);
                            if (Directory.Exists(combined))
                            {
                                next.Add(combined);
                            }
                        }
                        break;

                    case TemplatePathSegmentType.EnumerateDirectory:
                        foreach (var basePath in current)
                        {
                            if (!Directory.Exists(basePath))
                            {
                                continue;
                            }

                            IEnumerable<string> dirs;
                            try
                            {
                                dirs = Directory.EnumerateDirectories(basePath, "*", SearchOption.TopDirectoryOnly);
                            }
                            catch
                            {
                                continue;
                            }

                            foreach (var dir in dirs)
                            {
                                scannedDirectories++;
                                if (scannedDirectories > ScanDirectoryLimit)
                                {
                                    break;
                                }

                                if (GetRelativeDepth(basePath, dir) > ScanDepthLimit)
                                {
                                    continue;
                                }

                                next.Add(dir);
                            }

                            if (scannedDirectories > ScanDirectoryLimit)
                            {
                                break;
                            }
                        }
                        break;

                    default:
                        foreach (var basePath in current)
                        {
                            var combined = CombinePathSegment(basePath, segment.Value);
                            if (Directory.Exists(combined))
                            {
                                next.Add(combined);
                            }
                        }
                        break;
                }

                current = next
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (current.Count == 0)
                {
                    return Array.Empty<string>();
                }
            }

            return current
                .Where(path => ValidateMarkers(path, rule.Markers))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool ValidateMarkers(string path, IEnumerable<TemplatePathMarker>? markers)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            if (markers == null)
            {
                return true;
            }

            foreach (var marker in markers)
            {
                if (marker == null || string.IsNullOrWhiteSpace(marker.Value))
                {
                    continue;
                }

                var safeName = Path.GetFileName(marker.Value);
                if (string.IsNullOrWhiteSpace(safeName))
                {
                    continue;
                }

                var targetPath = Path.Combine(path, safeName);
                var matched = marker.Type switch
                {
                    TemplatePathMarkerType.RequiredDirectory => Directory.Exists(targetPath),
                    TemplatePathMarkerType.RequiredFile => File.Exists(targetPath),
                    TemplatePathMarkerType.OptionalDirectory => true,
                    TemplatePathMarkerType.OptionalFile => true,
                    _ => true
                };

                if (!matched)
                {
                    return false;
                }
            }

            return true;
        }

        private static string ResolvePlaceholder(string? token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return string.Empty;
            }

            return token.Trim() switch
            {
                "UserProfile" => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Documents" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "SavedGames" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games"),
                "AppDataRoaming" => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AppDataLocal" => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AppDataLocalLow" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow"),
                "ProgramData" => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "UserName" => Environment.UserName,
                _ => string.Empty
            };
        }

        private static string CombinePathSegment(string? basePath, string segment)
        {
            if (string.IsNullOrWhiteSpace(segment))
            {
                return basePath ?? string.Empty;
            }

            if (Path.IsPathRooted(segment))
            {
                return segment;
            }

            if (string.IsNullOrWhiteSpace(basePath))
            {
                return segment;
            }

            return Path.Combine(basePath, segment);
        }

        private static int GetRelativeDepth(string parent, string child)
        {
            try
            {
                var relative = Path.GetRelativePath(parent, child);
                if (string.IsNullOrWhiteSpace(relative) || relative == ".")
                {
                    return 0;
                }

                return relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries).Length;
            }
            catch
            {
                return int.MaxValue;
            }
        }

        private static bool IsDynamicCandidate(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return SidRegex.IsMatch(value)
                || GuidRegex.IsMatch(value)
                || LongDigitsRegex.IsMatch(value)
                || LongHexRegex.IsMatch(value);
        }

        private static bool IsSensitiveSegment(string value, string userName, string userProfileName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return string.Equals(value, userName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, userProfileName, StringComparison.OrdinalIgnoreCase)
                || IsDynamicCandidate(value);
        }

        private static IEnumerable<(string Token, string Path)> GetKnownRoots()
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            yield return ("AppDataLocalLow", Path.Combine(userProfile, "AppData", "LocalLow"));
            yield return ("AppDataRoaming", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
            yield return ("AppDataLocal", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            yield return ("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            yield return ("SavedGames", Path.Combine(userProfile, "Saved Games"));
            yield return ("ProgramData", Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
            yield return ("UserProfile", userProfile);
        }

        private static (bool Success, string Message, ConfigTemplate? Template) ReadTemplateFromFile(string sourcePath)
        {
            var json = File.ReadAllText(sourcePath);
            ConfigTemplate? template = null;

            var envelope = JsonSerializer.Deserialize(json, AppJsonContext.Default.TemplateShareEnvelope);
            if (envelope != null && string.Equals(envelope.Magic, ShareMagic, StringComparison.OrdinalIgnoreCase))
            {
                if (!IsSupportedShareSchemaVersion(envelope.SchemaVersion))
                {
                    return (false, I18n.GetString("Template_Import_SchemaUnsupported"), null);
                }

                template = envelope.Template;
            }

            template ??= JsonSerializer.Deserialize(json, AppJsonContext.Default.ConfigTemplate);
            if (template == null)
            {
                return (false, I18n.GetString("Template_Import_InvalidFile"), null);
            }

            NormalizeImportedTemplate(template);
            return (true, string.Empty, template);
        }

        private static ConfigTemplate? FindImportConflict(IEnumerable<ConfigTemplate> existingTemplates, ConfigTemplate template)
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

        private static bool IsSupportedShareSchemaVersion(string? schemaVersion)
        {
            if (string.IsNullOrWhiteSpace(schemaVersion))
            {
                return true;
            }

            if (string.Equals(schemaVersion.Trim(), ShareSchemaVersion, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (Version.TryParse(schemaVersion, out var parsed) && parsed.Major <= 1)
            {
                return true;
            }

            return false;
        }

        private static AutomationSettings CreateTemplateAutomationPreset(AutomationSettings? source)
        {
            var preset = CloneAutomation(source ?? new AutomationSettings());
            preset.AutoBackupEnabled = false;
            preset.RunOnAppStart = false;
            preset.ScheduledMode = false;
            preset.LastAutoBackupUtc = DateTime.MinValue;
            preset.LastScheduledRunDateLocal = DateTime.MinValue;
            preset.ConsecutiveNoChangeCount = 0;
            preset.ScheduleEntries = new ObservableCollection<ScheduleEntry>();
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

        private static Dictionary<string, string> FilterTemplateExtendedProperties(Dictionary<string, string>? source)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (source == null)
            {
                return result;
            }

            foreach (var pair in source)
            {
                if (!IsSafeTemplateExtendedProperty(pair.Key, pair.Value))
                {
                    continue;
                }

                result[pair.Key] = pair.Value?.Trim() ?? string.Empty;
            }

            return result;
        }

        private static bool IsSafeTemplateExtendedProperty(string? key, string? value)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            var normalizedKey = key.Trim();
            if (string.Equals(normalizedKey, "TemplateId", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalizedKey, "TemplateName", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var normalizedValue = value.Trim();
            if (normalizedValue.Length > 256)
            {
                return false;
            }

            if (normalizedKey.Contains("password", StringComparison.OrdinalIgnoreCase)
                || normalizedKey.Contains("secret", StringComparison.OrdinalIgnoreCase)
                || normalizedKey.Contains("token", StringComparison.OrdinalIgnoreCase)
                || normalizedKey.Contains("path", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (Path.IsPathRooted(normalizedValue)
                || normalizedValue.Contains("://", StringComparison.OrdinalIgnoreCase)
                || normalizedValue.Contains('\\')
                || normalizedValue.Contains('/'))
            {
                return false;
            }

            return true;
        }

        private static void SanitizeTemplateForShare(ConfigTemplate template)
        {
            var userName = Environment.UserName;
            var userProfileName = Path.GetFileName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            template.Automation = CreateTemplateAutomationPreset(template.Automation);
            template.Cloud = CreateTemplateCloudPreset();
            template.ExtendedProperties = FilterTemplateExtendedProperties(template.ExtendedProperties);

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

        private static void NormalizeImportedTemplate(ConfigTemplate template)
        {
            if (string.IsNullOrWhiteSpace(template.Id))
            {
                template.Id = Guid.NewGuid().ToString("N");
            }

            if (string.IsNullOrWhiteSpace(template.ShareId))
            {
                template.ShareId = Guid.NewGuid().ToString("N");
            }

            template.Name = template.Name?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(template.Name))
            {
                template.Name = I18n.GetString("Template_DefaultName");
            }

            template.BaseConfigType = string.IsNullOrWhiteSpace(template.BaseConfigType) ? "Default" : template.BaseConfigType;
            template.Version = string.IsNullOrWhiteSpace(template.Version) ? "1.0" : template.Version;
            template.CreatedUtc = template.CreatedUtc == DateTime.MinValue ? DateTime.UtcNow : template.CreatedUtc;
            template.UpdatedUtc = DateTime.UtcNow;

            template.Archive ??= new ArchiveSettings();
            template.Automation ??= new AutomationSettings();
            template.Filters ??= new FilterSettings();
            template.Automation = CreateTemplateAutomationPreset(template.Automation);
            template.Cloud = CreateTemplateCloudPreset();
            template.PathRules ??= new ObservableCollection<TemplatePathRule>();
            template.RequiredPluginIds ??= new ObservableCollection<string>();
            template.ExtendedProperties = FilterTemplateExtendedProperties(template.ExtendedProperties);

            if (template.ExtendedProperties.TryGetValue("Plugin", out var pluginId)
                && !string.IsNullOrWhiteSpace(pluginId)
                && !template.RequiredPluginIds.Any(id => string.Equals(id, pluginId, StringComparison.OrdinalIgnoreCase)))
            {
                template.RequiredPluginIds.Add(pluginId);
            }

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

        private static string BuildCopyTemplateName(string sourceName, IEnumerable<ConfigTemplate> existingTemplates)
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

                foreach (var segment in rule.Segments)
                {
                    if (segment == null)
                    {
                        continue;
                    }

                    var value = segment?.Value?.Trim() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        continue;
                    }

                    if (value.Contains("..", StringComparison.Ordinal)
                        || value.Contains(':')
                        || value.Contains('\\')
                        || value.Contains('/')
                        || Path.IsPathRooted(value))
                    {
                        errors.Add(I18n.Format("Template_Submission_RuleContainsUnsafePath", string.IsNullOrWhiteSpace(rule.Name) ? I18n.GetString("Template_Preview_UnnamedRule") : rule.Name, value));
                        break;
                    }

                    if (segment.Type == TemplatePathSegmentType.Static && IsSensitiveDirectoryName(value))
                    {
                        errors.Add(I18n.Format("Template_Submission_RuleContainsSensitiveSegment", string.IsNullOrWhiteSpace(rule.Name) ? I18n.GetString("Template_Preview_UnnamedRule") : rule.Name, value));
                        break;
                    }
                }

                IEnumerable<TemplatePathMarker> markers = rule.Markers != null
                    ? rule.Markers
                    : Array.Empty<TemplatePathMarker>();

                foreach (var marker in markers)
                {
                    var markerValue = marker?.Value?.Trim() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(markerValue))
                    {
                        continue;
                    }

                    if (markerValue.Contains("..", StringComparison.Ordinal)
                        || markerValue.Contains('\\')
                        || markerValue.Contains('/')
                        || Path.IsPathRooted(markerValue))
                    {
                        errors.Add(I18n.Format("Template_Submission_RuleContainsUnsafeMarker", string.IsNullOrWhiteSpace(rule.Name) ? I18n.GetString("Template_Preview_UnnamedRule") : rule.Name, markerValue));
                        break;
                    }
                }
            }

            return errors;
        }

        private static bool IsSensitiveDirectoryName(string value)
        {
            return string.Equals(value, "Windows", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "System32", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "Program Files", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "Program Files (x86)", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "Users", StringComparison.OrdinalIgnoreCase);
        }

        private static ConfigTemplate CloneTemplate(ConfigTemplate template)
        {
            var json = JsonSerializer.Serialize(template, AppJsonContext.Default.ConfigTemplate);
            return JsonSerializer.Deserialize(json, AppJsonContext.Default.ConfigTemplate) ?? new ConfigTemplate();
        }

        private static ArchiveSettings CloneArchive(ArchiveSettings source)
        {
            var json = JsonSerializer.Serialize(source ?? new ArchiveSettings(), AppJsonContext.Default.ArchiveSettings);
            return JsonSerializer.Deserialize(json, AppJsonContext.Default.ArchiveSettings) ?? new ArchiveSettings();
        }

        private static AutomationSettings CloneAutomation(AutomationSettings source)
        {
            var json = JsonSerializer.Serialize(source ?? new AutomationSettings(), AppJsonContext.Default.AutomationSettings);
            var cloned = JsonSerializer.Deserialize(json, AppJsonContext.Default.AutomationSettings) ?? new AutomationSettings();
            cloned.MigrateFromLegacy();
            return cloned;
        }

        private static FilterSettings CloneFilters(FilterSettings source)
        {
            var json = JsonSerializer.Serialize(source ?? new FilterSettings(), AppJsonContext.Default.FilterSettings);
            var cloned = JsonSerializer.Deserialize(json, AppJsonContext.Default.FilterSettings) ?? new FilterSettings();
            cloned.RestoreWhitelist ??= new ObservableCollection<string>();
            return cloned;
        }

        private static CloudSettings CloneCloud(CloudSettings source)
        {
            var json = JsonSerializer.Serialize(source ?? new CloudSettings(), AppJsonContext.Default.CloudSettings);
            return JsonSerializer.Deserialize(json, AppJsonContext.Default.CloudSettings) ?? new CloudSettings();
        }
    }
}
