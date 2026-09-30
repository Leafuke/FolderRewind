using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;

namespace FolderRewind.History.Merge;

/// <summary>
/// 什么算「可以自动合并的文本文件」，以及怎么把它读成行、写完再原样写回去。
/// <para>
/// 判定口径是<b>扩展名白名单</b>（"文档 + 配置 + 源码"），不做内容嗅探 —— 这是与用户定下的口径，
/// 扩白名单属于改契约，要回去问。
/// </para>
/// <para>
/// 拒绝一律给英文诊断串，与历史引擎其余部分的写法一致；这些串会进日志，不进界面。
/// </para>
/// <para>
/// 三处保守取舍，都是「宁可报冲突也不猜」：
/// </para>
/// <list type="bullet">
/// <item>无 BOM 且不是合法 UTF-8 的一律不碰（GBK/ANSI 中文文件就在此列）——
/// 猜错编码会把内容写成乱码，比让用户手动选一边代价大得多。</item>
/// <item>解出来含 NUL 字符的当作二进制拒绝，挡住「叫 .txt 的二进制」。</item>
/// <item>超过 <see cref="MaxFileBytes"/> 的不做行级合并。</item>
/// </list>
/// </summary>
/// <summary>
/// 「不能按行合并」的原因。界面据此给用户一句可读的话，所以每一种都对应一句不同的提示，
/// 不要为了省事把它们合并成「无法合并」。
/// <para>
/// <see cref="NotWhitelisted"/> 由调用方判定（它才有逻辑路径，见 <see cref="TextMergePolicy.TryMerge"/>），
/// 其余由本类判定。
/// </para>
/// </summary>
public enum TextMergeRefusalKind
{
    /// <summary>扩展名不在文本白名单里，按二进制看。</summary>
    NotWhitelisted,

    /// <summary>这一侧没有这个文件，或文件不存在。</summary>
    Missing,

    /// <summary>超过单文件大小上限。</summary>
    TooLarge,

    /// <summary>没有 BOM 且不是合法 UTF-8：编码认不出来。</summary>
    Undecodable,

    /// <summary>解出来含 NUL 字符，按二进制看。</summary>
    Binary,

    /// <summary>合是合了，但仍有需要人定的重叠冲突 —— 这不是坏事，是本来就要问人的那种情形。</summary>
    UnresolvedConflict
}

/// <summary>一次拒绝：机器可判的原因 + 给日志看的英文细节。</summary>
public sealed record TextMergeRefusal(TextMergeRefusalKind Kind, string Diagnostic);

/// <summary>三方文本一起读出来的结果，供界面展示与逐块选择。</summary>
public sealed record TextMergeDocuments(
    TextFileContent Base,
    TextFileContent Ours,
    TextFileContent Theirs,
    LineMergeOutcome Outcome);

public static class TextMergePolicy
{
    /// <summary>单个文件做行级合并的大小上限。超过就走整文件语义。</summary>
    public const int MaxFileBytes = 4 * 1024 * 1024;

    /// <summary>
    /// 扩展名白名单。比较时忽略大小写。
    /// <para>
    /// 不含的即按二进制处理（图片、压缩包、Office 二进制、数据库文件等）。
    /// 无扩展名的特例文件（如 <c>.gitignore</c>）由 <see cref="Path.GetExtension(string)"/> 原样返回全名，
    /// 所以它们要在这里逐个列出，不是靠「无扩展名」兜底。
    /// </para>
    /// </summary>
    public static readonly ImmutableHashSet<string> WhitelistedExtensions = ImmutableHashSet.Create(
        StringComparer.OrdinalIgnoreCase,
        // 文档与数据
        ".txt", ".md", ".markdown", ".rst", ".log", ".csv", ".tsv", ".adoc",
        // 配置
        ".json", ".jsonc", ".xml", ".yml", ".yaml", ".toml", ".ini", ".cfg", ".conf", ".properties",
        ".editorconfig", ".env", ".gitignore", ".gitattributes", ".dockerignore",
        // 源码与脚本
        ".cs", ".xaml", ".csproj", ".props", ".targets", ".sln", ".slnx", ".sql",
        ".js", ".mjs", ".cjs", ".ts", ".tsx", ".jsx", ".vue", ".svelte",
        ".py", ".java", ".kt", ".kts", ".go", ".rs", ".swift", ".rb", ".php", ".lua", ".pl", ".r",
        ".c", ".h", ".cc", ".cpp", ".cxx", ".hpp", ".m", ".mm", ".cshtml", ".razor",
        ".sh", ".bash", ".zsh", ".ps1", ".psm1", ".bat", ".cmd", ".gradle", ".cmake",
        // 样式与标记
        ".css", ".scss", ".sass", ".less", ".html", ".htm", ".svg", ".resx", ".resw");

