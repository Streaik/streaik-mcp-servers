using System.Collections.ObjectModel;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Streaik.Providers;

namespace Streaik.Mcp.Servers;

internal static class ProviderToolSupport
{
    internal static readonly IReadOnlyDictionary<string, string> NoHeaders = ReadOnlyDictionary<string, string>.Empty;

    internal static McpServerPrimitiveCollection<McpServerTool> CreateProviderTools(Type type, params (string Name, string Description, bool ReadOnly)[] tools)
    {
        var collection = new McpServerPrimitiveCollection<McpServerTool>();
        foreach (var (name, description, readOnly) in tools)
        {
            var method = type.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;
            collection.Add(McpServerTool.Create(method, (object?)null, new McpServerToolCreateOptions
            {
                Name = name, Description = description, ReadOnly = readOnly, Destructive = false, Idempotent = readOnly, OpenWorld = true
            }));
        }
        return collection;
    }

    internal static async Task<byte[]> Send(IServiceProvider services, string clientName, HttpRequestMessage request, int limit, CancellationToken ct)
    {
        using var client = services.GetRequiredService<IHttpClientFactory>().CreateClient(clientName);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        return await ImageInputs.ReadBoundedAsync(response.Content, limit, ct);
    }

    internal static string Key(IServiceProvider services, string provider) => services.GetRequiredService<IProviderCredentials>().GetApiKey(provider) is { } key
        && !string.IsNullOrWhiteSpace(key) ? key : throw new ProviderCredentialException(provider);

    internal static string RequiredText(string value, int max = 16_384) => !string.IsNullOrWhiteSpace(value) && value.Length <= max
        ? value : throw new ArgumentException($"Expected nonempty text up to {max} characters.");

    internal static string ProviderModel(string model, string prefix)
    {
        var name = RequiredText(model, 200).Trim();
        return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? RequiredText(name[prefix.Length..], 200) : name;
    }

    internal static async Task<CallToolResult> Image(IServiceProvider services, string data, string mime, string? filename,
        ImageFolder? folder, CancellationToken ct)
    {
        var image = new ImageInput(mime, ImageInputs.Decode(data));
        var files = services.GetRequiredService<DelegatedFiles>();
        if (files.Enabled) return new() { Content = [await files.SaveImageAsync(image, filename, folder, ct)] };

        var block = ImageContentBlock.FromBytes(image.Bytes, image.Mime);
        return string.IsNullOrWhiteSpace(filename)
            ? new() { Content = [block] }
            : new() { Content = [block, new TextContentBlock { Text = $"Image: {Path.GetFileNameWithoutExtension(filename)}.{image.Extension}" }] };
    }

    internal static CallToolResult Text(string? text) => string.IsNullOrWhiteSpace(text) ? Failure("The provider returned no text.")
        : new() { Content = [new TextContentBlock { Text = text }] };

    internal static CallToolResult Failure(string message) => new() { IsError = true, Content = [new TextContentBlock { Text = message }] };

    internal static Task<ImageFolder?> PrepareImageOutput(IServiceProvider services, string provider, string? folderUrl, CancellationToken ct)
    {
        _ = Key(services, provider);
        var files = services.GetRequiredService<DelegatedFiles>();
        if (files.Enabled) return files.PrepareImageDestinationAsync(folderUrl, ct);
        if (folderUrl is not null) throw new ArgumentException("Saving to a Microsoft folder requires Microsoft sign-in mode.");
        return Task.FromResult<ImageFolder?>(null);
    }

    internal static async Task<CallToolResult> Run(Func<CancellationToken, Task<CallToolResult>> action, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try { return await action(timeout.Token); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return Failure("The operation timed out."); }
        catch (ProviderCredentialException) { return Failure("The provider credential is missing."); }
        catch (DelegatedFileException ex) { return Failure(ex.Message); }
        catch (ArgumentException ex) when (ex.ParamName is null) { return Failure(ex.Message); }
        catch (McpProviderException ex) { return Failure(ex.Message); }
        catch (HttpRequestException ex) { return Failure(ex.StatusCode is { } status
            ? $"An upstream request failed (HTTP {(int)status})." : "Could not connect to the upstream service."); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or InvalidDataException)
        { return Failure("The provider returned an unusable result."); }
    }
}
