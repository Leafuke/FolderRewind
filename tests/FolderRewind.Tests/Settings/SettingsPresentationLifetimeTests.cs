using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class SettingsPresentationLifetimeTests
{
    [TestMethod]
    public async Task DeactivationAndLateCommandCompletionCannotRestartBindingTracking()
    {
        var presentation = new SettingsPresentationLifetime();
        using var commands = new AsyncCommandLifetime(_ => Assert.Fail());
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var updates = 0;
        commands.StateChanged += () => presentation.UpdateBindings(() => updates++);
        presentation.Activate();
        var operation = commands.RunAsync(_ => completion.Task);
        Assert.AreEqual(1, updates);

        // This ordering is required: Deactivate synchronously publishes command state.
        presentation.Deactivate();
        commands.Deactivate();
        completion.SetResult();
        await operation;
        Assert.AreEqual(1, updates);

        presentation.Activate();
        commands.Activate();
        Assert.AreEqual(2, updates);
        Assert.IsTrue(presentation.CanEdit);
    }

    [TestMethod]
    public void ReentryRejectsPickerResultsFromPreviousActivation()
    {
        var presentation = new SettingsPresentationLifetime();
        presentation.Activate();
        var pickerGeneration = presentation.Generation;
        presentation.Activate();
        Assert.IsTrue(presentation.IsCurrent(pickerGeneration));
        presentation.Deactivate();
        presentation.Deactivate();
        presentation.Activate();
        Assert.IsFalse(presentation.IsCurrent(pickerGeneration));
        Assert.IsTrue(presentation.IsCurrent(presentation.Generation));
    }

    [TestMethod]
    public void MaterializingDeferredContentDoesNotAdmitEditsOrRecursiveBindingUpdates()
    {
        var presentation = new SettingsPresentationLifetime();
        var section = new DeferredSettingsSection();
        var edits = 0;
        var creations = 0;
        section.PropertyChanged += (_, _) =>
        {
            creations++;
            if (presentation.CanEdit) edits++;
            presentation.UpdateBindings(() => Assert.Fail("Reentrant binding update"));
        };
        presentation.Activate();
        presentation.UpdateBindings(section.Create);
        presentation.UpdateBindings(section.Create);
        presentation.Deactivate();
        presentation.Activate();
        presentation.UpdateBindings(section.Create);
        Assert.AreEqual(1, creations);
        Assert.AreEqual(0, edits);
        Assert.IsTrue(presentation.CanEdit);
    }

    [TestMethod]
    public void FailedCreationCanRetryAndDoesNotPermanentlySuppressEditing()
    {
        var presentation = new SettingsPresentationLifetime();
        var section = new DeferredSettingsSection();
        var attempts = 0;
        section.PropertyChanged += (_, _) => { if (++attempts == 1) throw new IOException("creation failed"); };
        presentation.Activate();
        Assert.ThrowsExactly<IOException>(() => presentation.UpdateBindings(section.Create));
        Assert.IsFalse(section.IsCreated);
        Assert.IsTrue(presentation.CanEdit);
        presentation.UpdateBindings(section.Create);
        Assert.IsTrue(section.IsCreated);
        Assert.AreEqual(2, attempts);
    }
}
