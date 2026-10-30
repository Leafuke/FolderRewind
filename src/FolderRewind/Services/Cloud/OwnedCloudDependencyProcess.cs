using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

// 只管理当前 Host 亲自启动且仍可核对原生进程句柄的实例。退出/租约释放从不自动终止它。
internal sealed class OwnedCloudDependencyProcess
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _owned;
    private string? _identity, _executable;
    private DateTime _started;
    private int _users;
    public bool Owns(string identity)
    {
        try
        {
            return _owned is not null && !_owned.HasExited && _identity == identity && _owned.StartTime.ToUniversalTime() == _started
                && StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(_owned.MainModule!.FileName), _executable);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }
    public bool CanStop(string identity) => Volatile.Read(ref _users) == 0 && Owns(identity);
    public async Task<IDisposable> AcquireAsync(string identity, bool allowStart, Func<ProcessStartInfo> startFactory,
        Func<CancellationToken, Task<bool>> probe, TimeSpan timeout, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!await probe(token).ConfigureAwait(false))
            {
                if (!allowStart) throw new InvalidOperationException("[openlist.not-authorized] " + I18n.GetString("OpenList_StartNotAuthorized"));
                if (_owned is not null && !_owned.HasExited && !Owns(identity))
                    throw new InvalidOperationException("[openlist.identity-unknown] " + I18n.GetString("OpenList_UnknownOwnership"));
                if (!Owns(identity))
                {
                    var start = startFactory(); start.UseShellExecute = false; start.CreateNoWindow = true; start.WindowStyle = ProcessWindowStyle.Hidden;
                    var process = new Process { StartInfo = start };
                    if (!process.Start()) { process.Dispose(); throw new IOException(I18n.GetString("OpenList_StartFailed")); }
                    _owned?.Dispose(); _owned = process; _identity = identity;
                    _executable = Path.GetFullPath(start.FileName); _started = process.StartTime.ToUniversalTime();
                }
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(token); budget.CancelAfter(timeout);
                while (!await probe(budget.Token).ConfigureAwait(false))
                {
                    if (_owned!.HasExited) throw new IOException(I18n.GetString("OpenList_StartFailed"));
                    await Task.Delay(250, budget.Token).ConfigureAwait(false);
                }
            }
            Interlocked.Increment(ref _users);
            return new UseLease(this);
        }
        finally { _gate.Release(); }
    }
    public async Task StopAsync(string identity, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!CanStop(identity)) throw new InvalidOperationException("[openlist.identity-unknown] " + I18n.GetString("OpenList_StopUnavailable"));
            var process = _owned!;
            if (process.CloseMainWindow())
            {
                try { await process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false); }
                catch (TimeoutException) { }
            }
            if (!process.HasExited) process.Kill(); // 不按进程名称/PID 文件批量停止，也不杀外部子进程。
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            _owned = null; process.Dispose(); _identity = null;
        }
        finally { _gate.Release(); }
    }
    private sealed class UseLease(OwnedCloudDependencyProcess owner) : IDisposable
    {
        private OwnedCloudDependencyProcess? _owner = owner;
        public void Dispose()
        {
            var owned = Interlocked.Exchange(ref _owner, null);
            if (owned is not null) Interlocked.Decrement(ref owned._users);
        }
    }
}
