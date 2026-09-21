using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PuddingCode.Configuration;
using PuddingCode.Tasks;
using PuddingHost.Hosting;
using PuddingPlatform.Services;
using PuddingRuntime.Services;
using PuddingRuntime.Services.Skills;
using PuddingRuntime.Services.Skills.Telemetry;
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

            var terminalPolicy = app.Services.GetRequiredService<ITerminalCommandPolicy>();
            var terminalAdmission = app.Services.GetRequiredService<PuddingCode.Abstractions.ITerminalCommandAdmission>();
            Assert.Same(terminalPolicy, terminalAdmission);
            Assert.Same(terminalPolicy, app.Services.GetRequiredService<DefaultTerminalCommandPolicy>());
            Assert.IsType<PuddingPlatform.Services.Goals.GoalCheckRunner>(
                app.Services.GetRequiredService<PuddingCode.Goals.IGoalCheckRunner>());
            var goalResume = app.Services.GetRequiredService<PuddingCode.Goals.IGoalResumeService>();
            Assert.Same(goalResume,
                app.Services.GetRequiredService<PuddingPlatform.Services.Goals.GoalResumeService>());
            Assert.Same(app.Services.GetRequiredService<GoalResumeTool>(),
                app.Services.GetRequiredService<GoalResumeTool>());
            using (var scope = app.Services.CreateScope())
            {
                Assert.Same(goalResume,
                    scope.ServiceProvider.GetRequiredService<PuddingCode.Goals.IGoalResumeService>());
                Assert.IsType<PuddingPlatform.Services.Goals.GoalRunStore>(
                    scope.ServiceProvider.GetRequiredService<PuddingPlatform.Services.Goals.GoalRunStore>());
            }
            terminalAdmission.EnsureAllowed("dotnet test --no-restore", isYoloMode: false);
            Assert.Throws<UnauthorizedAccessException>(() =>
                terminalAdmission.EnsureAllowed("taskkill /PID 1234", isYoloMode: false));

            var visionContext = app.Services.GetRequiredService<FrozenVisionContextAccessor>();
            Assert.Same(visionContext, app.Services.GetRequiredService<FrozenVisionContextAccessor>());
            // Optional constructor parameters can silently resolve to null even when
            // ValidateOnBuild succeeds. Check the actual product consumers as well.
            foreach (var consumer in new object[]
            {
                app.Services.GetRequiredService<AgentExecutionService>(),
                app.Services.GetRequiredService<IRuntimeLlmClient>(),
            })
            {
                var field = consumer.GetType().GetField("_frozenVisionContext",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                Assert.NotNull(field);
                Assert.Same(visionContext, field.GetValue(consumer));
            }

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

            Assert.Single(
                app.Services.GetServices<IHostedService>()
                    .OfType<RetentionPruningService>());
        }
        finally
        {
            Serilog.Log.CloseAndFlush();
            if (Directory.Exists(dataRoot))
                Directory.Delete(dataRoot, recursive: true);
        }
    }
    /// <summary>
    /// RSI-G2：宿主必须**真的**把遥测 sink 注入到产品消费者里。
    /// <para>
    /// 只断言「接口能解析」不够：可选构造参数在 <c>ValidateOnBuild</c> 通过的情况下也可能静默为
    /// null（本文件既有注释已记录该失败模式），所以必须反射查<b>真实消费者的字段</b>。
    /// 并且断言 sink 绑定到<b>本宿主</b>的 data root，而不是某个硬编码路径。
    /// </para>
    /// </summary>
    [Fact]
    public async Task DesktopChild_CompositionRoot_InjectsSkillUsageTelemetrySinkIntoRealConsumer()
    {
        var dataRoot = Path.Combine(
            Path.GetTempPath(),
            "PuddingAgent",
            $"host-composition-telemetry-{Guid.NewGuid():N}");

        try
        {
            var options = PuddingHostOptionsFactory.ForDesktopChild(
            [
                "--desktop-child",
                "--desktop-parent-pid", Environment.ProcessId.ToString(),
                "--data-root", dataRoot,
                "--urls", "http://0.0.0.0:18082",
            ]);

            var builder = PuddingApplicationHost.CreateBuilder([], options);
            await using var app = PuddingApplicationHost.Build(builder);

            var expectedDirectory = PuddingDataPaths.FromRoot(dataRoot).SkillUsageTelemetryRoot;

            var sink = app.Services.GetRequiredService<ISkillUsageTelemetrySink>();
            Assert.IsType<JsonlSkillUsageTelemetrySink>(sink);

            // 1) 真实消费者字段必须指向同一个实例 —— 不是"接口恰好能解析"。
            var enforcer = app.Services.GetRequiredService<SkillEnforcerService>();
            var sinkField = typeof(SkillEnforcerService).GetField(
                "_telemetrySink",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(sinkField);
            Assert.Same(sink, sinkField.GetValue(enforcer));

            // 2) sink 必须绑定到本宿主的 data root（换 data root 就必须换落点）。
            var directoryField = typeof(JsonlSkillUsageTelemetrySink).GetField(
                "_directory",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(directoryField);
            Assert.Equal(expectedDirectory, directoryField.GetValue(sink) as string);

            // 3) 组合根不得在启动时创建遥测目录：缺目录是合法状态（尚无插桩），
            //    不得为了让目录存在而在启动期产生副作用。
            Assert.False(Directory.Exists(expectedDirectory));
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
