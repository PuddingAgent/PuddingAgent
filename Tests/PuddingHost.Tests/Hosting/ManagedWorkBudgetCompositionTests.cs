using Microsoft.Extensions.DependencyInjection;
using PuddingCode.Platform;
using PuddingHost.Hosting;
using PuddingPlatform.Services;
using PuddingPlatform.Services.AgentChat;
using PuddingPlatform.Services.AgentWork;
using PuddingPlatform.Services.ExecutionBudgets;

namespace PuddingHost.Tests.Hosting;

/// <summary>
/// CORE-I1-05 P1: the product composition root must be able to construct the managed-Work budget resolver and the
/// coordinator in both policy modes. The resolver is registered for every scope even while the policy is disabled, so
/// a missing dependency would break ordinary chat as well; this test resolves the real Host graph instead of a fixture.
/// </summary>
[Collection("Pudding application host composition")]
public sealed class ManagedWorkBudgetCompositionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealHostComposition_ResolvesResolverAndCoordinator_InBothPolicyModes(bool enabled)
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "PuddingAgent",
            "managed-work-composition-" + Guid.NewGuid().ToString("N"));
        try
        {
            var args = new List<string>
            {
                "--desktop-child", "--desktop-parent-pid", Environment.ProcessId.ToString(),
                "--data-root", dataRoot, "--urls", enabled ? "http://0.0.0.0:18112" : "http://0.0.0.0:18113",
            };
            if (enabled)
                args.Add("--Pudding:ExecutionBudgets:ManagedWork:Enabled=true");

            var options = PuddingHostOptionsFactory.ForDesktopChild([.. args]);
            var builder = PuddingApplicationHost.CreateBuilder([.. args], options);
            await using var app = (ServiceProvider)builder.Services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

            using var scope = app.CreateScope();
            var mapper = app.GetRequiredService<ExecutionBudgetAccountMapper>();
            Assert.NotNull(mapper);
            var resolver = scope.ServiceProvider.GetRequiredService<IManagedWorkBudgetBindingResolver>();
            Assert.IsType<ManagedWorkBudgetBindingResolver>(resolver);
            var coordinator = scope.ServiceProvider.GetRequiredService<IExecutionRunCoordinator>();
            Assert.IsType<ExecutionRunCoordinator>(coordinator);
            var policy = app.GetRequiredService<IManagedWorkBudgetPolicyProvider>().GetPolicy();
            Assert.Equal(enabled, policy is not null);
        }
        finally
        {
            Serilog.Log.CloseAndFlush();
            if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true);
        }
    }
}
