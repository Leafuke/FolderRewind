using FolderRewind.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

/// <summary>Keep the editor open until validation and durable persistence both succeed.</summary>
internal sealed class SettingsSaveController
{
    public bool IsSaving { get; private set; }
    public async Task<bool> SaveAsync(Func<string?> validate, Func<Task<ConfigSaveResult>> save,
        Action<string> report, CancellationToken token = default)
    {
        if (IsSaving) return false;
        IsSaving = true;
        try
        {
            token.ThrowIfCancellationRequested();
            var error = validate();
            if (error is not null) { report(error); return false; }
            // Once admitted, await the definitive write outcome even if the view is closing.
            var result = await save();
            if (!result.Success) { report(result.ErrorMessage); return false; }
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return false; }
        catch (Exception ex) { report(ex.Message); return false; }
        finally { IsSaving = false; }
    }
}
