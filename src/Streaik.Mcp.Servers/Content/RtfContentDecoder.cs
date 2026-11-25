using System.Net.Http.Headers;
using System.Text;
using RtfPipe;

namespace Streaik.Mcp.Servers.Content;

internal sealed class RtfContentDecoder : IContentDecoder
{
    static RtfContentDecoder() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public IReadOnlyList<string> MediaTypes { get; } = ["application/rtf", "text/rtf", "application/x-rtf"];

    public ValueTask<DecodedContent?> DecodeAsync(ReadOnlyMemory<byte> data, MediaTypeHeaderValue contentType, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!data.Span.StartsWith("{\\rtf"u8)) return ValueTask.FromResult<DecodedContent?>(null);

        using var stream = DocumentText.OpenStream(data);
        var html = Rtf.ToHtml(stream);
        var text = HtmlContentDecoder.ExtractText(html, cancellationToken);
        return ValueTask.FromResult<DecodedContent?>(text is null ? null : new(text));
    }
}
