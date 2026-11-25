using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Serialization;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using static Streaik.Mcp.Servers.LocalToolSupport;

namespace Streaik.Mcp.Servers;

internal static class ModelContextUtils
{
    private const int MaxWaitDurationSeconds = 120;
    private const int MaxWaitIterations = 1_000;
    private const int MaxProgressPrefixLength = 1_024;
    private const string Unsupported = "This tool requires MRTR/input-required support.";

    internal static McpServerPrimitiveCollection<McpServerTool> CreateTools() => LocalToolSupport.CreateTools(typeof(ModelContextUtils));

    public enum ElicitFieldType
    {
        [EnumMember(Value = "String")] String,
        [EnumMember(Value = "Email")] Email,
        [EnumMember(Value = "Date")] Date,
        [EnumMember(Value = "DateTime")] DateTime,
        [EnumMember(Value = "Uri")] Uri,
        [EnumMember(Value = "Number")] Number,
        [EnumMember(Value = "Enum")] Enum,
        [EnumMember(Value = "Boolean")] Boolean,
    }

    [Description("Test MCP progress notifications by waiting N seconds for X iterations and reporting progress each tick.")]
    [McpServerTool(Name = "model_context_utils_wait_with_progress", Title = "Wait with progress notifications (test)", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    public static async Task<CallToolResult> WaitWithProgress(
        RequestContext<CallToolRequestParams> requestContext,
        [Description("Seconds to wait per iteration (>= 0).")] int waitSeconds,
        [Description("Number of iterations (>= 1).")] int times,
        [Description("Optional progress message prefix.")] string? messagePrefix = null,
        CancellationToken cancellationToken = default)
    {
        waitSeconds = Math.Max(0, waitSeconds);
        times = Math.Max(1, times);
        if (times > MaxWaitIterations || (long)waitSeconds * times > MaxWaitDurationSeconds)
            return Text($"The progress test supports at most {MaxWaitIterations} iterations and {MaxWaitDurationSeconds} seconds per call.", error: true);

        var prefix = string.IsNullOrWhiteSpace(messagePrefix) ? "Waiting" : messagePrefix.Trim();
        if (prefix.Length > MaxProgressPrefixLength)
            return Text($"The progress message prefix must be at most {MaxProgressPrefixLength} characters.", error: true);

        var clock = Stopwatch.StartNew();
        try
        {
            await Report(0, $"{prefix} (starting)");
            for (var i = 1; i <= times; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(waitSeconds), cancellationToken);
                await Report(i, $"{prefix} ({i}/{times}) - elapsed {clock.Elapsed:mm\\:ss}");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Text($"{prefix}: cancelled after {clock.Elapsed:mm\\:ss}.");
        }

        return Text($"{prefix}: done. ({times}x {waitSeconds}s) total elapsed {clock.Elapsed:mm\\:ss}.");

        async Task Report(int progress, string message)
        {
            if (requestContext.Params?.ProgressToken is not { } token) return;
            await requestContext.Server.SendNotificationAsync("notifications/progress", new ProgressNotificationParams
            {
                ProgressToken = token,
                Progress = new() { Progress = progress, Total = times, Message = message },
            }, cancellationToken: cancellationToken);
        }
    }

    [Description("Test Elicit capabilites by requesting a form with a single field")]
    [McpServerTool(Name = "model_context_utils_test_elicit", Title = "Elicit single-field form test", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    public static CallToolResult TestElicit(
        RequestContext<CallToolRequestParams> requestContext,
        [Description("Elicit message")] string message,
        [Description("Field type")] ElicitFieldType fieldType,
        [Description("Field name")] string fieldName,
        [Description("Field description")] string description,
        [Description("Field required")] bool required,
        [Description("Field value")] string? defaultValue = null)
    {
        const string key = "elicit";
        if (ReadElicitation(requestContext, key) is { } resumed) return resumed;
        if (!requestContext.Server.IsMrtrSupported) return Text(Unsupported);

        var name = string.IsNullOrWhiteSpace(fieldName) ? "value" : fieldName;
        ElicitRequestParams.PrimitiveSchemaDefinition schema = fieldType switch
        {
            ElicitFieldType.String => StringSchema(null),
            ElicitFieldType.Email => StringSchema("email"),
            ElicitFieldType.Date => StringSchema("date"),
            ElicitFieldType.DateTime => StringSchema("date-time"),
            ElicitFieldType.Uri => StringSchema("uri"),
            ElicitFieldType.Number => new ElicitRequestParams.NumberSchema { Title = name, Description = description },
            ElicitFieldType.Boolean => new ElicitRequestParams.BooleanSchema { Title = name, Description = description },
            ElicitFieldType.Enum => new ElicitRequestParams.TitledSingleSelectEnumSchema
            {
                Title = name,
                Description = description,
                Default = defaultValue,
                OneOf = [new() { Title = "Option 1", Const = "Option1" }, new() { Title = "Option 2", Const = "Option2" }],
            },
            _ => throw new ArgumentOutOfRangeException(nameof(fieldType), fieldType, null),
        };
        throw RequireForm(key, "modelcontextutils:test-elicit", message, name, schema, required);

        ElicitRequestParams.StringSchema StringSchema(string? format) => new()
        {
            Title = name, Description = description, Default = defaultValue, Format = format,
        };
    }

    [Description("Test Elicit enum capabilities with configurable options")]
    [McpServerTool(Name = "model_context_utils_test_elicit_enum", Title = "Elicit enum test", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    public static CallToolResult TestElicitEnum(
        RequestContext<CallToolRequestParams> requestContext,
        [Description("Message shown above the enum field")] string message,
        [Description("Field name")] string fieldName,
        [Description("Field description")] string description,
        [Description("Enum options (comma separated, e.g. OptionA,OptionB,OptionC)")] string options,
        [Description("Is field required")] bool required = true,
        [Description("Is field multi select")] bool mutliSelect = false, // Existing wire spelling.
        [Description("Default value (must match one of the options)")] string? defaultValue = null)
    {
        const string key = "elicit_enum";
        if (ReadElicitation(requestContext, key) is { } resumed) return resumed;
        if (!requestContext.Server.IsMrtrSupported) return Text(Unsupported);

        var name = string.IsNullOrWhiteSpace(fieldName) ? "selection" : fieldName;
        var choices = SplitOptions(options).Select(value => new ElicitRequestParams.EnumSchemaOption { Title = value, Const = value }).ToList();
        if (choices.Count == 0) return Text("At least one enum option must be provided.", error: true);

        ElicitRequestParams.PrimitiveSchemaDefinition schema = mutliSelect
            ? new ElicitRequestParams.TitledMultiSelectEnumSchema
            {
                Title = name, Description = description, Default = SplitOptions(defaultValue ?? ""),
                Items = new() { AnyOf = choices },
            }
            : new ElicitRequestParams.TitledSingleSelectEnumSchema
            {
                Title = name, Description = description, Default = defaultValue, OneOf = choices,
            };
        throw RequireForm(key, "modelcontextutils:test-elicit-enum", message, name, schema, required);
    }

    private static string[] SplitOptions(string options) => options.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static CallToolResult? ReadElicitation(RequestContext<CallToolRequestParams> context, string key)
    {
        if (context.Params?.InputResponses?.TryGetValue(key, out var response) != true || response is null) return null;
        try
        {
            if (response.Deserialize(InputResponse.ElicitResultJsonTypeInfo) is { } result) return Structured(result);
        }
        catch (JsonException) { }
        return Text("Invalid elicitation response.", error: true);
    }

    private static InputRequiredException RequireForm(string key, string state, string message, string name,
        ElicitRequestParams.PrimitiveSchemaDefinition schema, bool required) => new(
        inputRequests: new Dictionary<string, InputRequest>
        {
            [key] = InputRequest.ForElicitation(new ElicitRequestParams
            {
                Mode = "form",
                Message = message,
                RequestedSchema = new()
                {
                    Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition> { [name] = schema },
                    Required = required ? [name] : [],
                },
            }),
        },
        requestState: state);
}
