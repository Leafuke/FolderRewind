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
        private const string ShareMagic = TemplateFormatPolicy.BackupPresetMagic;
        private const string ShareSchemaVersion = TemplateFormatPolicy.BackupPresetSchemaVersion;
        // 规则预览会扫目录，给个上限避免某些磁盘结构把 UI 卡死。
        private const int ScanDepthLimit = 6;
        private const int ScanDirectoryLimit = 5000;
        private const int MarkerSearchDepth = 1;
        public const string ShareFileExtension = ".frpreset.json";
        public const string LegacyShareFileExtension = ".frtemplate.json";

        private static readonly string[] KnownMarkerDirectories =
        {
            "saves",
            "save",
            "savegames",
            "profiles",
            "userdata",
            "slot",
            "remote",
            "savedata"
        };

        private static readonly string[] KnownMarkerFiles =
        {
            "level.dat",
            "profile.json",
            "savegame.sav",
            "globalgamemanagers",
            "steam_autocloud.vdf"
        };

        private static readonly Regex GuidRegex = new(
            "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
            RegexOptions.Compiled);

        private static readonly Regex SidRegex = new(
            "^S-1-5-[0-9-]+$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex LongDigitsRegex = new("^[0-9]{8,20}$", RegexOptions.Compiled);
        private static readonly Regex LongHexRegex = new("^[0-9a-fA-F]{16,32}$", RegexOptions.Compiled);
        private static readonly Regex DriveRootRegex = new("^[A-Za-z]:$", RegexOptions.Compiled);

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
            public string MarkerSummary { get; init; } = string.Empty;
            public double Confidence { get; init; }
            public bool IsSelectedByDefault { get; init; }
        }

        public sealed class TemplateImportInspectionResult
        {
            public bool Success { get; init; }
            public string Message { get; init; } = string.Empty;
            public BackupPreset? Template { get; init; }
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

        public sealed class TemplateRuleEditItem
        {
            public string Id { get; init; } = string.Empty;
            public string Name { get; init; } = string.Empty;
            public string DisplayPath { get; init; } = string.Empty;
            public string FileMatchList { get; init; } = string.Empty;
            public double Confidence { get; init; }
            public bool AutoAdd { get; init; }
        }

        public static IReadOnlyList<BackupPreset> GetTemplates()
        {
            return ConfigService.CurrentConfig?.BackupPresets?.ToList() ?? new List<BackupPreset>();
        }

        public static BackupPreset CreateStandardGamePreset()
        {
            return new BackupPreset
            {
                Id = "builtin.standard-game",
                ShareId = "builtin.standard-game",
                Name = I18n.GetString("BackupPreset_StandardGame_Name"),
                Description = I18n.GetString("BackupPreset_StandardGame_Description"),
                Version = "1.0",
                IsBuiltIn = true,
                Archive = new ArchiveSettings(),
                Automation = new AutomationSettings(),
                Filters = new FilterSettings(),
                BackupScope = new BackupScopeSettings(),
                Cloud = new CloudSettings()
            };
        }

        public static IReadOnlyList<TemplateRuleEditItem> BuildRuleEditItems(BackupPreset? template)
        {
            if (template?.PathRules == null)
            {
                return Array.Empty<TemplateRuleEditItem>();
            }

            return template.PathRules.Select(rule => new TemplateRuleEditItem
            {
                Id = rule.Id,
                Name = rule.Name,
                DisplayPath = rule.DisplayPath,
                FileMatchList = string.Join(";",
                    (rule.Markers ?? new ObservableCollection<TemplatePathMarker>())
                        .Where(marker => marker?.Type == TemplatePathMarkerType.RequiredFile)
                        .Select(marker => Path.GetFileName(marker?.Value ?? string.Empty))
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .Distinct(StringComparer.OrdinalIgnoreCase)),
                Confidence = rule.Confidence,
                AutoAdd = rule.AutoAdd
            }).ToList();
        }

        public static IReadOnlyList<TemplateRuleSyntaxHelpItem> GetRuleSyntaxHelpItems()
        {
            return new[]
            {
                new TemplateRuleSyntaxHelpItem
                {
                    Title = I18n.GetString("Template_SyntaxHelp_Variables_Title"),
                    Description = I18n.GetString("Template_SyntaxHelp_Variables_Description"),
                    Example = I18n.GetString("Template_SyntaxHelp_Variables_Example")
                },
                new TemplateRuleSyntaxHelpItem
                {
                    Title = I18n.GetString("Template_SyntaxHelp_Wildcard_Title"),
                    Description = I18n.GetString("Template_SyntaxHelp_Wildcard_Description"),
                    Example = I18n.GetString("Template_SyntaxHelp_Wildcard_Example")
                },
                new TemplateRuleSyntaxHelpItem
                {
                    Title = I18n.GetString("Template_SyntaxHelp_Process_Title"),
                    Description = I18n.GetString("Template_SyntaxHelp_Process_Description"),
                    Example = I18n.GetString("Template_SyntaxHelp_Process_Example")
                },
                new TemplateRuleSyntaxHelpItem
                {
                    Title = I18n.GetString("Template_SyntaxHelp_Root_Title"),
                    Description = I18n.GetString("Template_SyntaxHelp_Root_Description"),
                    Example = I18n.GetString("Template_SyntaxHelp_Root_Example")
                },
                new TemplateRuleSyntaxHelpItem
                {
                    Title = I18n.GetString("Template_SyntaxHelp_FileMatch_Title"),
                    Description = I18n.GetString("Template_SyntaxHelp_FileMatch_Description"),
                    Example = I18n.GetString("Template_SyntaxHelp_FileMatch_Example")
                }
            };
        }

        public static BackupPreset? GetTemplateById(string? templateId)
        {
            if (string.IsNullOrWhiteSpace(templateId))
            {
                return null;
            }

            return ConfigService.CurrentConfig?.BackupPresets?
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
            if (appConfig?.BackupPresets == null)
            {
                message = I18n.GetString("Template_Create_ConfigUnavailable");
                return false;
            }

            if (string.IsNullOrWhiteSpace(templateName))
            {
                message = I18n.GetString("Template_Update_NameRequired");
                return false;
            }

            var template = appConfig.BackupPresets.FirstOrDefault(t => string.Equals(t.Id, templateId, StringComparison.OrdinalIgnoreCase));
            if (template == null)
            {
                message = I18n.GetString("Template_Update_TemplateNotFound");
                return false;
            }

            var finalName = templateName.Trim();
            var hasConflict = appConfig.BackupPresets.Any(t =>
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

        public static (bool Success, string Message, BackupPreset? Template) DuplicateTemplate(string templateId)
        {
            var appConfig = ConfigService.CurrentConfig;
            if (appConfig?.BackupPresets == null)
            {
                return (false, I18n.GetString("Template_Create_ConfigUnavailable"), null);
            }

            var source = appConfig.BackupPresets.FirstOrDefault(t => string.Equals(t.Id, templateId, StringComparison.OrdinalIgnoreCase));
            if (source == null)
            {
                return (false, I18n.GetString("Template_Duplicate_TemplateNotFound"), null);
            }

            var clone = CloneTemplate(source);
            clone.Id = Guid.NewGuid().ToString("N");
            clone.ShareId = Guid.NewGuid().ToString("N");
            clone.CreatedUtc = DateTime.UtcNow;
            clone.UpdatedUtc = DateTime.UtcNow;
            clone.Name = BuildCopyTemplateName(source.Name, appConfig.BackupPresets);

            appConfig.BackupPresets.Add(clone);
            ConfigService.Save();

            return (true, I18n.Format("Template_Duplicate_Success", clone.Name), clone);
        }

        public static bool DeleteTemplate(string templateId, out string message)
        {
            message = string.Empty;
            var appConfig = ConfigService.CurrentConfig;
            if (appConfig?.BackupPresets == null)
            {
                message = I18n.GetString("Template_Create_ConfigUnavailable");
                return false;
            }

            var template = appConfig.BackupPresets.FirstOrDefault(t => string.Equals(t.Id, templateId, StringComparison.OrdinalIgnoreCase));
            if (template == null)
            {
                message = I18n.GetString("Template_Delete_TemplateNotFound");
                return false;
            }

            if (!appConfig.BackupPresets.Remove(template))
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
            if (!PluginService.GetAllSupportedConfigKinds(includeEncrypted: true).Any(option =>
                    string.Equals(option.Kind.OwnerId, template.Kind.OwnerId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(option.Kind.KindId, template.Kind.KindId, StringComparison.OrdinalIgnoreCase)
                    && option.IsEncrypted == template.IsEncrypted))
            {
                message = message + " " + I18n.Format(
                    "Template_ConfigKindUnavailable",
                    $"{template.Kind.OwnerId}/{template.Kind.KindId}");
            }

            return new TemplatePreviewResult
            {
                Success = true,
                Message = message,
                Items = items
            };
        }

        public static (bool Success, string Message, BackupPreset? Template) UpsertTemplateFromConfig(
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
            if (appConfig?.BackupPresets == null)
            {
                return (false, I18n.GetString("Template_Create_ConfigUnavailable"), null);
            }

            var now = DateTime.UtcNow;
            var existing = appConfig.BackupPresets
                .FirstOrDefault(t => string.Equals(t.Name, templateName.Trim(), StringComparison.OrdinalIgnoreCase));

            var template = existing ?? new BackupPreset
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
            // 模板沿用稳定 Config Kind；本地化名称不能充当插件身份。
            template.Kind = new ConfigKindReference
            {
                OwnerId = sourceConfig.Kind?.OwnerId ?? FolderRewind.Plugin.Runtime.Configuration.ConfigSchema.CoreOwnerId,
                KindId = sourceConfig.Kind?.KindId ?? FolderRewind.Plugin.Runtime.Configuration.ConfigSchema.CoreDefaultKindId
            };
            template.IsEncrypted = sourceConfig.IsEncrypted;
            template.IconGlyph = sourceConfig.IconGlyph;
            template.DefaultConfigName = sourceConfig.Name;
            template.Version = string.IsNullOrWhiteSpace(template.Version) ? "1.0" : template.Version;
            template.UpdatedUtc = now;

            template.Archive = CloneArchive(sourceConfig.Archive);
            // 模板要复用“策略”，不要顺手把用户本机的自动任务状态也打包进去。
            template.Automation = CreateTemplateAutomationPreset(sourceConfig.Automation);
            template.Filters = CloneFilters(sourceConfig.Filters);
            template.BackupScope = CloneBackupScope(sourceConfig.BackupScope);
            // 云同步这类配置很容易带出本地路径和远端地址，分享时宁可保守一点。
            template.Cloud = CreateTemplateCloudPreset();
            var requiredPlugins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(sourceConfig.RequiredPluginId))
            {
                requiredPlugins.Add(sourceConfig.RequiredPluginId);
            }
            template.RequiredPluginIds = new ObservableCollection<string>(requiredPlugins.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));

            template.PathRules = InferPathRules(sourceConfig);
            template.NormalizeDiscoverySources();
            if (template.PathRules.Count == 0)
            {
                // 没有可推断规则时不阻断创建，模板依旧可复用策略参数。
                LogService.Log(I18n.GetString("Template_Create_NoPathRules"), LogLevel.Warning);
            }

            if (existing == null)
            {
                appConfig.BackupPresets.Add(template);
            }

            ConfigService.Save();

            var messageKey = existing == null ? "Template_Create_Success" : "Template_Create_OverwriteSuccess";
            return (true, I18n.Format(messageKey, template.Name), template);
        }

    }
}
