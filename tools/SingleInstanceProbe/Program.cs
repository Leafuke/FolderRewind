using FolderRewind.Services;

if (args.Length < 2) return 2;
var identity = args[1];
if (args[0] == "primary")
{
    using var primary = SingleInstanceCoordinator.TryAcquire(identity);
    if (primary == null) return 10;
    var directory = args[2];
    primary.SetReady(() => { File.AppendAllText(Path.Combine(directory, "activation.txt"), "ACTIVATE\n"); return true; });
    File.WriteAllText(Path.Combine(directory, "ready.txt"), Environment.ProcessId.ToString());
    while (!File.Exists(Path.Combine(directory, "exit.txt"))) Thread.Sleep(50);
    return 0;
}
using var secondary = SingleInstanceCoordinator.TryAcquire(identity);
if (secondary != null) return 11;
return SingleInstanceCoordinator.NotifyAsync(identity, args[0] == "startup").GetAwaiter().GetResult() ? 0 : 12;
