namespace CodeFlow.Api;

public static class CodeFlowApiDefaults
{
    public const string AuthSectionName = "Auth";
    public const string DefaultScheme = "CodeFlow";

    public static class Policies
    {
        public const string Authenticated = "Authenticated";
        public const string AgentsRead = "AgentsRead";
        public const string AgentsWrite = "AgentsWrite";
        public const string WorkflowsRead = "WorkflowsRead";
        public const string WorkflowsWrite = "WorkflowsWrite";
        public const string TracesRead = "TracesRead";
        public const string TracesWrite = "TracesWrite";
        public const string HitlWrite = "HitlWrite";
    }

    public static class Permissions
    {
        public const string AgentsRead = "agents:read";
        public const string AgentsWrite = "agents:write";
        public const string WorkflowsRead = "workflows:read";
        public const string WorkflowsWrite = "workflows:write";
        public const string TracesRead = "traces:read";
        public const string TracesWrite = "traces:write";
        public const string HitlWrite = "hitl:write";
    }

    public static class Roles
    {
        public const string Viewer = "viewer";
        public const string Author = "author";
        public const string Operator = "operator";
        public const string Admin = "admin";
    }
}
