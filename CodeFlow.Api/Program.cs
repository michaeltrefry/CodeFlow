using CodeFlow.Api;
using CodeFlow.Api.Auth;
using CodeFlow.Api.Endpoints;
using CodeFlow.Host;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCodeFlowInfrastructure(builder.Configuration);
builder.Services.AddCodeFlowApiBus(builder.Configuration);
builder.Services.AddCodeFlowAuth(builder.Configuration);
builder.Services.AddCodeFlowApi(builder.Configuration);

var app = builder.Build();

await app.Services.ApplyDatabaseMigrationsAsync();

app.UseCodeFlowCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapCodeFlowEndpoints();

await app.RunAsync();

public partial class Program
{
}
