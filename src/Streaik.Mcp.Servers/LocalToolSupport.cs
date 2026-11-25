using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Streaik.Mcp.Servers;

internal static class LocalToolSupport
{
    private static readonly JsonSerializerOptions StructuredOptions = new(JsonSerializerOptions.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    internal static McpServerPrimitiveCollection<McpServerTool> CreateTools(Type type)
    {
        var tools = new McpServerPrimitiveCollection<McpServerTool>();
        foreach (var method in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly))
            if (method.IsDefined(typeof(McpServerToolAttribute))) tools.Add(McpServerTool.Create(method));
        return tools;
    }

    internal static CallToolResult Structured<T>(T value) => new() { StructuredContent = JsonSerializer.SerializeToElement(value, StructuredOptions) };

    internal static CallToolResult Text(string message, bool error = false) => new()
    {
        IsError = error ? true : null,
        Content = [new TextContentBlock { Text = message }],
    };
}
