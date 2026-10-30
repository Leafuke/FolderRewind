using System.Text.Json;
using System.Text.Json.Serialization;

namespace FolderRewind.Plugin.Abstractions;

public enum PathFilterTarget { Any = 0, File = 1, Directory = 2 }

/// <summary>An immutable filter. Version 1 preserves the original string-rule semantics; version 2 uses typed relative globs.</summary>
[JsonConverter(typeof(PathFilterRuleJsonConverter))]
public sealed record PathFilterRule(string Pattern, PathFilterTarget Target = PathFilterTarget.Any, int SemanticsVersion = 2)
{
    public static PathFilterRule Legacy(string pattern) => new(pattern, PathFilterTarget.Any, 1);
    public override string ToString() => Pattern;
}

public sealed class PathFilterRuleJsonConverter : JsonConverter<PathFilterRule>
{
    public override PathFilterRule Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return PathFilterRule.Legacy(reader.GetString()!);
        using var document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("A filter must be a string or a rule object.");
        string? pattern = null;
        PathFilterTarget? target = null;
        var version = 2;
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Name.Equals("Pattern", StringComparison.OrdinalIgnoreCase)) pattern = property.Value.GetString();
            else if (property.Name.Equals("Target", StringComparison.OrdinalIgnoreCase))
            {
                if (property.Value.ValueKind == JsonValueKind.String && Enum.TryParse<PathFilterTarget>(property.Value.GetString(), true, out var parsed)) target = parsed;
                else if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var number)) target = (PathFilterTarget)number;
                else throw new JsonException("Invalid filter target.");
            }
            else if (property.Name.Equals("SemanticsVersion", StringComparison.OrdinalIgnoreCase)) version = property.Value.GetInt32();
        }
        if (pattern is null || target is null || !Enum.IsDefined(target.Value) || version is not (1 or 2)
            || version == 1 && target != PathFilterTarget.Any) throw new JsonException("Invalid filter rule.");
        return new(pattern, target.Value, version);
    }

    public override void Write(Utf8JsonWriter writer, PathFilterRule value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("Pattern", value.Pattern);
        writer.WriteString("Target", value.Target.ToString());
        writer.WriteNumber("SemanticsVersion", value.SemanticsVersion);
        writer.WriteEndObject();
    }
}
