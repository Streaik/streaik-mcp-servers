using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Streaik.Mcp.Servers.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Streaik.Mcp.Servers.Tests;

public sealed class ResourceDecodingTests
{
    private const string BundleUri = "test://fixtures/bundle";
    private const string PdfUri = "test://fixtures/paper.pdf";

    [Fact]
    public async Task ReadDecodesPdfAndRtfWithoutChangingUrisOrderMetadataOrDiscovery()
    {
        var pdf = MakePdf(2);
        const string rtf = "{\\rtf1\\ansi\\ansicpg1252 Caf\\'e9\\par second line}";
        var original = new ReadResourceResult
        {
            Meta = new() { ["fixture"] = "bundle" }, CacheScope = CacheScope.Public, TimeToLive = TimeSpan.FromSeconds(60),
            Contents =
            [
                BlobResourceContents.FromBytes(pdf, PdfUri, "Application/PDF; charset=binary"),
                new TextResourceContents { Uri = "test://fixtures/keep.html", MimeType = "text/html", Text = "<p>Already text</p>" },
                Blob("note.rtf", "Text/RTF", rtf),
            ]
        };
        original.Contents[0].Meta = new() { ["label"] = "paper" };
        await using var host = await StartAsync(original, pdf);

        var result = Result(await RequestAsync(host, "/fixture", "resources/read", BundleUri));
        Assert.Equal("bundle", result.GetProperty("_meta").GetProperty("fixture").GetString());
        Assert.Equal("public", result.GetProperty("cacheScope").GetString());
        Assert.Equal(60_000, result.GetProperty("ttlMs").GetInt32());
        var contents = result.GetProperty("contents");
        Assert.Equal(3, contents.GetArrayLength());
        AssertText(contents[0], PdfUri, "text/plain", "Fixture page 1\nFixture page 2");
        Assert.Equal("paper", contents[0].GetProperty("_meta").GetProperty("label").GetString());
        AssertText(contents[1], "test://fixtures/keep.html", "text/html", "<p>Already text</p>");
        AssertText(contents[2], "test://fixtures/note.rtf", "text/plain", "Café\nsecond line");
        Assert.IsType<BlobResourceContents>(original.Contents[0]);
        Assert.IsType<BlobResourceContents>(original.Contents[2]);
        Assert.Single(original.Meta!);

        var listing = Result(await RequestAsync(host, "/fixture", "resources/list"));
        var resource = Assert.Single(listing.GetProperty("resources").EnumerateArray());
        Assert.Equal(PdfUri, resource.GetProperty("uri").GetString());
        Assert.Equal("application/pdf", resource.GetProperty("mimeType").GetString());
        var registered = Result(await RequestAsync(host, "/fixture", "resources/read", PdfUri));
        AssertText(registered.GetProperty("contents")[0], PdfUri, "text/plain", "Fixture page 1\nFixture page 2");

        var discovery = Result(await RequestAsync(host, "/modelcontext-utils", "server/discover"));
        Assert.False(discovery.GetProperty("capabilities").TryGetProperty("resources", out _));
        var missing = await RequestAsync(host, "/fixture", "resources/read", "test://fixtures/missing");
        Assert.Equal((int)McpErrorCode.ResourceNotFound, missing.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Theory]
    [InlineData("text/html; charset=utf-8", "<p>Hello &amp; goodbye</p><script>ignore</script><style>ignore</style>", "text/plain", "Hello & goodbye")]
    [InlineData("application/json; charset=utf-8", "\uFEFF{ \"value\": 1 }", "application/json", "{ \"value\": 1 }")]
    [InlineData("text/csv; charset=utf-8", "\uFEFFword,count\r\nCafé,1\r\n", "text/csv", "word,count\r\nCafé,1\r\n")]
    [InlineData("message/rfc822", "Subject: Fixture\r\nMIME-Version: 1.0\r\nContent-Type: text/html\r\n\r\n<p>Mail &amp; text</p><script>ignore</script>", "text/plain", "Subject: Fixture\n\nMail & text")]
    [InlineData("message/rfc822", "Subject: Fixture\r\nMIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary=foo\r\n\r\n--foo\r\nContent-Type: text/plain\r\n\r\nMail body\r\n--foo\r\nContent-Type: text/plain\r\nContent-Disposition: attachment; filename=no.txt\r\n\r\nIgnore attachment\r\n--foo--\r\n", "text/plain", "Subject: Fixture\n\nMail body")]
    public async Task ReadDecodesSimpleFormats(string mime, string source, string outputMime, string text)
    {
        var blob = Blob("test", mime, source);
        await using var host = await StartAsync(new ReadResourceResult { Contents = [blob] });
        var result = Result(await RequestAsync(host, "/fixture", "resources/read", BundleUri));
        AssertText(Assert.Single(result.GetProperty("contents").EnumerateArray()), blob.Uri, outputMime, text);
    }

    [Fact]
    public async Task ReadKeepsOriginalBlobsWhenNotSuitableForExtraction()
    {
        BlobResourceContents[] blobs =
        [
            Blob("image", "image/png", "Opaque binary"),
            Blob("outlook", "application/vnd.ms-outlook", "Unsupported format"),
            Blob("malformed.pdf", "application/pdf", "Not a PDF"),
            Blob("invalid.json", "application/json", "{oops}"),
            Blob("blank", "text/plain", "  "),
            Blob("large-text", "text/plain", new string('x', 256 * 1024 + 1)),
            Blob("mcp-app", "Text/Html; charset=utf-8; Profile=\"mcp-app\"", "<p>App markup</p>"),
            BlobResourceContents.FromBytes(MakePdf(1, includeText: false), "test://fixtures/blank.pdf", "application/pdf"),
            BlobResourceContents.FromBytes(MakePdf(101), "test://fixtures/long.pdf", "application/pdf"),
        ];
        await using var host = await StartAsync(new ReadResourceResult { Contents = blobs });
        var contents = Result(await RequestAsync(host, "/fixture", "resources/read", BundleUri)).GetProperty("contents");
        Assert.Equal(blobs.Length, contents.GetArrayLength());
        for (var i = 0; i < blobs.Length; i++)
        {
            Assert.Equal(blobs[i].Uri, contents[i].GetProperty("uri").GetString());
            Assert.Equal(blobs[i].MimeType, contents[i].GetProperty("mimeType").GetString());
            Assert.Equal(Encoding.ASCII.GetString(blobs[i].Blob.Span), contents[i].GetProperty("blob").GetString());
            Assert.False(contents[i].TryGetProperty("text", out _));
        }
    }

    [Fact]
    public async Task HostCanRegisterOverrideAndInputLimitKeepsDecoderFromRunning()
    {
        var custom = new FixtureDecoder();
        var oversized = BlobResourceContents.FromBytes(new byte[8 * 1024 * 1024 + 1], "test://fixtures/large", "text/plain");
        var small = Blob("small", "TEXT/plain; charset=utf-8", "override this text");
        var original = new ReadResourceResult { Contents = [oversized, small] };
        await using var host = await StartAsync(original, custom: custom);
        var contents = Result(await RequestAsync(host, "/fixture", "resources/read", BundleUri)).GetProperty("contents");
        Assert.Single(custom.Inputs);
        Assert.Equal("override this text", custom.Inputs[0]);
        Assert.Equal(Encoding.ASCII.GetString(oversized.Blob.Span), contents[0].GetProperty("blob").GetString());
        AssertText(contents[1], small.Uri, "text/plain", "custom result");
    }

    [Fact]
    public async Task BuiltInDecodersObserveCancellation()
    {
        await using var host = await StartAsync(new ReadResourceResult());
        using var scope = host.Services.CreateScope();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        foreach (var decoder in scope.ServiceProvider.GetServices<IContentDecoder>())
        {
            var mediaType = MediaTypeHeaderValue.Parse(decoder.MediaTypes[0]);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await decoder.DecodeAsync("placeholder"u8.ToArray(), mediaType, cts.Token));
        }
    }

