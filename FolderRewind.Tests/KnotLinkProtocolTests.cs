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
}
