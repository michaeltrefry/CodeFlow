using CodeFlow.Api;
using CodeFlow.Api.Auth;
using CodeFlow.Api.Endpoints;
using CodeFlow.Host;
using CodeFlow.Host.DeadLetter;
using Microsoft.AspNetCore.HttpOverrides;
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
builder.Services.AddCodeFlowForwardedHeaders(builder.Configuration);

var app = builder.Build();

await app.Services.ApplyDatabaseMigrationsAsync();

// Honor X-Forwarded-Proto/Host/For from the host-managed reverse proxy (Caddy) BEFORE auth/CORS
// so OIDC challenges, generated links, and same-origin checks see the public https origin
// (https://codeflow.trefry.net) rather than the container's internal http endpoint.
app.UseForwardedHeaders();

app.UseCodeFlowCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapCodeFlowEndpoints();

await app.RunAsync();

public partial class Program
{
}
