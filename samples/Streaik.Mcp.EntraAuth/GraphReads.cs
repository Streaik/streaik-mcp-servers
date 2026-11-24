using System.Net;
using System.Net.Http.Headers;
using ModelContextProtocol;
using Streaik.Mcp.Servers;

namespace Streaik.Mcp.EntraAuth;

internal sealed class GraphReads(IHttpClientFactory clients, IMcpOnBehalfOf obo)
{
    internal const string Target = "graph.microsoft.com";
    internal const string Origin = "https://graph.microsoft.com";
    internal const string HttpClientName = "StreaikMcpGraph";

    internal static void Add(IServiceCollection services)
    {
        services.AddScoped<GraphReads>();
        services.AddHttpClient(HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
            client.MaxResponseContentBufferSize = 4 * 1024 * 1024;
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
    }

    // The caller must match its validated URI against the current resource-bound server's catalog.
    internal async Task<string> ReadAsync(Uri uri, CancellationToken ct)
    {
        string token;
        try { token = await obo.GetAccessTokenAsync(Target, ct); }
        catch (Exception error) when (error is UnauthorizedAccessException or InvalidOperationException)
        { throw Failure("Microsoft Graph delegated access was not granted. Check the configured Graph scopes and consent."); }

        try
        {
            using var client = clients.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request, ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw Failure("Microsoft Graph denied access to this resource. Check the signed-in user's access and delegated consent.");
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new McpProtocolException("Microsoft Graph could not find this resource.", McpErrorCode.ResourceNotFound);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw Failure("Microsoft Graph is busy. Try again later.");
            if (!response.IsSuccessStatusCode) throw Failure("Microsoft Graph could not read this resource.");
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception error) when (!ct.IsCancellationRequested && error is HttpRequestException or OperationCanceledException or FormatException)
        { throw Failure("Microsoft Graph could not read this resource."); }
    }

    private static McpProtocolException Failure(string message) => new(message, McpErrorCode.InternalError);
}
