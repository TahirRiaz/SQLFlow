using Microsoft.AspNetCore.Authorization;
using SqlFlow.ControlPlane.Api;

namespace SqlFlow.ControlPlane.Hosting;

/// <summary>
/// Checks the search categories modules contribute once, as the host starts and before it serves a request, so a
/// conflicting registration (a duplicate or reserved key, a blank label, an undefined policy) stops startup with a message
/// naming the contributor instead of surfacing on the first search.
/// </summary>
internal sealed class SearchContributorStartupCheck : IHostedService
{
    private readonly IServiceScopeFactory _scopes;

    public SearchContributorStartupCheck(IServiceScopeFactory scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        _scopes = scopes;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        await SearchContributors.ValidateAsync(
                scope.ServiceProvider.GetServices<ISearchContributor>(),
                scope.ServiceProvider.GetRequiredService<IAuthorizationPolicyProvider>())
            .ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
