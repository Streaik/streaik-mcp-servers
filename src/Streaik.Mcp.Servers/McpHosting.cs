using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Streaik.Providers;
using Streaik.Providers.OpenAI;

namespace Streaik.Mcp.Servers;

public static class McpHosting
{
    public const string ProtocolVersion = "2026-07-28";
    private const string VersionHeader = "Mcp-Protocol-Version";
    private static readonly Dictionary<string, ServerDefinition> Implementations = new(StringComparer.Ordinal)
    {
        ["openai-images"] = new(OpenAIImages.CreateTools, OpenAIProvider.Id),
        ["google-images"] = new(GoogleImages.CreateTools, "google"),
        ["anthropic-skills"] = new(AnthropicSkills.CreateTools, "anthropic"),
        ["github-rest-countries"] = new(RestCountries.CreateTools, Complete: RestCountries.Complete),
        ["modelcontext-utils"] = new(ModelContextUtils.CreateTools),
    };

    private static readonly ServerEntry[] Servers = McpCatalog.Servers.Values.Where(entry => entry.Obo is null)
        .Select(entry =>
        {
            if (!Implementations.TryGetValue(entry.Id, out var implementation))
                throw new InvalidOperationException($"No MCP implementation for '{entry.Id}'.");

            CredentialHeader? credentials = null;
            if (implementation.Provider is { } provider)
            {
                var header = entry.Server.GetProperty("remotes")[0].GetProperty("headers")[0];
                credentials = new(provider, header.GetProperty("name").GetString()!,
                    header.TryGetProperty("value", out var format) ? format.GetString()!.Split(' ')[0] : null);
            }

            return new ServerEntry(entry, implementation, credentials, new CatalogPrompts(entry));
        }).ToArray();

    public static IReadOnlyList<string> ServerIds { get; } = Array.AsReadOnly(Servers.Select(server => server.Catalog.Id).ToArray());

    public static IServiceCollection AddStreaikMcp(this IServiceCollection services, IConfiguration configuration, bool headerAuth)
    {
        services.AddHttpContextAccessor();
        services.AddOpenAI();
        services.AddHttpClient(OpenAIProvider.Id).AddHttpMessageHandler(() => new ProviderErrors("OpenAI"));
        if (headerAuth)
            services.Replace(ServiceDescriptor.Scoped<IProviderCredentials, HeaderCredentials>());
        services.AddScoped<ImageInputs>();
        services.AddHttpClient("mcp-media", c => c.Timeout = TimeSpan.FromSeconds(20))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { UseCookies = false, UseProxy = false, AllowAutoRedirect = false });
        services.AddHttpClient("mcp-google", c => c.BaseAddress = new Uri("https://generativelanguage.googleapis.com/"))
            .ConfigurePrimaryHttpMessageHandler(ProviderHandler).AddHttpMessageHandler(() => new ProviderErrors("Google"));
        services.AddHttpClient("mcp-anthropic", c => c.BaseAddress = new Uri("https://api.anthropic.com/"))
            .ConfigurePrimaryHttpMessageHandler(ProviderHandler).AddHttpMessageHandler(() => new ProviderErrors("Anthropic"));
        services.AddMcpServer(options => options.ProtocolVersion = ProtocolVersion).WithHttpTransport(options =>
        {
            options.SessionMode = HttpServerSessionMode.Stateless;
            options.ConfigureSessionOptions = (context, session, _) =>
            {
                var configure = context.GetEndpoint()?.Metadata.GetMetadata<Action<McpServerOptions>>()
                    ?? throw new InvalidOperationException("No MCP server is mapped for the endpoint.");
                configure(session);
                return Task.CompletedTask;
            };
        });

