using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json.Serialization;

namespace FolderRewind.Models
{
    public enum TemplatePathSegmentType
    {
        Static = 0,
        Placeholder = 1,
        EnumerateDirectory = 2,
        ProcessDirectory = 3,
        RootPath = 4
    }

    public enum TemplatePathMarkerType
    {
        RequiredDirectory = 0,
        RequiredFile = 1,
        OptionalDirectory = 2,
        OptionalFile = 3
    }

    public class TemplatePathSegment : ObservableObject
    {
        private TemplatePathSegmentType _type;
        private string _value = string.Empty;

        public TemplatePathSegmentType Type { get => _type; set => SetProperty(ref _type, value); }
        public string Value { get => _value; set => SetProperty(ref _value, value ?? string.Empty); }
    }

    public class TemplatePathMarker : ObservableObject
    {
        private TemplatePathMarkerType _type;
        private string _value = string.Empty;

        public TemplatePathMarkerType Type { get => _type; set => SetProperty(ref _type, value); }
        public string Value { get => _value; set => SetProperty(ref _value, value ?? string.Empty); }
    }

    public class TemplatePathRule : ObservableObject
    {
        private string _id = Guid.NewGuid().ToString("N");
        private string _name = string.Empty;
        private ObservableCollection<TemplatePathSegment> _segments = new();
        private ObservableCollection<TemplatePathMarker> _markers = new();
        private double _confidence = 0.8;
        private bool _autoAdd = true;

        public string Id { get => _id; set => SetProperty(ref _id, value ?? string.Empty); }

        public string Name { get => _name; set => SetProperty(ref _name, value ?? string.Empty); }

        public ObservableCollection<TemplatePathSegment> Segments
        {
            get => _segments;
            set => SetProperty(ref _segments, value ?? new ObservableCollection<TemplatePathSegment>());
        }

        public ObservableCollection<TemplatePathMarker> Markers
        {
            get => _markers;
            set => SetProperty(ref _markers, value ?? new ObservableCollection<TemplatePathMarker>());
        }

        public double Confidence { get => _confidence; set => SetProperty(ref _confidence, value); }

        public bool AutoAdd { get => _autoAdd; set => SetProperty(ref _autoAdd, value); }

        [JsonIgnore]
        // DisplayPath 是给 UI 编辑和导出展示用的“可读语法”，真正执行以 Segments 为准。
        public string DisplayPath => string.Join("\\", Segments.Select(FormatSegment));

        private static string FormatSegment(TemplatePathSegment segment)
        {
            if (segment == null)
            {
                return string.Empty;
            }

            return segment.Type switch
            {
                TemplatePathSegmentType.Placeholder => "{" + segment.Value + "}",
                TemplatePathSegmentType.ProcessDirectory => "{Process:" + segment.Value + "}",
                TemplatePathSegmentType.EnumerateDirectory => "{" + (string.IsNullOrWhiteSpace(segment.Value) ? "*" : segment.Value) + "}",
                TemplatePathSegmentType.RootPath => segment.Value,
                _ => segment.Value
            };
        }
    }

    public enum BackupPresetDiscoverySourceKind
    {
        InlinePathRules = 0,
        ProviderReference = 1
    }

    public class BackupPresetDiscoverySource : ObservableObject
    {
        private BackupPresetDiscoverySourceKind _kind;
        private string _providerId = string.Empty;
        private string _definitionId = string.Empty;
        private ObservableCollection<TemplatePathRule> _pathRules = new();
        private Dictionary<string, string> _externalIds = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, string> _properties = new(StringComparer.OrdinalIgnoreCase);

