using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.Models;
using System.Collections.Generic;
using System.Linq;

namespace FolderRewind.Services;

public static class HistoryPresentationPolicy
{
    public static BranchId? ResolveBranch(HistoryPresentationMode mode, IEnumerable<BranchSummary> branches, BranchId? selected)
    {
        var available = branches.Where(branch => !branch.IsDeleted).ToArray();
        if (mode == HistoryPresentationMode.Normal) return available.FirstOrDefault(branch => branch.IsActive)?.BranchId;
        return available.FirstOrDefault(branch => branch.BranchId == selected)?.BranchId
            ?? available.FirstOrDefault(branch => branch.IsActive)?.BranchId
            ?? available.FirstOrDefault()?.BranchId;
    }
}
