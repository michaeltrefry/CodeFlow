using Anthropic;
using CodeFlow.Api.Assistant;
using CodeFlow.Api.Assistant.Tools;
using CodeFlow.Api.CascadeBump;
using CodeFlow.Api.Mcp;
using CodeFlow.Api.TraceEvents;
using CodeFlow.Api.Validation.Pipeline;
using CodeFlow.Api.Validation.Pipeline.Rules;
using CodeFlow.Api.WorkflowPackages;
using CodeFlow.Api.WorkflowTemplates;
using CodeFlow.Host;
using CodeFlow.Persistence;
using MassTransit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Text.Json.Serialization;

namespace CodeFlow.Api;

public static class ApiServiceCollectionExtensions
{
    public const string CodeFlowCorsPolicy = "CodeFlowUi";

    public static IServiceCollection AddCodeFlowApi(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddRouting();
        services.AddEndpointsApiExplorer();

        services.Configure<JsonOptions>(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
            options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });

        var allowedOrigins = configuration.GetSection("Cors:AllowedOrigins")
            .Get<string[]>() ?? new[] { "http://localhost:4200" };

        services.AddCors(corsOptions =>
        {
            corsOptions.AddPolicy(CodeFlowCorsPolicy, builder => builder
                .WithOrigins(allowedOrigins)
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials());
        });

        services.AddSingleton<TraceEventBroker>();

        services.Configure<McpEndpointPolicyOptions>(
            configuration.GetSection(McpEndpointPolicyOptions.SectionName));
        services.AddSingleton<IMcpEndpointPolicy, McpEndpointPolicy>();
        services.AddScoped<IWorkflowPackageResolver, WorkflowPackageResolver>();
        services.AddScoped<IWorkflowPackageImporter, WorkflowPackageImporter>();

        // sc-272 PR3: replay-with-edit admission. Singleton because it's a stateless pure
        // validator; the endpoint resolves it per request via Minimal API DI.
        services.AddSingleton<CodeFlow.Orchestration.Replay.Admission.ReplayRequestValidator>();

        // sc-274 phase 1: ambiguity preflight assessor. Stateless deterministic heuristics —
        // singleton with options binding so future phases (assistant chat, workflow launch)
        // share the same instance and config surface. Bridge IOptions<...> -> direct ctor
        // arg here so the runtime project doesn't need to take a Microsoft.Extensions.Options
        // dependency.
        services.Configure<CodeFlow.Runtime.Authority.Preflight.PreflightOptions>(
            configuration.GetSection("Preflight"));
        services.AddSingleton<CodeFlow.Runtime.Authority.Preflight.IIntentClarityAssessor>(sp =>
            new CodeFlow.Runtime.Authority.Preflight.DefaultIntentClarityAssessor(
                sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<
                    CodeFlow.Runtime.Authority.Preflight.PreflightOptions>>().Value));

        // sc-271: trace evidence bundle export. Scoped because the builder reads through the
        // per-request DbContext to collect saga + decision + refusal + authority + token-usage
        // rows; the artifact store is a singleton so it doesn't pull the lifetime down.
        services.AddScoped<CodeFlow.Api.TraceBundle.TraceEvidenceBundleBuilder>();

        // Workflow validation pipeline (F1). Rules are scoped because they take a per-request
        // DbContext via WorkflowValidationContext; the pipeline itself is scoped so it picks up
        // the correct DI scope's rule instances.
        services.AddScoped<IWorkflowValidationRule, StartNodeAdvisoryRule>();
        services.AddScoped<IWorkflowValidationRule, PortCouplingRule>();
        services.AddScoped<IWorkflowValidationRule, RoleAssignmentRule>();
        services.AddScoped<IWorkflowValidationRule, BackedgeRule>();
        services.AddScoped<IWorkflowValidationRule, PromptLintRule>();
        services.AddScoped<IWorkflowValidationRule, ProtectedVariableTargetRule>();
        services.AddScoped<IWorkflowValidationRule, WorkflowVarDeclarationRule>();
        services.AddScoped<WorkflowValidationPipeline>();

        // Authoring telemetry (O1). Singleton sink — stateless logger wrapper with stable event
        // names. Substituted with a recording fake in tests.
        services.AddSingleton<IAuthoringTelemetry, LoggerAuthoringTelemetry>();

