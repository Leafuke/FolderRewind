using System;

namespace FolderRewind.Services;

public static class OnboardingOperationBudgets
{
    public static readonly TimeSpan Discovery = TimeSpan.FromSeconds(30);
    public const int DiscoveryCandidates = 500;
    public static readonly TimeSpan RemoteBrowse = TimeSpan.FromSeconds(15);
    public const int RemoteBatch = 200;
    public const int RemoteInteraction = 1000;
    public static readonly TimeSpan ConnectionProbe = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan ProbeCleanup = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan ServiceProbe = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan ServiceWait = TimeSpan.FromSeconds(30);
    public const int RecentDiagnostics = 20;
}
