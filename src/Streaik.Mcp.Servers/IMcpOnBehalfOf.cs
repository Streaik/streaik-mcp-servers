namespace Streaik.Mcp.Servers;

/// <summary>Acquires a user-delegated token for an operator-configured downstream target in the current MCP request.</summary>
public interface IMcpOnBehalfOf
{
    Task<string> GetAccessTokenAsync(string target, CancellationToken cancellationToken = default);
}
