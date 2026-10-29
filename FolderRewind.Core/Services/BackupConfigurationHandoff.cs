using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace FolderRewind.Services;

/// <summary>Identifies the saved registration and complete source roster admitted by a follow-up operation.</summary>
internal sealed class BackupConfigurationHandoff
{
    private readonly object _registration;
    private readonly Guid _configId;
    private readonly ImmutableHashSet<Guid> _sources;
    internal string Revision { get; }
    internal BackupConfigurationHandoff(object registration, string configId, string revision, IEnumerable<string> sources)
    {
        _registration = registration;
        _configId = Guid.Parse(configId); Revision = revision;
        var ids = sources.Select(Guid.Parse).ToArray();
        _sources = ids.ToImmutableHashSet();
        if (_configId == Guid.Empty || string.IsNullOrWhiteSpace(revision) || ids.Length == 0 || ids.Length != _sources.Count || ids.Contains(Guid.Empty))
            throw new ArgumentException("A checkpoint requires a saved configuration and distinct stable source identities.");
    }
    internal bool Matches(object registration, string configId, string revision, IEnumerable<string> sources)
    {
        if (!ReferenceEquals(registration, _registration) || !Guid.TryParse(configId, out var id) || id != _configId || revision != Revision) return false;
        var values = sources.Select(s => Guid.TryParse(s, out var parsed) ? parsed : Guid.Empty).ToArray();
        return values.Length == _sources.Count && !values.Contains(Guid.Empty) && _sources.SetEquals(values);
    }
}
