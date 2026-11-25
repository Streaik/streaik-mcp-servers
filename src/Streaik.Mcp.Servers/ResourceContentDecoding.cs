using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Streaik.Mcp.Servers.Content;

namespace Streaik.Mcp.Servers;

internal sealed class ResourceContentDecoding(IEnumerable<IContentDecoder> decoders, ILogger<ResourceContentDecoding> logger)
{
    internal const int MaxBlobBytes = 8 * 1024 * 1024;
    private const int MaxEncodedBytes = ((MaxBlobBytes + 2) / 3) * 4;
    // Later registrations can override a built-in for one media type without affecting its other types.
    private readonly Dictionary<string, IContentDecoder> _decoders = decoders
        .SelectMany(decoder => decoder.MediaTypes.Select(type => (Type: type, Decoder: decoder)))
        .GroupBy(item => item.Type, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.Last().Decoder, StringComparer.OrdinalIgnoreCase);

    internal static void AddServices(IServiceCollection services)
    {
        services.TryAddScoped<ResourceContentDecoding>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IContentDecoder, PdfContentDecoder>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IContentDecoder, RtfContentDecoder>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IContentDecoder, HtmlContentDecoder>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IContentDecoder, JsonContentDecoder>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IContentDecoder, TextContentDecoder>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IContentDecoder, EmlContentDecoder>());
    }

    internal static McpRequestHandler<ReadResourceRequestParams, ReadResourceResult> Filter(
        McpRequestHandler<ReadResourceRequestParams, ReadResourceResult> next) => async (request, ct) =>
    {
        var result = await next(request, ct);
        return await request.Services!.GetRequiredService<ResourceContentDecoding>().DecodeAsync(result, ct);
    };

    private async ValueTask<ReadResourceResult> DecodeAsync(ReadResourceResult result, CancellationToken ct)
    {
        ResourceContents[]? contents = null;
        for (var i = 0; i < result.Contents.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (result.Contents[i] is BlobResourceContents blob && await TryDecodeAsync(blob, ct) is { } decoded)
            {
                contents ??= result.Contents.ToArray();
                contents[i] = new TextResourceContents { Uri = blob.Uri, MimeType = decoded.MimeType, Text = decoded.Text, Meta = blob.Meta };
            }
        }
        ct.ThrowIfCancellationRequested();

        // Handlers can reuse read results; don't mutate them or their entries.
        return contents is null ? result : new ReadResourceResult
        {
            Contents = contents,
            Meta = result.Meta?.DeepClone().AsObject(),
            CacheScope = result.CacheScope,
            TimeToLive = result.TimeToLive,
        };
    }

    private async ValueTask<DecodedContent?> TryDecodeAsync(BlobResourceContents blob, CancellationToken ct)
    {
        if (!MediaTypeHeaderValue.TryParse(blob.MimeType, out var mediaType) || mediaType.MediaType is not { } type ||
            !_decoders.TryGetValue(type, out var decoder) || IsMcpAppHtml(type, mediaType)) return null;

        // The SDK's Blob holds Base64 bytes, not the underlying document.
        if (blob.Blob.Length is 0 or > MaxEncodedBytes) return null;

        try
        {
            var data = blob.DecodedData;
            if (data.Length is 0 or > MaxBlobBytes) return null;

            var decoded = await decoder.DecodeAsync(data, mediaType, ct);
            ct.ThrowIfCancellationRequested();
            return decoded is { Text.Length: <= DocumentText.MaxCharacters } && !string.IsNullOrWhiteSpace(decoded.Text)
                ? decoded : null;
        }
        catch (Exception e) when (e is not (OperationCanceledException or OutOfMemoryException))
        {
            // Parser failures are a fallback, not a resource error. Don't log parser messages or document contents.
            logger.LogDebug("Could not decode a resource of media type {MediaType} ({ErrorType}).", type, e.GetType().Name);
            return null;
        }
    }

    private static bool IsMcpAppHtml(string type, MediaTypeHeaderValue mediaType) =>
        type.Equals("text/html", StringComparison.OrdinalIgnoreCase) && mediaType.Parameters.Any(parameter =>
            parameter.Name.Equals("profile", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(parameter.Value?.Trim('"'), "mcp-app", StringComparison.OrdinalIgnoreCase));
}
