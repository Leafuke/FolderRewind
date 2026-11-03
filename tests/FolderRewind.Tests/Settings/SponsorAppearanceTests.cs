using FolderRewind.Models;
using FolderRewind.Services;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FolderRewind.Tests;

[TestClass]
public sealed class SponsorAppearanceTests
{
    private static SponsorAppearanceState Original() => new(new SponsorAppearanceSnapshot
    {
        AccentColorIndex = 2,
        TitleText = "My archive",
        TitleIconGlyph = "\uE734",
        BackgroundEnabled = true,
        BackgroundImagePath = "C:\\Pictures\\original.png"
    }, string.Empty, Array.Empty<SponsorAppearanceScheme>());

    private sealed class Fixture
    {
        internal SponsorAppearanceState State = Original();
        internal bool Unlocked = true;
        internal int Writes;
        internal string? Persisted;
        internal Func<Task<ConfigSaveResult>>? Save;
        internal SponsorAppearanceController Controller { get; }
        internal Fixture()
        {
            Controller = new(() => State, value => State = value, async () =>
            {
                Writes++;
                var result = Save is null ? new ConfigSaveResult { Success = true } : await Save();
                if (result.Success) Persisted = JsonSerializer.Serialize(State, SponsorAppearanceTestJsonContext.Default.SponsorAppearanceState);
                return result;
            }, () => Unlocked, () => "save failed");
        }
        internal void Reload() => State = JsonSerializer.Deserialize(Persisted!, SponsorAppearanceTestJsonContext.Default.SponsorAppearanceState)!;
    }

    [TestMethod]
    public void OldAppearanceKeepsImageAndUnknownGradientCannotReplaceIt()
    {
        var old = JsonSerializer.Deserialize("{}", SponsorAppearanceTestJsonContext.Default.SponsorAppearanceSnapshot)!;
        Assert.AreEqual(SponsorBackgroundKind.Image, old.BackgroundKind);
        Assert.AreEqual(SponsorNavigationAnimationKind.None, old.NavigationAnimation);
        Assert.AreEqual(string.Empty, old.ThemeId);
        var current = Original();
        Assert.AreEqual(current.Current, SponsorAppearancePolicy.Normalize(current).Current);
        var unknown = SponsorAppearancePolicy.Normalize(current.Current with
        {
            ThemeId = "removed-theme", BackgroundKind = SponsorBackgroundKind.ThemeGradient
        });
        Assert.IsFalse(unknown.BackgroundEnabled);
        Assert.AreEqual(current.Current.BackgroundImagePath, unknown.BackgroundImagePath);
        Assert.AreEqual(string.Empty, unknown.ThemeId);
    }

    [TestMethod]
    public void OldSavedSchemeAndUnknownAnimationSafelyUseNoAnimation()
    {
        const string legacy = """
            {"Current":{},"ActiveSchemeId":"old","Schemes":[{"Id":"old","Name":"Legacy","Appearance":{"TitleText":"Old title"}}]}
            """;
        var state = JsonSerializer.Deserialize(legacy, SponsorAppearanceTestJsonContext.Default.SponsorAppearanceState)!;
        Assert.AreEqual(SponsorNavigationAnimationKind.None, state.Current.NavigationAnimation);
        Assert.AreEqual(SponsorNavigationAnimationKind.None, state.Schemes.Single().Appearance.NavigationAnimation);
        Assert.IsTrue(SponsorAppearancePolicy.IsModified(state)); // Existing title difference is retained.
        var unknown = JsonSerializer.Deserialize("{\"NavigationAnimation\":99}", SponsorAppearanceTestJsonContext.Default.SponsorAppearanceSnapshot)!;
        Assert.AreEqual(SponsorNavigationAnimationKind.None, SponsorAppearancePolicy.Normalize(unknown).NavigationAnimation);
    }

