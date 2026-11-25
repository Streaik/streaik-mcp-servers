using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;

namespace Streaik.Mcp.Servers;

/// <summary>A startup snapshot: a request cannot select the credential source.</summary>
public sealed class McpProviderAccess
{
    private readonly Dictionary<string, string> keys = new(StringComparer.Ordinal);

    internal McpProviderAccess(IConfiguration configuration, bool headerAuth, IEnumerable<ProviderBinding> bindings)
    {
        if (headerAuth) return;
        foreach (var binding in bindings)
        {
            var key = configuration[$"Providers:{binding.Provider}:ApiKey"] ??
                configuration[$"{binding.Provider.ToUpperInvariant()}_API_KEY"];
            if (string.IsNullOrWhiteSpace(key))
            {
                var header = configuration[$"DomainHeaders:{binding.Host}:{binding.Header}"];
                if (string.IsNullOrWhiteSpace(header)) continue;
                key = binding.ParseHeader(header);
            }
            else key = ProviderBinding.IsKey(key.Trim()) ? key.Trim() : null;

            if (key is null) throw new InvalidOperationException($"Invalid central API key configuration for '{binding.Provider}'.");
            keys.Add(binding.ServerId, key);
        }
    }

    public bool IsManaged(string serverId) => keys.ContainsKey(serverId);

    internal string? GetKey(string serverId) => keys.GetValueOrDefault(serverId);
}

internal sealed record ProviderBinding(string ServerId, string Provider, string Host, string Header, string? Scheme)
{
    internal static bool IsKey(string? value) => value is { Length: > 0 and <= 4096 } &&
        value.All(c => c is > ' ' and < '\u007f' and not ',');

    internal string? ParseHeader(string value)
    {
        if (value.Length > 4112 || value.Contains('\r') || value.Contains('\n')) return null;
        var key = Scheme is null ? value.Trim() :
            AuthenticationHeaderValue.TryParse(value, out var auth) &&
            string.Equals(auth.Scheme, Scheme, StringComparison.OrdinalIgnoreCase) ? auth.Parameter : null;
        return IsKey(key) ? key : null;
    }
}
