using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Streaik.Mcp.Servers.Tests;

public sealed class DeleteConfirmationTests
{
    [Theory]
    [InlineData("Item_123", true)]
    [InlineData("item_123", false)]
    [InlineData("Item_123 ", false)]
    [InlineData("Item_456", false)]
    public async Task DeleteConfirmationMatchesTheOriginalTargetExactly(string confirmation, bool matches)
    {
        await using var server = CreateServer();
        var context = new RequestContext<CallToolRequestParams>(server, Request(), new()
        {
            Name = "fixture_delete",
            RequestState = "elicit:fixture_delete:confirmDelete",
            InputResponses = new Dictionary<string, InputResponse>
            {
                ["confirmDelete"] = InputResponse.FromElicitResult(new ElicitResult
                {
                    Action = "accept", Content = new Dictionary<string, JsonElement> { ["confirmation"] = JsonSerializer.SerializeToElement(confirmation) },
                }),
            },
        });

        if (matches) context.ConfirmDelete("Item_123", CancellationToken.None);
        else Assert.Throws<ToolElicitationException>(() => context.ConfirmDelete("Item_123", CancellationToken.None));
    }

    [Fact]
    public async Task DeleteConfirmationIsSkippedForClientsWithoutFormSupport()
    {
        await using var server = CreateServer();
        var context = new RequestContext<CallToolRequestParams>(server, Request(), new() { Name = "fixture_delete" });
        context.ConfirmDelete("Item_123", CancellationToken.None);
    }

    private static McpServer CreateServer() => McpServer.Create(new StreamServerTransport(new MemoryStream(), new MemoryStream(), "test"),
        new McpServerOptions { ProtocolVersion = McpHosting.ProtocolVersion });

    private static JsonRpcRequest Request() => new() { Method = "tools/call", Id = new RequestId(1) };
}
