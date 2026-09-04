using System;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal enum WindowCloseChoice { Cancel, Hide, Exit }
internal sealed record WindowCloseAnswer(WindowCloseChoice Choice, bool Remember);

internal sealed class WindowCloseController(
    Func<WindowCloseChoice?> rememberedChoice,
    Func<Task<WindowCloseAnswer>> ask,
    Func<WindowCloseChoice, Task> save,
    Action hide,
    Action close,
    Action<Exception> reportError)
{
    private bool _allowCloseOnce;
    private bool _asking;
    public Task PendingTask { get; private set; } = Task.CompletedTask;

    /// <returns>True when the native closing event must be canceled.</returns>
    public bool HandleClosing(bool forceExit)
    {
        if (forceExit) return false;
        if (_allowCloseOnce) { _allowCloseOnce = false; return false; }
        if (_asking) return true;
        var choice = rememberedChoice();
        if (choice == WindowCloseChoice.Exit) return false;
        if (choice == WindowCloseChoice.Hide) { hide(); return true; }
        _asking = true;
        PendingTask = AskAndApplyAsync();
        return true;
    }

    private async Task AskAndApplyAsync()
    {
        try
        {
            var answer = await ask();
            if (answer.Choice == WindowCloseChoice.Cancel) return;
            if (answer.Remember) await save(answer.Choice);
            if (answer.Choice == WindowCloseChoice.Hide) hide();
            else { _allowCloseOnce = true; close(); }
        }
        catch (Exception ex) { reportError(ex); }
        finally { _asking = false; }
    }
}
