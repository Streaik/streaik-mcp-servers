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
    private static readonly ServerEntry[] Servers =
    [
        new("openai-images", "OpenAI-Images", "OpenAI Images", "1.0.0", "openai", "Authorization", "Bearer"),
        new("google-images", "Google-Images", "Google Images", "1.0.0", "google", "x-goog-api-key"),
        new("anthropic-skills", "Anthropic-Skills", "Anthropic Skills", "1.0.0", "anthropic", "x-api-key")
    ];

    public static IServiceCollection AddStreaikMcp(this IServiceCollection services, IConfiguration configuration, bool headerAuth)
    {
        services.AddHttpContextAccessor();
        services.AddOpenAI();
        if (headerAuth)
            services.Replace(ServiceDescriptor.Scoped<IProviderCredentials, HeaderCredentials>());
        services.AddScoped<ImageInputs>();
        services.AddHttpClient("mcp-media", c => c.Timeout = TimeSpan.FromSeconds(20))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { UseCookies = false, UseProxy = false, AllowAutoRedirect = false });
        services.AddHttpClient("mcp-google", c => c.BaseAddress = new Uri("https://generativelanguage.googleapis.com/"))
            .ConfigurePrimaryHttpMessageHandler(ProviderHandler);
        services.AddHttpClient("mcp-anthropic", c => c.BaseAddress = new Uri("https://api.anthropic.com/"))
            .ConfigurePrimaryHttpMessageHandler(ProviderHandler);
        services.AddMcpServer(options => options.ProtocolVersion = ProtocolVersion).WithHttpTransport(options =>
        {
            options.SessionMode = HttpServerSessionMode.Stateless;
            options.ConfigureSessionOptions = (context, session, _) =>
            {
                var server = GetServer(context) ?? throw new InvalidOperationException("No MCP server is mapped for the endpoint.");
                session.ServerInfo = new Implementation { Name = server.Name, Title = server.Title, Version = server.Version };
                session.ToolCollection = Tools.For(server.Id);
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
    public static void MapStreaikMcp(this WebApplication app, string? authorizationPolicy = null)
    {
        foreach (var server in Servers)
        {
            var endpoint = app.MapMcp($"/{server.Id}");
            endpoint.WithMetadata(server).RequireCors("StreaikMcp");
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
        }
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

    private sealed record ServerEntry(string Id, string Name, string Title, string Version, string Provider, string Header, string? Scheme = null);
}
