using System;
using System.Collections.Generic;
using System.Linq;

namespace FolderRewind.Services
{
    internal enum BackupChainPlanStatus
    {
        Success,
        MissingBaseFull
    }

    internal sealed class BackupChainPlan<T> where T : class
    {
        public BackupChainPlanStatus Status { get; init; }
        public IReadOnlyList<T> Items { get; init; } = Array.Empty<T>();
    }

    /// <summary>
    /// 统一“最近全量 + 时间窗内增量”的链路骨架。
    /// 具体排序、身份键和目标补齐策略由调用方提供，以保留本地文件与云端历史的兼容差异。
    /// </summary>
    internal static class BackupChainPlanner
    {
        public static BackupChainPlan<T> Build<T>(
            IEnumerable<T>? candidates,
            T target,
            bool targetIsIncremental,
            BackupChainPlanOptions<T> options)
            where T : class
        {
            if (!targetIsIncremental)
            {
                return Success([target]);
            }

            var candidateList = candidates?.Where(item => item != null).ToList() ?? new List<T>();
            DateTime targetTimestamp = options.GetTimestamp(target);
            T? baseFull = options.SelectBaseFull(candidateList.Where(item =>
                options.IsFull(item) && options.GetTimestamp(item) <= targetTimestamp));
            if (baseFull == null)
            {
                return new BackupChainPlan<T> { Status = BackupChainPlanStatus.MissingBaseFull };
            }

            DateTime baseTimestamp = options.GetTimestamp(baseFull);
            string baseIdentity = options.GetIdentity(baseFull);
            string targetIdentity = options.GetIdentity(target);
            var windowItems = candidateList.Where(item =>
            {
                DateTime timestamp = options.GetTimestamp(item);
                if (timestamp < baseTimestamp || timestamp > targetTimestamp)
                {
                    return false;
                }

                string identity = options.GetIdentity(item);
                return (!options.PrependBaseFull && options.IdentityComparer.Equals(identity, baseIdentity))
                    || (options.IncludeTargetInWindow && options.IdentityComparer.Equals(identity, targetIdentity))
                    || options.IsIncremental(item);
            });

            var chain = new List<T>();
            HashSet<string>? added = options.Deduplicate
                ? new HashSet<string>(options.IdentityComparer)
                : null;

            void Add(T item)
            {
                if (added == null || added.Add(options.GetIdentity(item)))
                {
                    chain.Add(item);
                }
            }

            if (options.PrependBaseFull)
            {
                Add(baseFull);
            }

            foreach (T item in options.OrderChain(windowItems))
            {
                Add(item);
            }

            if (options.EnsureTargetIncluded)
            {
                Add(target);
            }

            return Success(chain);
        }

        private static BackupChainPlan<T> Success<T>(IReadOnlyList<T> items) where T : class
            => new()
            {
                Status = BackupChainPlanStatus.Success,
                Items = items
            };
    }

    internal sealed class BackupChainPlanOptions<T> where T : class
    {
        public required Func<T, DateTime> GetTimestamp { get; init; }
        public required Func<T, string> GetIdentity { get; init; }
        public required Func<T, bool> IsFull { get; init; }
        public required Func<T, bool> IsIncremental { get; init; }
        public required Func<IEnumerable<T>, T?> SelectBaseFull { get; init; }
        public required Func<IEnumerable<T>, IEnumerable<T>> OrderChain { get; init; }
        public IEqualityComparer<string> IdentityComparer { get; init; } = StringComparer.Ordinal;
        public bool PrependBaseFull { get; init; }
        public bool IncludeTargetInWindow { get; init; } = true;
        public bool EnsureTargetIncluded { get; init; }
        public bool Deduplicate { get; init; }
    }
}
