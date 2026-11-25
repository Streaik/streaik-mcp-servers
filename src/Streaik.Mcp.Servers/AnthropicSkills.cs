using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using static Streaik.Mcp.Servers.ProviderToolSupport;

namespace Streaik.Mcp.Servers;

internal static class AnthropicSkills
{
    internal static McpServerPrimitiveCollection<McpServerTool> CreateTools() => CreateProviderTools(typeof(AnthropicSkills),
        (nameof(AnthropicSkills_List), "List Anthropic skills, optionally filtering by source or passing a page token.", true),
        (nameof(AnthropicSkills_Get), "Read the details of a single Anthropic skill.", true));

    private static Task<CallToolResult> AnthropicSkills_List(
        IServiceProvider services, CancellationToken ct,
        [Description("Number of results, from 1 through 100.")] int limit = 20,
        [Description("Continuation token from the previous page.")] string? page = null,
        [Description("Filter by anthropic or custom.")] string? source = null) => Run(async ct =>
    {
        if (limit is < 1 or > 100 || source is not (null or "anthropic" or "custom"))
            throw new ArgumentException("Invalid skills limit or source.");
        var path = $"v1/skills?limit={limit}" + (page is null ? "" : $"&page={Uri.EscapeDataString(RequiredText(page, 1024))}")
            + (source is null ? "" : $"&source={source}");
        return await Anthropic(services, path, ct);
    }, ct);

    private static Task<CallToolResult> AnthropicSkills_Get(
        [Description("The skill ID to read.")] string skillId, IServiceProvider services, CancellationToken ct) => Run(async ct =>
    {
        if (string.IsNullOrWhiteSpace(skillId) || skillId.Length > 200 || !skillId.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
            throw new ArgumentException("Invalid skill ID.");
        return await Anthropic(services, $"v1/skills/{skillId}", ct);
    }, ct);

    private static async Task<CallToolResult> Anthropic(IServiceProvider services, string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("x-api-key", Key(services, "anthropic"));
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Headers.Add("anthropic-beta", "skills-2025-10-02");
        var bytes = await Send(services, "mcp-anthropic", request, 256 * 1024, ct);
        using var document = JsonDocument.Parse(bytes);
        return Text(document.RootElement.GetRawText());
    }
}