    [TestMethod]
    public async Task AnimationEditsNeedExplicitUpdateAndSchemesRestoreAfterReload()
    {
        var fixture = new Fixture();
        fixture.State = fixture.State with { Current = fixture.State.Current with { NavigationAnimation = SponsorNavigationAnimationKind.Slide } };
        await fixture.Controller.SaveAsAsync("Slide");
        var slideId = fixture.State.ActiveSchemeId;
        fixture.State = fixture.State with { Current = fixture.State.Current with { NavigationAnimation = SponsorNavigationAnimationKind.Entrance } };
        Assert.IsTrue(SponsorAppearancePolicy.IsModified(fixture.State));
        Assert.AreEqual(SponsorNavigationAnimationKind.Slide, fixture.State.Schemes.Single().Appearance.NavigationAnimation);
        await fixture.Controller.SaveAsAsync("Entrance");
        var entranceId = fixture.State.ActiveSchemeId;
        await fixture.Controller.ApplySchemeAsync(slideId);
        fixture.Reload();
        Assert.AreEqual(SponsorNavigationAnimationKind.Slide, fixture.State.Current.NavigationAnimation);
        Assert.IsFalse(SponsorAppearancePolicy.IsModified(fixture.State));
        fixture.State = fixture.State with { Current = fixture.State.Current with { NavigationAnimation = SponsorNavigationAnimationKind.DrillIn } };
        await fixture.Controller.UpdateAsync(slideId);
        fixture.Reload();
        Assert.AreEqual(SponsorNavigationAnimationKind.DrillIn, fixture.State.Schemes.Single(scheme => scheme.Id == slideId).Appearance.NavigationAnimation);
        Assert.AreEqual(SponsorNavigationAnimationKind.Entrance, fixture.State.Schemes.Single(scheme => scheme.Id == entranceId).Appearance.NavigationAnimation);
        foreach (var theme in SponsorAppearancePolicy.Themes)
        {
            await fixture.Controller.ApplyThemeAsync(theme.Id);
            Assert.AreEqual(SponsorNavigationAnimationKind.DrillIn, fixture.State.Current.NavigationAnimation);
            Assert.IsFalse(SponsorAppearancePolicy.IsModified(fixture.State));
        }
    }

    [TestMethod]
    public async Task FailedSchemeSwitchRestoresAnimationInMemoryAndOnDisk()
    {
        var fixture = new Fixture();
        await fixture.Controller.SaveAsAsync("None");
        var noneId = fixture.State.ActiveSchemeId;
        fixture.State = fixture.State with { Current = fixture.State.Current with { NavigationAnimation = SponsorNavigationAnimationKind.DrillIn } };
        await fixture.Controller.SaveAsAsync("Zoom");
        var before = fixture.State;
        var attempts = 0;
        fixture.Save = () => Task.FromResult(++attempts == 1
            ? new ConfigSaveResult { ErrorMessage = "write failed" } : new ConfigSaveResult { Success = true });
        await Assert.ThrowsExactlyAsync<IOException>(() => fixture.Controller.ApplySchemeAsync(noneId));
        Assert.AreEqual(before, fixture.State);
        fixture.Reload();
        Assert.AreEqual(SponsorNavigationAnimationKind.DrillIn, fixture.State.Current.NavigationAnimation);
        Assert.AreEqual(before.ActiveSchemeId, fixture.State.ActiveSchemeId);
        Assert.IsFalse(SponsorAppearancePolicy.IsModified(fixture.State));
    }

    [TestMethod]
    public async Task ThemePreservesTitleAndImageAndSchemeSwitchRestoresAnIndependentSnapshot()
    {
        var fixture = new Fixture();
        await fixture.Controller.ApplyThemeAsync("paper");
        Assert.AreEqual(Original().Current.TitleText, fixture.State.Current.TitleText);
        Assert.AreEqual(Original().Current.TitleIconGlyph, fixture.State.Current.TitleIconGlyph);
        Assert.AreEqual(Original().Current.BackgroundImagePath, fixture.State.Current.BackgroundImagePath);
        Assert.AreEqual(SponsorBackgroundKind.ThemeGradient, fixture.State.Current.BackgroundKind);
        await fixture.Controller.SaveAsAsync(" Paper copy ");
        var saved = fixture.State.Schemes.Single();
        Assert.AreEqual("Paper copy", saved.Name);
        fixture.State = fixture.State with { Current = fixture.State.Current with { TitleText = "Changed", AccentColorIndex = 4 } };
        Assert.IsTrue(SponsorAppearancePolicy.IsModified(fixture.State));
        Assert.AreEqual("My archive", saved.Appearance.TitleText);
        await fixture.Controller.ApplyThemeAsync("forest"); // Switching deliberately does not prompt or update the saved snapshot.
        await fixture.Controller.ApplySchemeAsync(saved.Id);
        fixture.Reload();
        Assert.AreEqual(saved.Appearance, fixture.State.Current);
        Assert.IsFalse(SponsorAppearancePolicy.IsModified(fixture.State));
        Assert.AreEqual(saved.Id, fixture.State.ActiveSchemeId);
    }