        public BackupPresetDiscoverySourceKind Kind { get => _kind; set => SetProperty(ref _kind, value); }
        public string ProviderId { get => _providerId; set => SetProperty(ref _providerId, value?.Trim() ?? string.Empty); }
        public string DefinitionId { get => _definitionId; set => SetProperty(ref _definitionId, value?.Trim() ?? string.Empty); }
        public ObservableCollection<TemplatePathRule> PathRules
        {
            get => _pathRules;
            set => SetProperty(ref _pathRules, value ?? new ObservableCollection<TemplatePathRule>());
        }
        public Dictionary<string, string> ExternalIds
        {
            get => _externalIds;
            set => SetProperty(ref _externalIds, value == null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(value, StringComparer.OrdinalIgnoreCase));
        }
        public Dictionary<string, string> Properties
        {
            get => _properties;
            set => SetProperty(ref _properties, value == null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(value, StringComparer.OrdinalIgnoreCase));
        }
    }

    public class BackupPreset : ObservableObject
    {
        private string _id = Guid.NewGuid().ToString("N");
        private string _shareId = Guid.NewGuid().ToString("N");
        private string _shareCode = string.Empty;
        private string _name = string.Empty;
        private string _author = string.Empty;
        private string _description = string.Empty;
        private string _gameName = string.Empty;
        private int? _steamAppId;
        private string _version = "1.0";
        private int _schemaVersion = 1;
        private ConfigKindReference _kind = new();
        private bool _isEncrypted;
        private string _iconGlyph = "\uE8B7";
        private string _defaultConfigName = string.Empty;
        private DateTime _createdUtc = DateTime.UtcNow;
        private DateTime _updatedUtc = DateTime.UtcNow;
        private ArchiveSettings _archive = new();
        private AutomationSettings _automation = new();
        private FilterSettings _filters = new();
        private BackupScopeSettings _backupScope = new();
        private CloudSettings _cloud = new();
        private ObservableCollection<string> _requiredPluginIds = new();
        private ObservableCollection<TemplatePathRule> _pathRules = new();
        private ObservableCollection<BackupPresetDiscoverySource> _discoverySources = new();
        private bool _isBuiltIn;
        private bool _isRecommended;

        public string Id { get => _id; set => SetProperty(ref _id, value ?? string.Empty); }
        public string ShareId { get => _shareId; set => SetProperty(ref _shareId, value ?? string.Empty); }
        public string ShareCode { get => _shareCode; set => SetProperty(ref _shareCode, value ?? string.Empty); }
        public string Name { get => _name; set => SetProperty(ref _name, value ?? string.Empty); }
        public string Author { get => _author; set => SetProperty(ref _author, value ?? string.Empty); }
        public string Description { get => _description; set => SetProperty(ref _description, value ?? string.Empty); }
        public string GameName { get => _gameName; set => SetProperty(ref _gameName, value ?? string.Empty); }
        public int? SteamAppId { get => _steamAppId; set => SetProperty(ref _steamAppId, value); }
        public string Version { get => _version; set => SetProperty(ref _version, value ?? "1.0"); }
        public int SchemaVersion { get => _schemaVersion; set => SetProperty(ref _schemaVersion, value); }
        public ConfigKindReference Kind { get => _kind; set => SetProperty(ref _kind, value ?? new ConfigKindReference()); }
        [JsonIgnore]
        public string KindDisplay => $"{Kind.OwnerId}/{Kind.KindId}";
        public bool IsEncrypted { get => _isEncrypted; set => SetProperty(ref _isEncrypted, value); }
        public string IconGlyph { get => _iconGlyph; set => SetProperty(ref _iconGlyph, value ?? string.Empty); }
        public string DefaultConfigName { get => _defaultConfigName; set => SetProperty(ref _defaultConfigName, value ?? string.Empty); }
        public DateTime CreatedUtc { get => _createdUtc; set => SetProperty(ref _createdUtc, value); }
        public DateTime UpdatedUtc { get => _updatedUtc; set => SetProperty(ref _updatedUtc, value); }

        public ArchiveSettings Archive
        {
            get => _archive;
            set => SetProperty(ref _archive, value ?? new ArchiveSettings());
        }

