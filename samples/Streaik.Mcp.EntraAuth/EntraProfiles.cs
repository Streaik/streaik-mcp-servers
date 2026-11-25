using System.Text.RegularExpressions;
using Streaik.Mcp.Servers;

namespace Streaik.Mcp.EntraAuth;

internal sealed record EntraProfile(string Name, string TenantId, string ClientId, string ClientSecret, string Scope,
    IReadOnlySet<string> ExchangeClients, string? V1Audience, IReadOnlySet<string> DirectBearerClients,
    IReadOnlyDictionary<string, string> DownstreamScopes)
{
    internal string Authority => $"https://login.microsoftonline.com/{TenantId}";
    internal string ScopeName => Scope[(Scope.LastIndexOf('/') + 1)..];
    internal string Route => $"/oauth/{Name}";
}

internal sealed record McpResource(EntraProfile Profile, string Url, string MetadataUrl, IReadOnlyDictionary<string, string>? Obo);

internal sealed class EntraProfiles
{
    internal const string ResourceMetadata = "/.well-known/oauth-protected-resource";
    internal const string IssuerMetadata = "/.well-known/oauth-authorization-server";
    internal string BaseUrl { get; }
    internal string Origin { get; }
    internal string Prefix { get; }
    internal IReadOnlyDictionary<string, EntraProfile> Profiles { get; }
    internal IReadOnlyDictionary<string, McpResource> Servers { get; }
    internal IReadOnlyDictionary<string, HashSet<string>> PublicClients { get; }

    private EntraProfiles(Uri baseUri, Dictionary<string, EntraProfile> profiles, Dictionary<string, McpResource> servers,
        Dictionary<string, HashSet<string>> publicClients)
    {
        BaseUrl = baseUri.AbsoluteUri.TrimEnd('/');
        Origin = baseUri.GetLeftPart(UriPartial.Authority);
        Prefix = baseUri.AbsolutePath.TrimEnd('/');
        Profiles = profiles;
        Servers = servers;
        PublicClients = publicClients;
    }

    internal static EntraProfiles? Read(IConfiguration configuration, McpProviderAccess access)
    {
        var bridge = configuration.GetSection("Mcp:OAuth");
        var profilesSection = bridge.GetSection("Profiles");
        var serverSection = bridge.GetSection("ServerProfiles");
        var publicClientsSection = bridge.GetSection("PublicClients");
        if (string.IsNullOrWhiteSpace(bridge["PublicBaseUrl"]) && !profilesSection.Exists() && !serverSection.Exists() && !publicClientsSection.Exists())
        {
            if (McpHosting.ServerIds.Any(access.IsManaged))
                throw Invalid("Mcp:OAuth is required when a central provider key is configured");
            return null;
        }

        if (!TryWebUrl(bridge["PublicBaseUrl"], out var baseUri) || baseUri!.Query.Length != 0 ||
            !Regex.IsMatch(baseUri.AbsolutePath, @"\A(/[-a-zA-Z0-9._~]+)*/?\z", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("Mcp:OAuth:PublicBaseUrl must be an HTTPS base URL (HTTP on loopback only) with a simple path and no query or fragment.");

        var profiles = new Dictionary<string, EntraProfile>(StringComparer.Ordinal);
        foreach (var section in profilesSection.GetChildren())
        {
            if (!Regex.IsMatch(section.Key, @"\A[a-z0-9][a-z0-9-]{0,39}\z", RegexOptions.CultureInvariant))
                throw Invalid(section.Path);
            if (section.Key != "default") profiles.Add(section.Key, ReadProfile(section.Key, section, section));
        }
        var defaults = profilesSection.GetSection("default");
        var defaultApp = new[] { "TenantId", "ClientId", "ClientSecret" }.Any(key => defaults[key] is not null) ? defaults : configuration.GetSection("AzureAd");
        profiles.Add("default", ReadProfile("default", defaultApp, defaults, configuration["Mcp:EntraScope"] ?? "access_as_user"));

        var assignments = serverSection.GetChildren().ToDictionary(section => section.Key, section => section.Value, StringComparer.Ordinal);
        foreach (var (server, profile) in assignments)
        {
            if (!McpCatalog.Servers.ContainsKey(server) || profile is null || !profiles.ContainsKey(profile))
                throw Invalid($"{serverSection.Path}:{server}");
        }

        var publicClients = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var section in publicClientsSection.GetChildren())
        {
            var redirects = section.Get<string[]>() ?? [];
            if (!Regex.IsMatch(section.Key, @"\A[a-zA-Z0-9_.-]{1,100}\z", RegexOptions.CultureInvariant) || redirects.Length == 0 ||
                redirects.Any(redirect => redirect is not { Length: <= 2048 } || !TryWebUrl(redirect, out var uri) || uri!.Query.Length != 0))
                throw Invalid(section.Path);
            publicClients.Add(section.Key, redirects.ToHashSet(StringComparer.Ordinal));
        }

        var servers = McpCatalog.Servers.Where(pair => pair.Value.Obo is not null || access.IsManaged(pair.Key)).ToDictionary(pair => pair.Key, pair =>
        {
            var (id, entry) = pair;
            if (entry.Obo is { } obo && (obo.Count != 1 || !obo.TryGetValue(GraphReads.Target, out var scopes) ||
                !IsDownstreamScopeSet(scopes, GraphReads.Origin + "/")))
                throw Invalid($"MCP catalog OBO scopes for '{id}'");
            // Shares resolution requires Files.ReadWrite even for input. Image outputs go to the signed-in user's drive.
            var scopesForResource = entry.Obo ?? new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [GraphReads.Target] = GraphReads.Origin + "/Files.ReadWrite",
            };
            return new McpResource(profiles[assignments.GetValueOrDefault(id) ?? "default"], $"{baseUri.AbsoluteUri.TrimEnd('/')}/{id}",
                $"{baseUri.GetLeftPart(UriPartial.Authority)}{ResourceMetadata}{baseUri.AbsolutePath.TrimEnd('/')}/{id}", scopesForResource);
        }, StringComparer.Ordinal);
        return new EntraProfiles(baseUri, profiles, servers, publicClients);
    }

