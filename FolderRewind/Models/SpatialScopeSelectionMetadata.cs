using FolderRewind.Plugin.Abstractions;
using System.Collections.Immutable;

namespace FolderRewind.Models;

/// <summary>Editable presentation metadata, valid only for these exact provider parameters.</summary>
public sealed record SpatialScopeSelectionMetadata(string ParametersDigest, ImmutableArray<SpatialScopeSelection> Selections);
