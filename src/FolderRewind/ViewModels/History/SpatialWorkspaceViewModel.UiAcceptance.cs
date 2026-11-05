#if SPATIAL_WORKSPACE_UI_ACCEPTANCE
using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Artifacts;
using FolderRewind.Services;
using FolderRewind.Services.Development;
using FolderRewind.Services.Plugins.V3;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace FolderRewind.ViewModels;

// Acceptance-only presentation input. This never bypasses production history materialization.
// The fixture supplies its own private trees and reports no history/backup/restore coverage.
public sealed partial class SpatialWorkspaceViewModel
{
    internal Task InitializeUiAcceptanceAsync(BackupConfig original, OwnedPluginTemporaryStorage storage,
        string databasePath, SpatialFixedInput left, SpatialFixedInput right, SpatialIndexDiagnostics? diagnostics = null)
    {
        if (_index is not null || _storage is not null || Inputs.Count != 0)
            throw new InvalidOperationException("Spatial UI fixture requires a new view model.");
        _navigation = new(original.Id, Guid.Parse(original.SourceFolders.Single().Id), SpatialWorkspaceMode.Compare);
        _config = ConfigSnapshotMapper.Copy(original);
        _origin = PluginV3ModelMapper.ToSnapshot(_config);
        _storage = storage;
        _index = new(databasePath, diagnostics);
        return RunAsync(async token =>
        {
            Title = "Spatial workspace presentation acceptance";
            _inputs.Add("left", new("left", left, false));
            _inputs.Add("right", new("right", right, false));
            Inputs.Add(new("left", "Fixture left"));
            Inputs.Add(new("right", "Fixture right"));
            Left = Inputs[0]; Right = Inputs[1];
            ChoicesChanged?.Invoke();
            await OpenPanesAsync(token);
            StartComparison();
            await ReloadRowsAsync();
        });
    }

    internal (bool Active, bool ScanCompleted, bool RowsCurrent, long Revision, string[] Ids) UiAcceptanceState()
        => (_active, _scan.IsCompleted, _rowsPublishedRevision == _rowsRevision, _rowsRevision,
            Items.Select(item => item.Id).ToArray());

    internal object[] UiAcceptanceWeakTargets()
        => new object?[] { this, _index, LeftSession, RightSession }.Where(target => target is not null)
            .Select(target => target!).ToArray();
    internal int UiAcceptanceIndexedCount() => _index!.Counts().Total;
    internal bool UiAcceptanceIndexDisposed => _index?.IsDisposed == true;
    internal UiOverlaySourceRow[] CaptureUiAcceptanceOverlayRows()
        => Items.Select(item => item.Row).Select(row => new UiOverlaySourceRow(row.Id, row.Status,
            row.Components.ToArray(), row.Components.Select(CategoryName).ToArray(),
            row.Locations.Select(location => new UiOverlayLocation(location.WorldId, location.LayerId,
                location.Bounds is { } bounds ? new(bounds.MinX, bounds.MinY, bounds.MaxX, bounds.MaxY) : null)).ToArray())).ToArray();
    internal Task SaveUiAcceptanceIndexSnapshotAsync(string destination)
        => Task.Run(() => _index!.SaveDiagnosticSnapshot(destination));

    internal Task<SpatialIndexOperationSample[]> MeasureUiAcceptanceQueriesAsync()
    {
        var index = _index!;
        var query = CurrentQuery();
        return Task.Run(() =>
        {
            _ = index.ReadPresentation(query, 0, merge: false);
            var first = index.Diagnostics!.Operations.Max(sample => sample.Id);
            for (var repeat = 0; repeat < 3; repeat++)
                _ = index.ReadPresentation(query, 0, merge: false);
            return index.Diagnostics.Operations.Where(sample => sample.Id > first && sample.Kind == "presentation").ToArray();
        });
    }
}
#endif
