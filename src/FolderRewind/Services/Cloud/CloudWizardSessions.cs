using FolderRewind.Models;
using FolderRewind.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FolderRewind.Services;

internal sealed class CloudWizardSession(CloudSetupNavigationParameter options)
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public CloudSetupNavigationParameter Options { get; } = options;
    public CloudSetupViewModel ViewModel { get; } = new();
    public bool Initialized { get; set; }
}

// In-process navigation state only; credentials are never serialized with a wizard draft.
internal static class CloudWizardSessions
{
    private static readonly Dictionary<string, CloudWizardSession> Sessions = [];
    public static CloudWizardSession Get(CloudSetupNavigationParameter options)
    {
        if (options.SessionId is { } id && Sessions.TryGetValue(id, out var found)) return found;
        var matching = Sessions.Values.FirstOrDefault(session => session.Options == options);
        if (matching is not null)
        {
            if (matching.ViewModel.WizardStep != 5) return matching;
            End(matching); // An explicit new operation must not reopen a completed authorization session.
        }
        var created = new CloudWizardSession(options); Sessions.Add(created.Id, created); return created;
    }
    public static void End(CloudWizardSession session)
    { Sessions.Remove(session.Id); session.ViewModel.Dispose(); }
}
