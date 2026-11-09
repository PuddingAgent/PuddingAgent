using Microsoft.Extensions.DependencyInjection;
using PuddingAgentWork.Contracts;
using PuddingHost.Hosting;
using PuddingPlatform.Services.AgentWork;

namespace PuddingHost.Tests.Hosting;

[Collection("Pudding application host composition")]
public sealed class AgentWorkCompositionTests
{
    [Fact]
    public async Task DesktopChild_ResolvesCanonicalWorkBackendAndScopedTargetResolver()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "PuddingAgent", "work-composition-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = PuddingHostOptionsFactory.ForDesktopChild([
                "--desktop-child", "--desktop-parent-pid", Environment.ProcessId.ToString(), "--data-root", dataRoot,
                "--urls", "http://0.0.0.0:18080"]);
            var builder = PuddingApplicationHost.CreateBuilder([], options);
            await using var app = PuddingApplicationHost.Build(builder);
            using var first = app.Services.CreateScope(); using var second = app.Services.CreateScope();
            var backend = app.Services.GetRequiredService<IAgentWorkBackend>();
            Assert.IsType<CanonicalAgentWorkBackend>(backend);
            Assert.Same(backend, first.ServiceProvider.GetRequiredService<IAgentWorkBackend>());
            Assert.NotNull(app.Services.GetRequiredService<AgentWorkApiFactory>());
            var resolver = first.ServiceProvider.GetRequiredService<IAgentWorkTargetResolver>();
            Assert.IsType<AgentWorkTargetResolver>(resolver);
            Assert.NotSame(resolver, second.ServiceProvider.GetRequiredService<IAgentWorkTargetResolver>());
        }
        finally
        {
            Serilog.Log.CloseAndFlush();
            if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true);
        }
    }
}
