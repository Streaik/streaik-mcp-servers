using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;

namespace Streaik.Mcp.Servers;

/// <summary>Drive operations for the signed-in user. Provider and download clients never receive Graph credentials.</summary>
internal sealed class DelegatedFiles(IHttpContextAccessor context, IServiceProvider services, IHttpClientFactory clients)
{
    private static readonly Uri Graph = new("https://graph.microsoft.com/");
    internal const string GraphClient = "mcp-graph-files";
    internal const string DownloadClient = "mcp-graph-file-download";
    private const int MaxSkillBytes = 8 * 1024 * 1024;
    private const int MaxSkillFiles = 64;
    private string? token;

    internal bool Enabled => McpHosting.IsManagedProviderRequest(context.HttpContext);

    internal static void AddServices(IServiceCollection services)
    {
        services.AddScoped<DelegatedFiles>();
        foreach (var name in new[] { GraphClient, DownloadClient })
            services.AddHttpClient(name, client => client.Timeout = TimeSpan.FromSeconds(20))
                .RemoveAllLoggers() // Share links and temporary download URLs may contain access secrets.
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false });
    }

    internal static bool IsMicrosoftHost(Uri uri) => uri.IdnHost.EndsWith(".sharepoint.com", StringComparison.OrdinalIgnoreCase)
        || uri.IdnHost.Equals("1drv.ms", StringComparison.OrdinalIgnoreCase);

    internal async Task AuthorizeAsync(CancellationToken ct)
    {
        if (!Enabled || context.HttpContext?.User.Identity?.IsAuthenticated != true ||
            services.GetService<IMcpOnBehalfOf>() is not { } obo)
            throw new DelegatedFileException("This operation requires Microsoft sign-in and delegated file access.");

        try { token ??= await obo.GetAccessTokenAsync(Graph.Host, ct); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or HttpRequestException)
        { throw new DelegatedFileException("Microsoft Graph delegated access was not granted. Check the configured scopes and consent."); }
    }

    internal async Task<ImageInput> ReadImageAsync(Uri uri, CancellationToken ct)
    {
        var item = await ResolveAsync(uri, ct);
        var mime = FileMime(item);
        if (!ImageInputs.Supported(mime)) throw new ArgumentException("The Microsoft link must point to a PNG, JPEG, or WebP file.");
        return new ImageInput(mime!, await DownloadAsync(Drive(item), Id(item), ImageInputs.MaxImageBytes, ct));
    }

    internal async Task<ImageFolder?> PrepareImageDestinationAsync(string? folderUrl, CancellationToken ct)
    {
        await AuthorizeAsync(ct);
        if (folderUrl is null) return null;
        var folder = await ResolveFolderAsync(folderUrl, ct);
        return new ImageFolder(Drive(folder), Id(folder));
    }

    internal async Task<ResourceLinkBlock> SaveImageAsync(ImageInput image, string? filename, ImageFolder? folder, CancellationToken ct)
    {
        // Keep the user-visible basename, but never overwrite a file in the user's drive.
        var stem = string.IsNullOrWhiteSpace(filename) ? "streaik-image" : Path.GetFileNameWithoutExtension(filename);
        if (!SafeSegment(stem)) throw new ArgumentException("Use a simple output filename without path separators or special characters.");
        var name = $"{stem}-{Guid.NewGuid():N}.{image.Extension}";
        var parent = folder is null
            ? $"drives/{Id(await JsonAsync(HttpMethod.Get, new Uri(Graph, "v1.0/me/drive?$select=id"), ct))}/root"
            : $"drives/{folder.DriveId}/items/{folder.ItemId}";
        using var content = new ByteArrayContent(image.Bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(image.Mime);
        var item = await JsonAsync(HttpMethod.Put,
            new Uri(Graph, $"v1.0/{parent}:/{Uri.EscapeDataString(name)}:/content"), ct, content);
        if (!TryMicrosoftUrl(Value(item, "webUrl"), out var uri))
            throw new DelegatedFileException("Microsoft Graph did not return a usable link for the uploaded image.");

        return new ResourceLinkBlock { Uri = uri.AbsoluteUri, Name = name, MimeType = image.Mime, Size = image.Bytes.Length };
    }

    internal async Task<IReadOnlyList<SkillFile>> ReadSkillFolderAsync(string folderUrl, CancellationToken ct)
    {
        var folder = await ResolveFolderAsync(folderUrl, ct);
        var drive = Drive(folder);
        var root = Name(folder);
        var candidates = new List<(string Path, string ItemId, string Mime)>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        await CollectAsync(Id(folder), root, 0);
        if (!candidates.Any(file => file.Path == root + "/SKILL.md"))
            throw new ArgumentException("The folder must contain a top-level SKILL.md file.");

        var result = new List<SkillFile>(candidates.Count);
        var remaining = MaxSkillBytes;
        foreach (var file in candidates)
        {
            var bytes = await DownloadAsync(drive, file.ItemId, remaining, ct);
            remaining -= bytes.Length;
            result.Add(new SkillFile(file.Path, file.Mime, bytes));
        }
        return result;

        async Task CollectAsync(string folderId, string path, int depth)
        {
            if (depth > 8 || !visited.Add(folderId) || ++count > 256)
                throw new ArgumentException("The skill folder tree exceeds the supported limits.");

            var children = new Uri(Graph, $"v1.0/drives/{drive}/items/{folderId}/children");
            var next = children;
            var pages = 0;
            while (true)
            {
                if (++pages > 32 || !SameGraphPath(next, children))
                    throw new DelegatedFileException("Microsoft Graph returned invalid folder pagination.");
                var page = await JsonAsync(HttpMethod.Get, next, ct);
                if (!page.TryGetProperty("value", out var values) || values.ValueKind != JsonValueKind.Array)
                    throw new DelegatedFileException("Microsoft Graph returned an invalid folder listing.");
                foreach (var child in values.EnumerateArray())
                {
                    if (++count > 256) throw new ArgumentException("The skill folder has too many entries.");
                    // A shortcut can point outside the selected folder; do not follow it.
                    if (IsObject(child, "remoteItem") || IsObject(child, "package")) continue;
                    var childPath = path + "/" + Name(child);
                    if (IsObject(child, "folder")) await CollectAsync(Id(child), childPath, depth + 1);
                    else if (FileMime(child) is { } mime)
                    {
                        if (candidates.Count >= MaxSkillFiles) throw new ArgumentException("A skill folder can contain at most 64 files.");
                        candidates.Add((childPath, Id(child), mime));
                    }
                }
                if (Value(page, "@odata.nextLink") is not { } url) break;
                if (!Uri.TryCreate(url, UriKind.Absolute, out next!))
                    throw new DelegatedFileException("Microsoft Graph returned invalid folder pagination.");
            }
        }
    }

    private async Task<JsonElement> ResolveFolderAsync(string folderUrl, CancellationToken ct)
    {
        if (!TryMicrosoftUrl(folderUrl, out var uri)) throw new ArgumentException("Supply an HTTPS SharePoint or OneDrive folder link.");
        var folder = await ResolveAsync(uri, ct);
        return IsObject(folder, "folder") ? folder : throw new ArgumentException("The Microsoft link must point to a folder.");
    }

    private async Task<JsonElement> ResolveAsync(Uri uri, CancellationToken ct)
    {
        if (!TryMicrosoftUrl(uri.AbsoluteUri, out _)) throw new ArgumentException("Supply an HTTPS SharePoint or OneDrive file link.");
        var share = "u!" + Convert.ToBase64String(Encoding.UTF8.GetBytes(uri.AbsoluteUri)).TrimEnd('=').Replace('/', '_').Replace('+', '-');
        return await JsonAsync(HttpMethod.Get, new Uri(Graph, $"v1.0/shares/{share}/driveItem"), ct);
    }

    private async Task<byte[]> DownloadAsync(string drive, string item, int limit, CancellationToken ct)
    {
        if (limit <= 0) throw new ArgumentException("The files exceed the upload size limit.");
        using var timeout = Deadline(ct);
        using var graph = await SendAsync(HttpMethod.Get, new Uri(Graph, $"v1.0/drives/{drive}/items/{item}/content"), timeout.Token);
        if (graph.StatusCode is not (HttpStatusCode.Found or HttpStatusCode.TemporaryRedirect))
        {
            DemandSuccess(graph);
            return await ImageInputs.ReadBoundedAsync(graph.Content, limit, timeout.Token);
        }

        // Only Graph can provide the preauthenticated URL. Do not follow further redirects or attach any headers from Graph.
        if (graph.Headers.Location is not { IsAbsoluteUri: true } location || !SafeWebUrl(location) ||
            !location.IdnHost.EndsWith(".sharepoint.com", StringComparison.OrdinalIgnoreCase))
            throw new DelegatedFileException("Microsoft Graph returned an unsupported file download URL.");
        using var client = clients.CreateClient(DownloadClient);
        using var response = await client.GetAsync(location, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        DemandSuccess(response);
        return await ImageInputs.ReadBoundedAsync(response.Content, limit, timeout.Token);
    }

    private async Task<JsonElement> JsonAsync(HttpMethod method, Uri uri, CancellationToken ct, HttpContent? content = null)
    {
        using var timeout = Deadline(ct);
        using var response = await SendAsync(method, uri, timeout.Token, content);
        DemandSuccess(response);
        using var json = JsonDocument.Parse(await ImageInputs.ReadBoundedAsync(response.Content, 512 * 1024, timeout.Token));
        return json.RootElement.Clone();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri uri, CancellationToken ct, HttpContent? content = null)
    {
        if (!SafeWebUrl(uri) || uri.IdnHost != Graph.Host) throw new DelegatedFileException("Invalid Microsoft Graph endpoint.");
        await AuthorizeAsync(ct);
        using var client = clients.CreateClient(GraphClient);
        using var request = new HttpRequestMessage(method, uri) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private static void DemandSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode) throw new DelegatedFileException(response.StatusCode switch
        {
            HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized => "Microsoft Graph denied access. Check the signed-in user's file permissions and delegated consent.",
            HttpStatusCode.NotFound => "Microsoft Graph could not find the file or folder.",
            _ => $"The Microsoft file operation failed (HTTP {(int)response.StatusCode}).",
        });
    }

    private static CancellationTokenSource Deadline(CancellationToken ct)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        return timeout;
    }

    private static string? Value(JsonElement item, string name) => item.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
        ? property.GetString() : null;
    private static bool IsObject(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object;
    private static string? FileMime(JsonElement item) => IsObject(item, "file")
        ? Value(item.GetProperty("file"), "mimeType") ?? "application/octet-stream" : null;
    private static string Id(JsonElement item) => EncodeId(Value(item, "id"));
    private static string Drive(JsonElement item) => EncodeId(IsObject(item, "parentReference") ? Value(item.GetProperty("parentReference"), "driveId") : null);
    private static string EncodeId(string? value) => value is { Length: > 0 and <= 256 } &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '!' or '_' or '-' or '=')
        ? Uri.EscapeDataString(value) : throw new DelegatedFileException("Microsoft Graph returned an invalid file identifier.");
    private static string Name(JsonElement item) => Value(item, "name") is { } name && SafeSegment(name) ? name
        : throw new DelegatedFileException("The folder contains an unsupported filename.");
    private static bool SafeSegment(string name) => name is { Length: > 0 and <= 160 } && name == name.Trim() && !name.EndsWith('.') &&
        !name.Any(c => char.IsControl(c) || "/\\:\"<>|?*".Contains(c));
    private static bool TryMicrosoftUrl(string? value, out Uri uri)
    {
        uri = null!;
        if (value is not { Length: > 0 and <= 4096 } || !Uri.TryCreate(value, UriKind.Absolute, out var parsed) ||
            !SafeWebUrl(parsed) || !IsMicrosoftHost(parsed)) return false;
        uri = parsed;
        return true;
    }
    private static bool SafeWebUrl(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort && uri.HostNameType == UriHostNameType.Dns &&
        uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;
    private static bool SameGraphPath(Uri uri, Uri expected) => SafeWebUrl(uri) && uri.IdnHost == Graph.Host &&
        uri.AbsolutePath.Equals(expected.AbsolutePath, StringComparison.Ordinal);
}

internal sealed record SkillFile(string Path, string Mime, byte[] Bytes);
internal sealed record ImageFolder(string DriveId, string ItemId);
internal sealed class DelegatedFileException(string message) : Exception(message);
