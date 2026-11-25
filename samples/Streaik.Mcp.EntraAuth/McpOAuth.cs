using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;
using Streaik.Mcp.Servers;

namespace Streaik.Mcp.EntraAuth;

internal static class McpOAuth
{
    internal const string ExchangeGrant = "urn:ietf:params:oauth:grant-type:token-exchange";
    private const string AccessTokenType = "urn:ietf:params:oauth:token-type:access_token";
    private const string CodeGrant = "authorization_code";
    private const string Policy = "StreaikMcpWrappedAccess";

    internal static void AddMcpOAuth(this IServiceCollection services, EntraProfiles settings)
    {
        services.AddSingleton(settings);
        services.AddDataProtection().SetApplicationName("Streaik.Mcp.EntraAuth");
        services.AddSingleton<WrappedTokens>();
        services.AddSingleton<OAuthTransactions>();
        services.AddSingleton<IEntraTokens, EntraTokens>();
        services.AddScoped<IMcpOnBehalfOf, McpOnBehalfOf>();
        if (settings.Servers.Keys.Any(id => GraphMcp.Find(id) is not null))
            GraphReads.Add(services);
        services.AddHttpClient(EntraTokens.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
            client.MaxResponseContentBufferSize = 1024 * 1024;
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, McpBearerHandler>(McpBearerHandler.SchemeName, _ => { });
        services.AddAuthorizationBuilder().AddPolicy(Policy, policy => policy.AddAuthenticationSchemes(McpBearerHandler.SchemeName).RequireAuthenticatedUser());
    }

    internal static void MapMcpOAuth(this WebApplication app, EntraProfiles settings)
    {
        app.MapStreaikMcp(Policy, (id, endpoint) =>
        {
            if (settings.Servers.TryGetValue(id, out var resource)) endpoint.WithMetadata(resource);
        });

        foreach (var (id, resource) in settings.Servers)
        {
            if (GraphMcp.Find(id) is { } graph)
                app.MapProtectedStreaikMcp(id, Policy, graph.Configure).WithMetadata(resource);

            app.MapGet($"{EntraProfiles.ResourceMetadata}{settings.Prefix}/{id}", () => Results.Json(new
            {
                resource = resource.Url,
                authorization_servers = new[] { settings.Issuer(resource.Profile) },
                scopes_supported = new[] { resource.Profile.Scope },
                bearer_methods_supported = new[] { "header" },
            })).RequireCors("StreaikMcp");
        }

        foreach (var profile in settings.Profiles.Values)
        {
            var issuer = settings.Issuer(profile);
            app.MapGet(EntraProfiles.IssuerMetadata + settings.Prefix + profile.Route, () => Results.Json(new
            {
                issuer,
                authorization_endpoint = issuer + "/authorize",
                token_endpoint = issuer + "/token",
                scopes_supported = new[] { profile.Scope },
                response_types_supported = new[] { "code" },
                grant_types_supported = profile.ExchangeClients.Count > 0 ? new[] { CodeGrant, ExchangeGrant } : new[] { CodeGrant },
                code_challenge_methods_supported = new[] { "S256" },
                token_endpoint_auth_methods_supported = new[] { "none" },
            })).RequireCors("StreaikMcp");

            app.MapGet(profile.Route + "/authorize", (HttpContext context, OAuthTransactions store) => Authorize(context, settings, profile, store));
            app.MapGet(profile.Route + "/callback", (HttpContext context, OAuthTransactions store) => Callback(context, settings, profile, store));
            app.MapPost(profile.Route + "/token", (HttpContext context, OAuthTransactions store, IEntraTokens entra, WrappedTokens wrappers) =>
                TokenAsync(context, settings, profile, store, entra, wrappers)).RequireCors("StreaikMcp");
        }
    }

    private static IResult Authorize(HttpContext context, EntraProfiles settings, EntraProfile profile, OAuthTransactions store)
    {
        NoStore(context);
        var query = context.Request.Query;
        var client = One(query["client_id"], 100);
        var redirect = One(query["redirect_uri"], 2048);
        if (client is null || redirect is null || !settings.PublicClients.TryGetValue(client, out var redirects) || !redirects.Contains(redirect))
            return Error("invalid_client"); // Never redirect until the exact client/URI pair is checked.
        var state = One(query["state"], 1024);
        var challenge = One(query["code_challenge"], 43);
        var resource = FindResource(settings, profile, query["resource"]);
        if (state is null || One(query["response_type"], 20) != "code" || One(query["code_challenge_method"], 5) != "S256" || !Pkce(challenge, 43, 43) ||
            resource is null || (query.ContainsKey("scope") && One(query["scope"], 2048) != profile.Scope))
            return Error("invalid_request");

        var upstreamState = Nonce();
        var browser = Nonce();
        var verifier = Nonce();
        store.Add(upstreamState, new PendingAuthorization(resource.Url, client, redirect, state, challenge!, verifier, browser), TimeSpan.FromMinutes(5));
        context.Response.Cookies.Append(CookieName(upstreamState), browser, Cookie(settings, profile));
        return Results.Redirect(QueryHelpers.AddQueryString(profile.Authority + "/oauth2/v2.0/authorize", new Dictionary<string, string?>
        {
            ["client_id"] = profile.ClientId, ["redirect_uri"] = settings.Callback(profile), ["response_type"] = "code",
            ["response_mode"] = "query", ["scope"] = profile.Scope, ["state"] = upstreamState,
            ["code_challenge"] = Challenge(verifier), ["code_challenge_method"] = "S256",
        }));
    }

    private static IResult Callback(HttpContext context, EntraProfiles settings, EntraProfile profile, OAuthTransactions store)
    {
        NoStore(context);
        var query = context.Request.Query;
        var state = One(query["state"], 43);
        if (!Pkce(state, 43, 43) || !context.Request.Cookies.TryGetValue(CookieName(state!), out var browser) ||
            store.Take<PendingAuthorization>(state!, pending => Same(pending.Browser, browser) &&
                FindResource(settings, profile, pending.Resource) is not null) is not { } authorization)
            return Error("invalid_request");
        var cookie = Cookie(settings, profile);
        cookie.MaxAge = null;
        context.Response.Cookies.Delete(CookieName(state!), cookie);
        var code = One(query["code"], 8192);
        if (query.ContainsKey("error") || code is null)
            return Results.Redirect(QueryHelpers.AddQueryString(authorization.Redirect, new Dictionary<string, string?>
            { ["error"] = "access_denied", ["state"] = authorization.State }));

        var bridgeCode = Nonce();
        store.Add(bridgeCode, new PendingCode(authorization, code), TimeSpan.FromMinutes(2));
        return Results.Redirect(QueryHelpers.AddQueryString(authorization.Redirect, new Dictionary<string, string?>
        { ["code"] = bridgeCode, ["state"] = authorization.State }));
    }

    private static async Task<IResult> TokenAsync(HttpContext context, EntraProfiles settings, EntraProfile profile,
        OAuthTransactions store, IEntraTokens entra, WrappedTokens wrappers)
    {
        NoStore(context);
        const int maxBody = 32_768;
        var request = context.Request;
        if (request.ContentLength > maxBody || request.ContentType?.Split(';')[0].Trim().Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) != true)
            return Error("invalid_request");
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = maxBody;

        IFormCollection form;
        try { form = await request.ReadFormAsync(context.RequestAborted); }
        catch (Exception error) when (error is InvalidDataException or BadHttpRequestException) { return Error("invalid_request"); }

        var resource = FindResource(settings, profile, form["resource"]);
        if (resource is null) return Error("invalid_target");
        if (form.ContainsKey("scope") && One(form["scope"], 2048) != profile.Scope) return Error("invalid_scope");
        var client = One(form["client_id"], 100);
        if (client is null) return Error("invalid_client");

        string? accessToken;
        string authorizedClient;
        var grant = One(form["grant_type"], 100);
        if (grant == CodeGrant)
        {
            var code = One(form["code"], 43);
            var redirect = One(form["redirect_uri"], 2048);
            var verifier = One(form["code_verifier"], 128);
            if (!Pkce(code, 43, 43) || !Pkce(verifier, 43, 128) || redirect is null) return Error("invalid_grant");
            var proof = Challenge(verifier!);
            var redeemed = store.Take<PendingCode>(code!, entry => entry.Authorization.Client == client && entry.Authorization.Redirect == redirect &&
                entry.Authorization.Resource == resource.Url && Same(entry.Authorization.Challenge, proof));
            if (redeemed is null) return Error("invalid_grant");
            accessToken = await entra.ExchangeAsync(profile, new()
            {
                ["grant_type"] = CodeGrant, ["code"] = redeemed.EntraCode, ["redirect_uri"] = settings.Callback(profile),
                ["code_verifier"] = redeemed.Authorization.Verifier, ["scope"] = profile.Scope,
            }, context.RequestAborted);
            authorizedClient = profile.ClientId;
        }
        else if (grant == ExchangeGrant && profile.ExchangeClients.Count > 0)
        {
            if (!profile.ExchangeClients.Contains(client)) return Error("invalid_client");
            if (One(form["subject_token_type"], 100) != AccessTokenType || form.ContainsKey("actor_token") ||
                (form.ContainsKey("requested_token_type") && One(form["requested_token_type"], 100) != AccessTokenType)) return Error("invalid_request");
            accessToken = One(form["subject_token"], 16_384);
            authorizedClient = client;
        }
        else return Error("unsupported_grant_type");

        if (accessToken is null || await entra.ValidateAsync(profile, accessToken, authorizedClient, context.RequestAborted) is not { } assertion ||
            wrappers.Issue(resource, assertion) is not { } wrapped) return Error(grant == ExchangeGrant ? "invalid_request" : "invalid_grant");

        return Results.Json(new
        {
            access_token = wrapped.Token, token_type = "Bearer", expires_in = wrapped.ExpiresIn, scope = profile.Scope,
            issued_token_type = grant == ExchangeGrant ? AccessTokenType : null,
        }, new System.Text.Json.JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
    }

    private static McpResource? FindResource(EntraProfiles settings, EntraProfile profile, StringValues value) =>
        One(value, 2048) is { } url ? settings.Servers.Values.FirstOrDefault(resource => resource.Profile == profile && resource.Url == url) : null;

    private static string? One(StringValues value, int length) => value.Count == 1 && value[0] is { Length: > 0 } text && text.Length <= length ? text : null;
    private static bool Pkce(string? text, int min, int max) => text is not null && text.Length >= min && text.Length <= max && Regex.IsMatch(text, @"\A[A-Za-z0-9_~.-]+\z");
    private static string Nonce() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
    private static string Challenge(string text) => WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(text)));
    private static bool Same(string expected, string actual) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));
    private static string CookieName(string state) => $"streaik-mcp-{state}";
    private static CookieOptions Cookie(EntraProfiles settings, EntraProfile profile) => new()
    {
        HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = settings.Origin.StartsWith("https://", StringComparison.Ordinal),
        Path = settings.Prefix + profile.Route + "/callback", MaxAge = TimeSpan.FromMinutes(5), IsEssential = true,
    };

    private static void NoStore(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
    }

    private static IResult Error(string error) => Results.Json(new { error }, statusCode: StatusCodes.Status400BadRequest);
    private sealed record PendingAuthorization(string Resource, string Client, string Redirect, string State, string Challenge, string Verifier, string Browser);
    private sealed record PendingCode(PendingAuthorization Authorization, string EntraCode);
}

internal sealed class OAuthTransactions : IDisposable
{
    private readonly MemoryCache _entries = new(new MemoryCacheOptions { SizeLimit = 1024 });
    private readonly object _gate = new();

    internal void Add<T>(string key, T entry, TimeSpan lifetime) where T : class =>
        _entries.Set(key, entry, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = lifetime });

    internal T? Take<T>(string key, Func<T, bool> verify) where T : class
    {
        lock (_gate)
        {
            if (_entries.Get(key) is not T entry || !verify(entry)) return null;
            _entries.Remove(key);
            return entry;
        }
    }

    public void Dispose() => _entries.Dispose();
}
