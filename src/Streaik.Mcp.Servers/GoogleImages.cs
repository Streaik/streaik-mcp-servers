using System.ComponentModel;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using static Streaik.Mcp.Servers.ProviderToolSupport;

namespace Streaik.Mcp.Servers;

internal static class GoogleImages
{
    private const string GoogleImageModel = "gemini-nano-banana-2.1";

    internal static McpServerPrimitiveCollection<McpServerTool> CreateTools() => CreateProviderTools(typeof(GoogleImages),
        (nameof(GoogleNanoBanana_CreateImage), "Generate or edit a Gemini image. Include reference images to edit.", false),
        (nameof(GoogleNanoBanana_AskImages), "Ask Gemini a question about supplied images; no interaction is stored.", true));

    private static Task<CallToolResult> GoogleNanoBanana_CreateImage(
        [Description("The image prompt.")] string prompt, RequestContext<CallToolRequestParams> context, IServiceProvider services, CancellationToken ct,
        [Description("Gemini image generation model, e.g. gemini-nano-banana-2.1 or gemini-3.1-flash-lite-image.")] string model = GoogleImageModel,
        [Description("Optional inline image, allowed HTTPS image URL, or SharePoint/OneDrive link in Microsoft sign-in mode.")] string? fileUrl = null,
        [Description("Aspect ratio, e.g. 1:1 or 16:9. Omit for the model default.")] string? aspectRatio = null,
        [Description("Case-sensitive resolution, e.g. 1K, 2K or 4K where supported. Flash Lite supports only 1K.")] string? imageSize = null,
        string[]? referenceImageUrls = null, [Description("Optional output image basename.")] string? filename = null,
        [Description("Optional SharePoint/OneDrive destination folder. Requires Microsoft sign-in mode; defaults to your OneDrive.")] string? outputFolderUrl = null) => Run(async ct =>
    {
        var urls = (fileUrl is null ? Enumerable.Empty<string>() : [fileUrl]).Concat(referenceImageUrls ?? []).ToArray();
        var values = context.ReviewText("imageGeneration", urls.Length == 0 ? "Review the Google image generation request before sending it."
            : "Review the Google image edit request before sending it. The supplied images will be used as-is.", ct,
            new(nameof(prompt), "Prompt", prompt, 16_384, Required: true), new(nameof(model), "Model", model, 200, Required: true),
            new(nameof(aspectRatio), "Aspect ratio", aspectRatio, 20), new(nameof(imageSize), "Resolution", imageSize, 10),
            new(nameof(filename), "Output filename", filename, 255));
        var reviewedModel = values[nameof(model)]!;
        _ = GoogleModel(reviewedModel); // Validate before fetching referenced images.
        var reviewedPrompt = RequiredText(values[nameof(prompt)]!);
        var format = new JsonObject { ["type"] = "image", ["mime_type"] = "image/jpeg" };
        if (values[nameof(aspectRatio)] is { } ratio) format["aspect_ratio"] = RequiredText(ratio, 20);
        if (values[nameof(imageSize)] is { } resolution) format["image_size"] = RequiredText(resolution, 10);
        var folder = await PrepareImageOutput(services, "google", outputFolderUrl, ct);
        var input = await GoogleInput(services, urls, reviewedPrompt, ct);
        var result = await GoogleInteraction(services, reviewedModel, input, format, ct);
        var image = GoogleOutput(result).FirstOrDefault(block => (string?)block["type"] == "image");
        if (image?["data"]?.GetValue<string>() is not { } data) return Failure("Google returned no image.");
        return await Image(services, data, (string?)image["mime_type"] ?? "image/jpeg", values[nameof(filename)], folder, ct);
    }, ct);

    private static Task<CallToolResult> GoogleNanoBanana_AskImages(
        [Description("Question about the supplied images.")] string question,
        [Description("Ordered image URLs or inline data URLs. SharePoint and OneDrive links require Microsoft sign-in mode.")] string[] imageUrls,
        IServiceProvider services, CancellationToken ct, string model = "gemini-flash-latest") => Run(async ct =>
    {
        _ = Key(services, "google");
        if (imageUrls.Length == 0) throw new ArgumentException("At least one image is required.");
        var result = await GoogleInteraction(services, model, await GoogleInput(services, imageUrls, RequiredText(question), ct), null, ct);
        return Text(string.Join("\n\n", GoogleOutput(result).Where(block => (string?)block["type"] == "text").Select(block => (string?)block["text"])));
    }, ct);

    private static async Task<JsonArray> GoogleInput(IServiceProvider services, string[] urls, string prompt, CancellationToken ct)
    {
        var input = new JsonArray();
        if (urls.Length > 0)
            foreach (var image in await services.GetRequiredService<ImageInputs>().ReadAsync(urls, ct))
                input.Add(new JsonObject { ["type"] = "image", ["mime_type"] = image.Mime, ["data"] = Convert.ToBase64String(image.Bytes) });
        input.Add(new JsonObject { ["type"] = "text", ["text"] = prompt });
        return input;
    }

    private static string GoogleModel(string model) => ProviderModel(ProviderModel(model, "google/"), "models/");

    private static async Task<JsonNode> GoogleInteraction(IServiceProvider services, string model, JsonArray input, JsonObject? format, CancellationToken ct)
    {
        var payload = new JsonObject { ["model"] = GoogleModel(model), ["input"] = input, ["store"] = false };
        if (format is not null) payload["response_format"] = format;
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1beta/interactions") { Content = JsonContent.Create(payload) };
        request.Headers.Accept.ParseAdd("application/json");
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
}
