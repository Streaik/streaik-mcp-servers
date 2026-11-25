using System.Text.Json;

namespace Streaik.Mcp.EntraAuth.Tests;

public sealed partial class BridgeTests
{
    private static readonly object FormCapability = new { elicitation = new { form = new { } } };

    [Theory]
    [InlineData("OpenAIImages_CreateImage", "/v1/images/generations", "imageGeneration")]
    [InlineData("OpenAIImages_CreateImageEdit", "/v1/images/edits", "imageEdit")]
    [InlineData("GoogleNanoBanana_CreateImage", "/v1beta/interactions", "imageGeneration")]
    public async Task ImageFormResumesWithReviewedValues(string tool, string path, string key)
    {
        const string prompt = "A watercolor of a comet";
        var google = path == "/v1beta/interactions";
        var edit = path == "/v1/images/edits";
        var model = google ? "gemini-3.1-flash-lite-image" : "gpt-image-2.6-flare";
        using var provider = new ProviderStub(async (request, ct) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(path, request.RequestUri!.AbsolutePath);
            if (edit)
            {
                var form = Assert.IsType<MultipartFormDataContent>(request.Content)
                    .ToDictionary(part => part.Headers.ContentDisposition!.Name!.Trim('"'));
                Assert.Equal(prompt, await form["prompt"].ReadAsStringAsync(ct));
                Assert.Equal(model, await form["model"].ReadAsStringAsync(ct));
                Assert.Equal("1536x1024", await form["size"].ReadAsStringAsync(ct));
                Assert.Equal(ImageBytes, Convert.ToBase64String(await form["image[]"].ReadAsByteArrayAsync(ct)));
            }
            else
            {
                using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var body = document.RootElement;
                Assert.Equal(model, body.GetProperty("model").GetString());
                if (google)
                {
                    Assert.Equal(ImageBytes, body.GetProperty("input")[0].GetProperty("data").GetString());
                    Assert.Equal(prompt, body.GetProperty("input")[1].GetProperty("text").GetString());
                    Assert.Equal("16:9", body.GetProperty("response_format").GetProperty("aspect_ratio").GetString());
                    Assert.Equal("1K", body.GetProperty("response_format").GetProperty("image_size").GetString());
                }
                else
                {
                    Assert.Equal(prompt, body.GetProperty("prompt").GetString());
                    Assert.Equal("1536x1024", body.GetProperty("size").GetString());
                    Assert.Equal("high", body.GetProperty("quality").GetString());
                }
            }
            return ImageResponse(google);
        });
        await using var host = await Fixture.StartAsync(headerAuth: true,
            configureServices: services => UseStub(services, google ? "mcp-google" : "openai", provider));
        var endpoint = google ? "https://localhost/google-images" : "https://localhost/openai-images";
        var headers = google ? new Dictionary<string, string> { ["x-goog-api-key"] = "test-key" }
            : new Dictionary<string, string> { ["Authorization"] = "Bearer test-key" };
        var arguments = new Dictionary<string, object> { ["prompt"] = "Original prompt", ["filename"] = "original" };
        if (edit || google) arguments["fileUrl"] = $"data:image/png;base64,{ImageBytes}";
        var request = Tool(tool, arguments);

        var first = await Call();
        Assert.Equal("input_required", first.GetProperty("resultType").GetString());
        var input = first.GetProperty("inputRequests").GetProperty(key);
        Assert.Equal("elicitation/create", input.GetProperty("method").GetString());
        var form = input.GetProperty("params");
        Assert.Equal("form", form.GetProperty("mode").GetString());
        var schema = form.GetProperty("requestedSchema");
        Assert.Equal(new[] { "prompt", "model" }, schema.GetProperty("required").EnumerateArray().Select(field => field.GetString()));
        var fields = schema.GetProperty("properties");
        Assert.Equal("Original prompt", fields.GetProperty("prompt").GetProperty("default").GetString());
        Assert.Equal(google ? "gemini-nano-banana-2.1" : "gpt-image-2.6-flare", fields.GetProperty("model").GetProperty("default").GetString());
        Assert.Equal("original", fields.GetProperty("filename").GetProperty("default").GetString());
        Assert.False(fields.TryGetProperty("fileUrl", out _));
        Assert.False(fields.TryGetProperty("referenceImageUrls", out _));
        Assert.False(fields.TryGetProperty("outputFolderUrl", out _));
        Assert.Equal(0, provider.Calls);

