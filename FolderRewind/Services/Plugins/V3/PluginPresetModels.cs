using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services.Plugins.V3;

public enum PluginPresetStepOutcome { Success, SuccessWithWarnings, Blocked, Failed }
public sealed record PluginPresetStepResult(string ActionId, PluginPresetStepOutcome Outcome, string Message);
public sealed record PluginPresetRunResult(PluginPresetStepOutcome Outcome, IReadOnlyList<PluginPresetStepResult> Steps)
{
    public bool Success => Outcome is PluginPresetStepOutcome.Success or PluginPresetStepOutcome.SuccessWithWarnings;
}

public interface IPluginPresetConsentBroker
{
    ValueTask<bool> ConfirmExternalDownloadAsync(string name, string url, string sha256, CancellationToken cancellationToken);
    ValueTask<bool> ConfirmExternalLaunchAsync(string name, string localPath, CancellationToken cancellationToken);
}
