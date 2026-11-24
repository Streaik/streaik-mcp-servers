using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Streaik.Mcp.Servers;

namespace Streaik.Mcp.EntraAuth;

internal sealed record EntraAssertion(string Token, string ObjectId, DateTimeOffset ExpiresAt);

internal interface IEntraTokens
{
    Task<EntraAssertion?> ValidateAsync(EntraProfile profile, string accessToken, string authorizedClient, CancellationToken ct);
    Task<EntraAssertion?> ValidateDirectAsync(EntraProfile profile, string accessToken, CancellationToken ct);
    Task<string?> ExchangeAsync(EntraProfile profile, Dictionary<string, string> parameters, CancellationToken ct);
}

internal sealed class EntraTokens : IEntraTokens
{
    internal const string HttpClientName = "StreaikMcpEntra";
    private readonly IHttpClientFactory _clients;
    private readonly Dictionary<(string Tenant, bool V1), ConfigurationManager<OpenIdConnectConfiguration>> _metadata;
    private readonly ILogger<EntraTokens> _logger;
    private readonly JsonWebTokenHandler _handler = new() { MapInboundClaims = false, MaximumTokenSizeInBytes = 16_384 };

    public EntraTokens(EntraProfiles settings, IHttpClientFactory clients, ILogger<EntraTokens> logger)
    {
        _clients = clients;
        _logger = logger;
        var retriever = new HttpDocumentRetriever(clients.CreateClient(HttpClientName)) { RequireHttps = true };
        _metadata = settings.Profiles.Values.DistinctBy(profile => profile.TenantId)
            .SelectMany(profile => new[] { (profile, V1: false), (profile, V1: true) }).ToDictionary(
                item => (item.profile.TenantId, item.V1), item => new ConfigurationManager<OpenIdConnectConfiguration>(
                $"{item.profile.Authority}{(item.V1 ? "" : "/v2.0")}/.well-known/openid-configuration",
                new OpenIdConnectConfigurationRetriever(), retriever));
    }

    public Task<EntraAssertion?> ValidateAsync(EntraProfile profile, string accessToken, string authorizedClient, CancellationToken ct) =>
        ValidateCoreAsync(profile, accessToken, authorizedClient, ct);

    public Task<EntraAssertion?> ValidateDirectAsync(EntraProfile profile, string accessToken, CancellationToken ct) =>
        profile.DirectBearerClients.Count == 0 ? Task.FromResult<EntraAssertion?>(null) : ValidateCoreAsync(profile, accessToken, null, ct);

    private async Task<EntraAssertion?> ValidateCoreAsync(EntraProfile profile, string accessToken, string? authorizedClient, CancellationToken ct)
    {
        if (accessToken.Length is 0 or > 16_384 || !_handler.CanReadToken(accessToken)) return null;
        string? version;
        try { version = _handler.ReadJsonWebToken(accessToken).GetClaim("ver").Value; }
        catch (Exception error) when (error is ArgumentException or SecurityTokenException or JsonException or FormatException) { return null; }

        // An unverified claim only selects a validation policy; it never authorizes a request.
        var v1 = version == "1.0";
        if ((!v1 && (version != "2.0" || authorizedClient is null)) || (v1 && string.IsNullOrEmpty(profile.V1Audience))) return null;
        var manager = _metadata[(profile.TenantId, v1)];
        try
        {
            var configuration = await manager.GetConfigurationAsync(ct);
            var result = await _handler.ValidateTokenAsync(accessToken, new TokenValidationParameters
            {
                ValidIssuer = v1 ? $"https://sts.windows.net/{profile.TenantId}/" : profile.Authority + "/v2.0",
                ValidAudience = v1 ? profile.V1Audience : profile.ClientId,
                IssuerSigningKeys = configuration.SigningKeys,
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                ValidateIssuer = true,
                ValidateAudience = true,
                IgnoreTrailingSlashWhenValidatingAudience = false,
                ValidateIssuerSigningKey = true,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero,
            });
            if (result.Exception is SecurityTokenSignatureKeyNotFoundException) manager.RequestRefresh();
            if (!result.IsValid || result.SecurityToken is not JsonWebToken token)
            {
                _logger.LogDebug("Entra assertion rejected for MCP profile {Profile}: {FailureType}.", profile.Name, result.Exception?.GetType().Name);
                return null;
            }
            var caller = result.ClaimsIdentity.FindFirst(v1 ? "appid" : "azp")?.Value;
            if (result.ClaimsIdentity.FindFirst("ver")?.Value != version ||
                !string.Equals(result.ClaimsIdentity.FindFirst("tid")?.Value, profile.TenantId, StringComparison.OrdinalIgnoreCase) ||
                caller is null || (authorizedClient is null ? !profile.DirectBearerClients.Contains(caller)
                    : !string.Equals(caller, authorizedClient, StringComparison.OrdinalIgnoreCase)) ||
                !Guid.TryParse(result.ClaimsIdentity.FindFirst("oid")?.Value, out var oid) ||
                !(result.ClaimsIdentity.FindFirst("scp")?.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Contains(profile.ScopeName, StringComparer.Ordinal) ?? false))
            {
                _logger.LogDebug("Entra assertion rejected for MCP profile {Profile}: required delegated claims did not match.", profile.Name);
                return null;
            }
            return new EntraAssertion(accessToken, oid.ToString("D"), new DateTimeOffset(token.ValidTo, TimeSpan.Zero));
        }
        catch (InvalidOperationException) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("Entra metadata unavailable for MCP profile {Profile}.", profile.Name);
            return null;
        }
    }

    public async Task<string?> ExchangeAsync(EntraProfile profile, Dictionary<string, string> parameters, CancellationToken ct)
    {
        using var client = _clients.CreateClient(HttpClientName);
        parameters["client_id"] = profile.ClientId;
        parameters["client_secret"] = profile.ClientSecret;
        using var body = new FormUrlEncodedContent(parameters);
        try
        {
            using var response = await client.PostAsync($"{profile.Authority}/oauth2/v2.0/token", body, ct);
            if (!response.IsSuccessStatusCode) return null;
            var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            return result.ValueKind == JsonValueKind.Object && result.TryGetProperty("access_token", out var token) &&
                token.ValueKind == JsonValueKind.String ? token.GetString() : null;
        }
        catch (Exception error) when (!ct.IsCancellationRequested && error is HttpRequestException or JsonException or OperationCanceledException)
        { return null; }
    }
}

