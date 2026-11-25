using System.ComponentModel;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Streaik.ChatCompletions.Models;
using Streaik.OpenAI.Models;
using Streaik.Providers.OpenAI;
using static Streaik.Mcp.Servers.ProviderToolSupport;

namespace Streaik.Mcp.Servers;

internal static class OpenAIImages
{
    private const string OpenAiImageModel = "gpt-image-2.6-flare";

    internal static McpServerPrimitiveCollection<McpServerTool> CreateTools() => CreateProviderTools(typeof(OpenAIImages),
        (nameof(OpenAIImages_CreateImage), "Generate an image with OpenAI Images.", false),
        (nameof(OpenAIImages_CreateImageEdit), "Edit a supplied image with OpenAI Images.", false),
        (nameof(OpenAIImages_AskImages), "Ask OpenAI a question about supplied images; no conversation is stored.", true));

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
            Model = ProviderModel(model, "openai/"), Store = false,
            Messages = [new ChatMessage { Role = "user", Content = content.ToArray() }]
        }, NoHeaders, ct);
        return Text(result.Choices.FirstOrDefault()?.Message?.Content is { } answer ? ChatMessageContentExtensions.ToText(answer) : null);
    }, ct);

    private static string ImageModel(string model) => ProviderModel(model, "openai/") switch
    {
        var name when name.StartsWith("gpt_image_", StringComparison.Ordinal) => name.Replace("gpt_image_", "gpt-image-")
            .Replace("_6_", ".6-").Replace("_5_", ".5-").Replace("_", "-"),
        var name => name
    };

    private static string Size(string? size) => size switch { "square" => "1024x1024", "landscape" => "1536x1024", "portrait" => "1024x1536", _ => size ?? "auto" };

    private static CallToolResult OpenAiImage(OpenAIImagesResponse response, string? filename) => response.Data?.FirstOrDefault()?.B64Json is { } data
        ? Image(data, "image/png", filename) : Failure("OpenAI returned no inline image.");
}
