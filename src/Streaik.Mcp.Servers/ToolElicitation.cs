using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Streaik.Mcp.Servers;

internal static class ToolElicitation
{
    // Null means the client did not advertise form support. A rejected response never falls back.
    internal static IDictionary<string, JsonElement>? ElicitForm(this RequestContext<CallToolRequestParams> context,
        string key, string message, ElicitRequestParams.RequestSchema schema, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var call = context.Params!;
        var state = $"elicit:{call.Name}:{key}";
        if (call.RequestState is not null || call.InputResponses is not null)
        {
            if (call.RequestState != state || call.InputResponses?.TryGetValue(key, out var response) != true || response is null)
                throw InvalidResponse();

            ElicitResult? result;
            try { result = response.Deserialize(InputResponse.ElicitResultJsonTypeInfo); }
            catch (JsonException) { throw InvalidResponse(); }

            if (result?.Action == "decline") throw new ToolElicitationException("The user declined the request.");
            if (result?.Action == "cancel") throw new ToolElicitationException("The user cancelled the request.");
            if (result is not { Action: "accept", Content: { } values } ||
                schema.Required?.Any(name => !values.ContainsKey(name)) == true ||
                values.Keys.Any(name => !schema.Properties.ContainsKey(name)))
                throw InvalidResponse();
            return values;
        }

        // The SDK exposes the current request's advertised capabilities here for the stateless protocol.
        if (!context.Server.IsMrtrSupported || context.Server.ClientCapabilities?.Elicitation?.Form is null)
            return null;

        throw new InputRequiredException(new Dictionary<string, InputRequest>
        {
            [key] = InputRequest.ForElicitation(new ElicitRequestParams { Mode = "form", Message = message, RequestedSchema = schema }),
        }, requestState: state);
    }

    internal static Dictionary<string, string?> ReviewText(this RequestContext<CallToolRequestParams> context,
        string key, string message, CancellationToken ct, params TextField[] fields)
    {
        var values = context.ElicitForm(key, message, new()
        {
            Properties = fields.ToDictionary(field => field.Name, field => (ElicitRequestParams.PrimitiveSchemaDefinition)
                new ElicitRequestParams.StringSchema
                {
                    Title = field.Title, Default = field.Value, MinLength = field.Required ? 1 : null, MaxLength = field.MaxLength,
                }),
            Required = fields.Where(field => field.Required).Select(field => field.Name).ToList(),
        }, ct);
        return fields.ToDictionary(field => field.Name, field => values is null ? field.Value : ReadText(values, field));
    }

    // Pass the same resolved name or ID that the delete will use. Never take a new target from the reply.
    internal static void ConfirmDelete(this RequestContext<CallToolRequestParams> context, string target, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        const string field = "confirmation";
        var values = context.ElicitForm("confirmDelete", $"Type the exact name or ID to confirm deletion: {target}", new()
        {
            Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
            {
                [field] = new ElicitRequestParams.StringSchema { Title = "Name or ID", MinLength = 1, MaxLength = target.Length },
            },
            Required = [field],
        }, ct);
        if (values is not null && (values[field].ValueKind != JsonValueKind.String ||
            !string.Equals(values[field].GetString(), target, StringComparison.Ordinal)))
            throw new ToolElicitationException("The confirmation did not match the name or ID. Nothing was deleted.");
    }

    private static string? ReadText(IDictionary<string, JsonElement> values, TextField field)
    {
        if (!values.TryGetValue(field.Name, out var value)) return null; // Only optional fields can be absent.
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text || text.Length > field.MaxLength ||
            (field.Required && string.IsNullOrWhiteSpace(text)))
            throw new ToolElicitationException($"Invalid value for {field.Title}.");
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static ToolElicitationException InvalidResponse() => new("Invalid elicitation response.");

    internal readonly record struct TextField(string Name, string Title, string? Value, int MaxLength, bool Required = false);
}

internal sealed class ToolElicitationException(string message) : Exception(message);
