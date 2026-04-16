using FolderRewind.Models;
using FolderRewind.Services;
using System;
using System.Threading.Tasks;

namespace FolderRewind.ViewModels
{
    // 这个 VM 只负责对话框状态编排：分析、同步、按钮可用性，不承载具体云逻辑。
    public sealed class ConfigCloudSyncDialogViewModel : ViewModelBase
    {
        private readonly BackupConfig _config;
        private bool _isBusy;
        private ConfigCloudSyncMode _selectedMode = ConfigCloudSyncMode.HistoryOnly;
        private ConfigCloudHistoryAnalysisResult? _analysisResult;
        private string _statusMessage = string.Empty;

        public ConfigCloudSyncDialogViewModel(BackupConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _statusMessage = I18n.GetString("ConfigCloudSyncDialog_Status_Ready");
        }

        public BackupConfig Config => _config;

        public string EffectiveExecutablePath => CloudSyncService.GetEffectiveExecutablePath(_config);

        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (SetProperty(ref _isBusy, value))
                {
                    OnPropertyChanged(nameof(CanExecuteSync));
                    OnPropertyChanged(nameof(CanRefreshAnalysis));
                }
            }
        }

        public bool CanRefreshAnalysis => !IsBusy;

        public bool CanExecuteSync => !IsBusy && CloudSyncService.CanUseManualCloudActions(_config);

        public ConfigCloudSyncMode SelectedMode
        {
            get => _selectedMode;
            set => SetProperty(ref _selectedMode, value);
        }

        public int SelectedModeIndex
        {
            get => SelectedMode == ConfigCloudSyncMode.HistoryAndBackups ? 1 : 0;
            set => SelectedMode = value == 1 ? ConfigCloudSyncMode.HistoryAndBackups : ConfigCloudSyncMode.HistoryOnly;
        }

        public ConfigCloudHistoryAnalysisResult? AnalysisResult
        {
            get => _analysisResult;
            private set
            {
                if (SetProperty(ref _analysisResult, value))
                {
                    OnPropertyChanged(nameof(HasAnalysisResult));
                    OnPropertyChanged(nameof(AnalysisSummary));
                    OnPropertyChanged(nameof(AnalysisMatchedText));
                    OnPropertyChanged(nameof(AnalysisImportableText));
                    OnPropertyChanged(nameof(AnalysisUnmappedText));
                    OnPropertyChanged(nameof(AnalysisAmbiguousText));
                }
            }
        }

        public bool HasAnalysisResult => AnalysisResult != null;

        public string StatusMessage
        {
            get => _statusMessage;
            private set => SetProperty(ref _statusMessage, value ?? string.Empty);
        }

        public string AnalysisSummary => AnalysisResult?.Message ?? I18n.GetString("ConfigCloudSyncDialog_Analysis_Pending");

        public string AnalysisMatchedText => AnalysisResult == null
            ? "-"
            : I18n.Format("ConfigCloudSyncDialog_Analysis_Matched", AnalysisResult.MatchedEntries);

        public string AnalysisImportableText => AnalysisResult == null
            ? "-"
            : I18n.Format("ConfigCloudSyncDialog_Analysis_Importable", AnalysisResult.ImportableEntries);

        public string AnalysisUnmappedText => AnalysisResult == null
            ? "-"
            : I18n.Format("ConfigCloudSyncDialog_Analysis_Unmapped", AnalysisResult.UnmappedEntries);

        public string AnalysisAmbiguousText => AnalysisResult == null
            ? "-"
            : I18n.Format("ConfigCloudSyncDialog_Analysis_Ambiguous", AnalysisResult.AmbiguousEntries);

        public async Task InitializeAsync()
        {
            if (AnalysisResult != null)
            {
                return;
            }

            // 首次打开时自动分析一次，避免用户直接同步却看不到导入影响范围。
            await RefreshAnalysisAsync().ConfigureAwait(true);
        }

        public async Task RefreshAnalysisAsync()
        {
            if (IsBusy)
            {
                return;
            }

            IsBusy = true;
            try
            {
                StatusMessage = I18n.GetString("ConfigCloudSyncDialog_Status_Analyzing");
                AnalysisResult = await CloudSyncService.AnalyzeConfigurationHistoryAsync(_config).ConfigureAwait(true);
                StatusMessage = AnalysisResult.Success
                    ? I18n.GetString("ConfigCloudSyncDialog_Status_AnalysisReady")
                    : AnalysisResult.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }

        public async Task<bool> ExecuteSyncAsync()
        {
            if (IsBusy)
            {
                return false;
            }

            IsBusy = true;
            try
            {
                StatusMessage = I18n.GetString("ConfigCloudSyncDialog_Status_Syncing");
                var result = await CloudSyncService.SyncConfigurationFromCloudAsync(_config, SelectedMode).ConfigureAwait(true);
                // 同步后回填最新分析结果，确保统计卡片与真实导入状态一致。
                StatusMessage = result.Message;
                AnalysisResult = result.Analysis;
                return result.Success;
            }
            finally
            {
                IsBusy = false;
            }
        }
    }
}
