using System.Runtime.InteropServices;
using System.Text;

namespace Streaik.Mcp.Servers.Content;

internal static class DocumentText
{
    internal const int MaxCharacters = 256 * 1024;
    internal static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    internal static MemoryStream OpenStream(ReadOnlyMemory<byte> data) => MemoryMarshal.TryGetArray(data, out var bytes)
        ? new MemoryStream(bytes.Array!, bytes.Offset, bytes.Count, writable: false)
        : new MemoryStream(data.ToArray(), writable: false);

    internal static async Task<string?> ReadAsync(ReadOnlyMemory<byte> data, string? charset, int limit, CancellationToken ct)
    {
        var encoding = string.IsNullOrEmpty(charset) ? Utf8
            : Encoding.GetEncoding(charset.Trim('"'), EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        using var reader = new StreamReader(OpenStream(data), encoding, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[4096];
        var text = new StringBuilder();
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct)) != 0)
        {
            if (count > limit - text.Length) return null;
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }

    internal static bool TryAppendLine(StringBuilder target, string line)
    {
        if (line.Length + (target.Length == 0 ? 0 : 1) > MaxCharacters - target.Length) return false;
        if (target.Length != 0) target.Append('\n');
        target.Append(line);
        return true;
    }
}