internal sealed class WrappedTokens(IDataProtectionProvider protection)
{
    private const string TokenPrefix = "mcp1.";

    internal static bool IsWrapped(string token) => token.StartsWith(TokenPrefix, StringComparison.Ordinal);

    internal (string Token, int ExpiresIn)? Issue(McpResource resource, EntraAssertion assertion)
    {
        var now = DateTimeOffset.UtcNow;
        var expiresIn = (int)Math.Min((assertion.ExpiresAt - now).TotalSeconds, 900);
        return expiresIn <= 0 ? null : (TokenPrefix + Protector(resource).Protect(JsonSerializer.Serialize(assertion), now.AddSeconds(expiresIn)), expiresIn);
    }

    internal EntraAssertion? Read(McpResource resource, string token)
    {
        if (token.Length > 24_000 || !IsWrapped(token)) return null;
        try { return JsonSerializer.Deserialize<EntraAssertion>(Protector(resource).Unprotect(token[TokenPrefix.Length..])); }
        catch (Exception error) when (error is CryptographicException or FormatException or JsonException) { return null; }
    }

    private ITimeLimitedDataProtector Protector(McpResource resource) => protection.CreateProtector("Streaik.Mcp.AccessToken.v1",
        resource.Url, resource.Profile.Name, resource.Profile.TenantId, resource.Profile.ClientId, resource.Profile.Scope).ToTimeLimitedDataProtector();
}

internal sealed record ValidatedMcpAccess(McpResource Resource, EntraAssertion Assertion);

internal sealed class McpBearerHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    WrappedTokens tokens, IEntraTokens entra) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    internal const string SchemeName = "StreaikMcpBearer";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Context.GetEndpoint()?.Metadata.GetMetadata<McpResource>() is not { } resource ||
            Request.Headers.Authorization.Count != 1 || !AuthenticationHeaderValue.TryParse(Request.Headers.Authorization[0], out var bearer) ||
            !bearer.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) || bearer.Parameter is not { } raw)
            return AuthenticateResult.NoResult();
        var wrapped = WrappedTokens.IsWrapped(raw);
        var assertion = wrapped ? tokens.Read(resource, raw) : await entra.ValidateDirectAsync(resource.Profile, raw, Context.RequestAborted);
        if (assertion is null || assertion.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            Logger.LogDebug("MCP bearer rejected for profile {Profile}. Wrapped: {Wrapped}; direct v1 enabled: {DirectEnabled}.",
                resource.Profile.Name, wrapped, resource.Profile.DirectBearerClients.Count > 0);
            return AuthenticateResult.Fail("Invalid MCP access token.");
        }

        Context.Features.Set(new ValidatedMcpAccess(resource, assertion));
        var identity = new ClaimsIdentity([new Claim("oid", assertion.ObjectId), new Claim("tid", resource.Profile.TenantId),
            new Claim("scp", resource.Profile.ScopeName)], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.CacheControl = "no-store";
        if (Context.GetEndpoint()?.Metadata.GetMetadata<McpResource>() is { } resource)
            Response.Headers.WWWAuthenticate = $"Bearer resource_metadata=\"{resource.MetadataUrl}\"";
        return Task.CompletedTask;
    }
}

internal sealed class McpOnBehalfOf(IHttpContextAccessor accessor, IEntraTokens tokens) : IMcpOnBehalfOf
{
    public async Task<string> GetAccessTokenAsync(string target, CancellationToken cancellationToken = default)
    {
        var context = accessor.HttpContext;
        if (context?.Features.Get<ValidatedMcpAccess>() is not { } access ||
            !(access.Resource.Obo ?? access.Resource.Profile.DownstreamScopes).TryGetValue(target, out var scope))
            throw new UnauthorizedAccessException("No delegated access is configured for this MCP request and target.");

        using var ct = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.RequestAborted);
        var token = await tokens.ExchangeAsync(access.Resource.Profile, new()
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
            ["requested_token_use"] = "on_behalf_of",
            ["assertion"] = access.Assertion.Token,
            ["scope"] = scope,
        }, ct.Token);
        return !string.IsNullOrWhiteSpace(token) ? token : throw new InvalidOperationException("Entra did not grant downstream delegated access.");
    }
}
