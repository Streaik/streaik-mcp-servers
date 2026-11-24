using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;

namespace Streaik.Mcp.EntraAuth;

/// <summary>Matches only the literal paths and query keys in a server's bundled catalog.</summary>
internal sealed partial class GraphResource
{
    private readonly string[] _segments;
    private readonly Dictionary<string, StringValues> _query;
    internal string? MimeType { get; }

    internal GraphResource(string template, string? mimeType)
    {
        // Split before URI escaping so the catalog's simple {variables} remain intact.
        if (!template.StartsWith(GraphReads.Origin + "/beta/", StringComparison.Ordinal))
            throw new InvalidOperationException("An imported Graph resource must target the Microsoft Graph beta endpoint.");
        var parts = template[GraphReads.Origin.Length..].Split('?', 2);
        _segments = parts[0].Split('/');
        _query = parts.Length == 2 ? QueryHelpers.ParseQuery(parts[1]) : new();
        MimeType = mimeType;
    }

    internal static bool TryGraphUri(string input, [NotNullWhen(true)] out Uri? uri)
    {
        uri = null;
        return input.Length <= 8192 && input.StartsWith(GraphReads.Origin + "/beta/", StringComparison.Ordinal) &&
            !input.Contains('\\') && !input.Any(char.IsControl) && Uri.TryCreate(input, UriKind.Absolute, out uri) &&
            uri.Scheme == Uri.UriSchemeHttps && uri.Host == "graph.microsoft.com" && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;
    }

    internal bool Matches(Uri uri)
    {
        var segments = uri.AbsolutePath.Split('/');
        if (segments.Length != _segments.Length) return false;
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = Uri.UnescapeDataString(segments[i]);
            if (Variable().IsMatch(_segments[i]))
            {
                if (segment.Length is 0 or > 1024 || segment is "." or ".." || segment.Any(char.IsControl) || segment.IndexOfAny(['/', '\\', '?', '#', '%']) >= 0)
                    return false;
            }
            else if (segment != _segments[i]) return false;
        }

        var query = QueryHelpers.ParseQuery(uri.Query);
        return query.Count == _query.Count && _query.All(pair => pair.Value.Count == 1 &&
            query.TryGetValue(pair.Key, out var value) && value.Count == 1 && MatchValue(pair.Value[0]!, value[0]!));
    }

    private static bool MatchValue(string expected, string actual)
    {
        var slot = Variable().Match(expected);
        if (!slot.Success) return expected == actual;
        var before = expected.AsSpan(0, slot.Index);
        var after = expected.AsSpan(slot.Index + slot.Length);
        if (actual.Length <= before.Length + after.Length || !actual.AsSpan().StartsWith(before, StringComparison.Ordinal) ||
            !actual.AsSpan().EndsWith(after, StringComparison.Ordinal)) return false;
        var value = actual[before.Length..(actual.Length - after.Length)];
        if (value.Any(char.IsControl) || value.Contains('{') || value.Contains('}')) return false;
        // Intentionally allow the catalog's OData search/filter values, but never extra query keys.
        return slot.Value != "{top}" || int.TryParse(value, System.Globalization.NumberStyles.None, null, out var top) && top > 0;
    }

    [GeneratedRegex("\\{[A-Za-z0-9_-]+\\}", RegexOptions.CultureInvariant)]
    private static partial Regex Variable();
}
