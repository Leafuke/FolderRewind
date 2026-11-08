using FolderRewind.History.Domain;
using FolderRewind.History.Storage;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Application;

public sealed record HistoryTransferManifest(
    string Magic,
    int FormatVersion,
    HistoryConfigId ConfigId,
    bool IncludesPayloads,
    ImmutableSortedDictionary<string, string> PackSha256)
{
    public const string CurrentMagic = "FolderRewindHistoryTransfer";
    public const int CurrentFormatVersion = 1;
}
