using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Streaik.Mcp.Servers;

namespace Streaik.Mcp.EntraAuth;

internal sealed class GraphMcp
{
    private static readonly Lazy<Dictionary<string, GraphMcp>> Catalog = new(() => McpCatalog.Servers.Values
        .Where(entry => entry.Obo is not null)
        .ToDictionary(entry => entry.Id, entry => new GraphMcp(entry), StringComparer.Ordinal));
    private readonly McpCatalog.Entry _entry;
    private readonly ListResourcesResult _resources;
    private readonly ListResourceTemplatesResult _templates;
    private readonly List<CatalogPrompt> _prompts;
    private readonly GraphResource[] _graphResources;

    private GraphMcp(McpCatalog.Entry entry)
    {
        _entry = entry;

        _resources = entry.Capabilities.Deserialize<ListResourcesResult>(McpJsonUtilities.DefaultOptions)!;
        _templates = entry.Capabilities.Deserialize<ListResourceTemplatesResult>(McpJsonUtilities.DefaultOptions)!;
        _prompts = entry.Capabilities.GetProperty("prompts").Deserialize<List<CatalogPrompt>>(McpJsonUtilities.DefaultOptions)!;
        _graphResources = _resources.Resources.Select(resource => new GraphResource(resource.Uri, resource.MimeType))
            .Concat(_templates.ResourceTemplates.Select(template => new GraphResource(template.UriTemplate, template.MimeType))).ToArray();
    }

    internal static GraphMcp? Find(string id) => Catalog.Value.GetValueOrDefault(id);

    internal void Configure(McpServerOptions session)
    {
        session.ServerInfo = _entry.CreateServerInfo();
        // Handlers preserve order and multiple catalog entries with the same URI template.
        session.Handlers = new McpServerHandlers
        {
            ListResourcesHandler = (_, _) => ValueTask.FromResult(_resources),
            ListResourceTemplatesHandler = (_, _) => ValueTask.FromResult(_templates),
            ReadResourceHandler = ReadAsync,
        };
        if (_prompts.Count == 0) return;
        session.Handlers.ListPromptsHandler = (_, _) => ValueTask.FromResult(new ListPromptsResult { Prompts = _prompts.Select(item => item.Template).ToArray() });
        session.Handlers.GetPromptHandler = (request, _) => ValueTask.FromResult(GetPrompt(request.Params!));
    }

    private async ValueTask<ReadResourceResult> ReadAsync(RequestContext<ReadResourceRequestParams> request, CancellationToken ct)
    {
        var input = request.Params?.Uri ?? "";
        if (!GraphResource.TryGraphUri(input, out var uri) || _graphResources.FirstOrDefault(resource => resource.Matches(uri)) is not { } match)
            throw new McpProtocolException("Resource not found in this MCP catalog.", McpErrorCode.ResourceNotFound);

        var text = await request.Services!.GetRequiredService<GraphReads>().ReadAsync(uri, ct);
        return new() { Contents = [new TextResourceContents { Uri = input, MimeType = match.MimeType, Text = text }] };
    }

    private GetPromptResult GetPrompt(GetPromptRequestParams request)
    {
        var prompt = _prompts.Find(item => item.Template.Name == request.Name)
            ?? throw new McpProtocolException("Prompt not found in this MCP catalog.", McpErrorCode.InvalidParams);
        var args = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var arg in prompt.Template.Arguments ?? [])
        {
            if (request.Arguments?.TryGetValue(arg.Name, out var value) == true && value.ValueKind == JsonValueKind.String)
                args[arg.Name] = value.GetString()!;
            else if (arg.Required == true)
                throw new McpProtocolException($"Missing prompt argument '{arg.Name}'.", McpErrorCode.InvalidParams);
        }

        var text = Regex.Replace(prompt.Prompt, "\\{([A-Za-z0-9_-]+)\\}", match => args.GetValueOrDefault(match.Groups[1].Value) ?? match.Value);
        return new() { Description = prompt.Template.Description, Messages = [new PromptMessage { Role = Role.User, Content = new TextContentBlock { Text = text } }] };
    }

    private sealed record CatalogPrompt(string Prompt, Prompt Template);
}