    private sealed class FixtureDecoder : IContentDecoder
    {
        public IReadOnlyList<string> MediaTypes { get; } = ["text/plain"];
        public List<string> Inputs { get; } = [];

        public ValueTask<DecodedContent?> DecodeAsync(ReadOnlyMemory<byte> data, MediaTypeHeaderValue contentType, CancellationToken cancellationToken)
        {
            Inputs.Add(Encoding.UTF8.GetString(data.Span));
            return ValueTask.FromResult<DecodedContent?>(new("custom result"));
        }
    }

    private static async Task<WebApplication> StartAsync(ReadResourceResult bundle, byte[]? pdf = null, IContentDecoder? custom = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthorizationBuilder().AddPolicy("fixture", policy => policy.RequireAssertion(_ => true));
        builder.Services.AddStreaikMcp(builder.Configuration, headerAuth: true);
        if (custom is not null) builder.Services.AddSingleton(custom);
        var app = builder.Build();
        app.UseCors();
        app.MapStreaikMcp();
        app.MapProtectedStreaikMcp("fixture", "fixture", session =>
        {
            session.Handlers = new McpServerHandlers
            {
                ReadResourceHandler = (request, _) => request.Params?.Uri == BundleUri
                    ? ValueTask.FromResult(bundle)
                    : throw new McpProtocolException("Fixture not found.", McpErrorCode.ResourceNotFound),
            };
            if (pdf is not null)
                session.ResourceCollection = new McpServerResourceCollection
                {
                    McpServerResource.Create(() => BlobResourceContents.FromBytes(pdf, PdfUri, "application/pdf"),
                        new McpServerResourceCreateOptions { UriTemplate = PdfUri, Name = "paper", MimeType = "application/pdf" }),
                };
        });
        await app.StartAsync();
        return app;
    }

