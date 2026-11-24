using Streaik.Mcp.Servers;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddStreaikMcp(builder.Configuration, headerAuth: true);
var app = builder.Build();
app.UseCors();
app.MapStreaikMcp();
await app.RunAsync();
