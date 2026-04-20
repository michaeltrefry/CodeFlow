using CodeFlow.Host;
using Microsoft.Extensions.Hosting;

var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);
builder.Services.AddCodeFlowObservability(builder.Configuration, serviceName: "codeflow-worker");
builder.Services.AddCodeFlowHost(builder.Configuration);

using var host = builder.Build();
await host.ApplyDatabaseMigrationsAsync();
await host.RunAsync();
