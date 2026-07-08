using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FolderRewind.Services.KnotLink;
using Xunit;

namespace FolderRewind.Tests.KnotLink;

public sealed class KnotLinkProtocolTests
{
    [Fact]
    public void Metadata_reads_conversation_fields_from_request()
    {
        var request = KnotLinkCommandParser.Parse(
            "BACKUP -from=minerewind.mod -request-id=req-001 -flow=hot_backup -reply_to=client-a -protocol_version=2");

        var metadata = KnotLinkCommandMetadata.FromRequest(request);

        Assert.Equal("minerewind.mod", metadata.From);
        Assert.Equal("req-001", metadata.RequestId);
        Assert.Equal("hot_backup", metadata.Flow);
        Assert.Equal("client-a", metadata.ReplyTo);
        Assert.Equal("2", metadata.ProtocolVersion);
        Assert.True(metadata.HasConversation);
        Assert.True(metadata.HasCompleteConversation);
    }

    [Fact]
    public void Metadata_empty_has_no_conversation_fields()
    {
        var metadata = KnotLinkCommandMetadata.Empty;

        Assert.False(metadata.HasConversation);
        Assert.False(metadata.HasCompleteConversation);
        Assert.Empty(metadata.ToConversationFields());
    }

    [Fact]
    public void Metadata_returns_only_present_conversation_fields()
    {
        var metadata = KnotLinkCommandMetadata.FromRequest(
            KnotLinkCommandParser.Parse("LIST_BACKUPS -from=minerewind.plugin"));

        var fields = metadata.ToConversationFields();

        Assert.Single(fields);
        Assert.True(fields.TryGetValue("from", out var from));
        Assert.Equal("minerewind.plugin", from);
        Assert.False(fields.ContainsKey("request_id"));
    }

    [Fact]
    public void Context_exposes_command_and_conversation_status()
    {
        var context = new KnotLinkCommandContext(
            KnotLinkCommandParser.Parse("BACKUP -from=minerewind.mod -request_id=req-001"));

        Assert.Equal("BACKUP", context.Command);
        Assert.True(context.HasConversation);
        Assert.True(context.HasCompleteConversation);
    }

    [Fact]
    public void Context_distinguishes_partial_conversation_metadata()
    {
        var context = new KnotLinkCommandContext(
            KnotLinkCommandParser.Parse("LIST_BACKUPS -from=minerewind.plugin"));

        Assert.Equal("LIST_BACKUPS", context.Command);
        Assert.True(context.HasConversation);
        Assert.False(context.HasCompleteConversation);
    }

    [Fact]
    public void Validator_requires_conversation_metadata_for_core_async_parameterized_commands()
    {
        var request = KnotLinkCommandParser.Parse("BACKUP -config_id=main -folder=world1");
        var context = new KnotLinkCommandContext(request);

        var result = KnotLinkCommandValidator.Validate(context);

        Assert.False(result.IsValid);
        Assert.Equal(KnotLinkCommandValidationError.MissingConversationMetadata, result.Error);
        Assert.Equal(new[] { "from", "request_id" }, result.MissingMetadataKeys);
    }

    [Fact]
    public void Validator_exposes_required_metadata_commands_as_read_only_source_of_truth()
    {
        var expectedCommands = new[]
        {
            "BACKUP",
            "RESTORE",
            "BACKUP_ALL",
            "AUTO_BACKUP",
            "STOP_AUTO_BACKUP",
            "MARK_IMPORTANT"
        };

        var property = typeof(KnotLinkCommandValidator).GetProperty(
            nameof(KnotLinkCommandValidator.RequiredMetadataCommandNames),
            BindingFlags.Public | BindingFlags.Static);
        var requiredCommands = KnotLinkCommandValidator.RequiredMetadataCommandNames;

        Assert.NotNull(property);
        Assert.Equal(typeof(IReadOnlySet<string>), property.PropertyType);
        Assert.Null(property.GetSetMethod(nonPublic: true));
        Assert.Equal(
            expectedCommands.OrderBy(static command => command, StringComparer.Ordinal),
            requiredCommands.OrderBy(static command => command, StringComparer.Ordinal));
        Assert.True(requiredCommands.Contains("backup"));

        if (requiredCommands is ISet<string> mutableCommands)
        {
            Assert.Throws<NotSupportedException>(() => mutableCommands.Add("__MUTATED__"));
        }

        Assert.DoesNotContain("__MUTATED__", requiredCommands);
        foreach (var command in requiredCommands)
        {
            var result = KnotLinkCommandValidator.Validate(
                new KnotLinkCommandContext(KnotLinkCommandParser.Parse($"{command} -config_id=main -folder=world1")));

            Assert.False(result.IsValid);
            Assert.Equal(KnotLinkCommandValidationError.MissingConversationMetadata, result.Error);
        }
    }