    private static async Task<JsonElement> RequestAsync(WebApplication app, string endpoint, string method, string? uri = null)
    {
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Add("Mcp-Protocol-Version", McpHosting.ProtocolVersion);
        request.Headers.Add("Mcp-Method", method);
        if (uri is not null) request.Headers.Add("Mcp-Name", uri);
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        var parameters = new Dictionary<string, object>
        {
            ["_meta"] = new Dictionary<string, object>
            {
                ["io.modelcontextprotocol/protocolVersion"] = McpHosting.ProtocolVersion,
                ["io.modelcontextprotocol/clientCapabilities"] = new { },
                ["io.modelcontextprotocol/clientInfo"] = new { name = "Resource decoding tests", version = "1.0.0" },
            },
        };
        if (uri is not null) parameters["uri"] = uri;
        request.Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method, @params = parameters });
        using var response = await client.SendAsync(request);
        var payload = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {payload}");
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            payload = Assert.Single(payload.Split('\n'), line => line.StartsWith("data: ", StringComparison.Ordinal))[6..];
        return JsonSerializer.Deserialize<JsonElement>(payload);
    }

    private static JsonElement Result(JsonElement rpc)
    {
        Assert.True(rpc.TryGetProperty("result", out var result), rpc.GetRawText());
        return result;
    }

    private static void AssertText(JsonElement value, string uri, string mime, string text)
    {
        Assert.Equal(uri, value.GetProperty("uri").GetString());
        Assert.Equal(mime, value.GetProperty("mimeType").GetString());
        Assert.True(value.TryGetProperty("text", out var actual), value.GetRawText());
        Assert.Equal(text, actual.GetString());
        Assert.False(value.TryGetProperty("blob", out _));
    }

    private static BlobResourceContents Blob(string name, string mime, string content) =>
        BlobResourceContents.FromBytes(Encoding.UTF8.GetBytes(content), $"test://fixtures/{name}", mime);

    private static byte[] MakePdf(int pages, bool includeText = true)
    {
        using var document = new PdfDocumentBuilder();
        var font = document.AddStandard14Font(Standard14Font.Helvetica);
        for (var i = 1; i <= pages; i++)
        {
            var page = document.AddPage(200, 200);
            if (includeText) page.AddText($"Fixture page {i}", 12, new PdfPoint(10, 150), font);
        }
        return document.Build();
    }
}
