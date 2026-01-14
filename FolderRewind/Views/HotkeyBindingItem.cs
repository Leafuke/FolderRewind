using FolderRewind.Models;

namespace FolderRewind.Views
{
    public sealed class HotkeyBindingItem : ObservableObject
    {
        private string _currentGesture = string.Empty;
        private bool _isOverridden;

        public string Id { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string? Description { get; set; }

        public string ScopeText { get; set; } = string.Empty;
        public string OwnerText { get; set; } = string.Empty;

        public string DefaultGesture { get; set; } = string.Empty;

        public string CurrentGesture
        {
            get => _currentGesture;
            set => SetProperty(ref _currentGesture, value ?? string.Empty);
        }

        public bool IsOverridden
        {
            get => _isOverridden;
            set => SetProperty(ref _isOverridden, value);
        }
    }
}
