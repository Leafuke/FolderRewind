using FolderRewind.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Threading.Tasks;

namespace FolderRewind.ViewModels
{
    public abstract class ViewModelBase : ObservableObject
    {
        // ViewModel 层统一走这里切回 UI 线程，避免直接依赖具体页面对象。
        protected static void EnqueueOnUiThread(Action action)
        {
            UiDispatcherService.Enqueue(action);
        }

        protected async Task<bool> RunBusyAsync(Func<Task> operation)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            if (!TryEnterBusy()) return false;

            try
            {
                await operation().ConfigureAwait(true);
                return true;
            }
            finally
            {
                ExitBusy();
            }
        }

        protected async Task<(bool Entered, TResult Result)> RunBusyAsync<TResult>(Func<Task<TResult>> operation, TResult fallback = default!)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            if (!TryEnterBusy()) return (false, fallback);

            try
            {
                return (true, await operation().ConfigureAwait(true));
            }
            finally
            {
                ExitBusy();
            }
        }

        protected virtual bool TryEnterBusy() => true;

        protected virtual void ExitBusy()
        {
        }
    }
}
