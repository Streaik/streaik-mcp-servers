using System.Net.Http.Headers;
using System.Text;
using MimeKit;

namespace Streaik.Mcp.Servers.Content;

internal sealed class EmlContentDecoder : IContentDecoder
{
    public IReadOnlyList<string> MediaTypes { get; } = ["message/rfc822"];

    public async ValueTask<DecodedContent?> DecodeAsync(ReadOnlyMemory<byte> data, MediaTypeHeaderValue contentType, CancellationToken cancellationToken)
    {
        using var stream = DocumentText.OpenStream(data);
        using var message = await MimeMessage.LoadAsync(new ParserOptions { MaxMimeDepth = 32 }, stream, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var text = new StringBuilder();
        if (!AppendHeader("From", message.From.ToString()) || !AppendHeader("To", message.To.ToString()) ||
            !AppendHeader("Cc", message.Cc.ToString()) || !AppendHeader("Date", message.Headers[HeaderId.Date]) ||
            !AppendHeader("Subject", message.Subject)) return null;

        var body = message.TextBody;
        if (string.IsNullOrWhiteSpace(body) && message.HtmlBody is { } html)
        {
            body = HtmlContentDecoder.ExtractText(html, cancellationToken);
            if (body is null) return null;
        }
        if (!string.IsNullOrWhiteSpace(body))
        {
            if (text.Length != 0) text.Append('\n');
            if (!DocumentText.TryAppendLine(text, body.Trim())) return null;
        }
        return text.Length == 0 ? null : new(text.ToString());

        bool AppendHeader(string name, string? value) => string.IsNullOrWhiteSpace(value)
            || DocumentText.TryAppendLine(text, $"{name}: {value}");
    }
}
