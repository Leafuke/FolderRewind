using System.Threading;

namespace FolderRewind.Services;

internal sealed class ChangeTrackingState(bool initiallyChanged = false)
{
    private long _revision = initiallyChanged ? 1 : 0;
    private long _acknowledged;
    public long Revision => Interlocked.Read(ref _revision);
    public bool HasChanges => Revision != Interlocked.Read(ref _acknowledged);
    public void MarkChanged() => Interlocked.Increment(ref _revision);
    public void Acknowledge(long revision)
    {
        if (revision < 0 || revision > Revision) return;
        long previous;
        do
        {
            previous = Interlocked.Read(ref _acknowledged);
            if (previous >= revision) return;
        } while (Interlocked.CompareExchange(ref _acknowledged, revision, previous) != previous);
    }
}
