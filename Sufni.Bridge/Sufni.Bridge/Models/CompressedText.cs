using System.IO;
using System.IO.Compression;
using System.Text;

namespace Sufni.Bridge.Models;

/// <summary>
/// Storage codec for the multi-MB SVG text columns of session_cache: gzip on write,
/// transparent decode on read. Rows written before compression existed hold plain UTF-8
/// text — Unpack detects the gzip magic bytes and falls back to plain decoding, so both
/// row generations coexist without a schema migration.
/// </summary>
public static class CompressedText
{
    public static byte[]? Pack(string? text)
    {
        if (text is null)
            return null;
        var utf8 = Encoding.UTF8.GetBytes(text);
        using var output = new MemoryStream(utf8.Length / 8);
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(utf8, 0, utf8.Length);
        return output.ToArray();
    }

    /// <summary>True when the stored cell starts with the gzip magic bytes.</summary>
    public static bool IsPacked(byte[] stored) =>
        stored.Length >= 2 && stored[0] == 0x1f && stored[1] == 0x8b;

    public static string? Unpack(byte[]? stored)
    {
        if (stored is null)
            return null;
        if (!IsPacked(stored))
            return Encoding.UTF8.GetString(stored);
        using var gzip = new GZipStream(new MemoryStream(stored), CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
