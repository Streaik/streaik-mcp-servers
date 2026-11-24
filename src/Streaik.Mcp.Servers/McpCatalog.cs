using System.Collections.ObjectModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Streaik.Mcp.Servers;

/// <summary>Bundled catalog shared by the HeaderAuth and Entra runtimes.</summary>
public static class McpCatalog
{
    private const string Origin = "https://default-mcp-host.invalid/";
    private const string ResourcePrefix = "Streaik.Mcp.Catalog.";
    public static IReadOnlyDictionary<string, Entry> Servers { get; } = Load();

    public sealed record Entry(string Id, JsonElement Server, JsonElement Capabilities, IReadOnlyDictionary<string, string>? Obo)
    {
        public Implementation CreateServerInfo()
        {
            var info = Server.Deserialize<Implementation>(McpJsonUtilities.DefaultOptions)!;
            info.Name = info.Name[(info.Name.LastIndexOf('/') + 1)..];
            return info;
        }
    }

    private static IReadOnlyDictionary<string, Entry> Load()
    {
        var assembly = typeof(McpCatalog).Assembly;
        var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var name in assembly.GetManifestResourceNames().Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var file = JsonDocument.Parse(stream);
            var entry = file.RootElement;
            var server = entry.GetProperty("server");
            var id = name[ResourcePrefix.Length..^5];
            var url = server.GetProperty("remotes")[0].GetProperty("url").GetString()!;
            if (url != Origin + id)
                throw new InvalidOperationException($"The bundled MCP transport URL must match its catalog filename: '{id}'.");
            var obo = entry.TryGetProperty("obo", out var value)
                ? new ReadOnlyDictionary<string, string>(value.Deserialize<Dictionary<string, string>>()!) : null;
            entries.Add(id, new Entry(id, server.Clone(), entry.GetProperty("capabilities").Clone(), obo));
        }
        if (entries.Count == 0) throw new InvalidOperationException("The bundled MCP catalog is missing.");
        return new ReadOnlyDictionary<string, Entry>(entries);
    }
}