        // S3: workflow-template framework. Registry is a singleton (in-memory catalog of
        // static-shipped templates); the materializer is scoped because it pulls scoped
        // repositories.
        services.AddSingleton<WorkflowTemplateRegistry>();
        services.AddScoped<IWorkflowTemplateMaterializer, WorkflowTemplateMaterializer>();

        // E4: cascade-bump assistant. Both services are scoped because they depend on per-request
        // DbContext / repositories. The executor delegates to the planner under the hood, so the
        // planner is also resolvable directly for the plan endpoint.
        services.AddScoped<CascadeBumpPlanner>();
        services.AddScoped<CascadeBumpExecutor>();

        // HAA-1: Homepage AI Assistant backend. Anthropic SDK client is a singleton with no
        // baked-in key — per-call WithOptions() override pulls the live key from
        // ILlmProviderSettingsRepository so admin rotations take effect immediately. OpenAI/LMStudio
        // ChatClient is instantiated per call inside CodeFlowAssistant for the same reason.
        services.Configure<AssistantOptions>(configuration.GetSection(AssistantOptions.SectionName));
        services.AddSingleton<IAnthropicClient>(_ => new AnthropicClient());
        services.AddScoped<IAssistantConversationRepository, AssistantConversationRepository>();
        services.AddScoped<IAssistantSettingsResolver, AssistantSettingsResolver>();
        services.AddSingleton<IAssistantSystemPromptProvider, DefaultAssistantSystemPromptProvider>();
        services.AddScoped<ICodeFlowAssistant, CodeFlowAssistant>();
        services.AddScoped<AssistantChatService>();
        services.AddScoped<IAssistantUserResolver, AssistantUserResolver>();

        // HAA-4: Assistant tool registry. Each tool is scoped because most pull a per-request
        // DbContext / repository. The dispatcher is also scoped so it sees the request's tool
        // instances. Adding a new tool is a single AddScoped<IAssistantTool, ...>() line below.
        services.AddScoped<IAssistantTool, ListWorkflowsTool>();
        services.AddScoped<IAssistantTool, GetWorkflowTool>();
        services.AddScoped<IAssistantTool, ListWorkflowVersionsTool>();
        services.AddScoped<IAssistantTool, ListAgentsTool>();
        services.AddScoped<IAssistantTool, GetAgentTool>();
        services.AddScoped<IAssistantTool, ListAgentVersionsTool>();
        services.AddScoped<IAssistantTool, FindWorkflowsUsingAgentTool>();
        services.AddScoped<IAssistantTool, SearchPromptsTool>();
        services.AddScoped<IAssistantTool, ListAgentRolesTool>();
        services.AddScoped<IAssistantTool, GetAgentRoleTool>();

        // Catalog-discovery tools so the assistant can recommend host/MCP tools to grant when the
        // user is authoring an agent role. Mirror /api/host-tools and /api/mcp-servers/* — read-only,
        // no chip. ListHostToolsTool has no DI dependencies (it queries the static catalog) so it
        // could be a singleton, but stays scoped for parity with the rest of the registry.
        services.AddScoped<IAssistantTool, ListHostToolsTool>();
        services.AddScoped<IAssistantTool, ListMcpServersTool>();
        services.AddScoped<IAssistantTool, ListMcpServerToolsTool>();

        // HAA-5: trace introspection tools.
        services.AddScoped<IAssistantTool, ListTracesTool>();
        services.AddScoped<IAssistantTool, GetTraceTool>();
        services.AddScoped<IAssistantTool, GetTraceTimelineTool>();
        services.AddScoped<IAssistantTool, GetTraceTokenUsageTool>();
        services.AddScoped<IAssistantTool, GetNodeIoTool>();

        // HAA-10: confirmation-gated workflow package save. The tool itself is read-only
        // (preview); the UI completes the mutation by posting to the existing apply endpoint.
        // GetWorkflowPackageTool is the read-only companion that hands the LLM a canonical
        // package shape exemplar so it can mirror field names and enum casing exactly.
        services.AddScoped<IAssistantTool, GetWorkflowPackageTool>();