    /// <summary>路径是否落在白名单里。只看扩展名，不看文件是否存在、也不看内容。</summary>
    public static bool IsWhitelisted(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return false;
        var extension = Path.GetExtension(relativePath);
        return !string.IsNullOrEmpty(extension) && WhitelistedExtensions.Contains(extension);
    }

    /// <summary>
    /// 读一个文件成行。返回 false 时 <paramref name="refusal"/> 说明原因，调用方应回退到整文件语义。
    /// <para>
    /// 只看这一份文件，因此不做白名单判定 —— 那需要逻辑路径，见 <see cref="TryMerge"/>。
    /// </para>
    /// </summary>
    public static bool TryLoad(string path, out TextFileContent? content, out TextMergeRefusal? refusal)
    {
        content = null;
        refusal = null;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            refusal = new(TextMergeRefusalKind.Missing, "Text file is missing.");
            return false;
        }

        var info = new FileInfo(path);
        if (info.Length > MaxFileBytes)
        {
            refusal = new(TextMergeRefusalKind.TooLarge, $"Text file exceeds the {MaxFileBytes} byte merge limit.");
            return false;
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (IOException exception)
        {
            refusal = new(TextMergeRefusalKind.Missing, $"Text file could not be read: {exception.Message}");
            return false;
        }

        if (!TryDecode(bytes, out var encoding, out var text, out var diagnostic))
        {
            refusal = new(TextMergeRefusalKind.Undecodable, diagnostic);
            return false;
        }

        if (text.Contains('\0'))
        {
            refusal = new(TextMergeRefusalKind.Binary, "Text file contains NUL characters and is treated as binary.");
            return false;
        }

        content = Parse(text, encoding);
        return true;
    }

    /// <summary>把行按 <paramref name="like"/> 的编码、BOM 与行尾风格写回字节。</summary>
    public static byte[] Serialize(TextFileContent like, IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(like);
        ArgumentNullException.ThrowIfNull(lines);
        var body = string.Join(like.Newline, lines) + (like.EndsWithNewline && lines.Count > 0 ? like.Newline : string.Empty);
        var preamble = like.Encoding.GetPreamble();
        var encoded = like.Encoding.GetBytes(body);
        if (preamble.Length == 0) return encoded;
        var result = new byte[preamble.Length + encoded.Length];
        preamble.CopyTo(result, 0);
        encoded.CopyTo(result, preamble.Length);
        return result;
    }

    /// <summary>
    /// 把用户手改过的整份文本写回字节：编码、BOM 与行尾风格沿用 <paramref name="like"/>（原文件那一份）。
    /// <para>
    /// 「末尾有没有换行」<b>不</b>沿用 <paramref name="like"/>，而是看用户实际写的内容 —— 他在末尾删掉或补上换行，
    /// 那就是他要的结果，按旧文件的状态覆盖回去反而是错的。
    /// </para>
    /// <para>
    /// 切行规则与读盘时完全一致，这是必需的：界面文本框在多行时给出的是 <c>\r</c> 而不是用户以为的 <c>\r\n</c>，
    /// 不统一处理会让「手改一次」把整份文件的行尾风格换掉。
    /// </para>
    /// </summary>
    public static byte[] SerializeEdited(string text, TextFileContent like)
    {
        ArgumentNullException.ThrowIfNull(like);
        var lines = SplitLines(text ?? string.Empty, out var endsWithNewline);
        return Serialize(like with { EndsWithNewline = endsWithNewline }, lines);
    }

    /// <summary>
    /// 读三份文件并做行级合并，把结果原样交出来（可能仍带冲突）。
    /// 这是本类唯一的加载入口：白名单判定、编码判定、大小限制、行级合并都在这一条路径上。
    /// <para>
    /// <paramref name="relativePath"/> 是文件的<b>逻辑</b>路径（相对来源根），白名单按它判 ——
    /// 三个 <c>*Path</c> 是会话目录里的物化文件，名字与真实文件无关，拿它们判扩展名会永远判不中。
    /// </para>
    /// </summary>
    public static bool TryMergeDocuments(
        string relativePath,
        string basePath,
        string ourPath,
        string theirPath,
        out TextMergeDocuments? documents,
        out TextMergeRefusal? refusal)
    {
        documents = null;
        refusal = null;
        if (!IsWhitelisted(relativePath))
        {
            refusal = new(TextMergeRefusalKind.NotWhitelisted,
                $"'{relativePath}' is not a whitelisted text extension.");
            return false;
        }

        if (!TryLoad(basePath, out var baseContent, out refusal)
            || !TryLoad(ourPath, out var ourContent, out refusal)
            || !TryLoad(theirPath, out var theirContent, out refusal))
        {
            return false;
        }

        var outcome = LineMergeAlgorithm.Merge(baseContent!.Lines, ourContent!.Lines, theirContent!.Lines);
        documents = new(baseContent, ourContent, theirContent, outcome);
        return true;
    }