    [Fact]
    public void Validator_allows_query_commands_without_conversation_metadata()
    {
        var request = KnotLinkCommandParser.Parse("LIST_BACKUPS -config_id=main -folder=world1");
        var context = new KnotLinkCommandContext(request);

        var result = KnotLinkCommandValidator.Validate(context);

        Assert.True(result.IsValid);
        Assert.Equal(KnotLinkCommandValidationError.None, result.Error);
    }

    [Theory]
    [InlineData("BACKUP", true)]
    [InlineData("RESTORE", true)]
    [InlineData("BACKUP_ALL", true)]
    [InlineData("AUTO_BACKUP", true)]
    [InlineData("STOP_AUTO_BACKUP", true)]
    [InlineData("MARK_IMPORTANT", true)]
    [InlineData("LIST_BACKUPS", false)]
    [InlineData("GET_CAPABILITIES", false)]
    public void Validator_reports_which_commands_require_conversation_metadata(string command, bool expected)
    {
        Assert.Equal(expected, KnotLinkCommandValidator.RequiresConversationMetadata(command));
    }

    [Fact]
    public void Validator_rejects_deprecated_world_option_for_parameterized_commands()
    {
        var request = KnotLinkCommandParser.Parse("BACKUP -from=x -request_id=y -config_id=main -world=OldWorld");
        var context = new KnotLinkCommandContext(request);

        var result = KnotLinkCommandValidator.Validate(context);

        Assert.False(result.IsValid);
        Assert.Equal(KnotLinkCommandValidationError.DeprecatedWorldOption, result.Error);
        Assert.Equal("world", result.DeprecatedOption);
    }

    [Fact]
    public void Formatter_encodes_structured_ok_response_with_conversation_fields()
    {
        var context = new KnotLinkCommandContext(
            KnotLinkCommandParser.Parse("BACKUP -from=minerewind.mod -request_id=req-001 -current_save=true"));

        var response = KnotLinkProtocolFormatter.FormatOk(context, new Dictionary<string, string?>
        {
            ["message"] = "Backup started for 世界=1; Nether"
        });

        Assert.Equal(
            "OK:from=minerewind.mod;request_id=req-001;message=Backup%20started%20for%20%E4%B8%96%E7%95%8C%3D1%3B%20Nether",
            response);
    }

    [Fact]
    public void Formatter_encodes_event_with_event_name_first_then_conversation_fields()
    {
        var context = new KnotLinkCommandContext(
            KnotLinkCommandParser.Parse("RESTORE -from=minerewind.mod -request_id=req-002 -current_save=true -file=backup.7z"));

        var response = KnotLinkProtocolFormatter.FormatEvent(context, "restore_failed", new Dictionary<string, string?>
        {
            ["folder"] = "世界=1; Nether",
            ["error"] = "archive missing"
        });

        Assert.Equal(
            "event=restore_failed;from=minerewind.mod;request_id=req-002;folder=%E4%B8%96%E7%95%8C%3D1%3B%20Nether;error=archive%20missing",
            response);
    }

    [Fact]
    public void Formatter_wraps_legacy_handler_response_as_data_for_list_commands()
    {
        var context = new KnotLinkCommandContext(
            KnotLinkCommandParser.Parse("LIST_BACKUPS -from=minerewind.plugin -request_id=query-1 -current_save=true"));

        var response = KnotLinkProtocolFormatter.FormatHandlerResponse(
            context,
            "OK:backup one.7z;backup=two.7z",
            treatOkPayloadAsData: true);

        Assert.Equal(
            "OK:from=minerewind.plugin;request_id=query-1;data=backup%20one.7z%3Bbackup%3Dtwo.7z",
            response);
    }

    [Fact]
    public void KnotLinkService_pushes_and_restores_current_command_context()
    {
        var first = new KnotLinkCommandContext(
            KnotLinkCommandParser.Parse("BACKUP -from=a -request_id=1 -current_save=true"));
        var second = new KnotLinkCommandContext(
            KnotLinkCommandParser.Parse("BACKUP -from=b -request_id=2 -current_save=true"));

        Assert.Null(FolderRewind.Services.KnotLinkService.CurrentCommandContext);

        using (FolderRewind.Services.KnotLinkService.PushCommandContext(first))
        {
            Assert.Equal("1", FolderRewind.Services.KnotLinkService.CurrentCommandContext?.Metadata.RequestId);

            using (FolderRewind.Services.KnotLinkService.PushCommandContext(second))
            {
                Assert.Equal("2", FolderRewind.Services.KnotLinkService.CurrentCommandContext?.Metadata.RequestId);
            }

            Assert.Equal("1", FolderRewind.Services.KnotLinkService.CurrentCommandContext?.Metadata.RequestId);
        }

        Assert.Null(FolderRewind.Services.KnotLinkService.CurrentCommandContext);
    }
}
