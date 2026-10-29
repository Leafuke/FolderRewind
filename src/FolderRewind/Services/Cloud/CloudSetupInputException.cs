using System;

namespace FolderRewind.Services;

internal sealed class CloudSetupInputException(string resourceKey) : InvalidOperationException(I18n.GetString(resourceKey))
{
    internal string ResourceKey { get; } = resourceKey;
}
