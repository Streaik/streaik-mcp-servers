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
        ["openai-images"] = new(_ => OpenAIImages.CreateTools(), OpenAIProvider.Id, "api.openai.com"),
        ["google-images"] = new(_ => GoogleImages.CreateTools(), "google", "generativelanguage.googleapis.com"),
        ["anthropic-skills"] = new(AnthropicSkills.CreateTools, "anthropic", "api.anthropic.com"),
        ["github-rest-countries"] = new(_ => RestCountries.CreateTools(), Complete: RestCountries.Complete),
        ["modelcontext-utils"] = new(_ => ModelContextUtils.CreateTools()),
    };

    private static readonly ServerEntry[] Servers = McpCatalog.Servers.Values.Where(entry => entry.Obo is null)
        .Select(entry =>
        {
            if (!Implementations.TryGetValue(entry.Id, out var implementation))
                throw new InvalidOperationException($"No MCP implementation for '{entry.Id}'.");

            ProviderBinding? credentials = null;
            if (implementation.Provider is { } provider)
            {
                var header = entry.Server.GetProperty("remotes")[0].GetProperty("headers")[0];
                credentials = new(entry.Id, provider, implementation.ProviderHost!, header.GetProperty("name").GetString()!,
                    header.TryGetProperty("value", out var format) ? format.GetString()!.Split(' ')[0] : null);
            }

            return new ServerEntry(entry, implementation, credentials, new CatalogPrompts(entry));
        }).ToArray();

    public static IReadOnlyList<string> ServerIds { get; } = Array.AsReadOnly(Servers.Select(server => server.Catalog.Id).ToArray());

    public static McpProviderAccess ResolveProviderAccess(IConfiguration configuration, bool headerAuth = false) =>
        new(configuration, headerAuth, Servers.Select(server => server.Credentials).OfType<ProviderBinding>());

    public static IServiceCollection AddStreaikMcp(this IServiceCollection services, IConfiguration configuration, bool headerAuth) =>
        services.AddStreaikMcp(configuration, ResolveProviderAccess(configuration, headerAuth));

    public static IServiceCollection AddStreaikMcp(this IServiceCollection services, IConfiguration configuration, McpProviderAccess access)
    {
        services.AddHttpContextAccessor();
        services.AddSingleton(access);
        services.AddOpenAI();
        services.AddHttpClient(OpenAIProvider.Id).ConfigurePrimaryHttpMessageHandler(ProviderHandler)
            .AddHttpMessageHandler(() => new ProviderErrors("OpenAI"));
        services.Replace(ServiceDescriptor.Scoped<IProviderCredentials, RequestCredentials>());
        services.AddScoped<ImageInputs>();
        DelegatedFiles.AddServices(services);
        services.AddHttpClient("mcp-media", c => c.Timeout = TimeSpan.FromSeconds(20))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { UseCookies = false, UseProxy = false, AllowAutoRedirect = false });
        services.AddHttpClient("mcp-google", c => c.BaseAddress = new Uri("https://generativelanguage.googleapis.com/"))
            .ConfigurePrimaryHttpMessageHandler(ProviderHandler).AddHttpMessageHandler(() => new ProviderErrors("Google"));
        services.AddHttpClient("mcp-anthropic", c => c.BaseAddress = new Uri("https://api.anthropic.com/"))
            .ConfigurePrimaryHttpMessageHandler(ProviderHandler).AddHttpMessageHandler(() => new ProviderErrors("Anthropic"));
        ResourceContentDecoding.AddServices(services);
        services.AddMcpServer(options => options.ProtocolVersion = ProtocolVersion)
            .WithRequestFilters(filters => filters.AddReadResourceFilter(ResourceContentDecoding.Filter))
            .WithHttpTransport(options =>
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
        var access = app.Services.GetRequiredService<McpProviderAccess>();
        foreach (var server in Servers)
        {
            var id = server.Catalog.Id;
            var managed = access.IsManaged(id);
            if (managed && (string.IsNullOrWhiteSpace(authorizationPolicy) ||
                !app.Services.GetRequiredService<IServiceProviderIsService>().IsService(typeof(IMcpOnBehalfOf))))
                throw new InvalidOperationException($"Managed MCP '{id}' requires Microsoft OAuth and Graph OBO configuration.");

            var endpoint = MapServer(app, id, managed ? authorizationPolicy : null, session =>
            {
                session.ServerInfo = server.Catalog.CreateServerInfo();
                session.ServerInstructions = server.Catalog.Instructions;
                session.ToolCollection = server.Implementation.CreateTools(managed);
                if (server.Credentials is null)
                    foreach (var tool in session.ToolCollection)
                        if (tool.ProtocolTool.Icons is not { Count: > 0 }) tool.ProtocolTool.Icons = session.ServerInfo.Icons;
                if (server.Prompts.Any || server.Implementation.Complete is not null)
                {
                    session.Handlers = new McpServerHandlers { CompleteHandler = server.Implementation.Complete };
                    server.Prompts.Configure(session.Handlers);
                }
            }, allowAnonymous: !managed).WithMetadata(new ProviderEndpoint(server.Credentials, managed));
            configureServer?.Invoke(id, endpoint);
        }
    }

    public static void MapStreaikMcpAccessMetadata(this WebApplication app, bool graphObo)
    {
        var access = app.Services.GetRequiredService<McpProviderAccess>();
        var modes = Servers.Where(server => server.Credentials is not null).ToDictionary(server => server.Catalog.Id,
            server => access.IsManaged(server.Catalog.Id) ? "delegated" : "headers", StringComparer.Ordinal);
        app.MapGet("/.well-known/streaik-mcp-access", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Json(new { providerModes = modes, graphObo });
        }).AllowAnonymous();
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

    internal static bool IsManagedProviderRequest(HttpContext? context) =>
        context?.GetEndpoint()?.Metadata.GetMetadata<ProviderEndpoint>() is { Managed: true };

    private static SocketsHttpHandler ProviderHandler() => new() { UseCookies = false, AllowAutoRedirect = false };

    private sealed class RequestCredentials(IHttpContextAccessor accessor, McpProviderAccess access) : IProviderCredentials
    {
        public string? GetApiKey(string providerId)
        {
            var context = accessor.HttpContext;
            if (context?.GetEndpoint()?.Metadata.GetMetadata<ProviderEndpoint>() is not { Binding: { } binding } endpoint ||
                binding.Provider != providerId)
                return null;

            if (endpoint.Managed)
                return context.User.Identity?.IsAuthenticated == true ? access.GetKey(binding.ServerId) : null;

            return context.Request.Headers.TryGetValue(binding.Header, out var values) && values.Count == 1 &&
                values[0] is { } value ? binding.ParseHeader(value) : null;
        }
    }

    private sealed record ServerDefinition(Func<bool, McpServerPrimitiveCollection<McpServerTool>> CreateTools, string? Provider = null, string? ProviderHost = null,
        McpRequestHandler<CompleteRequestParams, CompleteResult>? Complete = null);

    private sealed record ProviderEndpoint(ProviderBinding? Binding, bool Managed);

    private sealed record ServerEntry(McpCatalog.Entry Catalog, ServerDefinition Implementation, ProviderBinding? Credentials, CatalogPrompts Prompts);
}
