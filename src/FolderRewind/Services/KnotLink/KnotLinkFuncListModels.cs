using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FolderRewind.Services.KnotLink
{
    public sealed class KnotLinkFuncList
    {
        [JsonPropertyName("specVersion")]
        public string SpecVersion { get; set; } = "1.0";

        [JsonPropertyName("manifestVersion")]
        public string ManifestVersion { get; set; } = "2.0.0";

        [JsonPropertyName("appName")]
        public string AppName { get; set; } = "FolderRewind";

        [JsonPropertyName("openSocket")]
        public SortedDictionary<string, KnotLinkOpenSocketFunction> OpenSocket { get; set; } = new();

        [JsonPropertyName("signal")]
        public SortedDictionary<string, KnotLinkSignalFunction> Signal { get; set; } = new();
    }

    public sealed class KnotLinkOpenSocketFunction
    {
        [JsonPropertyName("appID")]
        public string AppId { get; set; } = string.Empty;

        [JsonPropertyName("openSocketID")]
        public string OpenSocketId { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("args")]
        public SortedDictionary<string, KnotLinkFuncArgument> Args { get; set; } = new();

        [JsonPropertyName("returns")]
        public List<string[]> Returns { get; set; } = new();
    }

    public sealed class KnotLinkFuncArgument
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "input";

        [JsonPropertyName("value")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Value { get; set; }

        [JsonPropertyName("options")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string[]>? Options { get; set; }

        [JsonPropertyName("description")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Description { get; set; }

        [JsonPropertyName("defaultVal")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? DefaultValue { get; set; }
    }

    public sealed class KnotLinkSignalFunction
    {
        [JsonPropertyName("appID")]
        public string AppId { get; set; } = string.Empty;

        [JsonPropertyName("signalID")]
        public string SignalId { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("returns")]
        public SortedDictionary<string, KnotLinkSignalField> Returns { get; set; } = new();
    }

    public sealed class KnotLinkSignalField
    {
        [JsonPropertyName("description")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Description { get; set; }

        [JsonPropertyName("verification")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Verification { get; set; }
    }

    [JsonSerializable(typeof(KnotLinkFuncList))]
    [JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    internal partial class KnotLinkFuncListJsonContext : JsonSerializerContext
    {
    }
}
