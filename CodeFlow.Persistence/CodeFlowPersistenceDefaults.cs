namespace CodeFlow.Persistence;

public static class CodeFlowPersistenceDefaults
{
    public const string Host = "127.0.0.1";
    public const int Port = 3306;
    public const string Database = "codeflow";
    public const string Username = "codeflow";
    public const string Password = "codeflow_dev";
    public const string ConnectionStringEnvironmentVariable = "CODEFLOW_DB_CONNECTION_STRING";

    public static string LocalDevelopmentConnectionString =>
        $"Server={Host};Port={Port};Database={Database};User={Username};Password={Password};";
}
