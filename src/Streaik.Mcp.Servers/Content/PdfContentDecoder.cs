using System.Net.Http.Headers;
using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Streaik.Mcp.Servers.Content;

internal sealed class PdfContentDecoder : IContentDecoder
{
    internal const int MaxPages = 100;
    public IReadOnlyList<string> MediaTypes { get; } = ["application/pdf"];

    public ValueTask<DecodedContent?> DecodeAsync(ReadOnlyMemory<byte> data, MediaTypeHeaderValue contentType, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var document = PdfDocument.Open(data);
        if (document.NumberOfPages > MaxPages) return ValueTask.FromResult<DecodedContent?>(null);

        var text = new StringBuilder();
        for (var pageNumber = 1; pageNumber <= document.NumberOfPages; pageNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pageText = ContentOrderTextExtractor.GetText(document.GetPage(pageNumber)).Trim();
            if (pageText.Length == 0) continue;
            if (!DocumentText.TryAppendLine(text, pageText)) return ValueTask.FromResult<DecodedContent?>(null);
        }
        return ValueTask.FromResult<DecodedContent?>(text.Length == 0 ? null : new(text.ToString()));
    }
}
