using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Streaik.Mcp.Servers;

namespace Streaik.Mcp.EntraAuth.Tests;

public sealed class BridgeTests
{
    private const string Base = "https://tools.example.test/team/mcp";
    private const string Tenant = "11111111-1111-4111-8111-111111111111";
    private const string DefaultApp = "22222222-2222-4222-8222-222222222222";
    private const string ImagesApp = "33333333-3333-4333-8333-333333333333";
    private const string Spa = "44444444-4444-4444-8444-444444444444";
    private const string User = "55555555-5555-4555-8555-555555555555";
    private const string Redirect = "https://client.example.test/oauth/callback";
    private const string AccessTokenType = "urn:ietf:params:oauth:token-type:access_token";
    private const string Images = Base + "/google-images";
    private const string Default = Base + "/openai-images";
    private const string OtherDefault = Base + "/anthropic-skills";
    private const string Mail = Base + "/microsoft-outlook-mail";
    private const string LegacyAudience = "api://legacy-mcp.test";
    private const string Inbox = "https://graph.microsoft.com/beta/me/mailFolders/Inbox/messages?$top=15&$select=id,subject,from,receivedDateTime,isRead";

    [Fact]
    public async Task AllowlistedDirectV1BearerListsOutlookResourcesAndUsesOBO()
    {
        await using var host = await Fixture.StartAsync(directV1: true);
        var assertion = host.Entra.Jwt(LegacyAudience, Spa, v1: true);
        var listed = await ReadRpc(await host.McpAsync(Mail, assertion, "resources/list"));
        Assert.NotEmpty(listed.GetProperty("result").GetProperty("resources").EnumerateArray());
        Assert.Equal(0, host.Graph.Calls);

        var body = await ReadRpc(await host.McpAsync(Mail, assertion, "resources/read", Inbox, new() { ["uri"] = Inbox }));
        Assert.Equal("graph-test-response", body.GetProperty("result").GetProperty("contents")[0].GetProperty("text").GetString());
        Assert.Equal(assertion, host.Entra.LastForm["assertion"].ToString());
        Assert.Equal(DefaultApp, host.Entra.LastForm["client_id"].ToString());
        Assert.Equal("on_behalf_of", host.Entra.LastForm["requested_token_use"].ToString());
        Assert.Equal(McpCatalog.Servers["microsoft-outlook-mail"].Obo![GraphReads.Target], host.Entra.LastForm["scope"].ToString());
        Assert.Equal("downstream-token", host.Graph.LastBearer);
        Assert.DoesNotContain("downstream-token", body.GetRawText());
    }

    [Fact]
    public async Task DirectV1RejectsWrongAudienceCallerIssuerTenantScopeSignatureAndExpiry()
    {
        await using var host = await Fixture.StartAsync(directV1: true);
        using var untrusted = new FakeEntra();
        var valid = host.Entra.Jwt(LegacyAudience, Spa, v1: true);
        var rejected = new[]
        {
            host.Entra.Jwt("https://graph.microsoft.com", Spa, v1: true),
            host.Entra.Jwt(LegacyAudience, ImagesApp, v1: true),
            host.Entra.Jwt(LegacyAudience, Spa, tenant: ImagesApp, v1: true),
            host.Entra.Jwt(LegacyAudience, Spa, issuer: $"https://login.microsoftonline.com/{Tenant}/v2.0", v1: true),
            host.Entra.Jwt(LegacyAudience, Spa, scope: "other", v1: true),
            host.Entra.Jwt(LegacyAudience, Spa, scope: null, v1: true),
            host.Entra.Jwt(LegacyAudience, Spa, expires: DateTime.UtcNow.AddMinutes(-1), v1: true),
            untrusted.Jwt(LegacyAudience, Spa, v1: true),
            host.Jwt(DefaultApp, Spa),
            "mcp1.invalid",
        };
        foreach (var token in rejected)
            Assert.Equal(HttpStatusCode.Unauthorized, (await host.McpAsync(Mail, token, "resources/list")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.McpAsync(Images, valid)).StatusCode);
        Assert.Equal(0, host.Graph.Calls);
    }