    [TestMethod]
    public async Task OnlyExplicitUpdateChangesASavedSchemeAndSaveAsDoesNotAliasIt()
    {
        var fixture = new Fixture();
        await fixture.Controller.SaveAsAsync("Original");
        var id = fixture.State.ActiveSchemeId;
        fixture.State = fixture.State with { Current = fixture.State.Current with { BackgroundOverlayOpacity = 0.3 } };
        Assert.AreEqual(0.62, fixture.State.Schemes.Single().Appearance.BackgroundOverlayOpacity);
        await fixture.Controller.UpdateAsync(id);
        Assert.AreEqual(0.3, fixture.State.Schemes.Single().Appearance.BackgroundOverlayOpacity);
        await fixture.Controller.SaveAsAsync("Second");
        Assert.HasCount(2, fixture.State.Schemes);
        fixture.State = fixture.State with { Current = fixture.State.Current with { TitleText = "Second title" } };
        await fixture.Controller.UpdateAsync(fixture.State.ActiveSchemeId);
        Assert.AreEqual("My archive", fixture.State.Schemes.Single(scheme => scheme.Id == id).Appearance.TitleText);
        fixture.Reload();
        Assert.AreEqual("Second title", fixture.State.Current.TitleText);
    }

    [TestMethod]
    public async Task EmptyAndDuplicateNamesDoNotWriteAndRenameRetainsIdentity()
    {
        var fixture = new Fixture();
        await fixture.Controller.SaveAsAsync("First");
        var id = fixture.State.ActiveSchemeId;
        var empty = await Assert.ThrowsExactlyAsync<SponsorAppearanceException>(() => fixture.Controller.SaveAsAsync("  "));
        Assert.AreEqual(SponsorAppearanceError.NameRequired, empty.Error);
        var duplicate = await Assert.ThrowsExactlyAsync<SponsorAppearanceException>(() => fixture.Controller.SaveAsAsync(" first "));
        Assert.AreEqual(SponsorAppearanceError.DuplicateName, duplicate.Error);
        Assert.AreEqual(1, fixture.Writes);
        await fixture.Controller.RenameAsync(id, "Renamed");
        Assert.AreEqual(id, fixture.State.ActiveSchemeId);
        Assert.AreEqual("Renamed", fixture.State.Schemes.Single().Name);
        await fixture.Controller.SaveAsAsync("Other");
        await Assert.ThrowsExactlyAsync<SponsorAppearanceException>(() => fixture.Controller.RenameAsync(id, "OTHER"));
        Assert.AreEqual(3, fixture.Writes);
    }

    [TestMethod]
    public async Task DeleteRetainsCurrentAppearanceAndSharedImageReference()
    {
        var fixture = new Fixture();
        await fixture.Controller.SaveAsAsync("Keep image");
        var before = fixture.State.Current;
        await fixture.Controller.DeleteAsync(fixture.State.ActiveSchemeId);
        Assert.IsEmpty(fixture.State.Schemes);
        Assert.AreEqual(before, fixture.State.Current);
        Assert.AreEqual(string.Empty, fixture.State.ActiveSchemeId);
        fixture.Reload();
        Assert.AreEqual(before.BackgroundImagePath, fixture.State.Current.BackgroundImagePath);
    }

