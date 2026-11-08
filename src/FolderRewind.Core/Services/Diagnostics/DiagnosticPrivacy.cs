using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace FolderRewind.Services;

/// <summary>Diagnostic projections only. Never changes the business exception or its payload.</summary>
internal static class DiagnosticPrivacy
{
    internal static string Text(string? text) => CloudCommandSecurity.Redact(text);

    // Sensitive boundaries must not pass arbitrary messages, Data, paths or tool output.
    internal static string ExceptionMetadata(Exception error)
    {
        var result = new StringBuilder();
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        void Append(Exception current)
        {
            if (!seen.Add(current)) return;
            result.Append(CultureInfo.InvariantCulture, $"exception={current.GetType().FullName}; hresult=0x{current.HResult:X8}");
            if (current is System.ComponentModel.Win32Exception native)
                result.Append(CultureInfo.InvariantCulture, $"; nativeError={native.NativeErrorCode}");
            foreach (var frame in new StackTrace(current, false).GetFrames() ?? [])
                if (frame.GetMethod() is { } method)
                    result.Append('\n').Append(method.DeclaringType?.FullName).Append('.').Append(method.Name);
            if (current is AggregateException aggregate)
                foreach (var inner in aggregate.InnerExceptions) { result.Append('\n'); Append(inner); }
            else if (current.InnerException is { } inner) { result.Append('\n'); Append(inner); }
        }
        Append(error);
        return result.ToString();
    }

    internal static string ExceptionDetails(Exception error, bool sensitive = false)
        => sensitive ? ExceptionMetadata(error) : Text(error.ToString());

    internal static InvalidOperationException SensitiveFailure(string code, string stage, Exception error)
    {
        var safe = new InvalidOperationException("[" + code + "]");
        safe.Data["SafeDiagnostic"] = "stage=" + stage + "; " + ExceptionMetadata(error);
        return safe;
    }
}