    [Fact]
    public async Task V1CanStillBeWrappedAndIsNotAcceptedDirectlyWithoutTheDirectAllowlist()
    {
        await using var host = await Fixture.StartAsync();
        var assertion = host.Entra.Jwt(LegacyAudience, Spa, v1: true);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.McpAsync(Mail, assertion, "resources/list")).StatusCode);
        var wrapped = await ReadWrapped(await host.TokenAsync("default", Exchange(assertion, Mail)));
        Assert.Equal(HttpStatusCode.OK, (await host.McpAsync(Mail, wrapped, "resources/list")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.McpAsync(Default, wrapped)).StatusCode);
    }

    [Theory]
    [InlineData("microsoft-outlook-calendar", "https://graph.microsoft.com/beta/me/calendar/calendarView?startDateTime=2026-11-24T00%3A00%3A00Z&endDateTime=2026-11-25T00%3A00%3A00Z&$select=subject,start,end,location,organizer,isOnlineMeeting")]
    [InlineData("microsoft-outlook-mail", "https://graph.microsoft.com/beta/me/mailFolders/Inbox/messages?$top=15&$select=id,subject,from,receivedDateTime,isRead")]
    [InlineData("microsoft-onenote", "https://graph.microsoft.com/beta/me/onenote/pages/0-abcd!123/content")]
    public async Task CatalogCapabilitiesReadThroughResourceBoundDelegation(string id, string uri)
    {
        await using var host = await Fixture.StartAsync();
        var endpoint = Base + "/" + id;
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.McpAsync(endpoint, method: "resources/list")).StatusCode);
        var metadata = await host.Client.GetFromJsonAsync<JsonElement>("/.well-known/oauth-protected-resource/team/mcp/" + id);
        Assert.Equal(endpoint, metadata.GetProperty("resource").GetString());
        Assert.Equal(Base + "/oauth/default", metadata.GetProperty("authorization_servers")[0].GetString());
        var assertion = host.Jwt(DefaultApp, Spa);
        var token = await ReadWrapped(await host.TokenAsync("default", Exchange(assertion, endpoint)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.McpAsync(Default, token)).StatusCode);
        var another = McpCatalog.Servers.Values.First(entry => entry.Obo is not null && entry.Id != id);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.McpAsync(Base + "/" + another.Id, token, "resources/list")).StatusCode);

        var definition = McpCatalog.Servers[id].Capabilities;
        var discovered = (await ReadRpc(await host.McpAsync(endpoint, token, "server/discover"))).GetProperty("result");
        var capabilities = discovered.GetProperty("capabilities");
        Assert.True(capabilities.TryGetProperty("resources", out _));
        Assert.Equal(definition.GetProperty("prompts").GetArrayLength() > 0, capabilities.TryGetProperty("prompts", out _));
        Assert.False(capabilities.TryGetProperty("tools", out _));
        foreach (var (method, property) in new[] { ("resources/list", "resources"), ("resources/templates/list", "resourceTemplates") })
        {
            var result = (await ReadRpc(await host.McpAsync(endpoint, token, method))).GetProperty("result");
            Assert.Equal("complete", result.GetProperty("resultType").GetString());
            Assert.Equal("private", result.GetProperty("cacheScope").GetString());
            Assert.Equal(0, result.GetProperty("ttlMs").GetInt64());
            var listed = result.GetProperty(property);
            Assert.True(JsonElement.DeepEquals(definition.GetProperty(property), listed), listed.GetRawText());
        }

        Assert.Equal(0, host.Graph.Calls);
        var invalid = await ReadRpc(await host.McpAsync(endpoint, token, "resources/read", "https://graph.microsoft.com/beta/me", new() { ["uri"] = "https://graph.microsoft.com/beta/me" }));
        Assert.True(invalid.TryGetProperty("error", out _));
        Assert.Equal(0, host.Graph.Calls);

        var body = await ReadRpc(await host.McpAsync(endpoint, token, "resources/read", uri, new() { ["uri"] = uri }));
        var read = body.GetProperty("result");
        Assert.Equal("complete", read.GetProperty("resultType").GetString());
        Assert.Equal("private", read.GetProperty("cacheScope").GetString());
        Assert.Equal(0, read.GetProperty("ttlMs").GetInt64());
        var content = read.GetProperty("contents")[0];
        Assert.Equal(uri, content.GetProperty("uri").GetString());
        Assert.Equal(id == "microsoft-onenote" ? "text/plain" : "application/json", content.GetProperty("mimeType").GetString());
        Assert.Equal("graph-test-response", content.GetProperty("text").GetString());
        Assert.Equal(new Uri(uri), host.Graph.LastUri);
        Assert.Equal("downstream-token", host.Graph.LastBearer);
        Assert.DoesNotContain("downstream-token", body.GetRawText());
        Assert.Equal(assertion, host.Entra.LastForm["assertion"].ToString());
        Assert.Equal("on_behalf_of", host.Entra.LastForm["requested_token_use"].ToString());
        Assert.Equal(DefaultApp, host.Entra.LastForm["client_id"].ToString());
        Assert.Equal(McpCatalog.Servers[id].Obo![GraphReads.Target], host.Entra.LastForm["scope"].ToString());
    }

    [Fact]
    public async Task CatalogPromptsKeepMetadataAndRenderOriginalText()
    {
        await using var host = await Fixture.StartAsync();
        const string id = "microsoft-outlook-calendar";
        const string name = "daily-agenda-with-context";
        var endpoint = Base + "/" + id;
        var token = await ReadWrapped(await host.TokenAsync("default", Exchange(host.Jwt(DefaultApp, Spa), endpoint)));
        var list = await ReadRpc(await host.McpAsync(endpoint, token, "prompts/list"));
        var original = McpCatalog.Servers[id].Capabilities.GetProperty("prompts")[0];
        Assert.True(JsonElement.DeepEquals(original.GetProperty("template"), list.GetProperty("result").GetProperty("prompts")[0]));
        var prompt = await ReadRpc(await host.McpAsync(endpoint, token, "prompts/get", name,
            new() { ["name"] = name, ["arguments"] = new { date = "2026-11-24" } }));
        Assert.Equal(original.GetProperty("prompt").GetString()!.Replace("{date}", "2026-11-24"),
            prompt.GetProperty("result").GetProperty("messages")[0].GetProperty("content").GetProperty("text").GetString());
    }

    [Fact]
    public async Task GraphRoutesAreAbsentFromHeaderAuthAndAnUnconfiguredBridge()
    {
        await using var noBridge = await Fixture.StartAsync(bridge: false);
        await using var header = await Fixture.StartAsync(headerAuth: true);
        foreach (var id in McpCatalog.Servers.Values.Where(entry => entry.Obo is not null).Select(entry => entry.Id))
        {
            Assert.Equal(HttpStatusCode.NotFound, (await noBridge.McpAsync("https://localhost/" + id, method: "resources/list")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await noBridge.Client.GetAsync("/.well-known/oauth-protected-resource/" + id)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await header.McpAsync("https://localhost/" + id, "provider-key", "resources/list")).StatusCode);
        }
    }

    private static async Task<JsonElement> ReadRpc(HttpResponseMessage response)
    {
        using (response)
        {
            var value = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, value);
            if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
                value = value.Split('\n').Single(line => line.StartsWith("data: ", StringComparison.Ordinal))[6..].TrimEnd('\r');
            using var json = JsonDocument.Parse(value);
            return json.RootElement.Clone();
        }
    }

    [Fact]
    public async Task DefaultAndNamedProfilesPublishCanonicalDiscoveryAndRequireScopedWrappers()
    {
        await using var host = await Fixture.StartAsync();
        var unauthenticated = await host.McpAsync(Images);
        var metadataUrl = "https://tools.example.test/.well-known/oauth-protected-resource/team/mcp/google-images";
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        Assert.Equal($"Bearer resource_metadata=\"{metadataUrl}\"", unauthenticated.Headers.WwwAuthenticate.Single().ToString());
        var metadata = await host.Client.GetFromJsonAsync<JsonElement>(new Uri(metadataUrl).AbsolutePath);
        Assert.Equal(Images, metadata.GetProperty("resource").GetString());
        Assert.Equal(Base + "/oauth/images", metadata.GetProperty("authorization_servers")[0].GetString());
        var defaultMetadata = await host.Client.GetFromJsonAsync<JsonElement>("/.well-known/oauth-protected-resource/team/mcp/openai-images");
        Assert.Equal(Base + "/oauth/default", defaultMetadata.GetProperty("authorization_servers")[0].GetString());
        var issuer = await host.Client.GetFromJsonAsync<JsonElement>("/.well-known/oauth-authorization-server/team/mcp/oauth/images");
        Assert.Equal(Base + "/oauth/images/token", issuer.GetProperty("token_endpoint").GetString());

        var assertion = host.Jwt(ImagesApp, Spa);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.McpAsync(Images, assertion)).StatusCode);
        var wrapped = await ReadWrapped(await host.TokenAsync("images", Exchange(assertion, Images)));
        Assert.DoesNotContain(assertion, wrapped, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await host.McpAsync(Images, wrapped)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.McpAsync(Default, wrapped)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.McpAsync(Images, wrapped.Insert(10, "x"))).StatusCode);
        var defaultToken = await ReadWrapped(await host.TokenAsync("default", Exchange(host.Jwt(DefaultApp, Spa), Default)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.McpAsync(OtherDefault, defaultToken)).StatusCode);

        var delegated = await host.Client.SendAsync(Fixture.Bearer(HttpMethod.Get, new Uri(Base + "/delegated").AbsolutePath + "?target=graph", wrapped));
        Assert.Equal(HttpStatusCode.OK, delegated.StatusCode);
        Assert.Equal("downstream-token", await delegated.Content.ReadAsStringAsync());
        Assert.Equal(assertion, host.Entra.LastForm["assertion"].ToString());
        Assert.Equal(ImagesApp, host.Entra.LastForm["client_id"].ToString());
        Assert.Equal("https://graph.microsoft.com/User.Read", host.Entra.LastForm["scope"].ToString());
        Assert.Equal("on_behalf_of", host.Entra.LastForm["requested_token_use"].ToString());
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.SendAsync(Fixture.Bearer(HttpMethod.Get,
            new Uri(Base + "/delegated").AbsolutePath + "?target=unconfigured", wrapped))).StatusCode);
    }

    [Fact]
    public async Task ExchangeChecksAudienceTenantClientScopeExpiryAndResource()
    {
        await using var host = await Fixture.StartAsync();
        foreach (var assertion in new[]
        {
            host.Jwt(DefaultApp, Spa), host.Jwt(ImagesApp, DefaultApp), host.Jwt(ImagesApp, Spa, scope: "other"),
            host.Jwt(ImagesApp, Spa, tenant: "66666666-6666-4666-8666-666666666666"),
            host.Jwt(ImagesApp, Spa, expires: DateTime.UtcNow.AddMinutes(-1)), "not-a-token",
        })
            Assert.Equal(HttpStatusCode.BadRequest, (await host.TokenAsync("images", Exchange(assertion, Images))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.TokenAsync("images", Exchange(host.Jwt(ImagesApp, Spa), Default))).StatusCode);
        var unlistedClient = Exchange(host.Jwt(ImagesApp, DefaultApp), Images);
        unlistedClient["client_id"] = DefaultApp;
        Assert.Equal(HttpStatusCode.BadRequest, (await host.TokenAsync("images", unlistedClient)).StatusCode);
    }

    [Fact]
    public async Task AuthorizationCodeCorrelatesBrowserAndBindsRedirectClientPkceAndResource()
    {
        await using var host = await Fixture.StartAsync();
        var verifier = new string('a', 64);
        var challenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var authorize = new Uri(Base + "/oauth/default/authorize").AbsolutePath;
        Dictionary<string, string?> query = new()
        {
            ["client_id"] = "test-client", ["redirect_uri"] = "https://unregistered.example.test/callback", ["state"] = "client-state",
            ["response_type"] = "code", ["code_challenge"] = challenge, ["code_challenge_method"] = "S256", ["resource"] = Default,
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.GetAsync(QueryHelpers.AddQueryString(authorize, query))).StatusCode);
        query["redirect_uri"] = Redirect;
        var start = await host.Client.GetAsync(QueryHelpers.AddQueryString(authorize, query));
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var upstream = QueryHelpers.ParseQuery(start.Headers.Location!.Query);
        Assert.Equal(DefaultApp, upstream["client_id"].ToString());
        Assert.Equal(Base + "/oauth/default/callback", upstream["redirect_uri"].ToString());
        Assert.NotEqual(challenge, upstream["code_challenge"].ToString());
        Assert.NotEqual(query["state"], upstream["state"].ToString());

        var callback = "/team/mcp/oauth/default/callback?code=from-entra&state=" + upstream["state"];
        var cookie = start.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.GetAsync(callback)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.SendAsync(WithCookie(callback.Replace("/default/", "/images/"), cookie))).StatusCode);
        var returned = await host.Client.SendAsync(WithCookie(callback, cookie));
        Assert.Equal(HttpStatusCode.Redirect, returned.StatusCode);
        Assert.Equal(Redirect, returned.Headers.Location!.GetLeftPart(UriPartial.Path));
        var parameters = QueryHelpers.ParseQuery(returned.Headers.Location.Query);
        Assert.Equal("client-state", parameters["state"].ToString());
        Assert.NotEqual("from-entra", parameters["code"].ToString());
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.SendAsync(WithCookie(callback, cookie))).StatusCode);

        Dictionary<string, string> form = new()
        {
            ["grant_type"] = "authorization_code", ["code"] = parameters["code"].ToString(), ["code_verifier"] = new string('b', 64),
            ["client_id"] = "test-client", ["redirect_uri"] = Redirect, ["resource"] = Default,
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await host.TokenAsync("default", form)).StatusCode);
        form["code_verifier"] = verifier;
        form["resource"] = OtherDefault;
        Assert.Equal(HttpStatusCode.BadRequest, (await host.TokenAsync("default", form)).StatusCode);
        form["resource"] = Default;
        var wrapped = await ReadWrapped(await host.TokenAsync("default", form));
        Assert.Equal("from-entra", host.Entra.LastForm["code"].ToString());
        Assert.NotEqual(verifier, host.Entra.LastForm["code_verifier"].ToString());
        Assert.Equal(upstream["code_challenge"].ToString(), WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(host.Entra.LastForm["code_verifier"].ToString()))));
        Assert.Equal(HttpStatusCode.OK, (await host.McpAsync(Default, wrapped)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.TokenAsync("default", form)).StatusCode);
    }

    private static HttpRequestMessage WithCookie(string url, string cookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", cookie);
        return request;
    }

    private static Dictionary<string, string> Exchange(string token, string resource) => new()
    {
        ["client_id"] = Spa, ["grant_type"] = McpOAuth.ExchangeGrant, ["resource"] = resource,
        ["subject_token"] = token, ["subject_token_type"] = AccessTokenType,
    };

    private static async Task<string> ReadWrapped(HttpResponseMessage response)
    {
        var value = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, value);
        Assert.True(response.Headers.CacheControl!.NoStore);
        using var json = JsonDocument.Parse(value);
        Assert.InRange(json.RootElement.GetProperty("expires_in").GetInt32(), 1, 900);
        return json.RootElement.GetProperty("access_token").GetString()!;
    }

    private sealed class Fixture(WebApplication app, HttpClient client, FakeEntra entra, FakeGraph graph) : IAsyncDisposable
    {
        internal HttpClient Client { get; } = client;
        internal FakeEntra Entra { get; } = entra;
        internal FakeGraph Graph { get; } = graph;

        internal static async Task<Fixture> StartAsync(bool bridge = true, bool headerAuth = false, bool directV1 = false)
        {
            var entra = new FakeEntra();
            var downstream = new FakeGraph();
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var config = new Dictionary<string, string?>
            {
                ["Mcp:OAuth:PublicBaseUrl"] = Base, ["AzureAd:TenantId"] = Tenant, ["AzureAd:ClientId"] = DefaultApp,
                ["AzureAd:ClientSecret"] = "default-test-secret", ["Mcp:OAuth:Profiles:default:ExchangeClients:0"] = Spa,
                ["AzureAd:Audience"] = LegacyAudience, ["Mcp:EntraScope"] = LegacyAudience + "/access_as_user",
                ["Mcp:OAuth:Profiles:images:TenantId"] = Tenant, ["Mcp:OAuth:Profiles:images:ClientId"] = ImagesApp,
                ["Mcp:OAuth:Profiles:images:ClientSecret"] = "images-test-secret", ["Mcp:OAuth:Profiles:images:ExchangeClients:0"] = Spa,
                ["Mcp:OAuth:Profiles:images:DownstreamScopes:graph"] = "https://graph.microsoft.com/User.Read",
                ["Mcp:OAuth:ServerProfiles:google-images"] = "images", ["Mcp:OAuth:PublicClients:test-client:0"] = Redirect,
            };
            if (directV1) config["Mcp:OAuth:Profiles:default:DirectBearerClients:0"] = Spa;
            if (bridge) builder.Configuration.AddInMemoryCollection(config);
            builder.Services.AddStreaikMcp(builder.Configuration, headerAuth);
            if (headerAuth || !bridge)
            {
                var header = builder.Build();
                header.UseCors();
                header.MapStreaikMcp();
                await header.StartAsync();
                return new Fixture(header, new HttpClient { BaseAddress = new Uri(header.Urls.Single()) }, entra, downstream);
            }

            var settings = EntraProfiles.Read(builder.Configuration)!;
            builder.Services.AddMcpOAuth(settings);
            builder.Services.AddSingleton<IDataProtectionProvider, EphemeralDataProtectionProvider>();
            builder.Services.AddHttpClient(EntraTokens.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => entra);
            builder.Services.AddHttpClient(GraphReads.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => downstream);
            var app = builder.Build();
            app.UsePathBase(settings.Prefix);
            app.UseRouting();
            app.UseCors();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapMcpOAuth(settings);
            app.MapGet("/delegated", async (string target, IMcpOnBehalfOf obo) =>
            {
                try { return Microsoft.AspNetCore.Http.Results.Text(await obo.GetAccessTokenAsync(target)); }
                catch (UnauthorizedAccessException) { return Microsoft.AspNetCore.Http.Results.StatusCode(403); }
            }).WithMetadata(settings.Servers["google-images"]).RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = McpBearerHandler.SchemeName });
            await app.StartAsync();
            return new Fixture(app, new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
            { BaseAddress = new Uri(app.Urls.Single()) }, entra, downstream);
        }

        internal string Jwt(string audience, string clientId, string scope = "access_as_user", string tenant = Tenant, DateTime? expires = null) =>
            Entra.Jwt(audience, clientId, scope, tenant, expires);

        internal Task<HttpResponseMessage> TokenAsync(string profile, Dictionary<string, string> form) =>
            Client.PostAsync($"/team/mcp/oauth/{profile}/token", new FormUrlEncodedContent(form));

        internal Task<HttpResponseMessage> McpAsync(string endpoint, string? token = null, string method = "tools/list", string? name = null,
            Dictionary<string, object>? parameters = null)
        {
            var request = Bearer(HttpMethod.Post, new Uri(endpoint).AbsolutePath, token);
            request.Headers.Add("Mcp-Protocol-Version", McpHosting.ProtocolVersion);
            request.Headers.Add("Mcp-Method", method);
            if (name is not null) request.Headers.Add("Mcp-Name", name);
            request.Headers.Accept.ParseAdd("application/json, text/event-stream");
            parameters ??= new();
            parameters["_meta"] = new Dictionary<string, object>
            {
                ["io.modelcontextprotocol/protocolVersion"] = McpHosting.ProtocolVersion,
                ["io.modelcontextprotocol/clientCapabilities"] = new { },
                ["io.modelcontextprotocol/clientInfo"] = new { name = "Bridge tests", version = "1.0.0" },
            };
            request.Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method, @params = parameters });
            return Client.SendAsync(request);
        }

        internal static HttpRequestMessage Bearer(HttpMethod method, string path, string? token)
        {
            var request = new HttpRequestMessage(method, path);
            if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return request;
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.DisposeAsync();
            Entra.Dispose();
            Graph.Dispose();
        }
    }

    private sealed class FakeGraph : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        internal Uri? LastUri { get; private set; }
        internal string? LastBearer { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("graph.microsoft.com", request.RequestUri!.Host);
            Calls++;
            LastUri = request.RequestUri;
            LastBearer = request.Headers.Authorization?.Parameter;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("graph-test-response") });
        }
    }

    private sealed class FakeEntra : HttpMessageHandler
    {
        private readonly RSA _rsa = RSA.Create(2048);
        internal Dictionary<string, StringValues> LastForm { get; private set; } = [];

        internal string Jwt(string audience, string clientId, string? scope = "access_as_user", string tenant = Tenant,
            DateTime? expires = null, bool v1 = false, string? issuer = null)
        {
            var claims = new Dictionary<string, object> { [v1 ? "appid" : "azp"] = clientId, ["oid"] = User, ["tid"] = tenant,
                ["ver"] = v1 ? "1.0" : "2.0" };
            if (scope is not null) claims["scp"] = scope;
            return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Audience = audience, Issuer = issuer ?? (v1 ? $"https://sts.windows.net/{tenant}/" : $"https://login.microsoftonline.com/{tenant}/v2.0"),
                Claims = claims,
                NotBefore = DateTime.UtcNow.AddMinutes(-5), Expires = expires ?? DateTime.UtcNow.AddMinutes(10),
                SigningCredentials = new SigningCredentials(new RsaSecurityKey(_rsa) { KeyId = "fake-entra" }, SecurityAlgorithms.RsaSha256),
            });
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("openid-configuration", StringComparison.Ordinal))
                return path.Contains("/v2.0/", StringComparison.Ordinal)
                    ? Json(new { issuer = $"https://login.microsoftonline.com/{Tenant}/v2.0", jwks_uri = $"https://login.microsoftonline.com/{Tenant}/discovery/v2.0/keys" })
                    : Json(new { issuer = $"https://sts.windows.net/{Tenant}/", jwks_uri = $"https://login.microsoftonline.com/{Tenant}/discovery/keys" });
            if (path.EndsWith("/keys", StringComparison.Ordinal))
            {
                var publicKey = _rsa.ExportParameters(false);
                return Json(new { keys = new[] { new { kid = "fake-entra", kty = "RSA", use = "sig", n = WebEncoders.Base64UrlEncode(publicKey.Modulus!), e = WebEncoders.Base64UrlEncode(publicKey.Exponent!) } } });
            }
            Assert.Equal($"/{Tenant}/oauth2/v2.0/token", path);
            LastForm = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken));
            return Json(new { access_token = LastForm["grant_type"] == "authorization_code" ? Jwt(DefaultApp, DefaultApp) : "downstream-token" });
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
        protected override void Dispose(bool disposing) { if (disposing) _rsa.Dispose(); base.Dispose(disposing); }
    }
}
