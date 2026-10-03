using System;
using System.Collections.Generic;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace FolderRewind.History.Domain;

public static class DeterministicHistoryId
{
    public static Guid Create(string domainSeparator, IEnumerable<string> canonicalFields)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domainSeparator);
        ArgumentNullException.ThrowIfNull(canonicalFields);
        using var stream = new System.IO.MemoryStream();
        Write(domainSeparator);
        foreach (var field in canonicalFields) Write(field ?? string.Empty);
        var hash = SHA256.HashData(stream.ToArray());
        var bytes = hash[..16];
        bytes[7] = (byte)((bytes[7] & 0x0f) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes);

        void Write(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            Span<byte> length = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            stream.Write(length);
            stream.Write(bytes);
        }
    }
}