    [TestMethod]
    public async Task FailedSwitchCompensatesBothMemoryAndPersistedState()
    {
        var fixture = new Fixture();
        await fixture.Controller.SaveAsAsync("Before");
        var before = fixture.State;
        var attempts = 0;
        fixture.Save = () => Task.FromResult(++attempts == 1
            ? new ConfigSaveResult { ErrorMessage = "disk full" } : new ConfigSaveResult { Success = true });
        await Assert.ThrowsExactlyAsync<IOException>(() => fixture.Controller.ApplyThemeAsync("night"));
        Assert.AreEqual(before, fixture.State);
        Assert.AreEqual(2, attempts);
        fixture.Reload();
        Assert.AreEqual(before.Current, fixture.State.Current);
        Assert.AreEqual(before.ActiveSchemeId, fixture.State.ActiveSchemeId);
        Assert.IsFalse(fixture.Controller.IsBusy);
    }

    [TestMethod]
    public async Task LockedAndConcurrentOperationsDoNotMutateState()
    {
        var fixture = new Fixture { Unlocked = false };
        var before = fixture.State;
        var locked = await Assert.ThrowsExactlyAsync<SponsorAppearanceException>(() => fixture.Controller.ApplyThemeAsync("night"));
        Assert.AreEqual(SponsorAppearanceError.Locked, locked.Error);
        Assert.AreEqual(before, fixture.State);
        Assert.AreEqual(0, fixture.Writes);
        fixture.Unlocked = true;
        var pending = new TaskCompletionSource<ConfigSaveResult>();
        fixture.Save = () => pending.Task;
        var applying = fixture.Controller.ApplyThemeAsync("forest");
        var busy = await Assert.ThrowsExactlyAsync<SponsorAppearanceException>(() => fixture.Controller.SaveAsAsync("Concurrent"));
        Assert.AreEqual(SponsorAppearanceError.Busy, busy.Error);
        pending.SetResult(new ConfigSaveResult { Success = true });
        await applying;
        Assert.IsEmpty(fixture.State.Schemes);
    }

    [TestMethod]
    public void GradientRequiresEntitlementAndNormalThemeWithoutChangingPreferences()
    {
        foreach (var theme in SponsorAppearancePolicy.Themes)
        {
            Assert.IsTrue(SponsorAppearancePolicy.ShowGradient(theme.Id, SponsorBackgroundKind.ThemeGradient, true, true, false));
            Assert.IsFalse(SponsorAppearancePolicy.ShowGradient(theme.Id, SponsorBackgroundKind.ThemeGradient, true, false, false));
            Assert.IsFalse(SponsorAppearancePolicy.ShowGradient(theme.Id, SponsorBackgroundKind.ThemeGradient, true, true, true));
            Assert.IsFalse(SponsorAppearancePolicy.ShowGradient(theme.Id, SponsorBackgroundKind.Image, true, true, false));
            Assert.IsFalse(SponsorAppearancePolicy.ShowGradient(theme.Id, SponsorBackgroundKind.ThemeGradient, false, true, false));
        }
    }

    [TestMethod]
    public void InvalidSavedEntriesAreIsolatedAndFiniteAppearanceValuesAreRecovered()
    {
        var valid = new SponsorAppearanceScheme { Id = "valid", Name = "Same", Appearance = new() { BackgroundOpacity = double.NaN } };
        var normalized = SponsorAppearancePolicy.Normalize(Original() with
        {
            ActiveSchemeId = "missing",
            Schemes = new[] { valid, valid with { Id = "duplicate-name", Name = "same" }, valid with { Name = "duplicate-id" }, valid with { Id = "blank", Name = " " } }
        });
        Assert.HasCount(1, normalized.Schemes);
        Assert.AreEqual(0.28, normalized.Schemes.Single().Appearance.BackgroundOpacity);
        Assert.AreEqual(string.Empty, normalized.ActiveSchemeId);
    }
}

[JsonSerializable(typeof(SponsorAppearanceState))]
[JsonSerializable(typeof(SponsorAppearanceSnapshot))]
internal partial class SponsorAppearanceTestJsonContext : JsonSerializerContext;
