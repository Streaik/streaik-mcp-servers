using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Streaik.ChatCompletions.Models;
using Streaik.OpenAI.Models;
using Streaik.Providers;
using Streaik.Providers.OpenAI;

namespace Streaik.Mcp.Servers;

internal static class Tools
{
    private const string OpenAiImageModel = "gpt_image_2_6_flare";
    private static readonly IReadOnlyDictionary<string, string> NoHeaders = ReadOnlyDictionary<string, string>.Empty;

    public static McpServerPrimitiveCollection<McpServerTool> For(string serverId)
    {
        var collection = new McpServerPrimitiveCollection<McpServerTool>();
        foreach (var (name, description, readOnly) in serverId switch
        {
            "openai-images" => new[] {
                (nameof(OpenAIImages_CreateImage), "Generate an image with OpenAI Images.", false),
                (nameof(OpenAIImages_CreateImageEdit), "Edit a supplied image with OpenAI Images.", false),
                (nameof(OpenAIImages_AskImages), "Ask OpenAI a question about supplied images; no conversation is stored.", true) },
            "google-images" => new[] {
                (nameof(GoogleNanoBanana_CreateImage), "Generate or edit a Gemini image. Include reference images to edit.", false),
                (nameof(GoogleNanoBanana_AskImages), "Ask Gemini a question about supplied images; no interaction is stored.", true) },
            "anthropic-skills" => new[] {
                (nameof(AnthropicSkills_List), "List Anthropic skills, optionally filtering by source or passing a page token.", true),
                (nameof(AnthropicSkills_Get), "Read the details of a single Anthropic skill.", true) },
            _ => throw new InvalidOperationException($"Unknown MCP server '{serverId}'.")
        })
        {
            var method = typeof(Tools).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;
            collection.Add(McpServerTool.Create(method, (object?)null, new McpServerToolCreateOptions
            {
                Name = name, Description = description, ReadOnly = readOnly, Destructive = false, Idempotent = readOnly, OpenWorld = true
            }));
        }
        return collection;
    }

    private static Task<CallToolResult> OpenAIImages_CreateImage(
        [Description("The image prompt.")] string prompt, IServiceProvider services, CancellationToken ct,
        string model = OpenAiImageModel, string? filename = null, string? size = "auto", string? quality = "auto",
        string? background = "auto", string? moderation = "auto") => Run(async ct =>
    {
        var provider = services.GetRequiredService<OpenAIProvider>();
        var image = await provider.GenerateImageAsync(new OpenAIImageGenerationRequest
        {
            Model = ImageModel(model), Prompt = RequiredText(prompt), Size = Size(size), Quality = quality,
            Background = background, Moderation = moderation, OutputFormat = "png", N = 1
        }, NoHeaders, ct);
        return OpenAiImage(image, filename);
    }, ct);

    private static Task<CallToolResult> OpenAIImages_CreateImageEdit(
        [Description("The image prompt.")] string prompt,
        [Description("An HTTPS image URL on a configured host or an inline image data URL.")] string fileUrl,
        IServiceProvider services, CancellationToken ct, string model = OpenAiImageModel, string? filename = null,
        string? size = "auto", string? background = "auto", string? quality = "auto") => Run(async ct =>
    {
        var image = await services.GetRequiredService<ImageInputs>().ReadAsync(fileUrl, ct);
        using var stream = new MemoryStream(image.Bytes, writable: false);
        var input = new FormFile(stream, 0, stream.Length, "image", $"input.{image.Extension}")
        { Headers = new HeaderDictionary(), ContentType = image.Mime };
        var result = await services.GetRequiredService<OpenAIProvider>().EditImageAsync(new OpenAIImageEditRequest
        {
            Model = ImageModel(model), Prompt = RequiredText(prompt), Size = Size(size), Background = background,
            Quality = quality, OutputFormat = "png", N = 1, ImageFiles = [input]
        }, NoHeaders, ct);
        return OpenAiImage(result, filename);
    }, ct);

