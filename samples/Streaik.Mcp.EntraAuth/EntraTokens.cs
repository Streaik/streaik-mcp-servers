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
    Task<string?> ExchangeAsync(EntraProfile profile, Dictionary<string, string> parameters, CancellationToken ct);
}

internal sealed class EntraTokens : IEntraTokens
{
    internal const string HttpClientName = "StreaikMcpEntra";
    private readonly IHttpClientFactory _clients;
    private readonly Dictionary<string, ConfigurationManager<OpenIdConnectConfiguration>> _metadata;
    private readonly JsonWebTokenHandler _handler = new() { MapInboundClaims = false, MaximumTokenSizeInBytes = 16_384 };

    public EntraTokens(EntraProfiles settings, IHttpClientFactory clients)
    {
        _clients = clients;
        var retriever = new HttpDocumentRetriever(clients.CreateClient(HttpClientName)) { RequireHttps = true };
        _metadata = settings.Profiles.Values.ToDictionary(profile => profile.Name, profile =>
            new ConfigurationManager<OpenIdConnectConfiguration>($"{profile.Authority}/v2.0/.well-known/openid-configuration",
                new OpenIdConnectConfigurationRetriever(), retriever));
    }

    public async Task<EntraAssertion?> ValidateAsync(EntraProfile profile, string accessToken, string authorizedClient, CancellationToken ct)
    {
        if (accessToken.Length is 0 or > 16_384) return null;
        var manager = _metadata[profile.Name];
        try
        {
            var configuration = await manager.GetConfigurationAsync(ct);
            var result = await _handler.ValidateTokenAsync(accessToken, new TokenValidationParameters
            {
                ValidIssuer = profile.Authority + "/v2.0",
                ValidAudience = profile.ClientId,
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
            if (!result.IsValid || result.SecurityToken is not JsonWebToken token ||
                result.ClaimsIdentity.FindFirst("ver")?.Value != "2.0" ||
                !string.Equals(result.ClaimsIdentity.FindFirst("tid")?.Value, profile.TenantId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(result.ClaimsIdentity.FindFirst("azp")?.Value, authorizedClient, StringComparison.OrdinalIgnoreCase) ||
                !Guid.TryParse(result.ClaimsIdentity.FindFirst("oid")?.Value, out var oid) ||
                !(result.ClaimsIdentity.FindFirst("scp")?.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Contains(profile.ScopeName, StringComparer.Ordinal) ?? false)) return null;
            return new EntraAssertion(accessToken, oid.ToString("D"), new DateTimeOffset(token.ValidTo, TimeSpan.Zero));
        }
        catch (InvalidOperationException) when (!ct.IsCancellationRequested) { return null; } // OIDC metadata unavailable.
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

    internal (string Token, int ExpiresIn)? Issue(McpResource resource, EntraAssertion assertion)
    {
        var now = DateTimeOffset.UtcNow;
        var expiresIn = (int)Math.Min((assertion.ExpiresAt - now).TotalSeconds, 900);
        return expiresIn <= 0 ? null : (TokenPrefix + Protector(resource).Protect(JsonSerializer.Serialize(assertion), now.AddSeconds(expiresIn)), expiresIn);
    }

    internal EntraAssertion? Read(McpResource resource, string token)
    {
        if (token.Length > 24_000 || !token.StartsWith(TokenPrefix, StringComparison.Ordinal)) return null;
        try { return JsonSerializer.Deserialize<EntraAssertion>(Protector(resource).Unprotect(token[TokenPrefix.Length..])); }
        catch (Exception error) when (error is CryptographicException or FormatException or JsonException) { return null; }
    }

    private ITimeLimitedDataProtector Protector(McpResource resource) => protection.CreateProtector("Streaik.Mcp.AccessToken.v1",
        resource.Url, resource.Profile.Name, resource.Profile.TenantId, resource.Profile.ClientId, resource.Profile.Scope).ToTimeLimitedDataProtector();
}

internal sealed record ValidatedMcpAccess(McpResource Resource, EntraAssertion Assertion);

internal sealed class McpBearerHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    WrappedTokens tokens) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    internal const string SchemeName = "StreaikMcpBearer";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Context.GetEndpoint()?.Metadata.GetMetadata<McpResource>() is not { } resource ||
            Request.Headers.Authorization.Count != 1 || !AuthenticationHeaderValue.TryParse(Request.Headers.Authorization[0], out var bearer) ||
            !bearer.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) || bearer.Parameter is not { } raw)
            return Task.FromResult(AuthenticateResult.NoResult());
        if (tokens.Read(resource, raw) is not { } assertion || assertion.ExpiresAt <= DateTimeOffset.UtcNow)
            return Task.FromResult(AuthenticateResult.Fail("Invalid MCP access token."));

        Context.Features.Set(new ValidatedMcpAccess(resource, assertion));
        var identity = new ClaimsIdentity([new Claim("oid", assertion.ObjectId), new Claim("tid", resource.Profile.TenantId),
            new Claim("scp", resource.Profile.ScopeName)], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
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
            !access.Resource.Profile.DownstreamScopes.TryGetValue(target, out var scope))
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
