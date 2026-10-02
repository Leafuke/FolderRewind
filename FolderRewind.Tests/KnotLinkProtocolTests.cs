using FolderRewind.Services.KnotLink;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FolderRewind.Tests;

[TestClass]
public sealed class KnotLinkProtocolTests
{
    [TestMethod]
    public void Parser_ReadsStrictV2AndNormalizesCommand()
    {
        var request = KnotLinkCommandParser.Parse(
            "CMD=backup;folder=%E4%B8%96%E7%95%8C%20A;backup_whitelist=level.dat,region%2Fr.0.0.mca");

        Assert.AreEqual("BACKUP", request.Command);
        Assert.AreEqual("世界 A", request.GetString("folder"));
        CollectionAssert.AreEqual(
            new[] { "level.dat", "region/r.0.0.mca" },
            request.GetList("backup_whitelist").ToArray());
    }

    [TestMethod]
    public void Codec_RoundTripsReservedAndUnicodeValues()
    {
        var encoded = KnotLinkKeyValueCodec.Serialize(new Dictionary<string, string?>
        {
            ["event"] = "backup_success",
            ["message"] = "世界 A; x=y"
        });

        Assert.AreEqual("event=backup_success;message=%E4%B8%96%E7%95%8C%20A%3B%20x%3Dy", encoded);
        Assert.AreEqual("世界 A; x=y", KnotLinkKeyValueCodec.Parse(encoded).Values["message"]);
    }

    [TestMethod]
    public void Codec_EncodesListItemsBeforeJoining()
    {
        var encoded = KnotLinkKeyValueCodec.EncodeList(new[] { "a,b", "x/y", "世界" });
        Assert.AreEqual("a%2Cb,x%2Fy,%E4%B8%96%E7%95%8C", encoded);
        CollectionAssert.AreEqual(new[] { "a,b", "x/y", "世界" }, KnotLinkKeyValueCodec.DecodeList(encoded).ToArray());
    }

    [TestMethod]
    [DataRow("cmd=PING;cmd=BACKUP")]
    [DataRow("cmd=PING;bad-key=value")]
    [DataRow("cmd=PING;bad%20key=value")]
    [DataRow("cmd=PING;other%20key=value")]
    [DataRow("cmd=PING;other key=value")]
    [DataRow("cmd=PING;message=a=b")]
    [DataRow("cmd=PING;message=%ZZ")]
    [DataRow("cmd=PING;")]
    public void Parser_RejectsMalformedPayload(string payload)
    {
        Assert.ThrowsExactly<KnotLinkCommandParseException>(() => KnotLinkCommandParser.Parse(payload));
    }

    [TestMethod]
    public void ProtocolGuard_RejectsV1AndFormatterProducesStrictPingResponse()
    {
        Assert.IsFalse(KnotLinkCommandParser.HasV2CommandField("BACKUP config folder"));
        Assert.IsTrue(KnotLinkCommandParser.HasV2CommandField("cmd=PING"));

        var context = new KnotLinkCommandContext(KnotLinkCommandParser.Parse("cmd=ping"));
        var ping = KnotLinkProtocolFormatter.FormatMessageOk(context, "PONG");
        var fields = KnotLinkKeyValueCodec.Parse(ping).Values;
        Assert.AreEqual("ok", fields["status"]);
        Assert.AreEqual("PONG", fields["message"]);
    }

    [TestMethod]
    public void Validator_RequiresConversationMetadataForSideEffects()
    {
        var context = new KnotLinkCommandContext(
            KnotLinkCommandParser.Parse("cmd=BACKUP;config_id=test;folder=0"));
        var result = KnotLinkCommandValidator.Validate(context);

        Assert.IsFalse(result.IsValid);
        CollectionAssert.AreEquivalent(new[] { "from", "request_id" }, result.MissingMetadataKeys.ToArray());
    }

    [TestMethod]
    [DataRow("cmd=RESTORE;preserve_player_data=maybe")]
    [DataRow("cmd=RESTORE;preserve_player_data=")]
    [DataRow("cmd=BACKUP;current_save=maybe")]
    [DataRow("cmd=BACKUP;current_save=true;config_id=config")]
    [DataRow("cmd=RESTORE;current_save=true;folder=0")]
    [DataRow("cmd=BACKUP_ALL;current_save=true")]
    [DataRow("cmd=RESTORE;mode=")]
    [DataRow("cmd=RESTORE;mode=invalid")]
    [DataRow("cmd=RESTORE;compression_level=5")]
    [DataRow("cmd=LIST_BACKUPS;scope_areas=0,0,1,1")]
    public void ValidatorRejectsAmbiguousSelectorsAndInvalidOverrides(string payload)
        => Assert.IsNotNull(KnotLinkCommandValidator.ValidateOptions(KnotLinkCommandParser.Parse(payload)));

