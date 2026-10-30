using System;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace FolderRewind.Services;

internal static class JsonCloneService
{
    public static T Clone<T>(T source, JsonTypeInfo<T> typeInfo, string? errorMessage = null)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(typeInfo);

        var element = JsonSerializer.SerializeToElement(source, typeInfo);
        return element.Deserialize(typeInfo)
            ?? throw new InvalidOperationException(errorMessage ?? $"Failed to clone {typeof(T).Name}.");
    }
}
