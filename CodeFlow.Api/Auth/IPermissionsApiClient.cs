namespace CodeFlow.Api.Auth;

public interface IPermissionsApiClient
{
    Task<IReadOnlyList<string>> GetPermissionsAsync(string userId, CancellationToken cancellationToken = default);
}
