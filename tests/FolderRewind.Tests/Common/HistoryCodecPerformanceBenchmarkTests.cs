using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using FolderRewind.History.Domain;
using FolderRewind.History.Storage;

namespace FolderRewind.Tests;

[TestClass, DoNotParallelize]
public sealed class HistoryCodecPerformanceBenchmarkTests
{
    [TestMethod, TestCategory("PerformanceBenchmark")]
    public async Task MeasurePackDecodeAndUnionValidationSeparately()
    {
        var output = Environment.GetEnvironmentVariable("FOLDERREWIND_CODEC_OUTPUT");
        var repository = Environment.GetEnvironmentVariable("FOLDERREWIND_CODEC_REPOSITORY");
        if (string.IsNullOrWhiteSpace(output) || string.IsNullOrWhiteSpace(repository))
            Assert.Inconclusive("Set isolated codec output and an explicit metadata repository to opt in.");
        Assert.IsFalse(Path.Exists(output), "Measurement output must be new.");
        Directory.CreateDirectory(output);
        var repeats = int.TryParse(Environment.GetEnvironmentVariable("FOLDERREWIND_CODEC_REPEATS"), out var count) ? count : 5;
        Assert.IsTrue(repeats is > 0 and <= 20);
        var files = Directory.GetFiles(Path.Combine(repository, "packs"), "*.frpack", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).ToArray();
        Assert.IsNotEmpty(files);
        // Read only the explicitly selected immutable metadata repository. Loading bytes
        // precedes timings so this isolates codec/validator CPU and managed allocation.
        var bytes = new List<byte[]>();
        using var inputDigest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files)
        {
            var data = await File.ReadAllBytesAsync(file);
            bytes.Add(data); inputDigest.AppendData(data);
        }
        var codec = new HistoryPackCodec();
        var config = HistoryRepositoryDescriptor.Parse(await File.ReadAllBytesAsync(Path.Combine(repository, "repository.json"))).ConfigId;
        var rows = new List<object>();
        var warm = bytes.Select(data => codec.Decode(data)).ToArray();
        new HistoryRepositoryValidator(codec).Validate(config, warm);
        for (var repeat = 0; repeat < repeats; repeat++)
        {
            HistoryPackReadResult[] decoded = [];
            Measure("decode-including-canonical-hash-and-identity", () => decoded = bytes.Select(data => codec.Decode(data)).ToArray());
            Measure("union-validation-including-typed-deserialization", () => new HistoryRepositoryValidator(codec).Validate(config, decoded));
            var known = HistoryRepositoryValidator.BuildObjectMap(decoded).Values
                .Where(item => HistoryObjectKinds.IsKnown(item.Kind) && item.SchemaVersion == 1).ToArray();
            object[] typed = [];
            Measure("known-payload-deserialization-only", () => typed = known.Select(codec.DeserializeKnown).ToArray());
            using (var session = new HistoryPackReadSession())
                Measure("repository-owned-decode-and-cached-union-validation", () =>
                {
                    var owned = bytes.Select(data => codec.DecodeOwned(data, session)).ToArray();
                    new HistoryRepositoryValidator(codec).Validate(config, owned, session);
                    CollectionAssert.AreEqual(warm.Select(pack => pack.Pack.PackId).ToArray(), owned.Select(pack => pack.Pack.PackId).ToArray());
                });
            Assert.HasCount(warm.Length, decoded);
            Assert.HasCount(known.Length, typed);
            CollectionAssert.AreEqual(warm.Select(pack => pack.Pack.PackId).ToArray(), decoded.Select(pack => pack.Pack.PackId).ToArray());
            GC.KeepAlive(typed);

            void Measure(string stage, Action action)
            {
                var allocated = GC.GetTotalAllocatedBytes(precise: true);
                var watch = Stopwatch.StartNew();
                action(); watch.Stop();
                rows.Add(new { repeat, stage, elapsedMs = watch.Elapsed.TotalMilliseconds,
                    allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocated });
            }
        }
        await File.WriteAllTextAsync(Path.Combine(output, "codec-stages.json"), JsonSerializer.Serialize(new
        {
            runtime = Environment.Version.ToString(), config = config.ToString(), packCount = files.Length,
            inputBytes = bytes.Sum(data => (long)data.Length), inputSha256 = Convert.ToHexString(inputDigest.GetHashAndReset()),
            uniqueKnownObjects = HistoryRepositoryValidator.BuildObjectMap(warm).Values.Count(item => HistoryObjectKinds.IsKnown(item.Kind) && item.SchemaVersion == 1),
            scope = "In-memory immutable pack bytes, one warmup. The owned path models buffers already owned after repository I/O; public Decode still copies its caller input. Component costs cannot be subtracted mechanically from end-to-end commit.", rows
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
