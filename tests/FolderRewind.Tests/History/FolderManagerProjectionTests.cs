using System.Collections.Specialized;
using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class FolderManagerProjectionTests
{
    [TestMethod]
    public void Synchronize_PreservesReferencesAndUsesSingleItemNotifications()
    {
        var first = new object();
        var selected = new object();
        var last = new object();
        var added = new object();
        var view = new BatchObservableCollection<object>();
        view.Synchronize([first, selected, last]);
        var changes = new List<NotifyCollectionChangedAction>();
        view.CollectionChanged += (_, e) => changes.Add(e.Action);

        view.Synchronize([first, added, selected, last]);
        Assert.AreSame(selected, view[2]);
        CollectionAssert.AreEqual(new[] { NotifyCollectionChangedAction.Add }, changes);
        changes.Clear();
        view.Synchronize([first, selected, last]);
        Assert.AreSame(selected, view[1]);
        CollectionAssert.AreEqual(new[] { NotifyCollectionChangedAction.Remove }, changes);
        changes.Clear();
        view.Synchronize([first, selected, last]);
        Assert.HasCount(0, changes);
    }

    [TestMethod]
    public void Synchronize_LargeReplacementPublishesOneResetAndRetainsItems()
    {
        var view = new BatchObservableCollection<object>();
        var items = Enumerable.Range(0, 500).Select(_ => new object()).ToArray();
        var changes = new List<NotifyCollectionChangedAction>();
        view.CollectionChanged += (_, e) => changes.Add(e.Action);

        view.Synchronize(items);

        CollectionAssert.AreEqual(new[] { NotifyCollectionChangedAction.Reset }, changes);
        Assert.AreSame(items[250], view[250]);
    }
}
