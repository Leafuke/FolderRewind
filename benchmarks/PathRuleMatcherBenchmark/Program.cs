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

var matcher = PathRuleMatcher.CreateForBackup(
    rules,
    root,
    root,
    enableRegexRules: false);

_ = Measure(() => CountLegacyMatches(relativeCandidates.Take(100), rules));
_ = Measure(() => CountCompiledMatches(fullCandidates.Take(100), matcher));

var legacySamples = new List<double>(Iterations);
var compiledSamples = new List<double>(Iterations);
for (int iteration = 0; iteration < Iterations; iteration++)
{
    legacySamples.Add(Measure(() => CountLegacyMatches(relativeCandidates, rules)));
    compiledSamples.Add(Measure(() => CountCompiledMatches(fullCandidates, matcher)));
}

double legacyMedian = Median(legacySamples);
double compiledMedian = Median(compiledSamples);
Console.WriteLine($"rules={RuleCount};candidates={CandidateCount};iterations={Iterations}");
Console.WriteLine($"legacy_median_ms={legacyMedian:F2}");
Console.WriteLine($"compiled_median_ms={compiledMedian:F2}");
Console.WriteLine($"speedup={legacyMedian / compiledMedian:F2}x");

static int CountLegacyMatches(IEnumerable<string> candidates, IReadOnlyList<string> rules)
{
    int matched = 0;
    foreach (string candidate in candidates)
    {
        foreach (string rule in rules)
        {
            if (string.Equals(candidate, rule, StringComparison.OrdinalIgnoreCase))
            {
                matched++;
                break;
            }
        }
    }

    return matched;
}

static int CountCompiledMatches(
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

static double Measure(Func<int> operation)
{
    var stopwatch = Stopwatch.StartNew();
    int matched = operation();
    stopwatch.Stop();
    GC.KeepAlive(matched);
    return stopwatch.Elapsed.TotalMilliseconds;
}

static double Median(IEnumerable<double> values)
{
    double[] ordered = values.OrderBy(value => value).ToArray();
    return ordered[ordered.Length / 2];
}
