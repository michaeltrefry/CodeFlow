using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace CodeFlow.Api.Auth;

public static class AuthServiceCollectionExtensions
{
    public const string PolicySchemeName = "CodeFlow";

    public static IServiceCollection AddCodeFlowAuth(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var authSection = configuration.GetSection(CodeFlowApiDefaults.AuthSectionName);
        services.Configure<AuthOptions>(authSection);

        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, ClaimsCurrentUser>();
        services.AddSingleton<RoleBasedPermissionChecker>();
        services.AddMemoryCache();
        services.AddHttpClient<IPermissionsApiClient, PermissionsApiClient>();

        var authMode = ResolveAuthMode(authSection);
        if (authMode == AuthMode.Company)
        {
            services.AddSingleton<IPermissionChecker, CompanyPermissionChecker>();
        }
        else
        {
            services.AddSingleton<IPermissionChecker>(sp =>
                sp.GetRequiredService<RoleBasedPermissionChecker>());
        }

        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();

        services.AddAuthentication(PolicySchemeName)
            .AddPolicyScheme(PolicySchemeName, PolicySchemeName, schemeOptions =>
            {
                schemeOptions.ForwardDefaultSelector = context =>
                {
                    var auth = context.RequestServices.GetRequiredService<IOptions<AuthOptions>>().Value;
                    return auth.DevelopmentBypass
                        ? DevelopmentAuthenticationHandler.SchemeName
                        : JwtBearerDefaults.AuthenticationScheme;
                };
            })
            .AddScheme<DevelopmentAuthenticationOptions, DevelopmentAuthenticationHandler>(
                DevelopmentAuthenticationHandler.SchemeName,
                _ => { })
            .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, _ => { });

        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptionsMonitor<AuthOptions>>((bearer, authOptionsMonitor) =>
            {
                var authOptions = authOptionsMonitor.CurrentValue;
                bearer.Authority = authOptions.Authority;
                bearer.Audience = authOptions.Audience;
                bearer.RequireHttpsMetadata = authOptions.RequireHttpsMetadata;
                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = !string.IsNullOrWhiteSpace(authOptions.Authority),
                    ValidateAudience = !string.IsNullOrWhiteSpace(authOptions.Audience),
                    ValidateLifetime = true,
                    ValidIssuer = authOptions.Authority,
                    ValidAudience = authOptions.Audience,
                    NameClaimType = authOptions.NameClaim,
                    RoleClaimType = authOptions.RolesClaim
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(CodeFlowApiDefaults.Policies.Authenticated, policy =>
                policy.RequireAuthenticatedUser())
            .AddPolicy(CodeFlowApiDefaults.Policies.AgentsRead, policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement(CodeFlowApiDefaults.Permissions.AgentsRead)))
            .AddPolicy(CodeFlowApiDefaults.Policies.AgentsWrite, policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement(CodeFlowApiDefaults.Permissions.AgentsWrite)))
            .AddPolicy(CodeFlowApiDefaults.Policies.WorkflowsRead, policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement(CodeFlowApiDefaults.Permissions.WorkflowsRead)))
            .AddPolicy(CodeFlowApiDefaults.Policies.WorkflowsWrite, policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement(CodeFlowApiDefaults.Permissions.WorkflowsWrite)))
            .AddPolicy(CodeFlowApiDefaults.Policies.TracesRead, policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement(CodeFlowApiDefaults.Permissions.TracesRead)))
            .AddPolicy(CodeFlowApiDefaults.Policies.TracesWrite, policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement(CodeFlowApiDefaults.Permissions.TracesWrite)))
            .AddPolicy(CodeFlowApiDefaults.Policies.HitlWrite, policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement(CodeFlowApiDefaults.Permissions.HitlWrite)))
            .AddPolicy(CodeFlowApiDefaults.Policies.OpsRead, policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement(CodeFlowApiDefaults.Permissions.OpsRead)))
            .AddPolicy(CodeFlowApiDefaults.Policies.OpsWrite, policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement(CodeFlowApiDefaults.Permissions.OpsWrite)))
            .AddPolicy(CodeFlowApiDefaults.Policies.McpServersRead, policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement(CodeFlowApiDefaults.Permissions.McpServersRead)))
            .AddPolicy(CodeFlowApiDefaults.Policies.McpServersWrite, policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement(CodeFlowApiDefaults.Permissions.McpServersWrite)))
            .AddPolicy(CodeFlowApiDefaults.Policies.AgentRolesRead, policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement(CodeFlowApiDefaults.Permissions.AgentRolesRead)))
            .AddPolicy(CodeFlowApiDefaults.Policies.AgentRolesWrite, policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement(CodeFlowApiDefaults.Permissions.AgentRolesWrite)))
            .AddPolicy(CodeFlowApiDefaults.Policies.GitHostRead, policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement(CodeFlowApiDefaults.Permissions.GitHostRead)))
            .AddPolicy(CodeFlowApiDefaults.Policies.GitHostWrite, policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement(CodeFlowApiDefaults.Permissions.GitHostWrite)));

        return services;
    }

    private static AuthMode ResolveAuthMode(IConfigurationSection section)
    {
        var modeValue = section["Mode"];
        if (!string.IsNullOrWhiteSpace(modeValue)
            && Enum.TryParse<AuthMode>(modeValue, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return AuthMode.Generic;
    }
}