        var origins = configuration.GetSection("Mcp:BrowserOrigins").Get<string[]>() ?? [];
        services.AddCors(options => options.AddPolicy("StreaikMcp", policy => policy.WithOrigins(origins)
            .WithMethods("GET", "POST")
            .AllowAnyHeader() // SDK routing headers vary by method. CORS remains restricted to configured origins.
            .WithExposedHeaders(VersionHeader, "WWW-Authenticate")));
        return services;
    }

    // Only transport routes are mapped here. Discovery is hosted separately.
    public static void MapStreaikMcp(this WebApplication app, string? authorizationPolicy = null,
        Action<string, IEndpointConventionBuilder>? configureServer = null)
    {
        foreach (var server in Servers)
        {
            var id = server.Catalog.Id;
            var endpoint = MapServer(app, id, authorizationPolicy, session =>
            {
                session.ServerInfo = server.Catalog.CreateServerInfo();
                session.ServerInstructions = server.Catalog.Instructions;
                session.ToolCollection = server.Implementation.CreateTools();
                if (server.Credentials is null)
                    foreach (var tool in session.ToolCollection)
                        if (tool.ProtocolTool.Icons is not { Count: > 0 }) tool.ProtocolTool.Icons = session.ServerInfo.Icons;
                if (server.Prompts.Any || server.Implementation.Complete is not null)
                {
                    session.Handlers = new McpServerHandlers { CompleteHandler = server.Implementation.Complete };
                    server.Prompts.Configure(session.Handlers);
                }
            }, server.Catalog.AllowAnonymous).WithMetadata(server);
            configureServer?.Invoke(id, endpoint);
        }
    }

    // Opt-in transports must supply an authorization policy; the public sample only maps the built-ins above.
    public static IEndpointConventionBuilder MapProtectedStreaikMcp(this WebApplication app, string id,
        string authorizationPolicy, Action<McpServerOptions> configureSession)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizationPolicy);
        return MapServer(app, id, authorizationPolicy, configureSession);
    }

    private static IEndpointConventionBuilder MapServer(WebApplication app, string id, string? authorizationPolicy,
        Action<McpServerOptions> configureSession, bool allowAnonymous = false)
    {
        var endpoint = app.MapMcp($"/{id}").WithMetadata(configureSession).RequireCors("StreaikMcp");
        if (allowAnonymous)
            endpoint.AllowAnonymous();
        else if (authorizationPolicy is not null)
            endpoint.RequireAuthorization(authorizationPolicy);
        endpoint.Add(builder =>
        {
            var next = builder.RequestDelegate!;
            builder.RequestDelegate = async context =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (HttpMethods.IsPost(context.Request.Method) &&
                    (!context.Request.Headers.TryGetValue(VersionHeader, out var values) || values.Count != 1 || values[0] != ProtocolVersion))
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        jsonrpc = "2.0", id = (object?)null,
                        error = new { code = -32022, message = $"MCP protocol {ProtocolVersion} is required.", data = new { supported = new[] { ProtocolVersion } } }
                    }, context.RequestAborted);
                    return;
                }
                await next(context);
            };
        });
        return endpoint;
    }

    private static ServerEntry? GetServer(HttpContext context) => context.GetEndpoint()?.Metadata.GetMetadata<ServerEntry>();

    private static SocketsHttpHandler ProviderHandler() => new() { UseCookies = false, AllowAutoRedirect = false };

    private sealed class HeaderCredentials(IHttpContextAccessor accessor) : IProviderCredentials
    {
        public string? GetApiKey(string providerId)
        {
            var context = accessor.HttpContext;
            if (context is null || GetServer(context)?.Credentials is not { } credential || credential.Provider != providerId ||
                !context.Request.Headers.TryGetValue(credential.Name, out var values) || values.Count != 1 ||
                values[0] is not { } value || string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Contains(','))
                return null;

            return credential.Scheme is null ? value.Trim()
                : AuthenticationHeaderValue.TryParse(value, out var auth) &&
                    string.Equals(auth.Scheme, credential.Scheme, StringComparison.OrdinalIgnoreCase) ? auth.Parameter : null;
        }
    }

    private sealed record ServerDefinition(Func<McpServerPrimitiveCollection<McpServerTool>> CreateTools, string? Provider = null,
        McpRequestHandler<CompleteRequestParams, CompleteResult>? Complete = null);

    private sealed record CredentialHeader(string Provider, string Name, string? Scheme);

    private sealed record ServerEntry(McpCatalog.Entry Catalog, ServerDefinition Implementation, CredentialHeader? Credentials, CatalogPrompts Prompts);
}
