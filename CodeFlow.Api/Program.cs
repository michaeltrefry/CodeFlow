using CodeFlow.Api;
using CodeFlow.Api.Auth;
using CodeFlow.Api.Endpoints;
using CodeFlow.Host;
using CodeFlow.Host.DeadLetter;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCodeFlowObservability(
    builder.Configuration,
    serviceName: "codeflow-api",
    configureTracing: static tracing => tracing.AddAspNetCoreInstrumentation());
builder.Services.AddCodeFlowInfrastructure(builder.Configuration);
builder.Services.AddCodeFlowApiBus(builder.Configuration);
builder.Services.AddCodeFlowAuth(builder.Configuration, builder.Environment);
builder.Services.AddCodeFlowApi(builder.Configuration);
builder.Services.AddCodeFlowDeadLetter(builder.Configuration);

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
