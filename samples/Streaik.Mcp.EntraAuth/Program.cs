using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Identity.Web;
using Streaik.Mcp.Servers;

const string access = "Streaik.Mcp.Access";
var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;
var scope = configuration["Mcp:EntraScope"] ?? "access_as_user";
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(configuration.GetSection("AzureAd"));
builder.Services.AddAuthorizationBuilder().AddPolicy(access, policy => policy.RequireAuthenticatedUser()
    .RequireAssertion(context => context.User.Claims.Where(claim => claim.Type is "scp" or "http://schemas.microsoft.com/identity/claims/scope")
        .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Contains(scope, StringComparer.Ordinal)));

if (configuration["KeyVault:VaultUri"] is { Length: > 0 } vaultUri)
{
    if (!Uri.TryCreate(vaultUri, UriKind.Absolute, out var vault) || vault.Scheme != "https" || vault.AbsolutePath != "/" ||
        vault.UserInfo.Length != 0 || vault.Query.Length != 0 || vault.Fragment.Length != 0)
        throw new InvalidOperationException("KeyVault:VaultUri must be an HTTPS vault origin.");

    var secrets = new SecretClient(vault, new DefaultAzureCredential());
    var keys = new Dictionary<string, string?>();
    foreach (var provider in new[] { "openai", "google", "anthropic" })
        if (configuration[$"KeyVault:ProviderSecrets:{provider}"] is { Length: > 0 } name)
            keys[$"Providers:{provider}:ApiKey"] = (await secrets.GetSecretAsync(name)).Value.Value;
    configuration.AddInMemoryCollection(keys);
}

builder.Services.AddStreaikMcp(configuration, headerAuth: false);
var app = builder.Build();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapStreaikMcp(access);
await app.RunAsync();
