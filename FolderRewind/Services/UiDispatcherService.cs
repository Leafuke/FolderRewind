using Microsoft.UI.Dispatching;
using System;
using System.Threading.Tasks;

namespace FolderRewind.Services
{
    public static class UiDispatcherService
    {
        private static DispatcherQueue? _dispatcherQueue;

        public static void Initialize(DispatcherQueue? dispatcherQueue)
        {
            if (dispatcherQueue != null)
            {
                // 只在主窗口就绪后注入一次；后续所有 UI 回调都走这个入口。
                _dispatcherQueue = dispatcherQueue;
            }
        }

        public static void Enqueue(Action action)
        {
            if (action == null)
            {
                return;
            }

            var queue = _dispatcherQueue;
            if (queue == null || queue.HasThreadAccess)
            {
                // 启动早期或当前已在 UI 线程时直接执行，避免不必要的排队。
                action();
                return;
            }

            _ = queue.TryEnqueue(() => action());
        }

        public static Task RunOnUiAsync(Action action)
        {
            if (action == null)
            {
                return Task.CompletedTask;
            }

            var queue = _dispatcherQueue;
            if (queue == null || queue.HasThreadAccess)
            {
                action();
                return Task.CompletedTask;
            }

            var tcs = new TaskCompletionSource<object?>();

            if (!queue.TryEnqueue(() =>
            {
                try
                {
                    action();
                    tcs.SetResult(null);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            }))
            {
                tcs.TrySetException(new InvalidOperationException("Failed to enqueue UI action."));
            }

            return tcs.Task;
        }

        public static Task<T> RunOnUiAsync<T>(Func<Task<T>> action)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            var queue = _dispatcherQueue;
            if (queue == null || queue.HasThreadAccess)
            {
                return action();
            }

            var tcs = new TaskCompletionSource<T>();

            if (!queue.TryEnqueue(async () =>
            {
                try
                {
                    // 异常透传给调用方，避免后台线程静默吞错。
                    tcs.SetResult(await action().ConfigureAwait(false));
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            }))
            {
                tcs.TrySetException(new InvalidOperationException("Failed to enqueue UI action."));
            }

            return tcs.Task;
        }
    }
}