    [TestMethod]
    [DataRow("true", true)]
    [DataRow("false", false)]
    [DataRow("on", true)]
    [DataRow("off", false)]
    public void PlayerOverridePreservesExplicitFalse(string value, bool expected)
    {
        var request = KnotLinkCommandParser.Parse("cmd=RESTORE;preserve_player_data=" + value);
        Assert.IsNull(KnotLinkCommandValidator.ValidateOptions(request));
        Assert.AreEqual(expected, request.GetBool("preserve_player_data"));
        Assert.IsNull(KnotLinkCommandParser.Parse("cmd=RESTORE").GetBool("preserve_player_data"));
    }

    [TestMethod]
    public void StableTargetResolutionDoesNotReencodeListValuesOrDropConversation()
    {
        var request = KnotLinkCommandParser.Parse("cmd=BACKUP;current_save=true;backup_blacklist=a%2Cb,c;from=client;request_id=001");
        var target = new FolderRewind.Plugin.Abstractions.KnotLinkTarget("config", Guid.NewGuid());
        var resolved = request.WithResolvedTarget(target);
        CollectionAssert.AreEqual(new[] { "a,b", "c" }, resolved.GetList("backup_blacklist").ToArray());
        Assert.AreEqual(target, resolved.ResolvedTarget);
        var context = new KnotLinkCommandContext(resolved);
        Assert.AreEqual("client", context.Metadata.From); Assert.AreEqual("001", context.Metadata.RequestId);
    }

    [TestMethod]
    public void BackupOverrides_NormalizeValidValues()
    {
        var request = KnotLinkCommandParser.Parse(
            "cmd=BACKUP;backup_mode=INCREMENTAL;compression_method=lzma2;compression_level=7");

        var success = KnotLinkBackupOverrideResolver.TryResolve(
            request,
            "Deflate",
            5,
            out var overrides,
            out var error);

        Assert.IsTrue(success, error);
        Assert.AreEqual("Smart", overrides.BackupMode);
        Assert.AreEqual("LZMA2", overrides.CompressionMethod);
        Assert.AreEqual(7, overrides.CompressionLevel);
    }

    [TestMethod]
    public void BackupOverrides_AcceptLegacyIncrementalSpellingButNormalizeToSmart()
    {
        var request = KnotLinkCommandParser.Parse("cmd=BACKUP;backup_mode=incremental");

        var success = KnotLinkBackupOverrideResolver.TryResolve(
            request,
            "LZMA2",
            5,
            out var overrides,
            out var error);

        Assert.IsTrue(success, error);
        Assert.AreEqual("Smart", overrides.BackupMode);
    }

    [TestMethod]
    public void BackupOverrides_AllowIndependentValidOverrides()
    {
        var request = KnotLinkCommandParser.Parse("cmd=BACKUP;compression_level=22");

        var success = KnotLinkBackupOverrideResolver.TryResolve(
            request,
            "zstd",
            5,
            out var overrides,
            out var error);

        Assert.IsTrue(success, error);
        Assert.IsNull(overrides.BackupMode);
        Assert.IsNull(overrides.CompressionMethod);
        Assert.AreEqual(22, overrides.CompressionLevel);
    }

    [TestMethod]
    [DataRow("backup_mode=overwrite", "Invalid backup_mode")]
    [DataRow("compression_method=copy", "Invalid compression_method")]
    [DataRow("compression_method=zstd;compression_level=0", "Allowed range: 1-22")]
    [DataRow("compression_method=LZMA2;compression_level=10", "Allowed range: 0-9")]
    [DataRow("compression_level=fast", "Expected an integer")]
    public void BackupOverrides_RejectInvalidValues(string options, string expectedError)
    {
        var request = KnotLinkCommandParser.Parse($"cmd=BACKUP;{options}");

        var success = KnotLinkBackupOverrideResolver.TryResolve(
            request,
            "LZMA2",
            5,
            out _,
            out var error);

        Assert.IsFalse(success);
        StringAssert.Contains(error, expectedError);
    }

    [TestMethod]
    public void BackupOverrides_RejectMethodOnlyWhenInheritedLevelIsInvalid()
    {
        var request = KnotLinkCommandParser.Parse("cmd=BACKUP;compression_method=zstd");

        var success = KnotLinkBackupOverrideResolver.TryResolve(
            request,
            "LZMA2",
            0,
            out _,
            out var error);

        Assert.IsFalse(success);
        StringAssert.Contains(error, "Allowed range: 1-22");
    }
}
