#if NATIVE_ARTIFACT_ACCEPTANCE
using FolderRewind.Models;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

public static partial class ConfigService
{
    internal static async Task MigrateRedundancyCredentialFixtureAsync(CloudConnectionProfile profile,
        string source, string target, CancellationToken token)
        => await MigrateCredentialAsync(new(source, target, [new(profile, profile.RemoteName + ":", profile.ProfileRevision)]), token);
}
#endif