    internal string Issuer(EntraProfile profile) => BaseUrl + profile.Route;
    internal string Callback(EntraProfile profile) => Issuer(profile) + "/callback";

    private static EntraProfile ReadProfile(string name, IConfigurationSection app, IConfigurationSection options, string? fallbackScope = null)
    {
        if (!Guid.TryParseExact(app["TenantId"], "D", out var tenant) || tenant == Guid.Empty ||
            !Guid.TryParseExact(app["ClientId"], "D", out var client) || client == Guid.Empty ||
            app["ClientSecret"] is not { Length: > 0 } secret || string.IsNullOrWhiteSpace(secret))
            throw Invalid(app.Path + " (TenantId, ClientId and ClientSecret are required)");
        var scope = options["Scope"] ?? fallbackScope ?? "access_as_user";
        if (Regex.IsMatch(scope, @"\A[a-zA-Z0-9_.-]+\z", RegexOptions.CultureInvariant)) scope = $"api://{client:D}/{scope}";
        if (!IsScope(scope) || scope.EndsWith("/.default", StringComparison.OrdinalIgnoreCase)) throw Invalid(options.Path + ":Scope");

        var exchangeClients = ReadClients(options, "ExchangeClients");
        var directBearerClients = ReadClients(options, "DirectBearerClients");
        var v1Audience = app["Audience"];
        if (v1Audience != scope[..scope.LastIndexOf('/')] && v1Audience != client.ToString("D"))
        {
            if (directBearerClients.Count > 0)
                throw Invalid(app.Path + ":Audience must be this API's application ID URI or client ID for direct v1 authentication");
            v1Audience = null;
        }
        var downstream = options.GetSection("DownstreamScopes").Get<Dictionary<string, string>>() ?? [];
        if (downstream.Values.Any(value => !IsDownstreamScopeSet(value)))
            throw Invalid(options.Path + ":DownstreamScopes");
        return new EntraProfile(name, tenant.ToString("D"), client.ToString("D"), secret, scope, exchangeClients, v1Audience, directBearerClients, downstream);
    }

    private static HashSet<string> ReadClients(IConfigurationSection options, string key) =>
        (options.GetSection(key).Get<string[]>() ?? []).Select(value =>
            Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty ? id.ToString("D") : throw Invalid(options.Path + ":" + key))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool IsScope(string value) => value.Length <= 2048 && Regex.IsMatch(value, @"\A(api://|https://)[^\s?#]+/[a-zA-Z0-9_.-]+\z", RegexOptions.CultureInvariant);

    private static bool IsDownstreamScopeSet(string value, string? requiredPrefix = null) => value.Length <= 2048 &&
        value.Split(' ').All(scope => IsScope(scope) && (requiredPrefix is null ||
            (scope.StartsWith(requiredPrefix, StringComparison.Ordinal) && !scope.EndsWith("/.default", StringComparison.OrdinalIgnoreCase))));

    private static bool TryWebUrl(string? value, out Uri? uri) => Uri.TryCreate(value, UriKind.Absolute, out uri) &&
        uri.IsWellFormedOriginalString() && (uri.Scheme == "https" || (uri.Scheme == "http" && uri.IsLoopback)) &&
        uri.Host.Length != 0 && !uri.GetLeftPart(UriPartial.Authority).Contains('@') && uri.Fragment.Length == 0;

    private static InvalidOperationException Invalid(string path) => new($"Invalid Entra MCP configuration: {path}.");
}
