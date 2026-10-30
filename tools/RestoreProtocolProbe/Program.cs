if (args.Length > 0 && args[0].StartsWith("--capture-copy", StringComparison.Ordinal))
    await FolderRewind.Tests.CaptureCopyProbeHost.RunAsync(args);
else
    await FolderRewind.Tests.RestoreProtocolProbeHost.RunAsync(args);
