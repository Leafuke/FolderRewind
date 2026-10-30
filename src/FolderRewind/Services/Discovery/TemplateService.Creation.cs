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
        public static CreateConfigFromTemplateResult CreateConfigFromTemplate(
            BackupPreset template,
            string configName,
            PluginConfigKindOption? kindOverride = null)
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

            var useEncrypted = kindOverride?.IsEncrypted ?? template.IsEncrypted;
            var kindOptions = PluginService.GetAllSupportedConfigKinds(includeEncrypted: true);
            var selectedKind = kindOverride
                ?? kindOptions.FirstOrDefault(option =>
                    string.Equals(option.Kind.OwnerId, template.Kind?.OwnerId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(option.Kind.KindId, template.Kind?.KindId, StringComparison.OrdinalIgnoreCase)
                    && option.IsEncrypted == useEncrypted);

            if (selectedKind is null)
            {
                return new CreateConfigFromTemplateResult
                {
                    Success = false,
                    Message = I18n.Format(
                        "Template_ConfigKindUnavailable",
                        $"{template.Kind.OwnerId}/{template.Kind.KindId}")
                };
            }

            var config = new BackupConfig
            {
                Name = finalName,
                DestinationPath = ConfigService.BuildDefaultDestinationPath(finalName),
                Kind = selectedKind.CreateReference(),
                RequiredPluginId = selectedKind.RequiredPluginId
                    ?? template.RequiredPluginIds.FirstOrDefault()
                    ?? string.Empty,
                IsEncrypted = useEncrypted,
                IconGlyph = string.IsNullOrWhiteSpace(template.IconGlyph) ? "\uE8B7" : template.IconGlyph,
                SummaryText = string.Empty,
                Archive = CloneArchive(template.Archive),
                Automation = CloneAutomation(template.Automation),
                Filters = CloneFilters(template.Filters),
                BackupScope = CloneBackupScope(template.BackupScope),
                Cloud = CloneCloud(template.Cloud),
                HostOrigin = new HostConfigOrigin
                {
                    TemplateId = template.Id,
                    TemplateName = template.Name
                }
            };

            // 这里先生成候选项，不直接写进 Config.SourceFolders。
            // 游戏模板的推断再聪明，也不该替用户静默决定最终要备份哪些目录。
            var candidates = new List<TemplateFolderCandidate>();
            var folderPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rule in template.PathRules.OrderByDescending(r => r.Confidence))
            {
                // 规则按置信度从高到低展开，尽量让更像“标准存档路径”的结果排在前面。
                var resolvedPaths = ResolveRulePaths(rule).ToList();
                foreach (var path in resolvedPaths)
                {
                    if (!folderPaths.Add(path))
                    {
                        continue;
                    }

                    candidates.Add(new TemplateFolderCandidate
                    {
                        Path = path,
                        DisplayName = FolderNameConflictService.ResolveDisplayName(null, path),
                        RuleName = string.IsNullOrWhiteSpace(rule.Name) ? I18n.GetString("Template_Preview_UnnamedRule") : rule.Name,
                        MarkerSummary = BuildMarkerSummary(rule.Markers),
                        Confidence = rule.Confidence,
                        IsSelectedByDefault = rule.AutoAdd
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

        public static IReadOnlyList<string> GetMissingRequiredPluginIds(BackupPreset? template)
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

        public static TemplateValidationResult ValidateTemplateForOfficialSharing(BackupPreset? template)
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

            template.NormalizeDiscoverySources();
            var pathRules = template.PathRules ?? new ObservableCollection<TemplatePathRule>();
            var validProviderReferences = template.DiscoverySources
                .Where(source => source?.Kind == BackupPresetDiscoverySourceKind.ProviderReference)
                .Where(source => !string.IsNullOrWhiteSpace(source.ProviderId)
                    && !string.IsNullOrWhiteSpace(source.DefinitionId))
                .ToList();

            if (pathRules.Count == 0
                && validProviderReferences.Count == 0)
            {
                errors.Add(I18n.GetString("Template_Submission_PathRulesRequired"));
            }
            else if (pathRules.Count > 0)
            {
                foreach (var issue in ValidatePathRules(pathRules))
                {
                    errors.Add(issue);
                }
            }

            if (!PluginService.GetAllSupportedConfigKinds(includeEncrypted: true).Any(option =>
                    string.Equals(option.Kind.OwnerId, template.Kind.OwnerId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(option.Kind.KindId, template.Kind.KindId, StringComparison.OrdinalIgnoreCase)
                    && option.IsEncrypted == template.IsEncrypted))
            {
                errors.Add(I18n.Format(
                    "Template_ConfigKindUnavailable",
                    $"{template.Kind.OwnerId}/{template.Kind.KindId}"));
            }

            // 提交前做一次“干跑”，尽早发现规则在当前机器上无法解析的问题。
            var dryRun = CreateConfigFromTemplate(template, template.DefaultConfigName);
            if (!dryRun.Success && pathRules.Count > 0)
            {
                errors.Add(string.IsNullOrWhiteSpace(dryRun.Message)
                    ? I18n.GetString("Template_Submission_DryRunFailed")
                    : dryRun.Message);
            }
            else if (pathRules.Count == 0 && validProviderReferences.Count > 0)
            {
                warnings.Add(I18n.GetString("Template_Apply_SuccessNoFolders"));
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

    }
}
