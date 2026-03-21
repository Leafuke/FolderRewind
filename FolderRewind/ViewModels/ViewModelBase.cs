using FolderRewind.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace FolderRewind.ViewModels
{
    public abstract class ViewModelBase : ObservableObject
    {
        // ViewModel 层统一走这里切回 UI 线程，避免直接依赖具体页面对象。
        protected static void EnqueueOnUiThread(Action action)
        {
            UiDispatcherService.Enqueue(action);
        }
    }
}
