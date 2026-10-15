using System;

namespace FolderRewind.Services
{
    // A text-only projection shared by the dialog and fixture-based acceptance checks.
    internal static class AppUpdatePresentation
    {
        internal static string CreateContent(AppUpdatePolicy.Decision target, string notes,
            bool officialMetadata, Func<string, string> getString)
        {
            var content = string.Format(getString("Update_Dialog_Content"),
                target.CurrentVersion.ToString(4), target.LatestTag, target.LatestVersion.ToString(4),
                string.IsNullOrWhiteSpace(notes) ? getString("Update_Dialog_EmptyNotes") : notes);

            void Append(string key) => content += Environment.NewLine + Environment.NewLine + getString(key);
            if (!officialMetadata) Append("Update_Dialog_MirrorMetadataHint");
            if (target.InstallChannel == InstallChannel.Store)
                Append("Update_Dialog_StoreDelayHint");
            else if (target.InstallChannel == InstallChannel.Setup)
                Append("Update_Dialog_SetupExitHint");
            else
                Append("Update_Dialog_ManualMigrationHint");

            switch (target.Reason)
            {
                case UpdateDecisionReason.UnsafeReleaseUrl:
                case UpdateDecisionReason.UnsafeAssetUrl:
                    Append("Update_Dialog_UnsafeMetadataHint");
                    break;
                case UpdateDecisionReason.MissingAsset:
                case UpdateDecisionReason.AmbiguousAsset:
                    Append("Update_Dialog_PackageUnavailableHint");
                    break;
                case UpdateDecisionReason.UnsupportedArchitecture:
                    Append("Update_Dialog_UnsupportedArchitectureHint");
                    break;
            }
            return content;
        }

        internal static string PrimaryButtonResource(AppUpdatePolicy.Decision target) => target.Action switch
        {
            UpdatePrimaryAction.OpenStorePage => "Update_Dialog_OpenStore",
            UpdatePrimaryAction.OpenSetupDownload => "Update_Dialog_DownloadSetup",
            _ => "Update_Dialog_OpenRelease"
        };
    }
}
