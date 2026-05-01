using CommunityToolkit.Mvvm.Input;
using FolderRewind.Services;
using System;
using System.Threading.Tasks;

namespace FolderRewind.ViewModels
{
    public sealed class SponsorWindowViewModel : ViewModelBase
    {
        private bool _isBusy;

        public IAsyncRelayCommand OpenContributorGuideCommand { get; }

        public IAsyncRelayCommand PurchaseSponsorCommand { get; }

        public IAsyncRelayCommand OpenSponsorPolicyCommand { get; }

        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (!SetProperty(ref _isBusy, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(IsIdle));
                OpenContributorGuideCommand.NotifyCanExecuteChanged();
                PurchaseSponsorCommand.NotifyCanExecuteChanged();
                OpenSponsorPolicyCommand.NotifyCanExecuteChanged();
            }
        }

        public bool IsIdle => !IsBusy;

        public SponsorWindowViewModel()
        {
            OpenContributorGuideCommand = new AsyncRelayCommand(
                async () => await RunAsync(SponsorService.OpenContributorGuideAsync),
                () => IsIdle);

            PurchaseSponsorCommand = new AsyncRelayCommand(
                async () => await RunAsync(SponsorService.PurchaseAsync),
                () => IsIdle);

            OpenSponsorPolicyCommand = new AsyncRelayCommand(
                async () => await RunAsync(SponsorService.OpenSponsorPolicyAsync),
                () => IsIdle);
        }

        private async Task RunAsync(Func<Task> operation)
        {
            if (IsBusy)
            {
                return;
            }

            IsBusy = true;
            try
            {
                await operation();
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task RunAsync(Func<Task<SponsorOperationResult>> operation)
        {
            if (IsBusy)
            {
                return;
            }

            IsBusy = true;
            try
            {
                await operation();
            }
            finally
            {
                IsBusy = false;
            }
        }
    }
}
