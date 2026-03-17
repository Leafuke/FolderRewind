using FolderRewind.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace FolderRewind.ViewModels
{
    public abstract class ViewModelBase : ObservableObject
    {
        // 统一的 UI 线程调度入口，避免各页面 ViewModel 重复实现相同逻辑。
        protected static void EnqueueOnUiThread(Action action)
        {
            UiDispatcherService.Enqueue(action);
        }
    }
}
