using System.Collections.ObjectModel;
using System.Collections.Specialized;
using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class CollectionChangedSubscriptionTests
{
    [TestMethod]
    public void RepeatedAttachReplacementAndDisposeKeepExactlyOneLiveHandler()
    {
        var old = new ObservableCollection<int>();
        var current = new ObservableCollection<int>();
        var calls = 0;
        NotifyCollectionChangedEventHandler handler = (_, _) => calls++;
        using var subscription = new CollectionChangedSubscription();
        Assert.IsTrue(subscription.SetSource(old, handler));
        Assert.IsFalse(subscription.SetSource(old, handler));
        old.Add(1);
        Assert.AreEqual(1, calls);
        subscription.SetSource(current, handler);
        old.Add(2);
        Assert.AreEqual(1, calls);
        current.Add(1);
        Assert.AreEqual(2, calls);
        subscription.Dispose();
        subscription.Dispose();
        current.Add(2);
        Assert.AreEqual(2, calls);
        subscription.SetSource(current, handler); // Cached page activates again.
        current.Add(3);
        Assert.AreEqual(3, calls);
        subscription.SetSource(null, handler);
        current.Add(4);
        Assert.AreEqual(3, calls);
    }
}
