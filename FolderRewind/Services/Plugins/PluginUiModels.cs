using System;
using System.Collections.Generic;

namespace FolderRewind.Services.Plugins;

/// <summary>
/// v3 FormSchema 到 Host 设置界面的只读投影，不属于公共插件契约。
/// </summary>
public enum PluginFormFieldType
{
    String = 0,
    Boolean = 1,
    Integer = 2,
    MultilineString = 3
}

public sealed class PluginFormFieldDefinition
{
    public string Key { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? Description { get; init; }
    public PluginFormFieldType Type { get; init; }
    public string? DefaultValue { get; init; }
    public bool IsRequired { get; init; }
}

public sealed class PluginBackupScopeDefinition
{
    public string OwnerId { get; init; } = string.Empty;
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? Description { get; init; }
    public IReadOnlyList<PluginFormFieldDefinition> Parameters { get; init; } =
        Array.Empty<PluginFormFieldDefinition>();
}

public sealed class PluginBackupScopeValidationResult
{
    public bool Success { get; init; }
    public string ErrorCode { get; init; } = string.Empty;
    public string ErrorMessage { get; init; } = string.Empty;
}
