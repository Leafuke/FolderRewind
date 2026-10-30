using FolderRewind.Services;
using System.Security.Cryptography;

namespace FolderRewind.Tests;

[TestClass]
public sealed class ProtectedPasswordStoreTests
{
    private string _root = null!;
    [TestInitialize] public void Initialize() { _root = Path.Combine(Path.GetTempPath(), "FolderRewindPasswordStore", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root); }
    [TestCleanup] public void Cleanup() { Directory.Delete(_root, true); }
    private ProtectedPasswordStore Store(string path) => new(path, value => value.Select(b => (byte)(b ^ 0x5a)).ToArray(), value => value.Select(b => (byte)(b ^ 0x5a)).ToArray());
    [TestMethod] public void ReadbackAndIndependentEntriesSurviveReplacement()
    {
        var path = Path.Combine(_root, "passwords.dat"); var store = Store(path); store.Write("first", "old-password"); store.Write("second", "other-password"); store.Write("first", "new-password");
        Assert.AreEqual("new-password", store.Read("first")); Assert.AreEqual("other-password", store.Read("second")); Assert.IsFalse(File.ReadAllText(path).Contains("new-password", StringComparison.Ordinal));
        store.Remove("first"); Assert.IsNull(store.Read("first")); Assert.AreEqual("other-password", store.Read("second"));
    }
    [TestMethod] public void DamagedStoreIsNotOverwrittenAndProtectionFailureKeepsOldEntry()
    {
        var path = Path.Combine(_root, "passwords.dat"); var store = Store(path); store.Write("first", "old-password"); var before = File.ReadAllBytes(path);
        var failed = new ProtectedPasswordStore(path, _ => throw new CryptographicException("protection failed"), value => value);
        Assert.ThrowsExactly<CryptographicException>(() => failed.Write("first", "replacement")); CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
        File.WriteAllText(path, "corrupt-store"); Assert.ThrowsExactly<System.Text.Json.JsonException>(() => store.Write("second", "password")); Assert.AreEqual("corrupt-store", File.ReadAllText(path));
    }
    [TestMethod] public void PersistenceFailureIsReportedToCaller()
    {
        var path = Path.Combine(_root, "directory"); Directory.CreateDirectory(path); Assert.ThrowsExactly<UnauthorizedAccessException>(() => Store(path).Write("first", "password"));
    }
}
