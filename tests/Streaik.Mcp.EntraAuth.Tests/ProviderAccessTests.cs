using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Streaik.Mcp.Servers;

namespace Streaik.Mcp.EntraAuth.Tests;

public sealed partial class BridgeTests
{
    private const string Skills = Base + "/anthropic-skills";
    private const string Photo = "https://team.sharepoint.com/sites/media/photo.png";
    private const string Folder = "https://team.sharepoint.com/sites/tools/draft";
    private const string ImageBytes = "iVBORw0KGgo=";

    [Fact]
    public async Task MixedHostUsesManagedAnthropicAndCallerGoogleWithoutChangingTheToolRoutes()
    {
        using var anthropic = new ProviderStub((request, _) =>
        {
            Assert.Equal("central-anthropic", request.Headers.GetValues("x-api-key").Single());
            Assert.Null(request.Headers.Authorization);
            return Task.FromResult(JsonResponse(new { data = Array.Empty<object>() }));
        });
        using var google = new ProviderStub((request, _) =>
        {
            Assert.Equal("user-google", request.Headers.GetValues("x-goog-api-key").Single());
            Assert.Null(request.Headers.Authorization);
            return Task.FromResult(ImageResponse(google: true));
        });
        await using var host = await Fixture.StartAsync(overrides: new() { ["Providers:google:ApiKey"] = null }, configureServices: services =>
        {
            UseStub(services, "mcp-anthropic", anthropic);
            UseStub(services, "mcp-google", google);
        });
        var access = await host.Client.GetFromJsonAsync<JsonElement>("/team/mcp/.well-known/streaik-mcp-access");
        Assert.True(access.GetProperty("graphObo").GetBoolean());
        Assert.Equal("delegated", access.GetProperty("providerModes").GetProperty("anthropic-skills").GetString());
        Assert.Equal("headers", access.GetProperty("providerModes").GetProperty("google-images").GetString());
        Assert.DoesNotContain("central-", access.GetRawText());
        using var googleMetadata = await host.Client.GetAsync("/.well-known/oauth-protected-resource/team/mcp/google-images");
        Assert.Equal(HttpStatusCode.NotFound, googleMetadata.StatusCode);

        var anthHeader = new Dictionary<string, string> { ["x-api-key"] = "wrong-caller-key" };
        using var noSignIn = await host.McpAsync(Skills, headers: anthHeader);
        Assert.Equal(HttpStatusCode.Unauthorized, noSignIn.StatusCode);
        var token = await ManagedToken(host, Skills);
        const string list = "AnthropicSkills_List";
        var managed = await ProviderCall(host, Skills, list, new { }, token, anthHeader);
        Assert.False(IsError(managed), managed.GetRawText());
        Assert.Equal(1, anthropic.Calls);

        const string create = "GoogleNanoBanana_CreateImage";
        var missing = await ProviderCall(host, Images, create, new { prompt = "Comet" });
        Assert.True(IsError(missing));
        var googleHeader = new Dictionary<string, string> { ["x-goog-api-key"] = "user-google" };
        var denied = await ProviderCall(host, Images, create, new { prompt = "Restyle", fileUrl = Photo }, headers: googleHeader);
        Assert.True(IsError(denied));
        Assert.Equal(0, google.Calls);
        var byok = await ProviderCall(host, Images, create, new { prompt = "Comet" }, headers: googleHeader);
        Assert.Equal("image", byok.GetProperty("content")[0].GetProperty("type").GetString());
        Assert.Equal(1, google.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ManagedImageUsesProtectedInputAndReturnsOnlyAnAccessControlledDriveLink(bool google)
    {
        const string link = "https://team-my.sharepoint.com/personal/user/Image.png";
        using var graph = new ProviderStub(async (request, ct) =>
        {
            Assert.Equal("downstream-token", request.Headers.Authorization?.Parameter);
            var path = request.RequestUri!.AbsolutePath;
            if (path.StartsWith("/v1.0/shares/u!", StringComparison.Ordinal))
                return JsonResponse(new { id = "photo", parentReference = new { driveId = "shared-drive" }, file = new { mimeType = "image/png" } });
            if (path == "/v1.0/drives/shared-drive/items/photo/content")
                return new HttpResponseMessage(HttpStatusCode.Found)
                {
                    Headers = { Location = new Uri("https://team.sharepoint.com/download?ticket=example") },
                };
            if (path == "/v1.0/me/drive") return JsonResponse(new { id = "personal-drive" });
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.StartsWith("/v1.0/drives/personal-drive/root:/streaik-image-", path, StringComparison.Ordinal);
            Assert.Equal(ImageBytes, Convert.ToBase64String(await request.Content!.ReadAsByteArrayAsync(ct)));
            return JsonResponse(new { webUrl = link });
        });
        using var download = new ProviderStub((request, _) =>
        {
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("x-goog-api-key"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Convert.FromBase64String(ImageBytes)) });
        });
        using var provider = new ProviderStub(async (request, ct) =>
        {
            Assert.Equal(google ? "central-google" : "central-openai", google ? request.Headers.GetValues("x-goog-api-key").Single() : request.Headers.Authorization?.Parameter);
            Assert.Equal(google ? "/v1beta/interactions" : "/v1/images/edits", request.RequestUri!.AbsolutePath);
            var body = await request.Content!.ReadAsStringAsync(ct);
            Assert.Contains(google ? ImageBytes : "input.png", body);
            Assert.DoesNotContain(Photo, body);
            return ImageResponse(google);
        });
        await using var host = await Fixture.StartAsync(configureServices: services =>
        {
            UseStub(services, google ? "mcp-google" : "openai", provider);
            UseStub(services, "mcp-graph-files", graph);
            UseStub(services, "mcp-graph-file-download", download);
        });
        var endpoint = google ? Images : Default;
        var token = await ManagedToken(host, endpoint);
        var tool = google ? "GoogleNanoBanana_CreateImage" : "OpenAIImages_CreateImageEdit";
        var result = await ProviderCall(host, endpoint, tool, new { prompt = "Restyle", fileUrl = Photo }, token);
        Assert.False(IsError(result), result.GetRawText());
        var content = Assert.Single(result.GetProperty("content").EnumerateArray());
        Assert.Equal("resource_link", content.GetProperty("type").GetString());
        Assert.Equal(link, content.GetProperty("uri").GetString());
        Assert.Equal(1, provider.Calls);
        Assert.Equal(4, graph.Calls);
        Assert.Equal(1, download.Calls);
    }

