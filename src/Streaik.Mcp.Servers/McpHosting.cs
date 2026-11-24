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
    private static readonly ServerEntry[] Servers = McpCatalog.Servers.Values.Where(entry => entry.Obo is null)
        .Select(entry =>
        {
            var provider = entry.Id switch
            {
                "openai-images" => OpenAIProvider.Id,
                "google-images" => "google",
                "anthropic-skills" => "anthropic",
                _ => throw new InvalidOperationException($"No provider implementation for '{entry.Id}'."),
            };
            var header = entry.Server.GetProperty("remotes")[0].GetProperty("headers")[0];
            return new ServerEntry(entry, provider, header.GetProperty("name").GetString()!,
                header.TryGetProperty("value", out var format) ? format.GetString()!.Split(' ')[0] : null);
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
                session.ToolCollection = Tools.For(id);
            }).WithMetadata(server);
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
        Action<McpServerOptions> configureSession)
    {
        var endpoint = app.MapMcp($"/{id}").WithMetadata(configureSession).RequireCors("StreaikMcp");
        if (authorizationPolicy is not null)
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
            if (context is null || GetServer(context) is not { } server || server.Provider != providerId ||
                !context.Request.Headers.TryGetValue(server.Header, out var values) || values.Count != 1 ||
                values[0] is not { } value || string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Contains(','))
                return null;

            return server.Scheme is null ? value.Trim()
                : AuthenticationHeaderValue.TryParse(value, out var auth) &&
                    string.Equals(auth.Scheme, server.Scheme, StringComparison.OrdinalIgnoreCase) ? auth.Parameter : null;
        }
    }

    private sealed record ServerEntry(McpCatalog.Entry Catalog, string Provider, string Header, string? Scheme);
}
