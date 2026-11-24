using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Streaik.Mcp.Servers;

// Capture provider errors before the shared HTTP adapters discard the response body.
internal sealed class ProviderErrors(string provider) : DelegatingHandler
{
    private const int MaxBodyBytes = 16 * 1024;
    private static readonly Regex SensitiveText = new(
        """(?i)https?://[^\s'"<>]+|data:[^\s'"<>]+|\bBearer\s+[\w.+/=-]+|\b(?:sk-[\w-]+|AIza[\w-]{20,})""",
        RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);
    private static readonly Regex Identifier = new("""\A[A-Za-z0-9_.\[\]-]{1,128}\z""", RegexOptions.NonBacktracking);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var response = await base.SendAsync(request, ct);
        if (response.IsSuccessStatusCode) return response;

        using (response)
        {
            var status = response.StatusCode;
            var prefix = $"{provider} request failed (HTTP {(int)status})";
            var fallback = status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? "Check the provider credential and its model access."
                : "The provider did not supply readable error details.";

            // Auth errors can include partially masked credentials that cannot be matched exactly.
            if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new McpProviderException($"{prefix}: {fallback}", status);

            string? message = null, code = null, field = null;
            try
            {
                var bytes = await ImageInputs.ReadBoundedAsync(response.Content, MaxBodyBytes, ct);
                using var json = JsonDocument.Parse(bytes);
                var error = Property(json.RootElement, "error");
                message = Clean(String(error, "message"));
                code = Tag(String(error, "code") ?? String(error, "status") ?? String(error, "type"));
                field = Tag(String(error, "param"));
                if (Property(error, "details") is { ValueKind: JsonValueKind.Array } details)
                {
                    foreach (var detail in details.EnumerateArray())
                    {
                        if (Tag(String(detail, "reason")) is { } reason) code = reason;
                        if (Property(detail, "fieldViolations") is not { ValueKind: JsonValueKind.Array } violations) continue;
                        foreach (var violation in violations.EnumerateArray()) field ??= Tag(String(violation, "field"));
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or HttpRequestException or ArgumentException)
            { /* Do not fall back to raw response text. */ }

            var labels = string.Join(", ", new[] { code, field is null ? null : $"field: {field}" }.OfType<string>());
            throw new McpProviderException($"{prefix}{(labels.Length == 0 ? "" : $" [{labels}]")}: {message ?? fallback}", status);
        }

        string? Tag(string? value) => Clean(value) is { } cleaned && Identifier.IsMatch(cleaned) ? cleaned : null;

        string? Clean(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            foreach (var header in new[] { "Authorization", "x-api-key", "x-goog-api-key" })
                if (request.Headers.TryGetValues(header, out var values))
                    foreach (var secret in values.Where(s => !string.IsNullOrWhiteSpace(s)))
                        value = value.Replace(secret, "[redacted]", StringComparison.Ordinal);
            if (request.Headers.Authorization?.Parameter is { Length: > 0 } token)
                value = value.Replace(token, "[redacted]", StringComparison.Ordinal);
            value = SensitiveText.Replace(value, "[redacted]");
            var lines = value.Split('\n').TakeWhile(line => !line.TrimStart().StartsWith("at ", StringComparison.Ordinal));
            return new string(string.Join("\n", lines).Take(2048).Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim() is { Length: > 0 } text
                ? text : null;
        }
    }

    private static JsonElement Property(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field)
        ? field : default;

    private static string? String(JsonElement value, string name) => Property(value, name) is { ValueKind: JsonValueKind.String } field
        ? field.GetString() : null;
}

internal sealed class McpProviderException(string message, HttpStatusCode status) : HttpRequestException(message, null, status);
