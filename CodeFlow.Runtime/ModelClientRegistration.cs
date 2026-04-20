namespace CodeFlow.Runtime;

public sealed record ModelClientRegistration(
    string Provider,
    IModelClient Client);
