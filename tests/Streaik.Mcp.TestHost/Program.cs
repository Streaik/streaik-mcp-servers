using System.Net;
using System.Net.Http.Json;
using Streaik.Mcp.Servers;

// Only the test executable substitutes upstream HTTP. It never sends real provider requests.
var builder = WebApplication.CreateBuilder(args);
var delegated = builder.Configuration.GetValue<bool>("McpTest:Delegated");
builder.Configuration["Mcp:BrowserOrigins:0"] = "http://localhost:15273";
builder.Configuration["Providers:openai:ApiKey"] = "server-openai";
builder.Configuration["Providers:google:ApiKey"] = "server-google";
builder.Configuration["Providers:anthropic:ApiKey"] = "server-anthropic";
builder.Services.AddStreaikMcp(builder.Configuration, headerAuth: !delegated);
foreach (var provider in new[] { "openai", "mcp-google", "mcp-anthropic", "mcp-media" })
    builder.Services.AddHttpClient(provider).ConfigurePrimaryHttpMessageHandler(() => new FakeProvider(delegated ? "server" : "browser"));

var app = builder.Build();
app.UseCors();
if (delegated)
    app.Use(async (context, next) =>
    {
        // Stand-in for a validated caller token, not an Entra signing authority.
        if (HttpMethods.IsPost(context.Request.Method) && context.Request.Headers.Authorization != "Bearer test-mcp-token")
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        else await next(context);
    });
app.MapGet("/healthz", () => Results.Ok()); // Playwright readiness probe; not part of the tool runtime.
app.MapStreaikMcp();
await app.RunAsync();

sealed class FakeProvider(string mode) : HttpMessageHandler
{
    public const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jf2sAAAAASUVORK5CYII=";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!;
        var path = uri.AbsolutePath;
        var ok = uri.Host switch
        {
            "api.openai.com" => request.Headers.Authorization?.ToString() == $"Bearer {mode}-openai" &&
                !request.Headers.Contains("x-api-key") && !request.Headers.Contains("x-goog-api-key"),
            "api.anthropic.com" => Has("x-api-key", $"{mode}-anthropic") && Has("anthropic-beta", "skills-2025-10-02") &&
                request.Headers.Authorization is null && !request.Headers.Contains("x-goog-api-key"),
            "generativelanguage.googleapis.com" => Has("x-goog-api-key", $"{mode}-google") && Has("Api-Revision", "2026-05-20") &&
                request.Headers.Authorization is null && !request.Headers.Contains("x-api-key"),
            _ => false
        };
        if (!ok) return new HttpResponseMessage(HttpStatusCode.Unauthorized);

        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        return path switch
        {
            "/v1/images/generations" when request.Method == HttpMethod.Post && body.Contains("\"prompt\"") =>
                Json(new { created = 1, data = new[] { new { b64_json = Png } } }),
            "/v1/images/edits" when request.Method == HttpMethod.Post && body.Contains("input.png") =>
                Json(new { created = 1, data = new[] { new { b64_json = Png } } }),
            "/v1/chat/completions" when body.Contains("image_url") && body.Contains("\"store\":false") =>
                Json(new { choices = new[] { new { index = 0, message = new { role = "assistant", content = "OpenAI sees an image." }, finish_reason = "stop" } } }),
            "/v1beta/interactions" when body.Contains("\"store\":false") && body.Contains("response_format") =>
                Json(new { status = "completed", outputs = new[] { new { type = "image", mime_type = "image/png", data = Png } } }),
            "/v1beta/interactions" when body.Contains("\"store\":false") && body.Contains("\"data\"") =>
                Json(new { status = "completed", steps = new[] { new { type = "model_output", content = new[] { new { type = "text", text = "Google sees an image." } } } } }),
            "/v1/skills" when request.Method == HttpMethod.Get => Json(new { data = new[] { new { id = "skill_test", display_title = "Fixture Skill" } } }),
            "/v1/skills/skill_test" when request.Method == HttpMethod.Get => Json(new { id = "skill_test", display_title = "Fixture Skill" }),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        };

        bool Has(string header, string value) => request.Headers.TryGetValues(header, out var values) && values.SequenceEqual([value]);
    }

    private static HttpResponseMessage Json<T>(T body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
}
