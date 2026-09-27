using Microsoft.Extensions.DependencyInjection;
using PuddingDesktop.Foundation;

namespace PuddingDesktop.Composition;

/// <summary>
/// One isolated Core scope per settings operation. The caller receives the raw scoped provider and
/// calls the existing business service directly: no HTTP, no controller invocation, no DTO relay
/// and no per-endpoint SettingsClient.
/// </summary>
internal sealed class SettingsOperationScope(IServiceScopeFactory scopes) : ISettingsScope
{
    private readonly AsyncServiceScope _scope = scopes.CreateAsyncScope();
    public IServiceProvider Services => _scope.ServiceProvider;
    public ValueTask DisposeAsync() => _scope.DisposeAsync();
}
