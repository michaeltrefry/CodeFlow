namespace CodeFlow.Persistence;

public static class CodeFlowPersistenceDefaults
{
    // Local-dev defaults used when CODEFLOW_DB_CONNECTION_STRING is not set in the environment.
    // The shared MariaDB on trefry-network is the canonical local target — but its credentials
    // belong in your environment (`.env`, shell), NOT in source. This default exists only so
    // `dotnet ef` and tests have something to fall back to; when running against a real shared
    // container, set CODEFLOW_DB_CONNECTION_STRING explicitly.
    //
    // Use `localhost` (not 127.0.0.1) so macOS Docker Desktop routes through the host-network
    // shim — avoids spurious "Access denied" errors caused by the bridge-gateway source IP
    // (172.18.x) not matching `@'%'` grants on some MariaDB image variants.
    public const string Host = "localhost";
    public const int Port = 3306;
    public const string Database = "codeflow";
    public const string Username = "root";
    public const string Password = "change_me_in_env";
    public const string ConnectionStringEnvironmentVariable = "CODEFLOW_DB_CONNECTION_STRING";

    public static string LocalDevelopmentConnectionString =>
        $"Server={Host};Port={Port};Database={Database};User={Username};Password={Password};";
}
