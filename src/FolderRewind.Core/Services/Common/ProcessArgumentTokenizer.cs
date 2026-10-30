using System;
using System.Collections.Generic;
using System.Text;

namespace FolderRewind.Services;

// 将既有标准命令的 Windows 参数表达转换为 ArgumentList；不执行 shell。
public static class ProcessArgumentTokenizer
{
    public static IReadOnlyList<string> Parse(string text)
    {
        var tokens = new List<string>();
        int index = 0;
        while (index < text.Length)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
            if (index == text.Length) break;
            var token = new StringBuilder(); bool quoted = false;
            while (index < text.Length && (quoted || !char.IsWhiteSpace(text[index])))
            {
                int slashes = 0;
                while (index < text.Length && text[index] == '\\') { slashes++; index++; }
                if (index < text.Length && text[index] == '"')
                {
                    token.Append('\\', slashes / 2);
                    if (slashes % 2 != 0) token.Append('"');
                    else quoted = !quoted;
                    index++;
                }
                else
                {
                    token.Append('\\', slashes);
                    if (index < text.Length && (quoted || !char.IsWhiteSpace(text[index]))) token.Append(text[index++]);
                }
            }
            if (quoted) throw new ArgumentException("Unclosed process argument quote.");
            tokens.Add(token.ToString());
        }
        return tokens;
    }
}
