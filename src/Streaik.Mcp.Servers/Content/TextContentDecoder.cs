using System.Net.Http.Headers;

namespace Streaik.Mcp.Servers.Content;

internal sealed class TextContentDecoder : IContentDecoder
{
    public IReadOnlyList<string> MediaTypes { get; } = ["text/plain", "text/markdown", "text/csv", "text/tab-separated-values"];

    public async ValueTask<DecodedContent?> DecodeAsync(ReadOnlyMemory<byte> data, MediaTypeHeaderValue contentType, CancellationToken cancellationToken)
    {
        var text = await DocumentText.ReadAsync(data, contentType.CharSet, DocumentText.MaxCharacters, cancellationToken);
        return text is null ? null : new(text, contentType.MediaType!);
    }
}
