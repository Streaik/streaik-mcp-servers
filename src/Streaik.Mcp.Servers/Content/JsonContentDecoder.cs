using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Streaik.Mcp.Servers.Content;

internal sealed class JsonContentDecoder : IContentDecoder
{
    public IReadOnlyList<string> MediaTypes { get; } = ["application/json"];

    public ValueTask<DecodedContent?> DecodeAsync(ReadOnlyMemory<byte> data, MediaTypeHeaderValue contentType, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (data.Span.StartsWith(Encoding.UTF8.Preamble)) data = data[Encoding.UTF8.Preamble.Length..];
        if (DocumentText.Utf8.GetCharCount(data.Span) > DocumentText.MaxCharacters) return ValueTask.FromResult<DecodedContent?>(null);

        using var document = JsonDocument.Parse(data);
        return ValueTask.FromResult<DecodedContent?>(new(DocumentText.Utf8.GetString(data.Span), "application/json"));
    }
}
