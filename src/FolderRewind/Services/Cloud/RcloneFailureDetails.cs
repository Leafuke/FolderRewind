using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace FolderRewind.Services;

internal enum CredentialFailureStage { Unknown, ValidatePath, InspectFile, ParseConfiguration, PrepareStore, Recover, Begin, Complete, Merge, Ledger, Origin, Initialize, Probe }
internal enum CredentialFailureReason { Other, ReparsePoint, MultipleLinks, MetadataUnavailable, InvalidConfiguration, InvalidEvidence, UnattributedSnapshot, MissingProfile, RefreshConflict }

// Only allowlisted metadata crosses the logging boundary. Never retain configuration bytes or exception messages here.
internal sealed record RcloneFailureDetails(string Code, CredentialFailureStage Stage, CredentialFailureReason Reason,
    string CredentialKey = "", int? AncestorDepth = null, int? Attributes = null, uint? LinkCount = null, int? Win32Error = null,
    int? LineNumber = null, CredentialFailureStage Operation = CredentialFailureStage.Unknown,
    string ExceptionType = "", int HResult = 0, string CallStack = "", string PathKey = "")
{
    internal string ToLogString() => string.Create(CultureInfo.InvariantCulture,
        $"code={Code}; operation={Operation}; stage={Stage}; reason={Reason}; credential={CredentialKey}; path={PathKey}; ancestor={AncestorDepth}; attributes={Attributes}; links={LinkCount}; win32={Win32Error}; line={LineNumber}; exception={ExceptionType}; hresult={HResult:X8}\n{CallStack}");
}

internal readonly record struct CredentialFileInformation(bool Succeeded, uint LinkCount, int Win32Error);

internal static partial class RcloneConnectionDiagnostics
{
    private const string DetailsKey = "RcloneFailureDetails";
    internal static string StorageKey(string source)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(source).ToUpperInvariant())));

    internal static InvalidOperationException CredentialFailure(CredentialFailureReason reason, CredentialFailureStage stage,
        string source = "", int? ancestorDepth = null, int? attributes = null, uint? links = null, int? win32 = null, int? line = null)
    {
        var (code, resource) = reason switch
        {
            CredentialFailureReason.ReparsePoint => ("connection.path-unsafe", "CloudConnection_PathUnsafe"),
            CredentialFailureReason.MultipleLinks => ("connection.hardlink-unsafe", "CloudConnection_HardlinkUnsafe"),
            CredentialFailureReason.MetadataUnavailable => ("connection.metadata-unavailable", "CloudConnection_MetadataUnavailable"),
            CredentialFailureReason.InvalidConfiguration => ("connection.config-invalid", "CloudConnection_ConfigInvalid"),
            CredentialFailureReason.InvalidEvidence => ("connection.evidence-invalid", "CloudConnection_EvidenceInvalid"),
            CredentialFailureReason.UnattributedSnapshot => ("connection.snapshot-unattributed", "CloudConnection_SnapshotUnattributed"),
            CredentialFailureReason.MissingProfile => ("connection.profile-missing", "CloudConnection_ProfileMissing"),
            _ => ("connection.refresh-conflict", "CloudConnection_RefreshConflict")
        };
        var error = InvalidFailure(code, resource);
        var key = Path.IsPathFullyQualified(source) ? StorageKey(source) : "";
        error.Data[DetailsKey] = new RcloneFailureDetails(code, stage, reason,
            key, ancestorDepth, attributes, links, win32, line, PathKey: key);
        return error;
    }

    internal static RcloneFailureDetails? Details(Exception error)
    {
        if (error.Data[DetailsKey] is not RcloneFailureDetails details) return null;
        // Method identities suffice for triage; source paths, parameters, messages and Data are excluded.
        var frames = new StackTrace(error).GetFrames() ?? [];
        var stack = string.Join("\n", frames.Take(32).Select(f => f.GetMethod()).Where(m => m is not null)
            .Select(m => m!.DeclaringType?.FullName + "." + m.Name));
        return details with { ExceptionType = error.GetType().FullName ?? "", HResult = error.HResult, CallStack = stack };
    }

    internal static void AtStage(Exception error, CredentialFailureStage operation, string source)
    {
        var details = Details(error) ?? new RcloneFailureDetails(SafeCode(Code(error)), operation, CredentialFailureReason.Other);
        error.Data[DetailsKey] = details with { Operation = operation,
            CredentialKey = Path.IsPathFullyQualified(source) ? StorageKey(source) : details.CredentialKey };
    }
    internal static RcloneFailureDetails Capture(Exception error, CredentialFailureStage operation = CredentialFailureStage.Unknown, string source = "")
    {
        if (Details(error) is null) AtStage(error, operation, source);
        return Details(error)!;
    }
    private static string SafeCode(string code) => code.Length <= 80 && (code.StartsWith("connection.", StringComparison.Ordinal) || code.StartsWith("cloud.", StringComparison.Ordinal))
        && code.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '-') ? code : "cloud.access-unconfirmed";
}