    private static Task<CallToolResult> OpenAIImages_AskImages(
        [Description("Question about the supplied images.")] string question,
        [Description("Ordered image URLs or inline image data URLs.")] string[] imageUrls,
        IServiceProvider services, CancellationToken ct, string model = "gpt-4.1") => Run(async ct =>
    {
        var images = await services.GetRequiredService<ImageInputs>().ReadAsync(imageUrls, ct);
        var content = new List<ChatMessageContentPart> { new ChatMessageTextPart { Text = RequiredText(question) } };
        content.AddRange(images.Select(image => new ChatMessageImagePart { ImageUrl = new ChatMessageImageUrl { Url = image.DataUrl } }));
        var result = await services.GetRequiredService<OpenAIProvider>().CompleteChatAsync(new ChatCompletionOptions
        {
            Model = RequiredText(model, 200), Store = false,
            Messages = [new ChatMessage { Role = "user", Content = content.ToArray() }]
        }, NoHeaders, ct);
        return Text(result.Choices.FirstOrDefault()?.Message?.Content is { } answer ? ChatMessageContentExtensions.ToText(answer) : null);
    }, ct);

    private static Task<CallToolResult> GoogleNanoBanana_CreateImage(
        [Description("The image prompt.")] string prompt, string model, IServiceProvider services, CancellationToken ct,
        string? fileUrl = null, string? aspectRatio = null, string? imageSize = null, string[]? referenceImageUrls = null) => Run(async ct =>
    {
        var urls = (fileUrl is null ? Enumerable.Empty<string>() : [fileUrl]).Concat(referenceImageUrls ?? []).ToArray();
        var input = await GoogleInput(services, urls, RequiredText(prompt), ct);
        var format = new JsonObject { ["type"] = "image", ["mime_type"] = "image/jpeg" };
        if (aspectRatio is not null) format["aspect_ratio"] = RequiredText(aspectRatio, 20);
        if (imageSize is not null) format["image_size"] = RequiredText(imageSize, 10);
        var result = await GoogleInteraction(services, model, input, format, ct);
        var image = GoogleOutput(result).FirstOrDefault(block => (string?)block["type"] == "image");
        if (image?["data"]?.GetValue<string>() is not { } data) return Failure("Google returned no image.");
        return Image(data, (string?)image["mime_type"] ?? "image/jpeg", null);
    }, ct);

    private static Task<CallToolResult> GoogleNanoBanana_AskImages(
        [Description("Question about the supplied images.")] string question,
        [Description("Ordered image URLs or inline image data URLs.")] string[] imageUrls,
        IServiceProvider services, CancellationToken ct, string model = "gemini-flash-latest") => Run(async ct =>
    {
        if (imageUrls.Length == 0) throw new ArgumentException("At least one image is required.");
        var result = await GoogleInteraction(services, model, await GoogleInput(services, imageUrls, RequiredText(question), ct), null, ct);
        return Text(string.Join("\n\n", GoogleOutput(result).Where(block => (string?)block["type"] == "text").Select(block => (string?)block["text"])));
    }, ct);

    private static Task<CallToolResult> AnthropicSkills_List(
        IServiceProvider services, CancellationToken ct,
        [Description("Number of results, from 1 through 100.")] int limit = 20,
        [Description("Continuation token from the previous page.")] string? page = null,
        [Description("Filter by anthropic or custom.")] string? source = null) => Run(async ct =>
    {
        if (limit is < 1 or > 100 || source is not (null or "anthropic" or "custom"))
            throw new ArgumentException("Invalid skills limit or source.");
        var path = $"v1/skills?limit={limit}" + (page is null ? "" : $"&page={Uri.EscapeDataString(RequiredText(page, 1024))}")
            + (source is null ? "" : $"&source={source}");
        return await Anthropic(services, path, ct);
    }, ct);

