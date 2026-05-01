using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.System;
using Windows.Services.Store;

namespace FolderRewind.Services
{
    public sealed class SponsorOperationResult
    {
        public bool Success { get; init; }

        public bool IsUnlocked { get; init; }

        public string Message { get; init; } = string.Empty;
    }

    public static class SponsorService
    {
        private const string ServiceName = nameof(SponsorService);

        public const string SponsorAddOnStoreId = "9NZ03GJSWHK1";

        private static bool _isUnlocked;
        private static string _statusMessage = I18n.GetString("Sponsor_Status_Unknown");

        public static event Action? StateChanged;
        public static bool IsUnlocked
        {
            get => _isUnlocked;
            private set
            {
                if (_isUnlocked == value)
                {
                    return;
                }

                _isUnlocked = value;
                StateChanged?.Invoke();
            }
        }

        public static string StatusMessage
        {
            get => _statusMessage;
            private set
            {
                var next = value ?? string.Empty;
                if (string.Equals(_statusMessage, next, StringComparison.Ordinal))
                {
                    return;
                }

                _statusMessage = next;
                StateChanged?.Invoke();
            }
        }

        public static async Task<SponsorOperationResult> RefreshLicenseAsync(bool showNotification = false)
        {

            try
            {
                var context = GetStoreContext();
                var license = await context.GetAppLicenseAsync();
                var unlocked = license?.AddOnLicenses != null
                    && license.AddOnLicenses.Any(pair =>
                        pair.Value?.IsActive == true
                        && (string.Equals(pair.Key, SponsorAddOnStoreId, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(pair.Value.SkuStoreId, SponsorAddOnStoreId, StringComparison.OrdinalIgnoreCase)));

                var message = unlocked
                    ? I18n.GetString("Sponsor_Status_Unlocked")
                    : I18n.GetString("Sponsor_Status_Locked");

                ApplyState(unlocked, message);
                if (showNotification)
                {
                    if (unlocked)
                    {
                        NotificationService.ShowSuccess(message, I18n.GetString("Sponsor_Title"));
                    }
                    else
                    {
                        NotificationService.ShowInfo(message, I18n.GetString("Sponsor_Title"));
                    }
                }

                MainWindowService.ApplySponsorVisuals();
                return CreateResult(true, message);
            }
            catch (Exception ex)
            {
                var message = I18n.Format("Sponsor_Status_RefreshFailed", ex.Message);
                ApplyState(false, message);
                LogService.LogError(I18n.Format("Sponsor_Log_RefreshFailed", ex.Message), ServiceName, ex);
                if (showNotification)
                {
                    NotificationService.ShowError(message, I18n.GetString("Sponsor_Title"));
                }

                MainWindowService.ApplySponsorVisuals();
                return CreateResult(false, message);
            }
        }

        public static async Task<SponsorOperationResult> PurchaseAsync()
        {

            try
            {
                var context = GetStoreContext();
                var purchase = await context.RequestPurchaseAsync(SponsorAddOnStoreId);
                var message = purchase.Status switch
                {
                    StorePurchaseStatus.Succeeded => I18n.GetString("Sponsor_Purchase_Succeeded"),
                    StorePurchaseStatus.AlreadyPurchased => I18n.GetString("Sponsor_Purchase_AlreadyPurchased"),
                    StorePurchaseStatus.NetworkError => I18n.GetString("Sponsor_Purchase_NetworkError"),
                    StorePurchaseStatus.ServerError => I18n.GetString("Sponsor_Purchase_ServerError"),
                    StorePurchaseStatus.NotPurchased => I18n.GetString("Sponsor_Purchase_NotPurchased"),
                    _ => I18n.GetString("Sponsor_Purchase_NotPurchased")
                };

                if (purchase.Status == StorePurchaseStatus.Succeeded
                    || purchase.Status == StorePurchaseStatus.AlreadyPurchased)
                {
                    var refresh = await RefreshLicenseAsync(false);
                    if (refresh.IsUnlocked)
                    {
                        NotificationService.ShowSuccess(message, I18n.GetString("Sponsor_Title"));
                    }

                    return new SponsorOperationResult
                    {
                        Success = refresh.Success && refresh.IsUnlocked,
                        IsUnlocked = refresh.IsUnlocked,
                        Message = message
                    };
                }

                if (purchase.ExtendedError != null)
                {
                    LogService.LogWarning(I18n.Format("Sponsor_Log_PurchaseStatus", purchase.Status, purchase.ExtendedError.Message), ServiceName);
                }

                StatusMessage = message;
                NotificationService.ShowInfo(message, I18n.GetString("Sponsor_Title"));
                return CreateResult(false, message);
            }
            catch (Exception ex)
            {
                var message = I18n.Format("Sponsor_Purchase_Failed", ex.Message);
                StatusMessage = message;
                LogService.LogError(I18n.Format("Sponsor_Log_PurchaseFailed", ex.Message), ServiceName, ex);
                NotificationService.ShowError(message, I18n.GetString("Sponsor_Title"));
                return CreateResult(false, message);
            }
        }

        public static Task<SponsorOperationResult> RestoreAsync()
        {
            return RefreshLicenseAsync(showNotification: true);
        }

        public static Task OpenContributorGuideAsync()
        {
            return OpenUriAsync("https://github.com/Leafuke/FolderRewind/blob/dev/CONTRIBUTING.md", "Sponsor_Log_OpenContributorGuideFailed");
        }

        public static Task OpenSponsorPolicyAsync()
        {
            return OpenUriAsync("https://github.com/Leafuke/FolderRewind/blob/dev/docs/SponsorEdition.md", "Sponsor_Log_OpenSponsorPolicyFailed");
        }

        private static StoreContext GetStoreContext()
        {
            var context = StoreContext.GetDefault();
            MainWindowService.InitializeStoreContext(context);
            return context;
        }

        private static async Task OpenUriAsync(string uri, string logKey)
        {
            try
            {
                if (!await Launcher.LaunchUriAsync(new Uri(uri)))
                {
                    var message = I18n.GetString("Sponsor_OpenLinkFailed");
                    LogService.LogWarning(I18n.Format(logKey, message), ServiceName);
                    NotificationService.ShowWarning(message, I18n.GetString("Sponsor_Title"));
                }
            }
            catch (Exception ex)
            {
                LogService.LogError(I18n.Format(logKey, ex.Message), ServiceName, ex);
                NotificationService.ShowError(I18n.Format("Sponsor_OpenLinkFailedWithReason", ex.Message), I18n.GetString("Sponsor_Title"));
            }
        }

        private static void ApplyState(bool unlocked, string message)
        {
            StatusMessage = message;
            IsUnlocked = unlocked;
        }

        private static SponsorOperationResult CreateResult(bool success, string message)
        {
            return new SponsorOperationResult
            {
                Success = success,
                IsUnlocked = IsUnlocked,
                Message = message
            };
        }
    }
}
