using FolderRewind.Services;
using FolderRewind.History.Domain;
using FolderRewind.History.Storage;
using FolderRewind.History.Cloud;
using System.Text.Json;

namespace FolderRewind.Tests;

[TestClass]
public sealed class MigrationFailurePresentationTests
{
    [TestMethod]
    public void MigrationFailuresDistinguishFormatDataMissingAndPermissions()
    {
        Assert.AreEqual("Format", MigrationFailurePresentation.Kind(new HistoryPackCompatibilityException("unsupported")));
        Assert.AreEqual("Format", MigrationFailurePresentation.Kind(new InvalidDataException("config_schema_newer_than_host: schema 999")));
        Assert.AreEqual("Data", MigrationFailurePresentation.Kind(new InvalidDataException("bad zip")));
        Assert.AreEqual("Missing", MigrationFailurePresentation.Kind(new FileNotFoundException()));
        Assert.AreEqual("Permission", MigrationFailurePresentation.Kind(new UnauthorizedAccessException()));
        Assert.AreEqual("Space", MigrationFailurePresentation.Kind(new IOException("full", unchecked((int)0x80070070))));
        Assert.AreEqual("Io", MigrationFailurePresentation.Kind(new IOException()));
    }

}
