using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Streaik.Mcp.Servers;

namespace Streaik.Mcp.EntraAuth.Tests;

public sealed partial class BridgeTests
{
    private const string CountriesId = "github-rest-countries";
    private const string UtilsId = "modelcontext-utils";
    private const string LocalCountries = "https://localhost/" + CountriesId;
    private const string LocalUtils = "https://localhost/" + UtilsId;
    private const string Elicit = "model_context_utils_test_elicit";
    private const string ElicitEnum = "model_context_utils_test_elicit_enum";
    private const string Wait = "model_context_utils_wait_with_progress";

    [Theory]
    [InlineData(CountriesId, true, 5)]
    [InlineData(UtilsId, false, 2)]
    public async Task LocalCatalogRoutesAreAnonymousOnHeaderAuthAndBothEntraModes(string id, bool hasCompletion, int promptCount)
    {
        await using var header = await Fixture.StartAsync(headerAuth: true);
        await using var entra = await Fixture.StartAsync();
        await using var direct = await Fixture.StartAsync(bridge: false);
        var endpoint = Base + "/" + id;
        var keys = McpCatalog.Servers[id];
        Assert.True(keys.AllowAnonymous);
        using var metadata = await entra.Client.GetAsync("/.well-known/oauth-protected-resource/team/mcp/" + id);
        Assert.Equal(HttpStatusCode.NotFound, metadata.StatusCode);
        using var exchange = await entra.TokenAsync("default", Exchange(entra.Jwt(DefaultApp, Spa), endpoint));
        Assert.Equal(HttpStatusCode.BadRequest, exchange.StatusCode);
        using var protectedRoute = await entra.McpAsync(Default, method: "server/discover");
        Assert.Equal(HttpStatusCode.Unauthorized, protectedRoute.StatusCode);
        using var protectedDirect = await direct.McpAsync("https://localhost/openai-images", method: "server/discover");
        Assert.Equal(HttpStatusCode.Unauthorized, protectedDirect.StatusCode);

        foreach (var (host, url) in new[] { (header, "https://localhost/" + id), (entra, endpoint), (direct, "https://localhost/" + id) })
        {
            var discovery = Result(await ReadRpc(await host.McpAsync(url, method: "server/discover")));
            Assert.Equal(keys.Instructions, discovery.TryGetProperty("instructions", out var instructions) ? instructions.GetString() : null);
            var capabilities = discovery.GetProperty("capabilities");
            Assert.True(capabilities.TryGetProperty("tools", out _));
            Assert.True(capabilities.TryGetProperty("prompts", out _));
            Assert.Equal(hasCompletion, capabilities.TryGetProperty("completions", out _));
            Assert.False(capabilities.TryGetProperty("resources", out _));

            var prompts = Result(await ReadRpc(await host.McpAsync(url, method: "prompts/list"))).GetProperty("prompts");
            Assert.Equal(promptCount, prompts.GetArrayLength());
            var templates = keys.Capabilities.GetProperty("prompts").EnumerateArray()
                .OrderBy(item => item.GetProperty("template").GetProperty("name").GetString(), StringComparer.Ordinal).ToArray();
            Assert.All(Enumerable.Range(0, promptCount), i =>
            {
                var expected = JsonNode.Parse(templates[i].GetProperty("template").GetRawText())!.AsObject();
                expected["icons"] = JsonNode.Parse(keys.Server.GetProperty("icons").GetRawText());
                Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(prompts[i].GetRawText())), prompts[i].GetRawText());
            });
            var original = keys.Capabilities.GetProperty("prompts")[0];
            var name = original.GetProperty("template").GetProperty("name").GetString()!;
            var rendered = Result(await ReadRpc(await host.McpAsync(url, method: "prompts/get", name: name, parameters:
                new() { ["name"] = name, ["arguments"] = new { query = "Iceland" } })));
            Assert.Equal(original.GetProperty("prompt").GetString()!.Replace("{query}", "Iceland"),
                rendered.GetProperty("messages")[0].GetProperty("content").GetProperty("text").GetString());
            const string widget = "ui://widget/country-detail.html";
            var read = await ReadRpc(await host.McpAsync(url, method: "resources/read", name: widget, parameters: new() { ["uri"] = widget }), allowRpcError: true);
            Assert.True(read.TryGetProperty("error", out _), read.GetRawText());

