using FolderRewind.Models;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace FolderRewind.Services;

/// <summary>Commits only appearance state through the existing configuration transaction.</summary>
internal sealed class SponsorAppearanceController(
    Func<SponsorAppearanceState> capture,
    Action<SponsorAppearanceState> apply,
    Func<Task<ConfigSaveResult>> save,
    Func<bool> isUnlocked,
    Func<string> failureMessage)
{
    internal bool IsBusy { get; private set; }

    internal Task ApplyThemeAsync(string themeId) => CommitAsync(state => state with
    {
        Current = SponsorAppearancePolicy.ApplyTheme(state.Current, themeId),
        ActiveSchemeId = SponsorAppearancePolicy.ThemeSchemeId(themeId)
    });

    internal Task ApplySchemeAsync(string id) => CommitAsync(state => state with
    {
        Current = Find(state, id).Appearance with { }, ActiveSchemeId = id
    });

    internal Task SaveAsAsync(string name) => CommitAsync(state =>
    {
        var scheme = new SponsorAppearanceScheme
        {
            Name = SponsorAppearancePolicy.ValidateName(name, state.Schemes),
            Appearance = state.Current with { }
        };
        return state with { Schemes = state.Schemes.Append(scheme).ToArray(), ActiveSchemeId = scheme.Id };
    });

    internal Task UpdateAsync(string id) => CommitAsync(state =>
    {
        _ = Find(state, id);
        return state with
        {
            Schemes = state.Schemes.Select(scheme => scheme.Id == id ? scheme with { Appearance = state.Current with { } } : scheme).ToArray()
        };
    });

    internal Task RenameAsync(string id, string name) => CommitAsync(state =>
    {
        _ = Find(state, id);
        var validName = SponsorAppearancePolicy.ValidateName(name, state.Schemes, id);
        return state with { Schemes = state.Schemes.Select(scheme => scheme.Id == id ? scheme with { Name = validName } : scheme).ToArray() };
    });

    internal Task DeleteAsync(string id) => CommitAsync(state =>
    {
        _ = Find(state, id);
        return state with
        {
            Schemes = state.Schemes.Where(scheme => scheme.Id != id).ToArray(),
            ActiveSchemeId = state.ActiveSchemeId == id ? string.Empty : state.ActiveSchemeId
        };
    });

    private static SponsorAppearanceScheme Find(SponsorAppearanceState state, string id)
        => state.Schemes.FirstOrDefault(scheme => scheme.Id == id)
            ?? throw new SponsorAppearanceException(SponsorAppearanceError.MissingScheme);

    private async Task CommitAsync(Func<SponsorAppearanceState, SponsorAppearanceState> change)
    {
        if (!isUnlocked()) throw new SponsorAppearanceException(SponsorAppearanceError.Locked);
        if (IsBusy) throw new SponsorAppearanceException(SponsorAppearanceError.Busy);
        IsBusy = true;
        try
        {
            var before = capture();
            var after = change(before);
            await ConfigEditTransaction.ApplyAsync(() => apply(after), () => apply(before), save, failureMessage());
        }
        finally { IsBusy = false; }
    }
}
