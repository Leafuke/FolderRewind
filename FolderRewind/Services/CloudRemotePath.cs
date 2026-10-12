using System;
using System.IO;

namespace FolderRewind.Services;

// rclone 的冒号后路径不是本机路径；空后缀与前导斜杠必须逐字保留。
public static class CloudRemotePath
{
    public static (string Remote, string Suffix) Split(string root)
    {
        var separator = root.IndexOf(':');
        if (separator <= 0 || root.IndexOfAny(['\r', '\n', '\0', '\\']) >= 0
            || root[..separator].IndexOfAny(['/', '[', ']', ' ', '\t']) >= 0
            || root[(separator + 1)..].Contains(':'))
            throw new InvalidDataException("[connection.remote-mismatch] Invalid rclone remote path.");
        return (root[..separator], root[(separator + 1)..]);
    }

    public static string Append(string root, params string[] segments)
    {
        Split(root);
        var result = root;
        foreach (var segment in segments)
        {
            if (string.IsNullOrEmpty(segment)) continue;
            if (segment.StartsWith('/') || segment.IndexOfAny(['\\', ':', '\r', '\n', '\0']) >= 0
                || Array.Exists(segment.Split('/'), part => part is "." or ".."))
                throw new InvalidDataException("Invalid relative cloud object path.");
            result = result.EndsWith(':') || result.EndsWith('/') ? result + segment : result + "/" + segment;
        }
        return result;
    }
}
