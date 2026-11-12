using Microsoft.Extensions.DependencyInjection;
using Pudding.Api.Contracts;
using System.Text.Json;
using Pudding.ToolPlugins.Contracts;
using PuddingHost.Hosting;
using PuddingOperatorAccess;
using PuddingRuntime.Operators.PublicApi;

namespace PuddingHost.Tests.Hosting;

[Collection("Pudding application host composition")]
public sealed class OperatorApiCompositionTests
{
    [Fact]
    public async Task DesktopChild_ResolvesDeterministicOperatorsAndPluginFactory()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "PuddingAgent", "operator-composition-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = PuddingHostOptionsFactory.ForDesktopChild([
                "--desktop-child", "--desktop-parent-pid", Environment.ProcessId.ToString(), "--data-root", dataRoot,
                "--urls", "http://0.0.0.0:18080"]);
            var builder = PuddingApplicationHost.CreateBuilder([], options);
            await using var app = PuddingApplicationHost.Build(builder);
            Assert.IsType<PluginOperatorApiLeaseFactory>(app.Services.GetRequiredService<IPluginOperatorApiLeaseFactory>());
            var consumer = app.Services.GetRequiredService<PuddingRuntime.Services.Plugins.ExecutablePluginService>();
            var factoryField = typeof(PuddingRuntime.Services.Plugins.ExecutablePluginService).GetField("_operatorApi",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(factoryField);
            Assert.Same(app.Services.GetRequiredService<IPluginOperatorApiLeaseFactory>(), factoryField.GetValue(consumer));
            Assert.IsType<ConfiguredExecutionModelBinder>(app.Services.GetRequiredService<IExecutionModelBinder>());
            Assert.IsType<PuddingExecutionResources.ModelResourcePool>(app.Services.GetRequiredService<PuddingExecutionResources.Contracts.IModelResourcePool>());
            var modelField = typeof(PuddingRuntime.Services.Plugins.ExecutablePluginService).GetField("_modelApi",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(modelField);
            Assert.Same(app.Services.GetRequiredService<ModelEvaluationApiFactory>(), modelField.GetValue(consumer));
            var service = app.Services.GetRequiredService<OperatorAccessService>();
            await using var lease = service.Open(new("module:test", new("w", "a", "s"), DateTimeOffset.UtcNow.AddSeconds(30), true, [SkillValueApi.SceneKey]));
            var catalog = await lease.ListAsync(); Assert.Single(catalog.Items);
            var input = JsonSerializer.SerializeToElement(new SkillValueInput("skill", 5, 0, 0), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var result = await lease.ScoreAsync(new(SkillValueApi.SceneKey, SkillValueApi.InputSchema, SkillValueApi.InputVersion, input, new("composition")));
            Assert.Equal(1d, result.Value.Score); Assert.Equal("caller-supplied", result.InputOrigin);
            Assert.Null(result.Value.Provenance.ModelId);
        }
        finally
        {
            Serilog.Log.CloseAndFlush();
            if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true);
        }
    }
}
