using Windows.ApplicationModel;

namespace FolderRewind.ViewModels
{
    public sealed class StartupToggleResult
    {
        public bool DesiredEnabled { get; init; }

        public bool Success { get; init; }

        public StartupTaskState StartupState { get; init; }

        public bool DisabledByUser => StartupState == StartupTaskState.DisabledByUser;
    }
}
