using System;
using System.Collections.Generic;
using System.Linq;

namespace FolderRewind.Services;

// UI-independent context: deferred selection and backup requests must belong to
// the same visit to a project, even if the user switches away and back again.
internal sealed class FolderManagerContext<TProject, TFolder>(
    Func<TProject, IEnumerable<TFolder>> folders,
    Func<TFolder, string> folderId,
    Func<TFolder, string> folderPath)
    where TProject : class
    where TFolder : class
{
    private long _projectRevision;
    private long _selectionRevision;
    public TProject? Project { get; private set; }
    public TFolder? SelectedFolder { get; private set; }
    public PendingSelection? Pending { get; private set; }

    internal sealed record PendingSelection(long Revision, TProject Project, string Path);
    internal sealed record BackupRequest(long Revision, TProject Project, TFolder? Folder, IReadOnlyList<TFolder> Targets, string Comment);

    public void SwitchProject(TProject? project)
    {
        if (ReferenceEquals(Project, project)) return;
        Project = project;
        SelectedFolder = null;
        _projectRevision++;
        InvalidatePending();
    }

    public bool Contains(TFolder folder) => Project is not null && folders(Project).Any(f => ReferenceEquals(f, folder));

    public void Select(TFolder? folder)
    {
        SelectedFolder = folder is not null && Contains(folder) ? folder : null;
        InvalidatePending();
    }

    public void RefreshSelection()
    {
        if (SelectedFolder is not null)
            SelectedFolder = Project is null ? null : folders(Project).FirstOrDefault(f => folderId(f) == folderId(SelectedFolder));
    }

    public void RequestSelection(string? path)
    {
        InvalidatePending();
        if (Project is not null && !string.IsNullOrWhiteSpace(path))
            Pending = new(_selectionRevision, Project, path);
    }

    public bool IsCurrent(PendingSelection request) =>
        ReferenceEquals(Pending, request) && request.Revision == _selectionRevision && ReferenceEquals(request.Project, Project);

    public bool ApplySelection(PendingSelection request)
    {
        if (!IsCurrent(request)) return false;
        SelectedFolder = folders(request.Project).FirstOrDefault(f => string.Equals(folderPath(f), request.Path, StringComparison.OrdinalIgnoreCase));
        InvalidatePending();
        return true;
    }

    public void InvalidatePending()
    {
        _selectionRevision++;
        Pending = null;
    }

    public BackupRequest? CaptureBackup(TFolder? folder, string? comment)
    {
        if (Project is null || (folder is null ? !folders(Project).Any() : !Contains(folder))) return null;
        var targets = folder is null ? folders(Project).ToArray() : [folder];
        return new(_projectRevision, Project, folder, Array.AsReadOnly(targets), comment?.Trim() ?? string.Empty);
    }

    public bool IsCurrent(BackupRequest request)
    {
        if (request.Revision != _projectRevision || !ReferenceEquals(request.Project, Project)) return false;
        if (request.Folder is not null) return Contains(request.Folder);
        var current = folders(request.Project).ToArray();
        return current.Length > 0 && current.Length == request.Targets.Count &&
            request.Targets.All(target => current.Any(folder => ReferenceEquals(folder, target)));
    }
}
