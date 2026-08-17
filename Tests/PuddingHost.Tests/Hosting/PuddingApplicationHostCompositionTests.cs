using Microsoft.Extensions.DependencyInjection;
using PuddingCode.Tasks;
using PuddingHost.Hosting;
using PuddingRuntime.Services;
using PuddingRuntime.Services.TaskTools;

namespace PuddingHost.Tests.Hosting;

[Collection("Pudding application host composition")]
public sealed class PuddingApplicationHostCompositionTests
{
    [Fact]
    public async Task DesktopChild_CompositionRoot_ResolvesSingletonRuntimeToolsAndServices()
    {
        var dataRoot = Path.Combine(
            Path.GetTempPath(),
            "PuddingAgent",
            $"host-composition-{Guid.NewGuid():N}");

        try
        {
            var options = PuddingHostOptionsFactory.ForDesktopChild(
            [
                "--desktop-child",
                "--desktop-parent-pid", Environment.ProcessId.ToString(),
                "--data-root", dataRoot,
                "--urls", "http://0.0.0.0:18080",
            ]);

            var builder = PuddingApplicationHost.CreateBuilder([], options);
            await using var app = PuddingApplicationHost.Build(builder);

            Assert.IsType<UserPreferenceService>(
                app.Services.GetRequiredService<IUserPreferenceService>());

            var taskCommandService = app.Services.GetRequiredService<ITaskAgentCommandService>();
            Assert.Same(
                taskCommandService,
                app.Services.GetRequiredService<ITaskAgentCommandService>());
            Assert.Same(
                app.Services.GetRequiredService<TaskListTool>(),
                app.Services.GetRequiredService<TaskListTool>());
            Assert.Same(
                app.Services.GetRequiredService<TaskGetTool>(),
                app.Services.GetRequiredService<TaskGetTool>());
            Assert.Same(
                app.Services.GetRequiredService<TaskClaimTool>(),
                app.Services.GetRequiredService<TaskClaimTool>());
            Assert.Same(
                app.Services.GetRequiredService<TaskUpdateTool>(),
                app.Services.GetRequiredService<TaskUpdateTool>());
        }
        finally
        {
            Serilog.Log.CloseAndFlush();
            if (Directory.Exists(dataRoot))
                Directory.Delete(dataRoot, recursive: true);
        }
    }
}

[CollectionDefinition("Pudding application host composition", DisableParallelization = true)]
public sealed class PuddingApplicationHostCompositionCollection;
