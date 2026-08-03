using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.Services.Plugins;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;

namespace FolderRewind.ViewModels
{
    public sealed class AutomationFolderOption
    {
        public string Path { get; init; } = string.Empty;

        public string DisplayName { get; init; } = string.Empty;
    }

    public sealed class BackupScopeOption
    {
        public BackupScopeOption(string id, string displayName, string description, PluginBackupScopeDefinition? definition)
        {
            Id = id ?? string.Empty;
            DisplayName = displayName ?? string.Empty;
            Description = description ?? string.Empty;
            Definition = definition;
        }

        public string Id { get; }

        public string DisplayName { get; }

        public string Description { get; }

        public PluginBackupScopeDefinition? Definition { get; }
    }
}