    private static Task<CallToolResult> AnthropicSkills_Get(
        [Description("The skill ID to read.")] string skillId, IServiceProvider services, CancellationToken ct) => Run(async ct =>
    {
        if (string.IsNullOrWhiteSpace(skillId) || skillId.Length > 200 || !skillId.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
            throw new ArgumentException("Invalid skill ID.");
        return await Anthropic(services, $"v1/skills/{skillId}", ct);
    }, ct);

    private static async Task<CallToolResult> Anthropic(IServiceProvider services, string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("x-api-key", Key(services, "anthropic"));
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Headers.Add("anthropic-beta", "skills-2025-10-02");
        var bytes = await Send(services, "mcp-anthropic", request, 256 * 1024, ct);
        using var document = JsonDocument.Parse(bytes);
        return Text(document.RootElement.GetRawText());
    }

    private static async Task<JsonArray> GoogleInput(IServiceProvider services, string[] urls, string prompt, CancellationToken ct)
    {
        var input = new JsonArray();
        if (urls.Length > 0)
            foreach (var image in await services.GetRequiredService<ImageInputs>().ReadAsync(urls, ct))
                input.Add(new JsonObject { ["type"] = "image", ["mime_type"] = image.Mime, ["data"] = Convert.ToBase64String(image.Bytes) });
        input.Add(new JsonObject { ["type"] = "text", ["text"] = prompt });
        return input;
    }

    private static async Task<JsonNode> GoogleInteraction(IServiceProvider services, string model, JsonArray input, JsonObject? format, CancellationToken ct)
    {
        var payload = new JsonObject { ["model"] = RequiredText(model, 200), ["input"] = input, ["store"] = false, ["stream"] = false, ["background"] = false };
        if (format is not null) payload["response_format"] = format;
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1beta/interactions") { Content = JsonContent.Create(payload) };
        request.Headers.Add("x-goog-api-key", Key(services, "google"));
        request.Headers.Add("Api-Revision", "2026-05-20");
        var result = JsonNode.Parse(await Send(services, "mcp-google", request, 8 * 1024 * 1024, ct)) ?? throw new JsonException();
        if ((string?)result["status"] is { } status and not "completed") throw new InvalidOperationException("The Google interaction did not complete.");
        return result;
    }

    private static IEnumerable<JsonObject> GoogleOutput(JsonNode result) => result["steps"] is JsonArray steps
        ? steps.OfType<JsonObject>().Where(step => (string?)step["type"] == "model_output")
            .SelectMany(step => (step["content"] as JsonArray ?? []).OfType<JsonObject>())
        : (result["outputs"] as JsonArray ?? []).OfType<JsonObject>();

    private static async Task<byte[]> Send(IServiceProvider services, string clientName, HttpRequestMessage request, int limit, CancellationToken ct)
    {
        using var client = services.GetRequiredService<IHttpClientFactory>().CreateClient(clientName);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Provider request failed (HTTP {(int)response.StatusCode}).");
        return await ImageInputs.ReadBoundedAsync(response.Content, limit, ct);
    }

    private static string Key(IServiceProvider services, string provider) => services.GetRequiredService<IProviderCredentials>().GetApiKey(provider) is { } key
        && !string.IsNullOrWhiteSpace(key) ? key : throw new ProviderCredentialException(provider);

    private static string RequiredText(string value, int max = 16_384) => !string.IsNullOrWhiteSpace(value) && value.Length <= max
        ? value : throw new ArgumentException($"Expected nonempty text up to {max} characters.");

    private static string ImageModel(string model) => RequiredText(model, 200) switch
    {
        var name when name.StartsWith("gpt_image_", StringComparison.Ordinal) => name.Replace("gpt_image_", "gpt-image-")
            .Replace("_6_", ".6-").Replace("_5_", ".5-").Replace("_", "-"),
        var name => name
    };

    private static string Size(string? size) => size switch { "square" => "1024x1024", "landscape" => "1536x1024", "portrait" => "1024x1536", _ => size ?? "auto" };

    private static CallToolResult OpenAiImage(OpenAIImagesResponse response, string? filename) => response.Data?.FirstOrDefault()?.B64Json is { } data
        ? Image(data, "image/png", filename) : Failure("OpenAI returned no inline image.");

    private static CallToolResult Image(string data, string mime, string? filename)
    {
        var image = new ImageInput(mime, ImageInputs.Decode(data));
        var block = ImageContentBlock.FromBytes(image.Bytes, image.Mime);
        return string.IsNullOrWhiteSpace(filename)
            ? new() { Content = [block] }
            : new() { Content = [block, new TextContentBlock { Text = $"Image: {Path.GetFileNameWithoutExtension(filename)}.{image.Extension}" }] };
    }

    private static CallToolResult Text(string? text) => string.IsNullOrWhiteSpace(text) ? Failure("The provider returned no text.")
        : new() { Content = [new TextContentBlock { Text = text }] };

    private static CallToolResult Failure(string message) => new() { IsError = true, Content = [new TextContentBlock { Text = message }] };

    private static async Task<CallToolResult> Run(Func<CancellationToken, Task<CallToolResult>> action, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try { return await action(timeout.Token); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return Failure("The operation timed out."); }
        catch (ProviderCredentialException) { return Failure("The provider credential is missing."); }
        catch (ArgumentException ex) when (ex.ParamName is null) { return Failure(ex.Message); }
        catch (HttpRequestException) { return Failure("The upstream request failed. Check provider credentials and availability."); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or InvalidDataException)
        { return Failure("The provider returned an unusable result."); }
    }
}
