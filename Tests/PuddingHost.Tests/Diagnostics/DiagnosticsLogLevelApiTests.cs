using Microsoft.AspNetCore.Mvc;
using PuddingCode.Observability;
using PuddingHost.Controllers;
using Serilog.Events;
using Xunit;

namespace PuddingHost.Tests.Diagnostics;

/// <summary>
/// 日志级别开关（Debug 按钮的服务端出口）：<c>GET/PUT /api/admin/diagnostics/log-level</c>。
///
/// <para>
/// 这些用例会改动**进程级**的 <see cref="PuddingLogLevelSwitch"/>（Serilog 的开关本来就是全进程单例），
/// 因此每条都在 finally 里恢复原级别，并且全部放在同一个测试类内（xunit 保证类内串行）。
/// </para>
/// </summary>
public sealed class DiagnosticsLogLevelApiTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "pudding-log-level-tests", Guid.NewGuid().ToString("N"));

    private readonly LogEventLevel _originalLevel = PuddingLogLevelSwitch.Instance.MinimumLevel;

    private string ConfigFile => Path.Combine(_directory, "logging.json");

    public void Dispose()
    {
        PuddingLogLevelSwitch.Instance.MinimumLevel = _originalLevel;
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响用例结论
        }
    }

    [Fact]
    public async Task UnknownLevel_IsRejected_WithoutTouchingStateOrDisk()
    {
        var store = new PuddingLogLevelStore(ConfigFile);
        PuddingLogLevelSwitch.Instance.MinimumLevel = LogEventLevel.Warning;

        var applied = await store.TrySetAsync("loud-ish");

        Assert.False(applied);
        // fail closed：认不出来的级别名既不生效也不落盘（绝不猜测用户想开哪一档）。
        Assert.Equal(LogEventLevel.Warning, PuddingLogLevelSwitch.Instance.MinimumLevel);
        Assert.False(File.Exists(ConfigFile));
    }

    [Fact]
    public async Task AliasLevel_IsNormalizedAndPersisted()
    {
        var store = new PuddingLogLevelStore(ConfigFile);

        var applied = await store.TrySetAsync("debug");

        Assert.True(applied);
        Assert.Equal(LogEventLevel.Debug, PuddingLogLevelSwitch.Instance.MinimumLevel);
        Assert.True(store.IsVerboseEnabled);

        // 落盘的值必须是 Serilog 的正式名（别名只作为输入容错，不写进配置文件）。
        var json = await File.ReadAllTextAsync(ConfigFile);
        Assert.Contains("\"Debug\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"debug\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PersistedLevel_SurvivesRestart_AndFallsBackWhenMissingOrBroken()
    {
        var store = new PuddingLogLevelStore(ConfigFile);
        Assert.True(await store.TrySetAsync("Error"));

        // 模拟重启：开关回到默认，再从文件恢复。
        PuddingLogLevelSwitch.Instance.MinimumLevel = LogEventLevel.Information;
        Assert.Equal(LogEventLevel.Error, store.ApplyPersistedLevel(LogEventLevel.Information));

        // 文件损坏 ⇒ 沿用 fallback，不抛异常（日志配置损坏不该阻止进程启动）。
        await File.WriteAllTextAsync(ConfigFile, "{ not json ");
        Assert.Equal(LogEventLevel.Warning, store.ApplyPersistedLevel(LogEventLevel.Warning));

        // 文件里的值无法识别 ⇒ 同样沿用 fallback。
        await File.WriteAllTextAsync(ConfigFile, """{"Logging":{"Level":"nope"}}""");
        Assert.Equal(LogEventLevel.Warning, store.ApplyPersistedLevel(LogEventLevel.Warning));

        // 文件不存在 ⇒ 沿用 fallback。
        File.Delete(ConfigFile);
        Assert.Equal(LogEventLevel.Information, store.ApplyPersistedLevel(LogEventLevel.Information));
    }

    [Fact]
    public async Task Get_ReportsCurrentLevelAndSupportedValues()
    {
        var store = new PuddingLogLevelStore(ConfigFile);
        var controller = new DiagnosticsLogLevelController(store);

        var snapshot = controller.Get().Value;

        Assert.NotNull(snapshot);
        Assert.Equal(PuddingLogLevelStore.SupportedLevels.Count, snapshot!.SupportedLevels.Count);
        Assert.Contains("Debug", snapshot.SupportedLevels);
        Assert.Equal(ConfigFile, snapshot.ConfigFile);
    }

    [Fact]
    public async Task Put_UnknownLevel_Returns400_AndLeavesLevelUntouched()
    {
        var store = new PuddingLogLevelStore(ConfigFile);
        PuddingLogLevelSwitch.Instance.MinimumLevel = LogEventLevel.Information;
        var controller = new DiagnosticsLogLevelController(store);

        var result = await controller.Set(new SetDiagnosticsLogLevelRequest("shout"), CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(400, problem.StatusCode);
        Assert.Equal(LogEventLevel.Information, PuddingLogLevelSwitch.Instance.MinimumLevel);
    }

    [Fact]
    public async Task Put_ValidLevel_AppliesPersistsAndReportsVerbose()
    {
        var store = new PuddingLogLevelStore(ConfigFile);
        var controller = new DiagnosticsLogLevelController(store);

        var result = await controller.Set(new SetDiagnosticsLogLevelRequest("Verbose"), CancellationToken.None);

        var snapshot = result.Value;
        Assert.NotNull(snapshot);
        Assert.Equal("Verbose", snapshot!.Level);
        Assert.True(snapshot.IsVerbose, "Verbose/Debug 属于会放大日志量的档位，界面要据此提示用完关掉");
        Assert.Equal(LogEventLevel.Verbose, PuddingLogLevelSwitch.Instance.MinimumLevel);
        Assert.True(File.Exists(ConfigFile), "切换成功必须已落盘，否则重启后级别会悄悄退回");
    }
}