        public AutomationSettings Automation
        {
            get => _automation;
            set => SetProperty(ref _automation, value ?? new AutomationSettings());
        }

        public FilterSettings Filters
        {
            get => _filters;
            set => SetProperty(ref _filters, value ?? new FilterSettings());
        }

        public BackupScopeSettings BackupScope
        {
            get => _backupScope;
            set => SetProperty(ref _backupScope, value ?? new BackupScopeSettings());
        }

        public CloudSettings Cloud
        {
            get => _cloud;
            set => SetProperty(ref _cloud, value ?? new CloudSettings());
        }

        public ObservableCollection<string> RequiredPluginIds
        {
            get => _requiredPluginIds;
            set => SetProperty(ref _requiredPluginIds, value ?? new ObservableCollection<string>());
        }

        public Dictionary<string, PresetProviderDefaults> ProviderDefaults { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);

        public ObservableCollection<TemplatePathRule> PathRules
        {
            get => _pathRules;
            set => SetProperty(ref _pathRules, value ?? new ObservableCollection<TemplatePathRule>());
        }

        public ObservableCollection<BackupPresetDiscoverySource> DiscoverySources
        {
            get => _discoverySources;
            set => SetProperty(ref _discoverySources, value ?? new ObservableCollection<BackupPresetDiscoverySource>());
        }

        [JsonIgnore]
        public bool IsBuiltIn { get => _isBuiltIn; set => SetProperty(ref _isBuiltIn, value); }
        public bool IsRecommended { get => _isRecommended; set => SetProperty(ref _isRecommended, value); }

        public void NormalizeDiscoverySources()
        {
            DiscoverySources ??= new ObservableCollection<BackupPresetDiscoverySource>();
            PathRules ??= new ObservableCollection<TemplatePathRule>();

            var inlineSource = DiscoverySources.FirstOrDefault(source =>
                source?.Kind == BackupPresetDiscoverySourceKind.InlinePathRules);
            if (inlineSource == null && PathRules.Count > 0)
            {
                DiscoverySources.Insert(0, new BackupPresetDiscoverySource
                {
                    Kind = BackupPresetDiscoverySourceKind.InlinePathRules,
                    PathRules = PathRules
                });
            }
            else if (inlineSource != null)
            {
                inlineSource.PathRules ??= new ObservableCollection<TemplatePathRule>();
                if (PathRules.Count == 0)
                {
                    PathRules = inlineSource.PathRules;
                }
            }
        }
    }

    public class TemplateRulePreviewItem : ObservableObject
    {
        private string _ruleId = string.Empty;
        private string _ruleName = string.Empty;
        private string _pattern = string.Empty;
        private string _statusText = string.Empty;
        private string _matchSummary = string.Empty;
        private string _samplePath = string.Empty;
        private string _markerSummary = string.Empty;

