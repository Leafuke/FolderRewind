using FolderRewind.Models;
using System;

namespace FolderRewind.Services;

internal static class OpenListEnvironmentEdits
{
    internal static bool SameIdentity(OpenListRuntimeSettings a, OpenListRuntimeSettings b)
        => a.ExecutablePath == b.ExecutablePath && a.WorkingDirectory == b.WorkingDirectory
            && a.DataDirectory == b.DataDirectory && a.ConfigFilePath == b.ConfigFilePath && a.ServiceBaseUri == b.ServiceBaseUri;

    internal static bool SameSnapshot(OpenListRuntimeSettings a, OpenListRuntimeSettings b)
        => SameIdentity(a, b) && a.AllowStartOnDemand == b.AllowStartOnDemand && a.Ownership == b.Ownership
            && a.AuthorizationMachine == b.AuthorizationMachine && a.AuthorizationSignature == b.AuthorizationSignature;

    internal static void ClearAuthorization(OpenListRuntimeSettings settings)
    {
        settings.Ownership = OpenListOwnership.External;
        settings.AllowStartOnDemand = false;
        settings.AuthorizationMachine = settings.AuthorizationSignature = "";
    }

    internal static OpenListRuntimeSettings Merge(OpenListRuntimeSettings baseline, OpenListRuntimeSettings draft, OpenListRuntimeSettings current)
    {
        static T Field<T>(T before, T edited, T latest) where T : IEquatable<T>
        {
            if (before.Equals(edited)) return latest;
            if (!before.Equals(latest) && !edited.Equals(latest))
                throw new InvalidOperationException(I18n.GetString("OpenList_EditConflict"));
            return edited;
        }
        var merged = ConfigSnapshotMapper.Copy(current);
        merged.ExecutablePath = Field(baseline.ExecutablePath, draft.ExecutablePath, current.ExecutablePath);
        merged.WorkingDirectory = Field(baseline.WorkingDirectory, draft.WorkingDirectory, current.WorkingDirectory);
        merged.DataDirectory = Field(baseline.DataDirectory, draft.DataDirectory, current.DataDirectory);
        merged.ConfigFilePath = Field(baseline.ConfigFilePath, draft.ConfigFilePath, current.ConfigFilePath);
        merged.ServiceBaseUri = Field(baseline.ServiceBaseUri, draft.ServiceBaseUri, current.ServiceBaseUri);
        var onDemand = Field(baseline.AllowStartOnDemand, draft.AllowStartOnDemand, current.AllowStartOnDemand);
        if (!SameIdentity(merged, current)) ClearAuthorization(merged);
        merged.AllowStartOnDemand = onDemand;
        return merged;
    }
}
