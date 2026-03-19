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
        EnumerateDirectory = 2
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
        private double _confidence = 0.5;
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
                TemplatePathSegmentType.EnumerateDirectory => "{" + (string.IsNullOrWhiteSpace(segment.Value) ? "*" : segment.Value) + "}",
                _ => segment.Value
            };
        }
    }

    public class ConfigTemplate : ObservableObject
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
        private string _baseConfigType = "Default";
        private bool _isEncrypted;
        private string _iconGlyph = "\uE8B7";
        private string _defaultConfigName = string.Empty;
        private DateTime _createdUtc = DateTime.UtcNow;
        private DateTime _updatedUtc = DateTime.UtcNow;
        private ArchiveSettings _archive = new();
        private AutomationSettings _automation = new();
        private FilterSettings _filters = new();
        private CloudSettings _cloud = new();
        private Dictionary<string, string> _extendedProperties = new();
        private ObservableCollection<string> _requiredPluginIds = new();
        private ObservableCollection<TemplatePathRule> _pathRules = new();

        public string Id { get => _id; set => SetProperty(ref _id, value ?? string.Empty); }
        public string ShareId { get => _shareId; set => SetProperty(ref _shareId, value ?? string.Empty); }
        public string TemplateId { get => _shareId; set => SetProperty(ref _shareId, value ?? string.Empty); }
        public string ShareCode { get => _shareCode; set => SetProperty(ref _shareCode, value ?? string.Empty); }
        public string Name { get => _name; set => SetProperty(ref _name, value ?? string.Empty); }
        public string Author { get => _author; set => SetProperty(ref _author, value ?? string.Empty); }
        public string Description { get => _description; set => SetProperty(ref _description, value ?? string.Empty); }
        public string GameName { get => _gameName; set => SetProperty(ref _gameName, value ?? string.Empty); }
        public int? SteamAppId { get => _steamAppId; set => SetProperty(ref _steamAppId, value); }
        public string Version { get => _version; set => SetProperty(ref _version, value ?? "1.0"); }
        public string BaseConfigType { get => _baseConfigType; set => SetProperty(ref _baseConfigType, value ?? "Default"); }
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

        public CloudSettings Cloud
        {
            get => _cloud;
            set => SetProperty(ref _cloud, value ?? new CloudSettings());
        }

        public Dictionary<string, string> ExtendedProperties
        {
            get => _extendedProperties;
            set => SetProperty(ref _extendedProperties, value ?? new Dictionary<string, string>());
        }

        public ObservableCollection<string> RequiredPluginIds
        {
            get => _requiredPluginIds;
            set => SetProperty(ref _requiredPluginIds, value ?? new ObservableCollection<string>());
        }

        public ObservableCollection<TemplatePathRule> PathRules
        {
            get => _pathRules;
            set => SetProperty(ref _pathRules, value ?? new ObservableCollection<TemplatePathRule>());
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

    public class TemplateShareEnvelope
    {
        public string Magic { get; set; } = "FolderRewindTemplate";
        public string SchemaVersion { get; set; } = "1.0";
        public DateTime ExportedAtUtc { get; set; } = DateTime.UtcNow;
        public ConfigTemplate Template { get; set; } = new();
    }

    public class RemoteTemplateIndexItem
    {
        public string ShareCode { get; set; } = string.Empty;
        public string TemplateId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Author { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string GameName { get; set; } = string.Empty;
        public int? SteamAppId { get; set; }
        public string Version { get; set; } = string.Empty;
        public DateTime UpdatedUtc { get; set; }
        public string BaseConfigType { get; set; } = string.Empty;
        public ObservableCollection<string> RequiredPluginIds { get; set; } = new();
        public string FileUrl { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;
        public bool IsDisabled { get; set; }

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

    public class RemoteTemplateIndexDocument
    {
        public string SchemaVersion { get; set; } = "1.0";
        public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
        public ObservableCollection<RemoteTemplateIndexItem> Templates { get; set; } = new();
    }
}
