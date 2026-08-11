using Microsoft.Extensions.DependencyInjection;
using PuddingHost.Hosting;
using PuddingRuntime.Services;

namespace PuddingHost.Tests.Hosting;

[Collection("Pudding application host composition")]
public sealed class PuddingApplicationHostCompositionTests
{
    [Fact]
    public async Task DesktopChild_CompositionRoot_ResolvesUserPreferenceService()
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
