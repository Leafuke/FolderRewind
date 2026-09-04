using System.Text.Json.Serialization;

namespace FolderRewind.Models
{
    public class BackupTask : ObservableObject
    {
        private string _folderName = string.Empty;
        private double _progress;
        private string _status = string.Empty;
        private string _speed = string.Empty;
        private bool _isCompleted;
        private string _log = string.Empty;
        private string _errorMessage = string.Empty;
        private bool _isIndeterminate = true;
        private bool _isSuccess;
        private string _iconGlyph = "\uE8B7";

        public string FolderName { get => _folderName; set => SetProperty(ref _folderName, value ?? string.Empty); }
        public double Progress
        {
            get => _progress;
            set
            {
                if (SetProperty(ref _progress, value)) OnPropertyChanged(nameof(ProgressText));
            }
        }
        public string Status { get => _status; set => SetProperty(ref _status, value ?? string.Empty); }
        public string Speed { get => _speed; set => SetProperty(ref _speed, value ?? string.Empty); }
        public bool IsCompleted
        {
            get => _isCompleted;
            set
            {
                SetProperty(ref _isCompleted, value);
                OnPropertyChanged(nameof(StatusSemantic));
                OnPropertyChanged(nameof(StatusGlyph));
            }
        }
        public string Log { get => _log; set => SetProperty(ref _log, value ?? string.Empty); }
        public string ErrorMessage
        {
            get => _errorMessage;
            set
            {
                SetProperty(ref _errorMessage, value ?? string.Empty);
                OnPropertyChanged(nameof(StatusSemantic));
                OnPropertyChanged(nameof(StatusGlyph));
            }
        }
        public bool IsIndeterminate
        {
            get => _isIndeterminate;
            set
            {
                if (SetProperty(ref _isIndeterminate, value)) OnPropertyChanged(nameof(ProgressText));
            }
        }
        public bool IsSuccess
        {
            get => _isSuccess;
            set
            {
                SetProperty(ref _isSuccess, value);
                OnPropertyChanged(nameof(StatusSemantic));
                OnPropertyChanged(nameof(StatusGlyph));
            }
        }
        public string IconGlyph { get => _iconGlyph; set => SetProperty(ref _iconGlyph, value ?? string.Empty); }

        [JsonIgnore]
        public string ProgressText => IsIndeterminate ? string.Empty : $"{Progress:F0}%";

        [JsonIgnore]
        public SemanticStatus StatusSemantic => IsCompleted switch
        {
            true when IsSuccess => SemanticStatus.Success,
            true when !string.IsNullOrEmpty(ErrorMessage) => SemanticStatus.Error,
            _ => SemanticStatus.Info
        };

        [JsonIgnore]
        public string StatusGlyph => SemanticStatusGlyphs.GetGlyph(StatusSemantic);
    }
}
