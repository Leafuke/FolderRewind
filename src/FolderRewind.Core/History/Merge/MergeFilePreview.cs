using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Merge;

public enum MergePreviewKind { Missing, Text, Empty, Binary, Limited, Unavailable }
public sealed record MergeFilePreview(MergePreviewKind Kind, string Text, long Length, string EncodingName = "")
{
    public bool CanCompare => Kind is MergePreviewKind.Text or MergePreviewKind.Empty;
    public static async Task<MergeFilePreview> ReadAsync(MergeFileValue? file, CancellationToken token = default)
    {
        if (file is null) return new(MergePreviewKind.Missing, "", 0);
        const int maxBytes = 512 * 1024;
        try
        {
            await using var input = new FileStream(file.Handle, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            if (input.Length == 0) return new(MergePreviewKind.Empty, "", 0, "UTF-8");
            var bytes = new byte[(int)Math.Min(input.Length, maxBytes)];
            await input.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            var limited = input.Length > maxBytes;
            Encoding encoding = new UTF8Encoding(false, true); int skip = 0;
            if (bytes.Length >= 4 && ((bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 254 && bytes[3] == 255)
                || (bytes[0] == 255 && bytes[1] == 254 && bytes[2] == 0 && bytes[3] == 0)))
                return new(MergePreviewKind.Binary, "", input.Length);
            if (bytes.Length >= 3 && bytes[0] == 239 && bytes[1] == 187 && bytes[2] == 191) skip = 3;
            else if (bytes.Length >= 2 && bytes[0] == 255 && bytes[1] == 254) { encoding = new UnicodeEncoding(false, true, true); skip = 2; }
            else if (bytes.Length >= 2 && bytes[0] == 254 && bytes[1] == 255) { encoding = new UnicodeEncoding(true, true, true); skip = 2; }
            string text;
            try
            {
                // A truncated multibyte suffix is allowed only in a visibly limited preview.
                var decoder = encoding.GetDecoder(); var chars = new char[encoding.GetMaxCharCount(bytes.Length)];
                var count = decoder.GetChars(bytes, skip, bytes.Length - skip, chars, 0, !limited);
                text = new string(chars, 0, count);
            }
            catch (DecoderFallbackException) { return new(MergePreviewKind.Binary, "", input.Length); }
            if (text.Any(c => c == '\0' || (char.IsControl(c) && c is not ('\r' or '\n' or '\t'))))
                return new(MergePreviewKind.Binary, "", input.Length);
            var lines = text.Split('\n');
            if (lines.Length > 5000) { text = string.Join('\n', lines.Take(5000)); limited = true; }
            return new(limited ? MergePreviewKind.Limited : text.Length == 0 ? MergePreviewKind.Empty : MergePreviewKind.Text,
                text, input.Length, encoding.WebName);
        }
        catch (IOException) { return new(MergePreviewKind.Unavailable, "", file.Length); }
        catch (UnauthorizedAccessException) { return new(MergePreviewKind.Unavailable, "", file.Length); }
    }
}
