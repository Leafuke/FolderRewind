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

    internal sealed class SponsorLicenseProbeResult
    {
        public bool IsUnlocked { get; init; }

        public bool HadProbeError { get; init; }
    }

    public static class SponsorService
    {
        private const string ServiceName = nameof(SponsorService);

        // 发布前请替换为 Microsoft Partner Center 中“持久型加载项”的真实 Store ID。
        public const string SponsorAddOnStoreId = "9NZ03GJSWHK1";

        private static bool _isUnlocked;
        private static string _statusMessage = I18n.GetString("Sponsor_Status_Unknown");

        public static event Action? StateChanged;
        public static event Action? StatusChanged;
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
                StatusChanged?.Invoke();
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
                StatusChanged?.Invoke();
            }
        }

        public static void InitializeFromCache()
        {
            var settings = ConfigService.CurrentConfig?.GlobalSettings;
            if (settings?.SponsorEntitlementCached == true)
            {
                // 启动首帧优先相信本机最近确认过的状态，避免 Store 请求完成前外观闪回免费版。
                ApplyState(true, I18n.GetString("Sponsor_Status_UnlockedCached"));
                return;
            }

            ApplyState(false, I18n.GetString("Sponsor_Status_Unknown"));
        }

        public static async Task<SponsorOperationResult> RefreshLicenseAsync(bool showNotification = false, bool allowDowngrade = true)
        {

            try
            {
                var context = GetStoreContext();
                var probe = await HasSponsorLicenseAsync(context);
                var wasUnlocked = IsUnlocked;
                var hasCachedUnlock = ConfigService.CurrentConfig?.GlobalSettings?.SponsorEntitlementCached == true;
                var unlocked = probe.IsUnlocked
                    || (hasCachedUnlock && (probe.HadProbeError || !allowDowngrade));

                var message = unlocked
                    ? (probe.IsUnlocked ? I18n.GetString("Sponsor_Status_Unlocked") : I18n.GetString("Sponsor_Status_UnlockedCached"))
                    : I18n.GetString("Sponsor_Status_Locked");

                if (probe.IsUnlocked)
                {
                    PersistEntitlementCache(true);
                }
                else if (allowDowngrade && !probe.HadProbeError)
                {
                    PersistEntitlementCache(false);
                }

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

                if (wasUnlocked != unlocked)
                {
                    MainWindowService.ApplySponsorVisuals();
                }

                return CreateResult(true, message);
            }
            catch (Exception ex)
            {
                var message = I18n.Format("Sponsor_Status_RefreshFailed", ex.Message);
                var wasUnlocked = IsUnlocked;
                var keepCachedUnlock = ConfigService.CurrentConfig?.GlobalSettings?.SponsorEntitlementCached == true;
                ApplyState(keepCachedUnlock, keepCachedUnlock ? I18n.GetString("Sponsor_Status_UnlockedCached") : message);
                LogService.LogError(I18n.Format("Sponsor_Log_RefreshFailed", ex.Message), ServiceName, ex);
                if (showNotification)
                {
                    NotificationService.ShowError(message, I18n.GetString("Sponsor_Title"));
                }

                if (wasUnlocked != IsUnlocked)
                {
                    MainWindowService.ApplySponsorVisuals();
                }

                return CreateResult(false, message);
            }
        }

        public static async Task<SponsorOperationResult> PurchaseAsync()
        {

            try
            {
                var context = GetStoreContext();
                var existingLicense = await HasSponsorLicenseAsync(context);
                if (existingLicense.IsUnlocked)
                {
                    var alreadyOwnedMessage = I18n.GetString("Sponsor_Purchase_AlreadyPurchased");
                    PersistEntitlementCache(true);
                    ApplyState(true, I18n.GetString("Sponsor_Status_Unlocked"));
                    NotificationService.ShowSuccess(alreadyOwnedMessage, I18n.GetString("Sponsor_Title"));
                    MainWindowService.ApplySponsorVisuals();
                    return new SponsorOperationResult
                    {
                        Success = true,
                        IsUnlocked = true,
                        Message = alreadyOwnedMessage
                    };
                }

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
                    var refresh = await RefreshLicenseAsync(false, allowDowngrade: false);
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

        private static async Task<SponsorLicenseProbeResult> HasSponsorLicenseAsync(StoreContext context)
        {
            var licenseUnlocked = false;
            var hadProbeError = false;
            try
            {
                var license = await context.GetAppLicenseAsync();
                if (license?.AddOnLicenses != null)
                {
                    foreach (var pair in license.AddOnLicenses)
                    {
                        var addOn = pair.Value;
                        if (addOn?.IsActive == true
                            && (IsSponsorId(pair.Key) || IsSponsorId(addOn.SkuStoreId)))
                        {
                            licenseUnlocked = true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                hadProbeError = true;
                LogService.LogWarning(I18n.Format("Sponsor_Log_AppLicenseProbeFailed", ex.Message), ServiceName);
            }

            if (licenseUnlocked)
            {
                return new SponsorLicenseProbeResult { IsUnlocked = true, HadProbeError = hadProbeError };
            }

            try
            {
                // 促销码兑换后，有些 Store 环境在用户 Collection 中先体现 Durable 归属。
                var collection = await context.GetUserCollectionAsync(new[] { "Durable" });
                if (collection?.Products != null)
                {
                    foreach (var pair in collection.Products)
                    {
                        var product = pair.Value;
                        if (product != null
                            && (IsSponsorId(pair.Key)
                                || IsSponsorId(product.StoreId)
                                || IsSponsorId(product.InAppOfferToken)
                                || (product.Skus?.Any(sku => IsSponsorId(sku.StoreId)) == true)))
                        {
                            return new SponsorLicenseProbeResult { IsUnlocked = true, HadProbeError = hadProbeError };
                        }
                    }
                }

                if (collection?.ExtendedError != null)
                {
                    hadProbeError = true;
                    LogService.LogWarning(I18n.Format("Sponsor_Log_UserCollectionProbeFailed", collection.ExtendedError.Message), ServiceName);
                }
            }
            catch (Exception ex)
            {
                hadProbeError = true;
                LogService.LogWarning(I18n.Format("Sponsor_Log_UserCollectionProbeFailed", ex.Message), ServiceName);
            }

            return new SponsorLicenseProbeResult { IsUnlocked = false, HadProbeError = hadProbeError };
        }

        private static bool IsSponsorId(string? value)
        {
            return !string.IsNullOrWhiteSpace(value)
                && string.Equals(value.Trim(), SponsorAddOnStoreId, StringComparison.OrdinalIgnoreCase);
        }

        public static Task<SponsorOperationResult> RestoreAsync()
        {
            return RefreshLicenseAsync(showNotification: true);
        }

        private static void PersistEntitlementCache(bool unlocked)
        {
            var settings = ConfigService.CurrentConfig?.GlobalSettings;
            if (settings == null)
            {
                return;
            }

            if (settings.SponsorEntitlementCached == unlocked)
            {
                return;
            }

            settings.SponsorEntitlementCached = unlocked;
            settings.SponsorEntitlementLastVerifiedUtc = unlocked ? DateTime.UtcNow : DateTime.MinValue;
            ConfigService.Save();
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
