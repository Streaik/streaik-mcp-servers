using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Streaik.Mcp.EntraAuth;
using Streaik.Mcp.Servers;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;

var secretNames = new[] { "openai", "google", "anthropic" }.ToDictionary(provider => provider,
    provider => configuration[$"KeyVault:ProviderSecrets:{provider}"]);
if (configuration["KeyVault:VaultUri"] is { Length: > 0 } vaultUri)
{
    if (!Uri.TryCreate(vaultUri, UriKind.Absolute, out var vault) || vault.Scheme != "https" || vault.AbsolutePath != "/" ||
        vault.UserInfo.Length != 0 || vault.Query.Length != 0 || vault.Fragment.Length != 0)
        throw new InvalidOperationException("KeyVault:VaultUri must be an HTTPS vault origin.");

    var secrets = new SecretClient(vault, new DefaultAzureCredential());
    var keys = new Dictionary<string, string?>();
    foreach (var (provider, name) in secretNames)
        if (!string.IsNullOrWhiteSpace(name))
        {
            var key = (await secrets.GetSecretAsync(name)).Value.Value;
            if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException($"The configured Key Vault provider key for '{provider}' is empty.");
            keys[$"Providers:{provider}:ApiKey"] = key;
        }
    configuration.AddInMemoryCollection(keys);
}
else if (secretNames.Values.Any(name => !string.IsNullOrWhiteSpace(name)))
    throw new InvalidOperationException("KeyVault:VaultUri is required when provider secrets are configured.");

var access = McpHosting.ResolveProviderAccess(configuration);
var bridge = EntraProfiles.Read(configuration, access);
if (bridge is not null) builder.Services.AddMcpOAuth(bridge);
builder.Services.AddStreaikMcp(configuration, access);
var app = builder.Build();
if (bridge is { Prefix.Length: > 0 }) app.UsePathBase(bridge.Prefix);
app.UseRouting();
app.UseCors();
if (bridge is not null)
{
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapMcpOAuth(bridge);
}
else app.MapStreaikMcp();
app.MapStreaikMcpAccessMetadata(graphObo: bridge is not null);
await app.RunAsync();
