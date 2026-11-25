using System.ComponentModel;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using static Streaik.Mcp.Servers.ProviderToolSupport;

namespace Streaik.Mcp.Servers;

internal static class AnthropicSkills
{
    internal static McpServerPrimitiveCollection<McpServerTool> CreateTools(bool managed)
    {
        (string, string, bool)[] reads =
        [
            (nameof(AnthropicSkills_List), "List Anthropic skills, optionally filtering by source or passing a page token.", true),
            (nameof(AnthropicSkills_Get), "Read the details of a single Anthropic skill.", true),
        ];
        return CreateProviderTools(typeof(AnthropicSkills), managed ?
        [
            .. reads,
            (nameof(AnthropicSkills_Create), "Upload a SharePoint or OneDrive folder to Anthropic to create a skill.", false),
            (nameof(AnthropicSkills_CreateVersion), "Upload a SharePoint or OneDrive folder to Anthropic to create a skill version.", false),
        ] : reads);
    }

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
        return await Anthropic(services, $"v1/skills/{SkillId(skillId)}", ct);
    }, ct);

    private static Task<CallToolResult> AnthropicSkills_Create(
        [Description("The new skill's display title.")] string displayTitle,
        [Description("Microsoft folder link. The folder must contain a top-level SKILL.md.")] string folderUrl,
        IServiceProvider services, CancellationToken ct) => Run(async ct =>
        await UploadSkill(services, "v1/skills", folderUrl, RequiredText(displayTitle, 255), ct), ct);

    private static Task<CallToolResult> AnthropicSkills_CreateVersion(
        [Description("The skill ID to add a version to.")] string skillId,
        [Description("Microsoft folder link. The folder must contain a top-level SKILL.md.")] string folderUrl,
        IServiceProvider services, CancellationToken ct) => Run(async ct =>
        await UploadSkill(services, $"v1/skills/{SkillId(skillId)}/versions", folderUrl, null, ct), ct);

    private static string SkillId(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 200 &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ? value : throw new ArgumentException("Invalid skill ID.");

    private static async Task<CallToolResult> UploadSkill(IServiceProvider services, string path, string folderUrl, string? title, CancellationToken ct)
    {
        _ = Key(services, "anthropic");
        var files = await services.GetRequiredService<DelegatedFiles>().ReadSkillFolderAsync(folderUrl, ct);
        using var form = new MultipartFormDataContent();
        if (title is not null) form.Add(new StringContent(title), "display_title");
        foreach (var file in files)
        {
            var content = new ByteArrayContent(file.Bytes);
            content.Headers.ContentType = MediaTypeHeaderValue.TryParse(file.Mime, out var mime) ? mime : new("application/octet-stream");
            form.Add(content, "files[]", file.Path);
        }
        return await Anthropic(services, path, ct, form);
    }

    private static async Task<CallToolResult> Anthropic(IServiceProvider services, string path, CancellationToken ct, HttpContent? body = null)
    {
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, path) { Content = body };
        request.Headers.Add("x-api-key", Key(services, "anthropic"));
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Headers.Add("anthropic-beta", "skills-2025-10-02");
        var bytes = await Send(services, "mcp-anthropic", request, 256 * 1024, ct);
        using var document = JsonDocument.Parse(bytes);
        return Text(document.RootElement.GetRawText());
    }
}
