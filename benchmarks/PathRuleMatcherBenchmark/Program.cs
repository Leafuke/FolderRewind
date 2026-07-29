using System.Diagnostics;
using FolderRewind.Services;

const int RuleCount = 36_900;
const int CandidateCount = 10_000;
const int Iterations = 5;

string root = Path.Combine(Path.GetTempPath(), "FolderRewindFilterBenchmark");
string[] rules = Enumerable.Range(0, RuleCount)
    .Select(index => $"dimensions/minecraft/overworld/region/r.{index}.0.mca")
    .ToArray();
string[] relativeCandidates = Enumerable.Range(0, CandidateCount)
    .Select(index => rules[(index * 7_919) % RuleCount])
    .ToArray();
string[] fullCandidates = relativeCandidates
    .Select(relative => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)))
    .ToArray();

var warmupMatcher = CreateMatcher(rules, root);
_ = CountMatches(fullCandidates.Take(100), warmupMatcher);

var buildSamples = new List<double>(Iterations);
var matchSamples = new List<double>(Iterations);
var allocationSamples = new List<long>(Iterations);
for (int iteration = 0; iteration < Iterations; iteration++)
{
    var (matcher, buildMilliseconds) = Measure(() => CreateMatcher(rules, root));
    buildSamples.Add(buildMilliseconds);

    long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
    var (_, matchMilliseconds) = Measure(() => CountMatches(fullCandidates, matcher));
    allocationSamples.Add(GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
    matchSamples.Add(matchMilliseconds);
}

Console.WriteLine($"rules={RuleCount};candidates={CandidateCount};iterations={Iterations}");
Console.WriteLine($"build_median_ms={Median(buildSamples):F2}");
Console.WriteLine($"match_median_ms={Median(matchSamples):F2}");
Console.WriteLine($"match_allocated_bytes_median={MedianLong(allocationSamples)}");

static PathRuleMatcher CreateMatcher(IReadOnlyList<string> rules, string root) =>
    PathRuleMatcher.CreateForBackup(
        rules,
        root,
        root,
        enableRegexRules: false);

static int CountMatches(
    IEnumerable<string> candidates,
    PathRuleMatcher matcher)
{
    int matched = 0;
    foreach (string candidate in candidates)
    {
        if (matcher.IsMatch(candidate))
        {
            matched++;
        }
    }

    return matched;
}

static (T Result, double Milliseconds) Measure<T>(Func<T> operation)
{
    var stopwatch = Stopwatch.StartNew();
    T result = operation();
    stopwatch.Stop();
    GC.KeepAlive(result);
    return (result, stopwatch.Elapsed.TotalMilliseconds);
}

static double Median(IEnumerable<double> values)
{
    double[] ordered = values.OrderBy(value => value).ToArray();
    return ordered[ordered.Length / 2];
}

static long MedianLong(IEnumerable<long> values)
{
    long[] ordered = values.OrderBy(value => value).ToArray();
    return ordered[ordered.Length / 2];
}