            foreach (var origin in new[] { "http://localhost:5174", "https://untrusted.example.test" })
            {
                using var options = new HttpRequestMessage(HttpMethod.Options, new Uri(url).AbsolutePath);
                options.Headers.Add("Origin", origin);
                options.Headers.Add("Access-Control-Request-Method", "POST");
                options.Headers.Add("Access-Control-Request-Headers", "content-type,mcp-protocol-version,mcp-method");
                using var preflight = await host.Client.SendAsync(options);
                Assert.Equal(origin == "http://localhost:5174", preflight.Headers.TryGetValues("Access-Control-Allow-Origin", out var origins));
                if (origins is not null) Assert.Equal(origin, Assert.Single(origins));
            }
        }

        Assert.Equal(0, entra.Graph.Calls);
    }

    [Fact]
    public async Task CountriesKeepNonWidgetToolsSchemasAndStructuredResults()
    {
        await using var host = await Fixture.StartAsync(headerAuth: true);
        var tools = Result(await ReadRpc(await host.McpAsync(LocalCountries))).GetProperty("tools").EnumerateArray()
            .ToDictionary(item => item.GetProperty("name").GetString()!, StringComparer.Ordinal);
        var args = new Dictionary<string, string>
        {
            ["github_rest_countries_search_codes"] = "name",
            ["github_rest_countries_get_detail"] = "cca",
            ["github_rest_countries_get_by_region"] = "region",
        };
        Assert.Equal(args.Keys.Order(), tools.Keys.Order());
        foreach (var (name, argument) in args)
        {
            var tool = tools[name];
            var schema = tool.GetProperty("inputSchema");
            Assert.Equal(argument, Assert.Single(schema.GetProperty("properties").EnumerateObject()).Name);
            Assert.Equal(argument, Assert.Single(schema.GetProperty("required").EnumerateArray()).GetString());
            Assert.True(tool.TryGetProperty("outputSchema", out _));
            Assert.True(tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
        }

        var search = await Call(host, LocalCountries, "github_rest_countries_search_codes", new { name = "Iceland" });
        Assert.Equal("Iceland", Assert.Single(search.GetProperty("structuredContent").GetProperty("countries").EnumerateArray())
            .GetProperty("name").GetProperty("common").GetString());
        var detail = await Call(host, LocalCountries, "github_rest_countries_get_detail", new { cca = "PL" });
        Assert.Equal("Poland", detail.GetProperty("structuredContent").GetProperty("name").GetProperty("common").GetString());
        var region = await Call(host, LocalCountries, "github_rest_countries_get_by_region", new { region = "eUrOpE" });
        var countries = region.GetProperty("structuredContent").GetProperty("countries");
        Assert.NotEmpty(countries.EnumerateArray());
        Assert.All(countries.EnumerateArray(), item => Assert.Equal("Europe", item.GetProperty("region").GetString()));
        var missing = await Call(host, LocalCountries, "github_rest_countries_get_detail", new { cca = "ZZ" });
        Assert.True(missing.GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task CountriesCompletionUsesCountryContextAndDoesNotLeakOntoOtherRoutes()
    {
        await using var host = await Fixture.StartAsync(headerAuth: true);
        var names = await Complete("compare-two-countries", "countryA", "Iceland");
        var name = Assert.Single(names);
        Assert.Empty(await Complete("compare-two-countries", "countryA", "Iceland", new { countryB = name.ToLowerInvariant() }));
        Assert.Empty(await Complete("compare-two-countries", "countryB", "Iceland", new { countryA = name }));
        Assert.Equal(names, await Complete("show-country-details", "countryName", "Iceland"));
        var regions = await Complete("list-languages-by-region", "region", "Eu");
        Assert.Contains("Europe", regions);
        Assert.Equal(regions.Order(), regions);
        Assert.Empty(await Complete("find-countries-by-query", "query", "Europe"));
        var unsupported = await ReadRpc(await host.McpAsync(LocalUtils, method: "completion/complete", parameters: Completion("show-country-details", "countryName", "Iceland", null)), allowRpcError: true);
        Assert.True(unsupported.TryGetProperty("error", out _), unsupported.GetRawText());

        async Task<string[]> Complete(string prompt, string arg, string value, object? context = null) =>
            Result(await ReadRpc(await host.McpAsync(LocalCountries, method: "completion/complete", parameters: Completion(prompt, arg, value, context))))
                .GetProperty("completion").GetProperty("values").EnumerateArray().Select(item => item.GetString()!).ToArray();
    }

    [Fact]
    public async Task UtilsKeepsToolNamesDefaultsAndStreamsRequestProgress()
    {
        await using var host = await Fixture.StartAsync(headerAuth: true);
        var tools = Result(await ReadRpc(await host.McpAsync(LocalUtils))).GetProperty("tools").EnumerateArray()
            .ToDictionary(item => item.GetProperty("name").GetString()!);
        Assert.Equal(new[] { Elicit, ElicitEnum, Wait }, tools.Keys.Order());
        var enumArgs = tools[ElicitEnum].GetProperty("inputSchema").GetProperty("properties");
        Assert.True(enumArgs.GetProperty("required").GetProperty("default").GetBoolean());
        Assert.False(enumArgs.GetProperty("mutliSelect").GetProperty("default").GetBoolean());
        Assert.False(enumArgs.TryGetProperty("multiSelect", out _));

        using var progress = await host.McpAsync(LocalUtils, method: "tools/call", name: Wait,
            parameters: Tool(Wait, new { waitSeconds = 0, times = 2, messagePrefix = "Fixture" }), progressToken: "fixture-progress");
        Assert.Equal("text/event-stream", progress.Content.Headers.ContentType?.MediaType);
        var stream = await progress.Content.ReadAsStringAsync();
        var events = stream.Split('\n').Where(line => line.StartsWith("data: ", StringComparison.Ordinal)).Select(line => JsonSerializer.Deserialize<JsonElement>(line[6..])).ToArray();
        Assert.Equal(4, events.Length);
        for (var i = 0; i < events.Length - 1; i++)
        {
            Assert.Equal("notifications/progress", events[i].GetProperty("method").GetString());
            var info = events[i].GetProperty("params");
            Assert.Equal("fixture-progress", info.GetProperty("progressToken").GetString());
            Assert.Equal(i, info.GetProperty("progress").GetInt32());
            Assert.Equal(2, info.GetProperty("total").GetInt32());
        }
        Assert.StartsWith("Fixture: done. (2x 0s)", Result(events[^1]).GetProperty("content")[0].GetProperty("text").GetString());
        Assert.True((await Call(host, LocalUtils, Wait, new { waitSeconds = 120, times = 2 })).GetProperty("isError").GetBoolean());
        Assert.True((await Call(host, LocalUtils, Wait, new { waitSeconds = 0, times = 1_001 })).GetProperty("isError").GetBoolean());
        Assert.True((await Call(host, LocalUtils, Wait, new { waitSeconds = 0, times = 1, messagePrefix = new string('x', 1_025) })).GetProperty("isError").GetBoolean());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task UtilsResumesSingleFieldAndEnumElicitationAcrossHttpRequests(bool entra, bool useEnum)
    {
        await using var host = await Fixture.StartAsync(headerAuth: !entra);
        var url = entra ? Base + "/" + UtilsId : LocalUtils;
        var tool = useEnum ? ElicitEnum : Elicit;
        var inputKey = useEnum ? "elicit_enum" : "elicit";
        var state = useEnum ? "modelcontextutils:test-elicit-enum" : "modelcontextutils:test-elicit";
        object arguments = useEnum
            ? new { message = "Choose", fieldName = "answer", description = "Pick options", options = "Red, Blue", mutliSelect = true, defaultValue = "Blue" }
            : new { message = "Choose", fieldType = "Email", fieldName = "answer", description = "Your email", required = true, defaultValue = "a@example.test" };
        var capabilities = new { elicitation = new { form = new { } } };
        var first = Result(await ReadRpc(await host.McpAsync(url, method: "tools/call", name: tool, parameters: Tool(tool, arguments), clientCapabilities: capabilities)));
        Assert.Equal("input_required", first.GetProperty("resultType").GetString());
        Assert.Equal(state, first.GetProperty("requestState").GetString());
        var input = first.GetProperty("inputRequests").GetProperty(inputKey);
        Assert.Equal("elicitation/create", input.GetProperty("method").GetString());
        var form = input.GetProperty("params");
        Assert.Equal("form", form.GetProperty("mode").GetString());
        var schema = form.GetProperty("requestedSchema");
        Assert.Equal("answer", Assert.Single(schema.GetProperty("required").EnumerateArray()).GetString());
        var property = schema.GetProperty("properties").GetProperty("answer");
        if (useEnum)
        {
            Assert.Equal("Blue", property.GetProperty("default")[0].GetString());
            Assert.Equal("Red", property.GetProperty("items").GetProperty("anyOf")[0].GetProperty("const").GetString());
        }
        else
        {
            Assert.Equal("email", property.GetProperty("format").GetString());
            Assert.Equal("a@example.test", property.GetProperty("default").GetString());
        }

        var retry = Tool(tool, arguments);
        retry["requestState"] = state;
        object response = useEnum ? new[] { "Red", "Blue" } : "a@example.test";
        retry["inputResponses"] = new Dictionary<string, object> { [inputKey] = new { action = "accept", content = new { answer = response } } };
        var result = Result(await ReadRpc(await host.McpAsync(url, method: "tools/call", name: tool, parameters: retry, clientCapabilities: capabilities)));
        Assert.Equal("complete", result.GetProperty("resultType").GetString());
        var value = result.GetProperty("structuredContent");
        Assert.Equal("accept", value.GetProperty("action").GetString());
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(response), value.GetProperty("content").GetProperty("answer")));
    }

    private static Dictionary<string, object> Completion(string prompt, string name, string value, object? context) => new()
    {
        ["ref"] = new { type = "ref/prompt", name = prompt }, ["argument"] = new { name, value }, ["context"] = new { arguments = context },
    };

    private static Dictionary<string, object> Tool(string name, object arguments) => new() { ["name"] = name, ["arguments"] = arguments };

    private static async Task<JsonElement> Call(Fixture host, string url, string name, object arguments) =>
        Result(await ReadRpc(await host.McpAsync(url, method: "tools/call", name: name, parameters: Tool(name, arguments))));

    private static JsonElement Result(JsonElement rpc)
    {
        Assert.True(rpc.TryGetProperty("result", out var value), rpc.GetRawText());
        return value;
    }
}
