using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopConnection;
using Pudding.DesktopService;

namespace DesktopServiceTests;

/// <summary>
/// Desktop 侧能力通道的**组合根**（唯一会改变产品行为的一步）。
///
/// 这一批测试存在的理由是本轮抓到的真实故障形态：启用态出错时若只留一条日志，
/// 表现就是「配置看起来生效了，桌面侧却静默留在旧 Bridge」——默认关闭的路径全绿，
/// 只有打开开关才暴露，而且查不出来。因此这里把三条语义钉死：
/// ① 关闭 = 什么都不做（正常状态）；② 启用却起不来 = **明确失败**；③ 不做跨传输回退。
/// </summary>
public sealed class DesktopCapabilityChannelCompositionTests
{
    private const string UsableEndpoint = "named-pipe:pudding-capability-composition-test|1|core-test";
    private const string TestToken = "test-token-value-SECRET";

    [Fact]
    public async Task DisabledSettings_DoNothingAndReportWhy()
    {
        var log = new List<string>();

        var result = await StartAsync(
            settings: DesktopCapabilityChannelSettings.Disabled,
            endpointDescription: null,
            log: log);

        Assert.False(result.IsFailure);
        Assert.Null(result.Value);
        Assert.Contains(log, line => line.Contains("stays disabled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NullSettings_AreTreatedAsDisabledNotAsAnError()
    {
        var result = await StartAsync(settings: null, endpointDescription: null);

        Assert.False(result.IsFailure);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task EnabledButEndpointUnusable_FailsLoudlyInsteadOfStayingSilent()
    {
        // 这正是上一版「就绪端点描述不可解析」的形态：启用着、却永远起不来。
        // 只记日志的版本会让它看起来像「配置没生效」，因此这里必须是失败。
        var log = new List<string>();

        var result = await StartAsync(
            settings: EnabledSettings(),
            endpointDescription: null,
            log: log);

        Assert.True(result.IsFailure);
        // 失败即「什么都没构造」：CapabilityResult 是严格联合（失败态读 Value 会抛），
        // 因此这里不需要、也不能再断言 Value 为 null。
        Assert.Equal(DesktopCapabilityErrorCode.InvalidRequest, result.Error!.Code);
        Assert.Contains(log, line => line.Contains("missing or not usable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EnabledButEndpointMalformed_FailsInsteadOfFallingBackToAnotherTransport()
    {
        // 不做跨传输回退：解析不了就**不启动**（宁可暂时不启用，也不把同一次操作执行两次）。
        var result = await StartAsync(
            settings: EnabledSettings(),
            endpointDescription: "named-pipe:pudding-capability-composition-test|v1|core-test");

        Assert.True(result.IsFailure);
        // 失败即「什么都没构造」（严格联合：失败态没有 Value 可读）。
        Assert.Equal(DesktopCapabilityErrorCode.InvalidRequest, result.Error!.Code);
    }

    [Fact]
    public async Task EnabledWithUsableEndpoint_StartsAndReleasesTheSingleTransportClaim()
    {
        var result = await StartAsync(settings: EnabledSettings(), endpointDescription: UsableEndpoint);

        if (result.IsFailure)
        {
            // 严格联合：成功态读 Error 会抛，因此失败信息只能在这个分支里取。
            Assert.Fail($"expected a running channel but got {result.Error!.Code}: {result.Error.Message}");
        }

        var composition = result.Value;
        Assert.NotNull(composition);
        Assert.True(composition!.IsRunning);

        // 停止必须发生且必须有界：它运行在窗口退出路径上，也会释放「同一 DesktopId 单一活动传输」的名额。
        await composition.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(composition.IsRunning);

        // 可重复释放（退出路径可能被调用两次）。
        await composition.DisposeAsync();
        Assert.False(composition.IsRunning);
    }

    [Fact]
    public async Task Composition_ReusesTheGivenPageRegistry()
    {
        // 准入用的页面注册表必须**就是** Shell 那边驱动的那一个实例：
        // 换成另一个实例会让「页面已登记」与「准入能看见页面」互相矛盾。
        var pageTargets = new DesktopTargetRegistry();

        var result = await StartAsync(
            settings: EnabledSettings(),
            endpointDescription: UsableEndpoint,
            pageTargets: pageTargets);

        Assert.NotNull(result.Value);
        Assert.Same(pageTargets, result.Value!.Service.Targets);
        await result.Value.DisposeAsync();
    }

    [Fact]
    public async Task DefaultServiceOptions_AreFailClosed()
    {
        var options = DesktopCapabilityChannelComposition.CreateDefaultServiceOptions();

        // 允许集合 = 代码事实（实现即声明），不从配置放宽。
        Assert.Equal(DesktopCapabilityChannelSettings.DeclaredCapabilities, options.AllowedCapabilities);
        // Core 侧授权链路给出可信调用方身份之前，Shell 交互能力一律不开放。
        Assert.Equal(DesktopContextTrust.Untrusted, options.ShellCallerTrust);

        // 缺省选项必须真的能构造出服务（否则组合根会在启动期才炸）。
        await using var service = new DesktopService(
            new ManualUiDispatcher(),
            new RecordingUiSurface(),
            new DesktopTargetRegistry(),
            options);
        Assert.False(service.IsClosed);
    }

    [Fact]
    public async Task LogNeverCarriesCredentials()
    {
        var log = new List<string>();

        var result = await StartAsync(
            settings: EnabledSettings(),
            endpointDescription: UsableEndpoint,
            log: log);

        Assert.NotEmpty(log);
        foreach (var line in log)
        {
            Assert.DoesNotContain(TestToken, line, StringComparison.Ordinal);
            Assert.DoesNotContain("token=", line, StringComparison.OrdinalIgnoreCase);
        }

        if (result.Value is { } composition)
        {
            await composition.DisposeAsync();
        }
    }

    [Fact]
    public async Task FailureMessagesNeverCarryCredentials()
    {
        var result = await StartAsync(settings: EnabledSettings(), endpointDescription: "bogus:whatever|1|x");

        Assert.True(result.IsFailure);
        Assert.DoesNotContain(TestToken, result.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingCollaborators_ThrowInsteadOfSilentlyStartingADegradedChannel()
    {
        var settings = EnabledSettings();

        await Assert.ThrowsAsync<ArgumentNullException>(() => DesktopCapabilityChannelComposition.StartAsync(
            settings, UsableEndpoint, Authentication(), new DesktopProcessInstanceId("proc-1"),
            dispatcher: null!, surface: new RecordingUiSurface(), pageTargets: new DesktopTargetRegistry()));

        await Assert.ThrowsAsync<ArgumentNullException>(() => DesktopCapabilityChannelComposition.StartAsync(
            settings, UsableEndpoint, Authentication(), new DesktopProcessInstanceId("proc-1"),
            dispatcher: new ManualUiDispatcher(), surface: null!, pageTargets: new DesktopTargetRegistry()));

        await Assert.ThrowsAsync<ArgumentNullException>(() => DesktopCapabilityChannelComposition.StartAsync(
            settings, UsableEndpoint, Authentication(), new DesktopProcessInstanceId("proc-1"),
            dispatcher: new ManualUiDispatcher(), surface: new RecordingUiSurface(), pageTargets: null!));
    }

    private static DesktopCapabilityChannelSettings EnabledSettings() => new()
    {
        Enabled = true,
        DesktopId = "composition-test",
        ControlTokenHeader = DesktopCapabilityChannelSettings.DefaultControlTokenHeader,
        // 下限：本测试里没有真实 Core，拨号必然失败并进入退避；不影响 StartAsync 不等待握手这一点。
        HandshakeTimeoutSeconds = 1,
    };

    private static DesktopChannelAuthentication Authentication() =>
        DesktopChannelAuthentication.StaticHeader(
            DesktopCapabilityChannelSettings.DefaultControlTokenHeader, TestToken);

    private static Task<CapabilityResult<DesktopCapabilityChannelComposition?>> StartAsync(
        DesktopCapabilityChannelSettings? settings,
        string? endpointDescription,
        Action<string>? log = null,
        DesktopTargetRegistry? pageTargets = null) =>
        DesktopCapabilityChannelComposition.StartAsync(
            settings,
            endpointDescription,
            Authentication(),
            new DesktopProcessInstanceId("proc-1"),
            new ManualUiDispatcher(),
            new RecordingUiSurface(),
            pageTargets ?? new DesktopTargetRegistry(),
            log: log);

    private static Task<CapabilityResult<DesktopCapabilityChannelComposition?>> StartAsync(
        DesktopCapabilityChannelSettings? settings,
        string? endpointDescription,
        List<string> log) =>
        StartAsync(settings, endpointDescription, line => log.Add(line), null);
}
