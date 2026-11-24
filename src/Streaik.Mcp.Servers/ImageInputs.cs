using Microsoft.Extensions.Configuration;

namespace Streaik.Mcp.Servers;

internal sealed class ImageInputs(IHttpClientFactory clients, IConfiguration configuration)
{
    internal const int MaxImageBytes = 5 * 1024 * 1024;
    internal const int MaxImages = 4;

    public async Task<ImageInput[]> ReadAsync(string[] urls, CancellationToken ct)
    {
        if (urls.Length is 0 or > MaxImages)
            throw new ArgumentException($"Supply between 1 and {MaxImages} images.");
        var images = new ImageInput[urls.Length];
        for (var i = 0; i < urls.Length; i++) images[i] = await ReadAsync(urls[i], ct);
        return images;
    }

    public async Task<ImageInput> ReadAsync(string value, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("An image URL is required.");
        if (value.StartsWith("data:", StringComparison.Ordinal))
        {
            var comma = value.IndexOf(',');
            if (comma is < 13 or > 40 || !value.AsSpan(0, comma).EndsWith(";base64", StringComparison.Ordinal))
                throw new ArgumentException("Use a base64 image data URL.");
            return new(value[5..(comma - 7)], Decode(value[(comma + 1)..]));
        }

        var hosts = configuration.GetSection("Mcp:ImageHosts").Get<string[]>() ?? [];
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url) || url.Scheme != "https" || !url.IsDefaultPort ||
            url.HostNameType != UriHostNameType.Dns || url.UserInfo.Length != 0 || url.Fragment.Length != 0 ||
            !hosts.Contains(url.IdnHost, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Use an image data URL or an HTTPS URL on a configured image host.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var client = clients.CreateClient("mcp-media");
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode) throw new ArgumentException("Could not read the image from its host.");
        var mime = response.Content.Headers.ContentType?.MediaType;
        if (!Supported(mime)) throw new ArgumentException("Only PNG, JPEG, and WebP images are supported.");
        return new(mime!, await ReadBoundedAsync(response.Content, MaxImageBytes, timeout.Token));
    }

    internal static bool Supported(string? mime) => mime is "image/png" or "image/jpeg" or "image/webp";

    internal static byte[] Decode(string value)
    {
        if (value.Length > (MaxImageBytes + 2) / 3 * 4) throw new ArgumentException("Image exceeds the 5 MiB limit.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(value); }
        catch (FormatException) { throw new ArgumentException("Image data is not valid base64."); }
        if (bytes.Length is 0 or > MaxImageBytes) throw new ArgumentException("Invalid image size (limit: 5 MiB).");
        return bytes;
    }

    internal static async Task<byte[]> ReadBoundedAsync(HttpContent content, int limit, CancellationToken ct)
    {
        if (content.Headers.ContentLength > limit) throw new ArgumentException("The response exceeds the size limit.");
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > limit) throw new ArgumentException("The response exceeds the size limit.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}

internal sealed record ImageInput
{
    public string Mime { get; }
    public byte[] Bytes { get; }
    public string DataUrl => $"data:{Mime};base64,{Convert.ToBase64String(Bytes)}";
    public string Extension => Mime switch { "image/jpeg" => "jpg", "image/webp" => "webp", _ => "png" };

    public ImageInput(string mime, byte[] bytes)
    {
        if (!ImageInputs.Supported(mime)) throw new ArgumentException("Only PNG, JPEG, and WebP images are supported.");
        if (bytes.Length is 0 or > ImageInputs.MaxImageBytes) throw new ArgumentException("Invalid image size (limit: 5 MiB).");
        Mime = mime;
        Bytes = bytes;
    }
}
