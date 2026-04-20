namespace CodeFlow.Api.Auth;

public interface ICurrentUser
{
    bool IsAuthenticated { get; }

    string? Id { get; }

    string? Email { get; }

    string? Name { get; }

    IReadOnlyList<string> Roles { get; }
}
