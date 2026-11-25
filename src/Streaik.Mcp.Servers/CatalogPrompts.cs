using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Streaik.Mcp.Servers;

internal sealed class CatalogPrompts
{
    private readonly Definition[] _prompts;

    internal CatalogPrompts(McpCatalog.Entry entry)
    {
        _prompts = entry.Capabilities.GetProperty("prompts").Deserialize<Definition[]>(McpJsonUtilities.DefaultOptions)!
            .OrderBy(item => item.Template.Name, StringComparer.Ordinal).ToArray();
        var icons = entry.CreateServerInfo().Icons;
        foreach (var item in _prompts)
            if (item.Template.Icons is not { Count: > 0 }) item.Template.Icons = icons;
    }

    internal bool Any => _prompts.Length > 0;

    internal void Configure(McpServerHandlers handlers)
    {
        if (!Any) return;
        handlers.ListPromptsHandler = (_, _) => ValueTask.FromResult(new ListPromptsResult
        {
            Prompts = _prompts.Select(item => item.Template).ToArray(),
        });
        handlers.GetPromptHandler = (request, _) => ValueTask.FromResult(Render(request.Params!));
    }

    private GetPromptResult Render(GetPromptRequestParams request)
    {
        var prompt = Array.Find(_prompts, item => item.Template.Name == request.Name)
            ?? throw new McpProtocolException("Prompt not found in this MCP catalog.", McpErrorCode.InvalidParams);
        var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var argument in prompt.Template.Arguments ?? [])
        {
            if (request.Arguments?.TryGetValue(argument.Name, out var value) == true && value.ValueKind == JsonValueKind.String)
                arguments[argument.Name] = value.GetString()!;
            else if (argument.Required == true)
                throw new McpProtocolException($"Missing prompt argument '{argument.Name}'.", McpErrorCode.InvalidParams);
        }

        var text = Regex.Replace(prompt.Prompt, "\\{([A-Za-z0-9_-]+)\\}", match => arguments.GetValueOrDefault(match.Groups[1].Value) ?? match.Value);
        return new() { Description = prompt.Template.Description, Messages = [new PromptMessage { Role = Role.User, Content = new TextContentBlock { Text = text } }] };
    }

    private sealed record Definition(string Prompt, Prompt Template);
}