    [Fact]
    public async Task DeniedGraphConsentDoesNotRunProviderOrFallBackToCallerKey()
    {
        using var provider = new ProviderStub((_, _) => throw new InvalidOperationException("Unexpected provider call"));
        await using var host = await Fixture.StartAsync(configureServices: services => UseStub(services, "mcp-google", provider));
        var token = await ManagedToken(host, Images);
        host.Entra.DenyObo = true;
        var result = await ProviderCall(host, Images, "GoogleNanoBanana_CreateImage", new { prompt = "Comet" }, token,
            new Dictionary<string, string> { ["x-goog-api-key"] = "caller-key" });
        Assert.True(IsError(result));
        Assert.Contains("consent", result.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal(0, provider.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyManagedAnthropicPublishesFolderWriteToolsAndUsesRelativeMultipartFilenames(bool version)
    {
        using var graph = new ProviderStub((request, _) =>
        {
            Assert.Equal("downstream-token", request.Headers.Authorization?.Parameter);
            var path = request.RequestUri!.AbsolutePath;
            var requestUri = request.RequestUri;
            return Task.FromResult(path switch
            {
                var p when p.StartsWith("/v1.0/shares/u!", StringComparison.Ordinal) =>
                    JsonResponse(new { id = "root", name = "draft", parentReference = new { driveId = "drive" }, folder = new { } }),
                "/v1.0/drives/drive/items/root/children" when requestUri.Query == "" => JsonResponse(new Dictionary<string, object>
                {
                    ["value"] = new[] { new { id = "def", name = "SKILL.md", file = new { mimeType = "text/markdown" } } },
                    ["@odata.nextLink"] = "https://graph.microsoft.com/v1.0/drives/drive/items/root/children?$skiptoken=next",
                }),
                "/v1.0/drives/drive/items/root/children" => JsonResponse(new { value = new[] { new { id = "docs", name = "docs", folder = new { } } } }),
                "/v1.0/drives/drive/items/docs/children" => JsonResponse(new { value = new[] { new { id = "readme", name = "guide.md", file = new { mimeType = "text/markdown" } } } }),
                "/v1.0/drives/drive/items/def/content" or "/v1.0/drives/drive/items/readme/content" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("test content") },
                _ => throw new InvalidOperationException($"Unexpected Graph path: {path}"),
            });
        });
        using var anthropic = new ProviderStub((request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(version ? "/v1/skills/skill_123/versions" : "/v1/skills", request.RequestUri!.AbsolutePath);
            Assert.Equal("central-anthropic", request.Headers.GetValues("x-api-key").Single());
            Assert.Null(request.Headers.Authorization);
            Assert.Contains("skills-2025-10-02", request.Headers.GetValues("anthropic-beta"));
            var form = Assert.IsType<MultipartFormDataContent>(request.Content);
            Assert.Equal(new[] { "draft/SKILL.md", "draft/docs/guide.md" }, form.Where(part => part.Headers.ContentDisposition?.Name?.Trim('"') == "files[]")
                .Select(part => part.Headers.ContentDisposition!.FileName!.Trim('"')));
            Assert.Equal(!version, form.Any(part => part.Headers.ContentDisposition?.Name?.Trim('"') == "display_title"));
            return Task.FromResult(JsonResponse(new { id = "skill_123" }));
        });
        await using var host = await Fixture.StartAsync(configureServices: services =>
        {
            UseStub(services, "mcp-anthropic", anthropic);
            UseStub(services, "mcp-graph-files", graph);
        });
        await using var header = await Fixture.StartAsync(headerAuth: true);
        var token = await ManagedToken(host, Skills);
        var managedTools = Result(await ReadRpc(await host.McpAsync(Skills, token))).GetProperty("tools");
        var headerTools = Result(await ReadRpc(await header.McpAsync("https://localhost/anthropic-skills"))).GetProperty("tools");
        const string create = "AnthropicSkills_Create";
        const string createVersion = "AnthropicSkills_CreateVersion";
        foreach (var tool in new[] { create, createVersion })
        {
            Assert.Contains(managedTools.EnumerateArray(), item => item.GetProperty("name").GetString() == tool);
            Assert.DoesNotContain(headerTools.EnumerateArray(), item => item.GetProperty("name").GetString() == tool);
        }

        var result = version ? await ProviderCall(host, Skills, createVersion, new { skillId = "skill_123", folderUrl = Folder }, token)
            : await ProviderCall(host, Skills, create, new { displayTitle = "Example", folderUrl = Folder }, token);
        Assert.False(IsError(result), result.GetRawText());
        Assert.Equal(1, anthropic.Calls);
        Assert.Equal(6, graph.Calls);
    }

    [Fact]
    public void CentralKeyRequiresOAuthConfigurationAndIsFixedAtStartup()
    {
        var configuration = new ConfigurationManager();
        configuration["DomainHeaders:api.openai.com:Authorization"] = "Bearer central-key";
        var access = McpHosting.ResolveProviderAccess(configuration);
        Assert.True(access.IsManaged("openai-images"));
        Assert.False(access.IsManaged("google-images"));
        Assert.Throws<InvalidOperationException>(() => EntraProfiles.Read(configuration, access));
        configuration["DomainHeaders:api.openai.com:Authorization"] = null;
        Assert.True(access.IsManaged("openai-images"));
        Assert.False(McpHosting.ResolveProviderAccess(configuration).IsManaged("openai-images"));
    }

    private static void UseStub(IServiceCollection services, string name, ProviderStub stub) =>
        services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => stub);

    private static Task<string> ManagedToken(Fixture host, string endpoint) => endpoint == Images
        ? WrapToken(host, endpoint, "images", ImagesApp) : WrapToken(host, endpoint, "default", DefaultApp);

    private static async Task<string> WrapToken(Fixture host, string endpoint, string profile, string application) =>
        await ReadWrapped(await host.TokenAsync(profile, Exchange(host.Jwt(application, Spa), endpoint)));

    private static async Task<JsonElement> ProviderCall(Fixture host, string endpoint, string tool, object arguments,
        string? token = null, IReadOnlyDictionary<string, string>? headers = null) =>
        Result(await ReadRpc(await host.McpAsync(endpoint, token, "tools/call", tool, Tool(tool, arguments), headers: headers)));

    private static bool IsError(JsonElement result) => result.TryGetProperty("isError", out var error) && error.GetBoolean();
    private static HttpResponseMessage JsonResponse(object payload) => new(HttpStatusCode.OK) { Content = JsonContent.Create(payload) };
    private static HttpResponseMessage ImageResponse(bool google) => google ? JsonResponse(new { outputs = new[] { new { type = "image", data = ImageBytes, mime_type = "image/png" } } })
        : JsonResponse(new { data = new[] { new { b64_json = ImageBytes } } });

    private sealed class ProviderStub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return send(request, ct);
        }
    }
}