        public string RuleId { get => _ruleId; set => SetProperty(ref _ruleId, value ?? string.Empty); }
        public string RuleName { get => _ruleName; set => SetProperty(ref _ruleName, value ?? string.Empty); }
        public string Pattern { get => _pattern; set => SetProperty(ref _pattern, value ?? string.Empty); }
        public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value ?? string.Empty); }
        public string MatchSummary { get => _matchSummary; set => SetProperty(ref _matchSummary, value ?? string.Empty); }
        public string SamplePath { get => _samplePath; set => SetProperty(ref _samplePath, value ?? string.Empty); }
        public string MarkerSummary { get => _markerSummary; set => SetProperty(ref _markerSummary, value ?? string.Empty); }
    }

    public class TemplateRuleSyntaxHelpItem : ObservableObject
    {
        private string _title = string.Empty;
        private string _description = string.Empty;
        private string _example = string.Empty;

        public string Title { get => _title; set => SetProperty(ref _title, value ?? string.Empty); }
        public string Description { get => _description; set => SetProperty(ref _description, value ?? string.Empty); }
        public string Example { get => _example; set => SetProperty(ref _example, value ?? string.Empty); }
    }

    public class TemplateShareEnvelope
    {
        public string Magic { get; set; } = string.Empty;
        public string SchemaVersion { get; set; } = string.Empty;
        public DateTime ExportedAtUtc { get; set; } = DateTime.UtcNow;
        public BackupPreset Template { get; set; } = new();
    }

    public class BackupPresetShareEnvelope
    {
        public string Magic { get; set; } = string.Empty;
        public string SchemaVersion { get; set; } = string.Empty;
        public DateTime ExportedAtUtc { get; set; } = DateTime.UtcNow;
        public BackupPreset Preset { get; set; } = new();
    }

    public class RemoteTemplateIndexItem
    {
        [JsonPropertyName("shareId")]
        public string ShareId { get; set; } = string.Empty;

        [JsonPropertyName("shareCode")]
        public string ShareCode { get; set; } = string.Empty;

        [JsonPropertyName("templateId")]
        public string TemplateId { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("author")]
        public string Author { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("gameName")]
        public string GameName { get; set; } = string.Empty;

        [JsonPropertyName("steamAppId")]
        public int? SteamAppId { get; set; }

        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;

        [JsonPropertyName("updatedUtc")]
        public DateTime UpdatedUtc { get; set; }

        [JsonPropertyName("baseConfigType")]
        public string BaseConfigType { get; set; } = string.Empty;

        [JsonPropertyName("requiredPluginIds")]
        public ObservableCollection<string> RequiredPluginIds { get; set; } = new();

        [JsonPropertyName("fileUrl")]
        public string FileUrl { get; set; } = string.Empty;

        [JsonPropertyName("contentPath")]
        public string ContentPath { get; set; } = string.Empty;

        [JsonPropertyName("matches")]
        public ObservableCollection<RemoteBackupPresetMatchKey> Matches { get; set; } = new();

        [JsonPropertyName("isRecommended")]
        public bool IsRecommended { get; set; }

        [JsonPropertyName("sha256")]
        public string Sha256 { get; set; } = string.Empty;

        [JsonPropertyName("isDisabled")]
        public bool IsDisabled { get; set; }

        [JsonIgnore]
        public bool IsV2 { get; set; }

        [JsonIgnore]
        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(GameName) && !string.Equals(GameName, Name, StringComparison.OrdinalIgnoreCase))
                {
                    return $"{GameName} - {Name}";
                }

                return Name;
            }
        }
    }

    public class RemoteBackupPresetMatchKey
    {
        [JsonPropertyName("providerId")]
        public string ProviderId { get; set; } = string.Empty;

        [JsonPropertyName("definitionId")]
        public string DefinitionId { get; set; } = string.Empty;

        [JsonPropertyName("externalIds")]
        public Dictionary<string, string> ExternalIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public class RemoteBackupPresetIndexDocument
    {
        [JsonPropertyName("magic")]
        public string Magic { get; set; } = string.Empty;

        [JsonPropertyName("schemaVersion")]
        public string SchemaVersion { get; set; } = string.Empty;

        [JsonPropertyName("generatedAtUtc")]
        public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;

        [JsonPropertyName("presets")]
        public ObservableCollection<RemoteTemplateIndexItem> Presets { get; set; } = new();
    }

    public class RemoteTemplateIndexDocument
    {
        [JsonPropertyName("schemaVersion")]
        public string SchemaVersion { get; set; } = string.Empty;

        [JsonPropertyName("generatedAtUtc")]
        public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;

        [JsonPropertyName("templates")]
        private ObservableCollection<RemoteTemplateIndexItem> _templates = new();

        public ObservableCollection<RemoteTemplateIndexItem> Templates
        {
            get => _templates;
            set => _templates = value ?? new ObservableCollection<RemoteTemplateIndexItem>();
        }
    }
}
