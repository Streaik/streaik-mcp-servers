using System.Net.Http.Headers;

namespace Streaik.Mcp.Servers.Content;

/// <summary>Converts bytes of a supported media type to text without depending on the MCP protocol.</summary>
public interface IContentDecoder
{
    /// <summary>Media types without parameters, matched case-insensitively.</summary>
    IReadOnlyList<string> MediaTypes { get; }

    /// <summary>Returns text and its media type, or null to keep the original bytes.</summary>
    ValueTask<DecodedContent?> DecodeAsync(ReadOnlyMemory<byte> data, MediaTypeHeaderValue contentType, CancellationToken cancellationToken);
}

public sealed record DecodedContent(string Text, string MimeType = "text/plain");