    /// <summary>
    /// 三份文件的行级合并。只有「自动合得掉、且没有遗留冲突」才为 true，
    /// 否则给出原因让调用方回退到整文件冲突。
    /// <para>
    /// 输出沿用 ours 的编码、BOM 与行尾风格 —— 本地那一份是用户手上的工作副本。
    /// </para>
    /// </summary>
    public static bool TryMerge(
        string relativePath,
        string basePath,
        string ourPath,
        string theirPath,
        out byte[]? merged,
        out TextMergeRefusal? refusal)
    {
        merged = null;
        if (!TryMergeDocuments(relativePath, basePath, ourPath, theirPath, out var documents, out refusal))
            return false;

        if (documents!.Outcome.HasConflicts)
        {
            // 满不满足白名单与编码都过了，只是合不干净 —— 这是「要问人」的情形，不是拒绝。
            refusal = new(TextMergeRefusalKind.UnresolvedConflict,
                $"Text merge left {documents.Outcome.ConflictCount} unresolved conflict(s).");
            return false;
        }

        // 没有冲突时选择函数不会被调用，随便给一个也是确定的。
        merged = Serialize(documents.Ours, LineMergeAlgorithm.Compose(documents.Outcome, LineMergeChoice.Ours));
        return true;
    }

    /// <summary>
    /// 认编码。BOM 优先；无 BOM 时必须是严格合法的 UTF-8，否则拒绝。
    /// <paramref name="diagnostic"/> 只在拒绝时才有意义。
    /// </summary>
    private static bool TryDecode(
        byte[] bytes,
        out Encoding encoding,
        out string text,
        out string diagnostic)
    {
        encoding = Utf8NoBom;
        text = string.Empty;
        diagnostic = string.Empty;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            text = encoding.GetString(bytes, 3, bytes.Length - 3);
            return true;
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            encoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
            text = encoding.GetString(bytes, 2, bytes.Length - 2);
            return true;
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            encoding = new UnicodeEncoding(bigEndian: true, byteOrderMark: true);
            text = encoding.GetString(bytes, 2, bytes.Length - 2);
            return true;
        }

        try
        {
            text = StrictUtf8.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            diagnostic = "Text file is not valid UTF-8 and has no recognized byte order mark.";
            return false;
        }
    }

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// 按 \r\n / \n / \r 切行。<see cref="TextFileContent.Newline"/> 取文件中出现的第一种行尾，
    /// 混用行尾的文件在写回时会被统一成这一种 —— 这是有损的，但比把某一种行尾当正文写进内容里要诚实。
    /// </summary>
    private static TextFileContent Parse(string text, Encoding encoding)
    {
        var lines = SplitLines(text, out var endsWithNewline);
        return new([.. lines], encoding, FirstNewline(text), endsWithNewline);
    }

    /// <summary>
    /// 按 \r\n / \n / \r 切行。末尾那个换行不算一行 —— 它是 <paramref name="endsWithNewline"/>，
    /// 写回时由 <see cref="Serialize"/> 补上。<c>Serialize</c> 与 <c>Parse</c> 因此互为逆运算。
    /// </summary>
    private static List<string> SplitLines(string text, out bool endsWithNewline)
    {
        var lines = new List<string>();
        var buffer = new StringBuilder();
        var sawNewline = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '\r' && c != '\n')
            {
                buffer.Append(c);
                continue;
            }

            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            if (!sawNewline) sawNewline = true;
            lines.Add(buffer.ToString());
            buffer.Clear();
        }

        endsWithNewline = sawNewline && buffer.Length == 0;
        if (buffer.Length > 0) lines.Add(buffer.ToString());
        return lines;
    }

    /// <summary>文件中出现的第一种行尾；一个换行都没有时给平台默认值。</summary>
    private static string FirstNewline(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r') return i + 1 < text.Length && text[i + 1] == '\n' ? "\r\n" : "\r";
            if (text[i] == '\n') return "\n";
        }

        return Environment.NewLine;
    }
}

/// <summary>
/// 一份文本文件的内容：行、以及写回时需要复原的编码与行尾特征。
/// </summary>
public sealed record TextFileContent(
    ImmutableArray<string> Lines,
    Encoding Encoding,
    string Newline,
    bool EndsWithNewline);
