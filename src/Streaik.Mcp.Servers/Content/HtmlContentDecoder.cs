using System.Net.Http.Headers;
using System.Text;
using HtmlAgilityPack;

namespace Streaik.Mcp.Servers.Content;

internal sealed class HtmlContentDecoder : IContentDecoder
{
    public IReadOnlyList<string> MediaTypes { get; } = ["text/html", "application/xhtml+xml"];

    public async ValueTask<DecodedContent?> DecodeAsync(ReadOnlyMemory<byte> data, MediaTypeHeaderValue contentType, CancellationToken cancellationToken)
    {
        var html = await DocumentText.ReadAsync(data, contentType.CharSet, data.Length, cancellationToken);
        return html is not null && ExtractText(html, cancellationToken) is { } text ? new(text) : null;
    }

    // Null means the extracted text exceeded the limit; empty HTML can still be valid.
    internal static string? ExtractText(string html, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var document = new HtmlDocument { OptionMaxNestedChildNodes = 128 };
        document.LoadHtml(html);
        foreach (var node in document.DocumentNode.SelectNodes("//script|//style|//template|//noscript") ?? Enumerable.Empty<HtmlNode>())
            node.Remove();

        var text = new StringBuilder();
        foreach (var node in document.DocumentNode.Descendants())
        {
            ct.ThrowIfCancellationRequested();
            if (node is not HtmlTextNode textNode) continue;
            var line = HtmlEntity.DeEntitize(textNode.Text).Trim();
            if (line.Length != 0 && !DocumentText.TryAppendLine(text, line)) return null;
        }
        return text.ToString();
    }
}
