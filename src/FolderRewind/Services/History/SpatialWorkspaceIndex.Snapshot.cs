using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace FolderRewind.Services;

internal sealed record SpatialWorkspacePresentation(
    IReadOnlyList<SpatialWorkspaceRow> Rows, int Total, (int Total, int Selected, int NonSpatial) Global,
    IReadOnlyDictionary<string, int> Categories, int WholeDomain, int UnfilteredTotal, int[] MergeCounts, long IndexVersion = 0);

internal sealed partial class SpatialWorkspaceIndex
{
    // This private, rebuildable database has one owner. Hold its mutation gate across all
    // constituent reads so rows and summaries describe the same index generation.
    internal SpatialWorkspacePresentation ReadPresentation(SpatialWorkspaceQuery query, int offset, bool merge,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        using (EnterOperation("presentation", token, _diagnostics is null ? null : new Dictionary<string, string>
        {
            ["query"] = System.Text.Json.JsonSerializer.Serialize(query),
            ["offset"] = offset.ToString(System.Globalization.CultureInfo.InvariantCulture), ["merge"] = merge.ToString()
        }))
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var cancellation = new SqliteReadCancellation(Db, token);
            try
            {
                IReadOnlyList<SpatialWorkspaceRow> rows;
                using (Measure("presentation.rows")) rows = Query(query, offset, 101, token);
                token.ThrowIfCancellationRequested();
                int total;
                using (Measure("presentation.total")) total = Count(query);
                (int Total, int Selected, int NonSpatial) global;
                using (Measure("presentation.global")) global = Counts();
                IReadOnlyDictionary<string, int> categories;
                using (Measure("presentation.categories")) categories = CategoryCounts(query);
                token.ThrowIfCancellationRequested();
                int wholeDomain;
                using (Measure("presentation.whole-domain")) wholeDomain = WholeDomainCount(query);
                int unfilteredTotal;
                using (Measure("presentation.unfiltered")) unfilteredTotal = query.Categories is null ? total : Count(query with { Categories = null });
                int[] mergeCounts;
                using (Measure("presentation.merge-counts")) mergeCounts = merge ? new[] { "Automatic", "Resolved", "Unresolved", "Locked", "Blocked" }
                    .Select(status => { token.ThrowIfCancellationRequested(); return Count(query with { Status = status }); }).ToArray() : [];
                token.ThrowIfCancellationRequested();
                return new(rows, total, global, categories, wholeDomain, unfilteredTotal, mergeCounts, _mutationVersion);
            }
            catch (SqliteException error) when (error.SqliteErrorCode == 9 && token.IsCancellationRequested)
            {
                if (_currentOperation is { } operation) operation.Canceled = true;
                throw new OperationCanceledException("Spatial presentation read was superseded.", error, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                if (_currentOperation is { } operation) operation.Canceled = true;
                throw;
            }
        }
    }
}