        request["requestState"] = first.GetProperty("requestState").GetString()!;
        object reviewed = google ? new { prompt, model = $"google/models/{model}", aspectRatio = "16:9", imageSize = "1K", filename = "reviewed" }
            : new { prompt, model = $"openai/{model}", size = "landscape", quality = "high", filename = "reviewed" };
        request["inputResponses"] = new Dictionary<string, object> { [key] = new { action = "accept", content = reviewed } };
        var result = await Call();
        Assert.False(IsError(result), result.GetRawText());
        Assert.Equal("complete", result.GetProperty("resultType").GetString());
        var content = result.GetProperty("content");
        Assert.Equal("image", content[0].GetProperty("type").GetString());
        Assert.Equal("Image: reviewed.png", content[1].GetProperty("text").GetString());
        Assert.Equal(1, provider.Calls);

        async Task<JsonElement> Call() => Result(await ReadRpc(await host.McpAsync(endpoint, method: "tools/call", name: tool,
            parameters: request, clientCapabilities: FormCapability, headers: headers)));
    }

    [Fact]
    public async Task ImageFormRejectsInvalidContinuationsBeforeProviderCall()
    {
        using var provider = new ProviderStub((_, _) => Task.FromResult(ImageResponse(google: false)));
        await using var host = await Fixture.StartAsync(headerAuth: true, configureServices: services => UseStub(services, "openai", provider));
        const string tool = "OpenAIImages_CreateImage";
        const string key = "imageGeneration";
        var parameters = Tool(tool, new { prompt = "Original" });
        var first = await Call();
        parameters["requestState"] = first.GetProperty("requestState").GetString()!;
        var invalid = await Call(); // A state without its response is not a first leg.
        Assert.True(IsError(invalid), invalid.GetRawText());
        foreach (var payload in new[]
        {
            "null",
            """{"action":"accept"}""",
            """{"action":"accept","content":{"prompt":"Original"}}""",
            """{"action":"accept","content":{"prompt":123,"model":"gpt-image-2.6-flare"}}""",
            """{"action":"accept","content":{"prompt":" ","model":"gpt-image-2.6-flare"}}""",
            """{"action":"accept","content":{"prompt":"Original","model":"gpt-image-2.6-flare","fileUrl":"https://example.test"}}""",
        })
        {
            using var document = JsonDocument.Parse(payload);
            parameters["inputResponses"] = new Dictionary<string, object> { [key] = document.RootElement };
            invalid = await Call();
            Assert.True(IsError(invalid), invalid.GetRawText());
        }

        parameters["requestState"] = "unrelated-state";
        parameters["inputResponses"] = new Dictionary<string, object>
        {
            [key] = new { action = "accept", content = new { prompt = "Original", model = "gpt-image-2.6-flare" } },
        };
        invalid = await Call();
        Assert.True(IsError(invalid), invalid.GetRawText());
        Assert.Equal(0, provider.Calls);

        async Task<JsonElement> Call() => Result(await ReadRpc(await host.McpAsync("https://localhost/openai-images", method: "tools/call",
            name: tool, parameters: parameters, clientCapabilities: FormCapability,
            headers: new Dictionary<string, string> { ["Authorization"] = "Bearer test-key" })));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImageRequestRunsWithOriginalDefaultsWithoutFormSupport(bool urlOnly)
    {
        using var provider = new ProviderStub(async (request, ct) =>
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal("Original prompt", document.RootElement.GetProperty("prompt").GetString());
            Assert.Equal("gpt-image-2.6-flare", document.RootElement.GetProperty("model").GetString());
            Assert.Equal("auto", document.RootElement.GetProperty("size").GetString());
            return ImageResponse(google: false);
        });
        await using var host = await Fixture.StartAsync(headerAuth: true, configureServices: services => UseStub(services, "openai", provider));
        const string tool = "OpenAIImages_CreateImage";
        var result = Result(await ReadRpc(await host.McpAsync("https://localhost/openai-images", method: "tools/call", name: tool,
            parameters: Tool(tool, new { prompt = "Original prompt" }), clientCapabilities: urlOnly ? new { elicitation = new { url = new { } } } : null,
            headers: new Dictionary<string, string> { ["Authorization"] = "Bearer test-key" })));
        Assert.Equal("complete", result.GetProperty("resultType").GetString());
        Assert.Equal("image", result.GetProperty("content")[0].GetProperty("type").GetString());
        Assert.Equal(1, provider.Calls);
    }
}