        // SaveWorkflowPackageTool: registered DI-side as the workspace-blind fallback (used by
        // tests and any future caller that doesn't go through the per-turn assistant path). The
        // homepage assistant uses WorkflowDraftAssistantToolFactory below to produce a
        // workspace-AWARE instance that overrides this one on the per-turn dispatcher (the
        // factory's tool wins on the name collision).
        services.AddScoped<IAssistantTool, SaveWorkflowPackageTool>();

        // Per-turn factory that produces (a) a workspace-aware SaveWorkflowPackageTool and
        // (b) the four draft tools (set/get/patch/clear) when a workspace resolves. CodeFlowAssistant
        // merges the factory's tools into the dispatcher with override-priority for the save tool
        // so the workspace-aware version replaces the DI fallback for the LLM's view.
        services.AddScoped<WorkflowDraftAssistantToolFactory>();

        // HAA-11: confirmation-gated workflow run. Same split — the tool validates the run
        // request against the workflow's declared input schema; the UI POSTs to /api/traces
        // when the user clicks the chip.
        services.AddScoped<IAssistantTool, RunWorkflowTool>();

        // HAA-12: focused diagnosis tool. Composes the data the existing trace tools already
        // expose (saga + decisions + logic evals + token usage) into a single structured verdict
        // with anomaly heuristics applied server-side. Read-only; no chip.
        services.AddScoped<IAssistantTool, DiagnoseTraceTool>();

        // HAA-13: confirmation-gated Replay-with-Edit bridge. Validation-only tool; the UI POSTs
        // to /api/traces/{id}/replay (DryRunExecutor v4) when the user clicks the Replay chip.
        services.AddScoped<IAssistantTool, ProposeReplayWithEditTool>();

        services.AddScoped<AssistantToolDispatcher>();

        // HAA-18 — admin can assign an agent role to the homepage assistant; the role's host +
        // MCP tool grants are merged into the assistant's tool surface per turn. The factory is
        // singleton because it has no per-request state; per-conversation workspace context is
        // resolved inside Build(conversationId, ...) at call time.
        services.AddSingleton<AgentRoleToolFactory>();

        return services;
    }

    public static IServiceCollection AddCodeFlowApiBus(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddCodeFlowBus(configuration, x =>
        {
            x.AddConsumer<TraceInvokeRequestedObserver, TraceInvokeRequestedObserverDefinition>();
            x.AddConsumer<TraceInvocationCompletedObserver, TraceInvocationCompletedObserverDefinition>();
            x.AddConsumer<TraceTokenUsageRecordedObserver, TraceTokenUsageRecordedObserverDefinition>();
        });

        return services;
    }

    public static IApplicationBuilder UseCodeFlowCors(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseCors(CodeFlowCorsPolicy);
        return app;
    }

    /// <summary>
    /// Configure ForwardedHeaders so the API trusts X-Forwarded-Proto/Host/For from the host's
    /// reverse proxy (Caddy in production). The proxy is in a different network namespace than
    /// the container, so the per-request RemoteIpAddress is whatever Docker's bridge presents —
    /// which is not in the default trusted list (127.0.0.1, ::1). KnownNetworks/KnownProxies are
    /// cleared because the API is bound only to 127.0.0.1 / the trefry-network in production,
    /// so the only callers that can reach it are already trusted.
    /// </summary>
    public static IServiceCollection AddCodeFlowForwardedHeaders(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders =
                ForwardedHeaders.XForwardedFor
                | ForwardedHeaders.XForwardedProto
                | ForwardedHeaders.XForwardedHost;

            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();

            // Optional override: operators can constrain who is trusted to set the headers by
            // listing IPs/CIDRs in ForwardedHeaders:KnownProxies / KnownNetworks. When unset we
            // intentionally trust any upstream because the API is not exposed publicly.
            var knownProxies = configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>();
            if (knownProxies is not null)
            {
                foreach (var proxy in knownProxies)
                {
                    if (IPAddress.TryParse(proxy, out var address))
                    {
                        options.KnownProxies.Add(address);
                    }
                }
            }

            var knownNetworks = configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>();
            if (knownNetworks is not null)
            {
                foreach (var network in knownNetworks)
                {
                    if (System.Net.IPNetwork.TryParse(network, out var parsed))
                    {
                        options.KnownIPNetworks.Add(parsed);
                    }
                }
            }
        });

        return services;
    }
}
