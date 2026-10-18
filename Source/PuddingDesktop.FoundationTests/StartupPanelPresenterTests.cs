namespace PuddingDesktop.FoundationTests;

using PuddingDesktop.Foundation;

/// <summary>
/// 启动面板纯逻辑回归（设计规格：准备你的工作台 / 正在启动 Core / 失败与自动恢复）。
/// 覆盖 Present 的逐相位输出、15 秒慢启动边界、错误文案规则的匹配顺序，
/// 以及 <c>[StartupPhase]</c> 日志行解析（含 <c>[stdout] </c> 前缀与行内任意位置 marker）。
/// 这些断言把面板文案钉成单一真源：启动、取消、自动恢复、失败四条路径不允许各写一份。
/// </summary>
public sealed class StartupPanelPresenterTests
{
    private const string IdlePhaseText = "独立 Core 尚未运行。启动后即可进入工作台；日志与排障在「运行中心」。";
    private const string StartingPhaseText = "正在启动 Core 进程…";
    private const string CoreFailedHint = "请检查端口是否被占用、数据目录是否可写、Core 路径是否正确；也可以到运行中心查看完整日志。";

    // ───────────────────────────── Present：逐相位表 ─────────────────────────────

    [Theory]
    [InlineData(StartupPanelPhase.Unknown, "准备你的工作台", true, 0, StartupPanelAction.None, true, StartupPanelTone.Busy)]
    [InlineData(StartupPanelPhase.Idle, "准备你的工作台", false, 0, StartupPanelAction.StartCore, true, StartupPanelTone.Normal)]
    [InlineData(StartupPanelPhase.Starting, "正在启动 Core", true, 3, StartupPanelAction.CancelStartup, true, StartupPanelTone.Busy)]
    [InlineData(StartupPanelPhase.Cancelling, "正在停止 Core", true, 0, StartupPanelAction.None, true, StartupPanelTone.Warning)]
    [InlineData(StartupPanelPhase.WorkbenchLoading, "正在加载工作台", true, 3, StartupPanelAction.None, true, StartupPanelTone.Busy)]
    [InlineData(StartupPanelPhase.Ready, "", false, 0, StartupPanelAction.None, false, StartupPanelTone.Success)]
    [InlineData(StartupPanelPhase.RestartScheduled, "等待自动重启", true, 3, StartupPanelAction.Retry, true, StartupPanelTone.Warning)]
    [InlineData(StartupPanelPhase.CoreFailed, "Core 启动失败", false, 3, StartupPanelAction.Retry, true, StartupPanelTone.Error)]
    [InlineData(StartupPanelPhase.WorkbenchFailed, "工作台加载失败", false, 3, StartupPanelAction.Retry, true, StartupPanelTone.Error)]
    [InlineData(StartupPanelPhase.CircuitOpen, "自动恢复已暂停", false, 3, StartupPanelAction.Retry, true, StartupPanelTone.Error)]
    [InlineData(StartupPanelPhase.NeedsDataRoot, "需要先完成启动设置", false, 0, StartupPanelAction.OpenSettings, true, StartupPanelTone.Warning)]
    [InlineData(StartupPanelPhase.InvalidConfiguration, "启动配置有误", false, 0, StartupPanelAction.OpenSettings, true, StartupPanelTone.Warning)]
    [InlineData(StartupPanelPhase.DebugFailed, "调试组件启动失败", false, 3, StartupPanelAction.Retry, true, StartupPanelTone.Error)]
    public void Phase_table_maps_title_ring_steps_action_visibility_and_status_tone(
        StartupPanelPhase phase,
        string expectedTitle,
        bool expectedRingVisible,
        int expectedStepCount,
        StartupPanelAction expectedPrimaryAction,
        bool expectedPanelVisible,
        StartupPanelTone expectedStatusBarTone)
    {
        var model = Present(phase);

        Assert.Equal(expectedTitle, model.Title);
        Assert.Equal(expectedRingVisible, model.RingVisible);
        Assert.Equal(expectedStepCount, model.Steps.Count);
        Assert.Equal(expectedStepCount > 0, model.StepsVisible);
        Assert.Equal(expectedPrimaryAction, model.PrimaryAction);
        Assert.Equal(expectedPanelVisible, model.PanelVisible);
        Assert.Equal(expectedStatusBarTone, model.StatusBarTone);
    }

    [Theory]
    [InlineData(StartupPanelPhase.Unknown, StartupPanelTone.Busy, false)]
    [InlineData(StartupPanelPhase.Idle, StartupPanelTone.Normal, false)]
    [InlineData(StartupPanelPhase.Starting, StartupPanelTone.Busy, false)]
    [InlineData(StartupPanelPhase.Cancelling, StartupPanelTone.Warning, false)]
    [InlineData(StartupPanelPhase.WorkbenchLoading, StartupPanelTone.Busy, false)]
    [InlineData(StartupPanelPhase.Ready, StartupPanelTone.Success, false)]
    [InlineData(StartupPanelPhase.RestartScheduled, StartupPanelTone.Warning, false)]
    [InlineData(StartupPanelPhase.CoreFailed, StartupPanelTone.Error, true)]
    [InlineData(StartupPanelPhase.WorkbenchFailed, StartupPanelTone.Error, true)]
    [InlineData(StartupPanelPhase.CircuitOpen, StartupPanelTone.Error, true)]
    [InlineData(StartupPanelPhase.NeedsDataRoot, StartupPanelTone.Warning, false)]
    [InlineData(StartupPanelPhase.InvalidConfiguration, StartupPanelTone.Warning, false)]
    [InlineData(StartupPanelPhase.DebugFailed, StartupPanelTone.Error, true)]
    public void Phase_table_maps_tone_and_error_icon(
        StartupPanelPhase phase,
        StartupPanelTone expectedTone,
        bool expectedErrorIcon)
    {
        var model = Present(phase);

        Assert.Equal(expectedTone, model.Tone);
        Assert.Equal(expectedErrorIcon, model.ErrorIconVisible);
    }

    [Theory]
    [InlineData(StartupPanelPhase.Unknown, true)]
    [InlineData(StartupPanelPhase.Idle, true)]
    [InlineData(StartupPanelPhase.Starting, true)]
    [InlineData(StartupPanelPhase.Cancelling, true)]
    [InlineData(StartupPanelPhase.WorkbenchLoading, true)]
    [InlineData(StartupPanelPhase.Ready, false)]
    [InlineData(StartupPanelPhase.RestartScheduled, true)]
    [InlineData(StartupPanelPhase.CoreFailed, false)]
    [InlineData(StartupPanelPhase.WorkbenchFailed, false)]
    [InlineData(StartupPanelPhase.CircuitOpen, false)]
    [InlineData(StartupPanelPhase.NeedsDataRoot, true)]
    [InlineData(StartupPanelPhase.InvalidConfiguration, true)]
    [InlineData(StartupPanelPhase.DebugFailed, false)]
    public void Phase_visibility_follows_whether_phase_text_has_content(
        StartupPanelPhase phase,
        bool expectedPhaseVisible)
    {
        var model = Present(phase);

        Assert.Equal(expectedPhaseVisible, model.PhaseVisible);
        Assert.Equal(expectedPhaseVisible, model.PhaseText.Length > 0);
    }

    [Fact]
    public void Panel_is_visible_for_every_phase_except_ready()
    {
        // 面板是启动/取消/失败/恢复的唯一操作入口：只有工作台就绪后才允许整块隐藏。
        foreach (var phase in Enum.GetValues<StartupPanelPhase>())
            Assert.Equal(phase != StartupPanelPhase.Ready, Present(phase).PanelVisible);
    }

    [Fact]
    public void Every_phase_exposes_non_null_text_fields_and_steps()
    {
        foreach (var phase in Enum.GetValues<StartupPanelPhase>())
        {
            var model = Present(phase);

            Assert.NotNull(model.Title);
            Assert.NotNull(model.PhaseText);
            Assert.NotNull(model.MetaText);
            Assert.NotNull(model.DetailText);
            Assert.NotNull(model.ErrorText);
            Assert.NotNull(model.HintText);
            Assert.NotNull(model.PrimaryText);
            Assert.NotNull(model.RuntimeLinkText);
            Assert.NotNull(model.RawErrorText);
            Assert.NotNull(model.StatusBarText);
            Assert.NotNull(model.Steps);
            Assert.All(model.Steps, step => Assert.NotNull(step.Label));
        }
    }

    [Theory]
    [InlineData(StartupPanelPhase.Unknown, "Core · 准备中")]
    [InlineData(StartupPanelPhase.Idle, "Core · 已停止    尚未连接")]
    [InlineData(StartupPanelPhase.Starting, "Core · 启动中    已用 0.0 秒 · 尚未连接")]
    [InlineData(StartupPanelPhase.Cancelling, "Core · 停止中    正在取消启动")]
    [InlineData(StartupPanelPhase.WorkbenchLoading, "Core · 运行中    正在加载工作台")]
    [InlineData(StartupPanelPhase.Ready, "Core · 运行中")]
    [InlineData(StartupPanelPhase.RestartScheduled, "Core · 等待重启    尚未连接")]
    [InlineData(StartupPanelPhase.CoreFailed, "Core · 启动失败    尚未连接")]
    [InlineData(StartupPanelPhase.WorkbenchFailed, "Core · 运行中 · 界面加载失败")]
    [InlineData(StartupPanelPhase.CircuitOpen, "Core · 恢复已暂停")]
    [InlineData(StartupPanelPhase.NeedsDataRoot, "Core · 待配置    尚未连接")]
    [InlineData(StartupPanelPhase.InvalidConfiguration, "Core · 配置异常    尚未连接")]
    [InlineData(StartupPanelPhase.DebugFailed, "Core · 调试失败")]
    public void Status_bar_text_without_core_address_is_phase_specific(
        StartupPanelPhase phase,
        string expectedStatusBarText) =>
        Assert.Equal(expectedStatusBarText, Present(phase).StatusBarText);

    // ───────────────────────────── Present：Unknown / Idle ─────────────────────────────

    [Fact]
    public void Unknown_shows_initial_probe_state()
    {
        var model = Present(StartupPanelPhase.Unknown);

        Assert.True(model.PanelVisible);
        Assert.Equal("准备你的工作台", model.Title);
        Assert.True(model.RingVisible);
        Assert.False(model.ErrorIconVisible);
        Assert.Equal("正在读取启动配置…", model.PhaseText);
        Assert.True(model.PhaseVisible);
        Assert.False(model.StepsVisible);
        Assert.Empty(model.Steps);
        Assert.False(model.MetaVisible);
        Assert.False(model.PrimaryVisible);
        Assert.Equal("", model.PrimaryText);
        Assert.Equal(StartupPanelAction.None, model.PrimaryAction);
        Assert.False(model.RuntimeLinkVisible);
        Assert.False(model.SettingsLinkVisible);
        Assert.False(model.RawErrorLinkVisible);
        Assert.Equal("Core · 准备中", model.StatusBarText);
        Assert.Equal(StartupPanelTone.Busy, model.Tone);
        Assert.Equal(StartupPanelTone.Busy, model.StatusBarTone);
    }

    [Fact]
    public void Idle_offers_start_core_and_settings()
    {
        var model = Present(StartupPanelPhase.Idle);

        Assert.True(model.PanelVisible);
        Assert.Equal("准备你的工作台", model.Title);
        Assert.False(model.RingVisible);
        Assert.False(model.ErrorIconVisible);
        Assert.Equal(IdlePhaseText, model.PhaseText);
        Assert.True(model.PhaseVisible);
        Assert.False(model.StepsVisible);
        Assert.Empty(model.Steps);
        Assert.False(model.MetaVisible);
        Assert.True(model.PrimaryVisible);
        Assert.Equal("启动 Core", model.PrimaryText);
        Assert.Equal(StartupPanelAction.StartCore, model.PrimaryAction);
        Assert.Equal("运行中心", model.RuntimeLinkText);
        Assert.True(model.RuntimeLinkVisible);
        Assert.True(model.SettingsLinkVisible);
        Assert.False(model.RawErrorLinkVisible);
        Assert.Equal("Core · 已停止    尚未连接", model.StatusBarText);
        Assert.Equal(StartupPanelTone.Normal, model.Tone);
        Assert.Equal(StartupPanelTone.Normal, model.StatusBarTone);
    }

    [Fact]
    public void Idle_prefers_notice_over_default_phase_text()
    {
        const string notice = "启动配置已保存，可以启动 Core。";
        var model = StartupPanelPresenter.Present(new StartupPanelInput
        {
            Phase = StartupPanelPhase.Idle,
            Notice = notice,
        });

        Assert.Equal(notice, model.PhaseText);
        Assert.True(model.PhaseVisible);
    }

    // ───────────────────────────── Present：Starting 与 15 秒边界 ─────────────────────────────

    [Fact]
    public void Starting_fast_shows_typical_meta_and_cancel_action()
    {
        var model = Present(new StartupPanelInput
        {
            Phase = StartupPanelPhase.Starting,
            Elapsed = TimeSpan.FromSeconds(4.2),
        });

        Assert.True(model.PanelVisible);
        Assert.Equal("正在启动 Core", model.Title);
        Assert.True(model.RingVisible);
        Assert.False(model.ErrorIconVisible);
        Assert.Equal(StartingPhaseText, model.PhaseText);
        Assert.True(model.PhaseVisible);
        Assert.True(model.MetaVisible);
        Assert.Equal("已用 4.2 秒 · 通常需要 4–8 秒", model.MetaText);
        Assert.False(model.MetaIsWarning);
        Assert.False(model.DetailVisible);
        Assert.Equal("", model.DetailText);
        Assert.True(model.PrimaryVisible);
        Assert.Equal("取消启动", model.PrimaryText);
        Assert.Equal(StartupPanelAction.CancelStartup, model.PrimaryAction);
        Assert.Equal("查看日志", model.RuntimeLinkText);
        Assert.True(model.RuntimeLinkVisible);
        Assert.False(model.SettingsLinkVisible);
        Assert.Equal("Core · 启动中    已用 4.2 秒 · 尚未连接", model.StatusBarText);
        Assert.Equal(StartupPanelTone.Busy, model.Tone);
        Assert.Equal(StartupPanelTone.Busy, model.StatusBarTone);
        AssertSteps(
            model,
            ("启动进程", StartupStepState.Active),
            ("初始化服务", StartupStepState.Pending),
            ("打开界面", StartupStepState.Pending));
    }

    [Fact]
    public void Starting_without_activity_keeps_first_step_active()
    {
        var model = Present(new StartupPanelInput
        {
            Phase = StartupPanelPhase.Starting,
            Elapsed = TimeSpan.FromSeconds(3),
            Detail = "ignored while fast",
        });

        Assert.Equal(StartingPhaseText, model.PhaseText);
        Assert.False(model.DetailVisible);
        Assert.Equal("", model.DetailText);
        Assert.Equal("已用 3.0 秒 · 通常需要 4–8 秒", model.MetaText);
        AssertSteps(
            model,
            ("启动进程", StartupStepState.Active),
            ("初始化服务", StartupStepState.Pending),
            ("打开界面", StartupStepState.Pending));
    }

    [Fact]
    public void Starting_in_process_phase_keeps_the_first_step_active()
    {
        var model = Present(new StartupPanelInput
        {
            Phase = StartupPanelPhase.Starting,
            Elapsed = TimeSpan.FromSeconds(0.4),
            Activity = StartupPhaseActivity.ProcessActivity,
        });

        // 进程刚起来 ≠ 第一步完成：阶段文案仍是「正在启动 Core 进程…」时，
        // 步骤条不能提前把「启动进程」画成完成（否则文案与步骤互相矛盾）。
        Assert.Equal(StartupPhaseActivity.ProcessActivity, model.PhaseText);
        AssertSteps(
            model,
            ("启动进程", StartupStepState.Active),
            ("初始化服务", StartupStepState.Pending),
            ("打开界面", StartupStepState.Pending));
    }

    [Fact]
    public void Starting_with_activity_marks_process_step_done()
    {
        var model = Present(new StartupPanelInput
        {
            Phase = StartupPanelPhase.Starting,
            Elapsed = TimeSpan.FromSeconds(5.5),
            Activity = "正在初始化数据与索引…",
        });

        Assert.Equal("正在初始化数据与索引…", model.PhaseText);
        Assert.Equal("已用 5.5 秒 · 通常需要 4–8 秒", model.MetaText);
        Assert.False(model.MetaIsWarning);
        Assert.Equal(StartupPanelTone.Busy, model.Tone);
        AssertSteps(
            model,
            ("启动进程", StartupStepState.Done),
            ("初始化服务", StartupStepState.Active),
            ("打开界面", StartupStepState.Pending));
    }

    [Theory]
    [InlineData(14.9, false, StartupPanelTone.Busy)]
    [InlineData(15.0, true, StartupPanelTone.Warning)]
    public void Starting_slow_threshold_is_fifteen_seconds(
        double elapsedSeconds,
        bool expectedSlow,
        StartupPanelTone expectedTone)
    {
        var model = Present(new StartupPanelInput
        {
            Phase = StartupPanelPhase.Starting,
            Elapsed = TimeSpan.FromSeconds(elapsedSeconds),
        });

        Assert.Equal(StartupPanelPresenter.SlowStartupSeconds, 15);
        Assert.Equal(expectedSlow, model.MetaIsWarning);
        Assert.Equal(expectedTone, model.Tone);
        Assert.Equal(expectedSlow, model.MetaText.Contains("比平时更久", StringComparison.Ordinal));
        Assert.Equal(StartupPanelTone.Busy, model.StatusBarTone);
    }

    [Fact]
    public void Starting_slow_warns_and_shows_latest_output()
    {
        var model = Present(new StartupPanelInput
        {
            Phase = StartupPanelPhase.Starting,
            Elapsed = TimeSpan.FromSeconds(15),
            Activity = "正在初始化数据与索引…",
            Detail = "platform-db-ensure total=1820ms delta=1820ms",
        });

        Assert.Equal(StartupPanelTone.Warning, model.Tone);
        Assert.Equal(StartupPanelTone.Busy, model.StatusBarTone);
        Assert.True(model.MetaVisible);
        Assert.True(model.MetaIsWarning);
        Assert.Equal("已用 15.0 秒 · 比平时更久，可能正在升级数据或等待磁盘", model.MetaText);
        Assert.True(model.DetailVisible);
        Assert.Equal("最新输出 · platform-db-ensure total=1820ms delta=1820ms", model.DetailText);
        Assert.Equal("Core · 启动中    已用 15.0 秒 · 尚未连接", model.StatusBarText);
        Assert.Equal("取消启动", model.PrimaryText);
        Assert.Equal(StartupPanelAction.CancelStartup, model.PrimaryAction);
    }

    [Fact]
    public void Starting_slow_without_detail_hides_detail_block()
    {
        var model = Present(new StartupPanelInput
        {
            Phase = StartupPanelPhase.Starting,
            Elapsed = TimeSpan.FromSeconds(20),
        });

        Assert.True(model.MetaIsWarning);
        Assert.False(model.DetailVisible);
        Assert.Equal("", model.DetailText);
    }

    [Theory]
    [InlineData(4.2, "4.2")]
    [InlineData(0, "0.0")]
    [InlineData(8, "8.0")]
    public void Elapsed_renders_one_decimal_seconds(double elapsedSeconds, string expectedSeconds)
    {
        var model = Present(new StartupPanelInput
        {
            Phase = StartupPanelPhase.Starting,
            Elapsed = TimeSpan.FromSeconds(elapsedSeconds),
        });

        Assert.Equal($"已用 {expectedSeconds} 秒 · 通常需要 4–8 秒", model.MetaText);
        Assert.Equal($"Core · 启动中    已用 {expectedSeconds} 秒 · 尚未连接", model.StatusBarText);
    }

    // ───────────────────────────── Present：Cancelling / WorkbenchLoading ─────────────────────────────

    [Fact]
    public void Cancelling_warns_and_hides_primary_action()
    {
        var model = Present(StartupPanelPhase.Cancelling);

        Assert.True(model.PanelVisible);
        Assert.Equal("正在停止 Core", model.Title);
        Assert.True(model.RingVisible);
        Assert.Equal("正在取消启动，等待 Core 进程退出…", model.PhaseText);
        Assert.True(model.PhaseVisible);
        Assert.False(model.StepsVisible);
        Assert.Empty(model.Steps);
        Assert.True(model.MetaVisible);
        Assert.Equal("通常几秒内完成", model.MetaText);
        Assert.False(model.MetaIsWarning);
        Assert.False(model.PrimaryVisible);
        Assert.Equal("查看日志", model.RuntimeLinkText);
        Assert.True(model.RuntimeLinkVisible);
        Assert.False(model.SettingsLinkVisible);
        Assert.Equal("Core · 停止中    正在取消启动", model.StatusBarText);
        Assert.Equal(StartupPanelTone.Warning, model.Tone);
        Assert.Equal(StartupPanelTone.Warning, model.StatusBarTone);
    }

    [Theory]
    [InlineData(4.2, "127.0.0.1:5199", "Core 启动耗时 4.2 秒 · 首次加载界面可能稍慢", "Core · 运行中    127.0.0.1:5199 · 正在加载工作台")]
    [InlineData(null, "127.0.0.1:5199", "首次加载界面可能稍慢", "Core · 运行中    127.0.0.1:5199 · 正在加载工作台")]
    [InlineData(null, null, "首次加载界面可能稍慢", "Core · 运行中    正在加载工作台")]
    public void WorkbenchLoading_reports_core_duration_and_address(
        double? coreStartupSeconds,
        string? coreAddress,
        string expectedMetaText,
        string expectedStatusBarText)
    {
        var model = Present(new StartupPanelInput
        {
            Phase = StartupPanelPhase.WorkbenchLoading,
            CoreStartupDuration = coreStartupSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null,
            CoreAddress = coreAddress,
        });

        Assert.True(model.PanelVisible);
        Assert.Equal("正在加载工作台", model.Title);
        Assert.True(model.RingVisible);
        Assert.Equal("Core 已就绪，正在打开 Web 工作台…", model.PhaseText);
        Assert.True(model.PhaseVisible);
        Assert.True(model.MetaVisible);
        Assert.Equal(expectedMetaText, model.MetaText);
        Assert.False(model.MetaIsWarning);
        Assert.False(model.PrimaryVisible);
        Assert.Equal("查看日志", model.RuntimeLinkText);
        Assert.True(model.RuntimeLinkVisible);
        Assert.False(model.SettingsLinkVisible);
        Assert.Equal(expectedStatusBarText, model.StatusBarText);
        Assert.Equal(StartupPanelTone.Busy, model.Tone);
        Assert.Equal(StartupPanelTone.Busy, model.StatusBarTone);
        AssertSteps(
            model,
            ("启动进程", StartupStepState.Done),
            ("初始化服务", StartupStepState.Done),
            ("打开界面", StartupStepState.Active));
    }

    // ───────────────────────────── Present：Ready ─────────────────────────────

    [Fact]
    public void Ready_hides_everything_except_success_status_bar()
    {
        var model = Present(StartupPanelPhase.Ready);

        Assert.False(model.PanelVisible);
        Assert.Equal("", model.Title);
        Assert.Equal("", model.PhaseText);
        Assert.Equal("", model.MetaText);
        Assert.Equal("", model.DetailText);
        Assert.Equal("", model.ErrorText);
        Assert.Equal("", model.HintText);
        Assert.Equal("", model.PrimaryText);
        Assert.Equal("", model.RuntimeLinkText);
        Assert.Equal("", model.RawErrorText);
        Assert.False(model.RingVisible);
        Assert.False(model.ErrorIconVisible);
        Assert.False(model.PhaseVisible);
        Assert.False(model.StepsVisible);
        Assert.Empty(model.Steps);
        Assert.False(model.MetaVisible);
        Assert.False(model.MetaIsWarning);
        Assert.False(model.DetailVisible);
        Assert.False(model.ErrorVisible);
        Assert.False(model.HintVisible);
        Assert.False(model.PrimaryVisible);
        Assert.False(model.RuntimeLinkVisible);
        Assert.False(model.SettingsLinkVisible);
        Assert.False(model.RawErrorLinkVisible);
        Assert.Equal(StartupPanelAction.None, model.PrimaryAction);
        Assert.Equal("Core · 运行中", model.StatusBarText);
        Assert.Equal(StartupPanelTone.Success, model.Tone);
        Assert.Equal(StartupPanelTone.Success, model.StatusBarTone);
    }

    [Fact]
    public void Ready_reports_the_live_core_address()
    {
        var model = Present(new StartupPanelInput
        {
            Phase = StartupPanelPhase.Ready,
            CoreAddress = "127.0.0.1:5199",
        });

        Assert.Equal("Core · 运行中    127.0.0.1:5199", model.StatusBarText);
        Assert.Equal(StartupPanelTone.Success, model.StatusBarTone);
    }

    // ───────────────────────────── Present：RestartScheduled ─────────────────────────────

    [Theory]
    [InlineData(0.4, "Core 意外退出，将在 1 秒后自动恢复。")]
    [InlineData(2.2, "Core 意外退出，将在 3 秒后自动恢复。")]
    [InlineData(3.0, "Core 意外退出，将在 3 秒后自动恢复。")]
    public void RestartScheduled_ceils_countdown_to_whole_seconds(double remainingSeconds, string expectedPhaseText)
    {
        var model = Present(new StartupPanelInput
        {
            Phase = StartupPanelPhase.RestartScheduled,
            NextRestartIn = TimeSpan.FromSeconds(remainingSeconds),
            RestartAttempt = 1,
            RestartMaxAttempts = 5,
        });

        Assert.True(model.PanelVisible);
        Assert.Equal("等待自动重启", model.Title);
        Assert.True(model.RingVisible);
        Assert.False(model.ErrorIconVisible);
        Assert.Equal(expectedPhaseText, model.PhaseText);
        Assert.True(model.PhaseVisible);
        Assert.True(model.MetaVisible);
        Assert.Equal("第 1/5 次自动恢复", model.MetaText);
        Assert.False(model.MetaIsWarning);
        Assert.True(model.PrimaryVisible);
        Assert.Equal("立即重试", model.PrimaryText);
        Assert.Equal(StartupPanelAction.Retry, model.PrimaryAction);
        Assert.Equal("运行中心", model.RuntimeLinkText);
        Assert.True(model.RuntimeLinkVisible);
        Assert.False(model.SettingsLinkVisible);
        Assert.Equal("Core · 等待重启    尚未连接", model.StatusBarText);
        Assert.Equal(StartupPanelTone.Warning, model.Tone);
        Assert.Equal(StartupPanelTone.Warning, model.StatusBarTone);
        AssertSteps(
            model,
            ("启动进程", StartupStepState.Done),
            ("初始化服务", StartupStepState.Failed),
            ("打开界面", StartupStepState.Pending));
    }

    [Fact]
    public void RestartScheduled_without_countdown_waits_indefinitely()
    {
        var model = Present(StartupPanelPhase.RestartScheduled);

        Assert.Equal("Core 意外退出，正在等待自动恢复。", model.PhaseText);
    }

    [Fact]
    public void RestartScheduled_hides_attempt_meta_when_counts_are_missing()
    {
        var withoutCounts = Present(StartupPanelPhase.RestartScheduled);
        Assert.False(withoutCounts.MetaVisible);
        Assert.Equal("", withoutCounts.MetaText);

        var partialCounts = Present(new StartupPanelInput
        {
            Phase = StartupPanelPhase.RestartScheduled,
            RestartAttempt = 2,
        });
        Assert.False(partialCounts.MetaVisible);
        Assert.Equal("", partialCounts.MetaText);
    }

    // ───────────────────────────── Present：失败相位 ─────────────────────────────

    [Fact]
    public void CoreFailed_maps_raw_error_and_recovery_hint()
    {
        const string rawError = "Cannot find PuddingAgent.exe";
        var model = Present(new StartupPanelInput
        {
            Phase = StartupPanelPhase.CoreFailed,
            Error = rawError,
        });

        Assert.True(model.PanelVisible);
        Assert.Equal("Core 启动失败", model.Title);
        Assert.False(model.RingVisible);
        Assert.True(model.ErrorIconVisible);
        Assert.False(model.PhaseVisible);
        Assert.True(model.ErrorVisible);
        Assert.Equal("找不到 Core 可执行文件：请检查启动设置中的 Core 路径。", model.ErrorText);
        Assert.True(model.HintVisible);
        Assert.Equal(CoreFailedHint, model.HintText);
        Assert.True(model.PrimaryVisible);
        Assert.Equal("重试", model.PrimaryText);
        Assert.Equal(StartupPanelAction.Retry, model.PrimaryAction);
        Assert.Equal("运行中心", model.RuntimeLinkText);
        Assert.True(model.RuntimeLinkVisible);
        Assert.True(model.SettingsLinkVisible);
        Assert.True(model.RawErrorLinkVisible);
        Assert.Equal(rawError, model.RawErrorText);
        Assert.Equal("Core · 启动失败    尚未连接", model.StatusBarText);
        Assert.Equal(StartupPanelTone.Error, model.Tone);
        Assert.Equal(StartupPanelTone.Error, model.StatusBarTone);
        AssertSteps(
            model,
            ("启动进程", StartupStepState.Done),
            ("初始化服务", StartupStepState.Failed),
            ("打开界面", StartupStepState.Pending));
    }

    [Fact]
    public void CoreFailed_without_raw_error_hides_raw_link_and_uses_unknown_message()
    {
        var model = Present(StartupPanelPhase.CoreFailed);

        Assert.Equal("Core 启动失败：没有拿到具体错误信息。", model.ErrorText);
        Assert.True(model.ErrorVisible);
        Assert.False(model.RawErrorLinkVisible);
        Assert.Equal("", model.RawErrorText);
    }

    [Fact]
    public void WorkbenchFailed_keeps_core_running_status_and_retries_ui_only()
    {
        var model = Present(new StartupPanelInput
        {
            Phase = StartupPanelPhase.WorkbenchFailed,
            Error = "WebView2 初始化失败",
            CoreAddress = "127.0.0.1:5199",
        });

        Assert.True(model.PanelVisible);
        Assert.Equal("工作台加载失败", model.Title);
        Assert.False(model.RingVisible);
        Assert.True(model.ErrorIconVisible);
        Assert.True(model.ErrorVisible);
        Assert.Equal("Core 正在运行，但 Web 工作台没有加载出来。", model.ErrorText);
        Assert.True(model.HintVisible);
        Assert.Equal("可以重试加载界面；如果反复失败，请到运行中心查看日志。", model.HintText);
        Assert.True(model.PrimaryVisible);
        Assert.Equal("重试界面", model.PrimaryText);
        Assert.Equal(StartupPanelAction.Retry, model.PrimaryAction);
        Assert.Equal("运行中心", model.RuntimeLinkText);
        Assert.True(model.RuntimeLinkVisible);
        Assert.False(model.SettingsLinkVisible);
        Assert.True(model.RawErrorLinkVisible);
        Assert.Equal("WebView2 初始化失败", model.RawErrorText);
        Assert.Equal("Core · 运行中    127.0.0.1:5199 · 界面加载失败", model.StatusBarText);
        Assert.Equal(StartupPanelTone.Error, model.Tone);
        Assert.Equal(StartupPanelTone.Error, model.StatusBarTone);
        AssertSteps(
            model,
            ("启动进程", StartupStepState.Done),
            ("初始化服务", StartupStepState.Done),
            ("打开界面", StartupStepState.Failed));
    }

    [Fact]
    public void WorkbenchFailed_without_address_or_raw_error_stays_compact()
    {
        var model = Present(StartupPanelPhase.WorkbenchFailed);

        Assert.Equal("Core · 运行中 · 界面加载失败", model.StatusBarText);
        Assert.False(model.RawErrorLinkVisible);
        Assert.Equal("", model.RawErrorText);
    }

    [Fact]
    public void CircuitOpen_explains_paused_auto_recovery()
    {
        var model = Present(new StartupPanelInput
        {
            Phase = StartupPanelPhase.CircuitOpen,
            Error = "boom",
        });

        Assert.True(model.PanelVisible);
        Assert.Equal("自动恢复已暂停", model.Title);
        Assert.True(model.ErrorIconVisible);
        Assert.True(model.ErrorVisible);
        Assert.Equal("Core 在短时间内连续失败，自动恢复已暂停。", model.ErrorText);
        Assert.True(model.HintVisible);
        Assert.Equal("请检查端口、数据目录或 Core 路径，然后手动重试。", model.HintText);
        Assert.Equal("重试", model.PrimaryText);
        Assert.Equal(StartupPanelAction.Retry, model.PrimaryAction);
        Assert.True(model.SettingsLinkVisible);
        Assert.False(model.RingVisible);
        Assert.True(model.RawErrorLinkVisible);
        Assert.Equal("boom", model.RawErrorText);
        Assert.Equal("Core · 恢复已暂停", model.StatusBarText);
        Assert.Equal(StartupPanelTone.Error, model.Tone);
        Assert.Equal(StartupPanelTone.Error, model.StatusBarTone);
        AssertSteps(
            model,
            ("启动进程", StartupStepState.Done),
            ("初始化服务", StartupStepState.Failed),
            ("打开界面", StartupStepState.Pending));
    }

    [Fact]
    public void NeedsDataRoot_points_to_startup_settings()
    {
        var model = Present(StartupPanelPhase.NeedsDataRoot);

        Assert.True(model.PanelVisible);
        Assert.Equal("需要先完成启动设置", model.Title);
        Assert.False(model.RingVisible);
        Assert.False(model.ErrorIconVisible);
        Assert.Equal("还没有设置数据目录。设置好数据目录与 Core 路径后即可启动 Core。", model.PhaseText);
        Assert.True(model.PhaseVisible);
        Assert.False(model.StepsVisible);
        Assert.Empty(model.Steps);
        Assert.True(model.PrimaryVisible);
        Assert.Equal("打开启动设置", model.PrimaryText);
        Assert.Equal(StartupPanelAction.OpenSettings, model.PrimaryAction);
        Assert.Equal("运行中心", model.RuntimeLinkText);
        Assert.True(model.RuntimeLinkVisible);
        Assert.False(model.SettingsLinkVisible);
        Assert.Equal("Core · 待配置    尚未连接", model.StatusBarText);
        Assert.Equal(StartupPanelTone.Warning, model.Tone);
        Assert.Equal(StartupPanelTone.Warning, model.StatusBarTone);
    }

    [Fact]
    public void InvalidConfiguration_uses_description_as_phase_text_and_corrects_settings_name()
    {
        const string rawError = "请到「系统设置」里修改 Core 路径";
        var model = Present(new StartupPanelInput
        {
            Phase = StartupPanelPhase.InvalidConfiguration,
            Error = rawError,
        });

        Assert.True(model.PanelVisible);
        Assert.Equal("启动配置有误", model.Title);
        Assert.False(model.ErrorIconVisible);
        Assert.True(model.PhaseVisible);
        Assert.Equal("请到「启动设置」里修改 Core 路径", model.PhaseText);
        Assert.True(model.HintVisible);
        Assert.Equal("请到「启动设置」修正后重试。", model.HintText);
        Assert.False(model.StepsVisible);
        Assert.Empty(model.Steps);
        Assert.True(model.PrimaryVisible);
        Assert.Equal("打开启动设置", model.PrimaryText);
        Assert.Equal(StartupPanelAction.OpenSettings, model.PrimaryAction);
        Assert.Equal("运行中心", model.RuntimeLinkText);
        Assert.True(model.RuntimeLinkVisible);
        Assert.False(model.SettingsLinkVisible);
        Assert.True(model.RawErrorLinkVisible);
        Assert.Equal(rawError, model.RawErrorText);
        Assert.Equal("Core · 配置异常    尚未连接", model.StatusBarText);
        Assert.Equal(StartupPanelTone.Warning, model.Tone);
        Assert.Equal(StartupPanelTone.Warning, model.StatusBarTone);
    }

    [Fact]
    public void InvalidConfiguration_without_raw_error_uses_unknown_message_and_hides_raw_link()
    {
        var model = Present(StartupPanelPhase.InvalidConfiguration);

        Assert.Equal("Core 启动失败：没有拿到具体错误信息。", model.PhaseText);
        Assert.False(model.RawErrorLinkVisible);
        Assert.Equal("", model.RawErrorText);
    }

    [Fact]
    public void DebugFailed_maps_error_and_retry()
    {
        var model = Present(new StartupPanelInput
        {
            Phase = StartupPanelPhase.DebugFailed,
            Error = "调试端口被占用",
        });

        Assert.True(model.PanelVisible);
        Assert.Equal("调试组件启动失败", model.Title);
        Assert.True(model.ErrorIconVisible);
        Assert.False(model.RingVisible);
        Assert.True(model.ErrorVisible);
        Assert.Equal("调试端口被占用", model.ErrorText);
        Assert.True(model.HintVisible);
        Assert.Equal("请检查调试设置，或到运行中心查看日志。", model.HintText);
        Assert.Equal("重试", model.PrimaryText);
        Assert.Equal(StartupPanelAction.Retry, model.PrimaryAction);
        Assert.True(model.SettingsLinkVisible);
        Assert.True(model.RawErrorLinkVisible);
        Assert.Equal("调试端口被占用", model.RawErrorText);
        Assert.Equal("Core · 调试失败", model.StatusBarText);
        Assert.Equal(StartupPanelTone.Error, model.Tone);
        Assert.Equal(StartupPanelTone.Error, model.StatusBarTone);
        AssertSteps(
            model,
            ("启动进程", StartupStepState.Done),
            ("初始化服务", StartupStepState.Failed),
            ("打开界面", StartupStepState.Pending));
    }

    // ───────────────────────────── StartupErrorText ─────────────────────────────

    [Theory]
    [InlineData(null, "Core 启动失败：没有拿到具体错误信息。")]
    [InlineData("", "Core 启动失败：没有拿到具体错误信息。")]
    [InlineData("   ", "Core 启动失败：没有拿到具体错误信息。")]
    [InlineData(
        "Core did not emit a Ready signal or renew its startup lease within 90 seconds",
        "Core 启动超时：在设定时间内没有收到就绪信号。")]
    [InlineData(
        "Core did not emit a Ready signal within the bounded startup window",
        "Core 启动超时：超过最长等待时间仍未就绪。")]
    [InlineData("Core did not become healthy within 30s", "Core 已启动，但健康检查没有通过。")]
    [InlineData(
        "Core process exited before startup completed (exit code 1)",
        "Core 进程在启动完成前退出了（常见原因：端口被占用、数据目录被占用或配置错误）。")]
    [InlineData("Cannot find PuddingAgent.exe", "找不到 Core 可执行文件：请检查启动设置中的 Core 路径。")]
    [InlineData("Core 进程意外退出（exit code 1）", "Core 进程意外退出，请重试或查看运行中心日志。")]
    [InlineData("连续失败触发熔断", "Core 在短时间内连续失败，自动恢复已暂停。")]
    [InlineData("  磁盘写入失败  ", "磁盘写入失败")]
    [InlineData("socket error 10061", "Core 启动失败：请查看运行中心日志了解详细原因。")]
    public void Describe_maps_known_error_shapes(string? error, string expected) =>
        Assert.Equal(expected, StartupErrorText.Describe(error));

    [Fact]
    public void Describe_rewrites_settings_name_before_passing_through_cjk()
    {
        const string error = "请到「系统设置」里修改 Core 路径";
        Assert.Equal("请到「启动设置」里修改 Core 路径", StartupErrorText.Describe(error));
    }

    [Theory]
    // 「找不到可执行文件」在中文透传规则之前，不能被「含中文就原样返回」抢先。
    [InlineData("Cannot find PuddingAgent.exe：路径不存在", "找不到 Core 可执行文件：请检查启动设置中的 Core 路径。")]
    // 健康检查规则排在「进程意外退出」与中文透传之前。
    [InlineData("Core 进程意外退出，did not become healthy", "Core 已启动，但健康检查没有通过。")]
    // 未列出但包含中文的错误原样返回。
    [InlineData("Core 启动被拦下：端口 5199 已占用", "Core 启动被拦下：端口 5199 已占用")]
    public void Describe_matches_actionable_rules_before_cjk_passthrough(string error, string expected) =>
        Assert.Equal(expected, StartupErrorText.Describe(error));

    [Fact]
    public void Describe_truncates_long_original_cjk_errors_at_160_chars()
    {
        var described = StartupErrorText.Describe("  " + new string('错', 200) + "  ");

        Assert.Equal(new string('错', 160) + "…", described);
        Assert.Equal(161, described.Length);
    }

    [Fact]
    public void Describe_keeps_original_cjk_text_at_the_limit_untouched()
    {
        var error = new string('错', 160);
        Assert.Equal(error, StartupErrorText.Describe(error));
    }

    // ───────────────────────────── StartupPhaseActivity ─────────────────────────────

    [Fact]
    public void Log_prefix_matches_the_core_tracker_contract() =>
        Assert.Equal("[StartupPhase] ", StartupPhaseActivity.LogPrefix);

    [Fact]
    public void FindLatestPhase_reads_stdout_prefixed_lines()
    {
        IReadOnlyList<string> tail =
        [
            "[stdout] [StartupPhase] process-start total=12ms delta=12ms",
            "[stdout] [StartupPhase] options-resolved total=15ms delta=3ms",
        ];

        Assert.Equal("options-resolved", StartupPhaseActivity.FindLatestPhase(tail));
    }

    [Fact]
    public void FindLatestPhase_reads_bare_lines()
    {
        IReadOnlyList<string> tail = ["[StartupPhase] ready total=100ms delta=3ms"];
        Assert.Equal("ready", StartupPhaseActivity.FindLatestPhase(tail));
    }

    [Fact]
    public void FindLatestPhase_tolerates_marker_anywhere_in_line()
    {
        IReadOnlyList<string> tail =
        [
            "2026-10-18 08:00:00.123 INF [stdout] noise [StartupPhase] host-built total=88ms delta=88ms",
        ];

        Assert.Equal("host-built", StartupPhaseActivity.FindLatestPhase(tail));
    }

    [Fact]
    public void FindLatestPhase_scans_from_tail()
    {
        IReadOnlyList<string> tail =
        [
            "[StartupPhase] process-start total=1ms delta=1ms",
            "[StartupPhase] initialized total=90ms delta=2ms",
            "[StartupPhase] ready total=100ms delta=3ms",
        ];

        Assert.Equal("ready", StartupPhaseActivity.FindLatestPhase(tail));
    }

    [Fact]
    public void FindLatestPhase_returns_null_without_phase_lines()
    {
        Assert.Null(StartupPhaseActivity.FindLatestPhase(Array.Empty<string>()));
        Assert.Null(StartupPhaseActivity.FindLatestPhase(
            ["[stdout] Core listening on http://127.0.0.1:5199"]));
    }

    [Fact]
    public void FindLatestPhase_takes_the_token_before_the_first_space()
    {
        IReadOnlyList<string> tail = ["[stdout] [StartupPhase] platform-db-ensure total=1820ms delta=1820ms"];
        Assert.Equal("platform-db-ensure", StartupPhaseActivity.FindLatestPhase(tail));
    }

    [Fact]
    public void Read_returns_activity_and_full_latest_line()
    {
        IReadOnlyList<string> tail =
        [
            "[stdout] [StartupPhase] initialized total=500ms delta=10ms",
            "[stdout] [StartupPhase] platform-db-ensure total=1820ms delta=1820ms",
        ];

        var (activity, detail) = StartupPhaseActivity.Read(tail);

        Assert.Equal("正在初始化数据与索引…", activity);
        Assert.Equal("platform-db-ensure total=1820ms delta=1820ms", detail);
    }

    [Fact]
    public void Read_returns_nulls_for_empty_tail()
    {
        var (activity, detail) = StartupPhaseActivity.Read(Array.Empty<string>());

        Assert.Null(activity);
        Assert.Null(detail);
    }

    [Fact]
    public void Read_returns_nulls_without_phase_lines()
    {
        var (activity, detail) = StartupPhaseActivity.Read(["[stdout] Core starting..."]);

        Assert.Null(activity);
        Assert.Null(detail);
    }

    [Fact]
    public void Read_trims_detail_and_supports_token_only_lines()
    {
        var (activity, detail) = StartupPhaseActivity.Read(
            ["[stdout] [StartupPhase] server-started   "]);

        Assert.Equal("正在启动本地接口…", activity);
        Assert.Equal("server-started", detail);
    }

    [Theory]
    [InlineData("schema-AppUserSchemaBootstrapper", "正在初始化数据与索引…")]
    [InlineData("SCHEMA-TaskDispatchSchemaBootstrapper", "正在初始化数据与索引…")]
    [InlineData("platform-db-ensure", "正在初始化数据与索引…")]
    [InlineData("event-store-ensure", "正在初始化数据与索引…")]
    [InlineData("memory-db-ensure", "正在初始化数据与索引…")]
    [InlineData("workspace-catalog", "正在初始化数据与索引…")]
    [InlineData("jieba-backfill", "正在初始化数据与索引…")]
    [InlineData("process-start", "正在启动 Core 进程…")]
    [InlineData("options-resolved", "正在启动 Core 进程…")]
    [InlineData("data-root-lease", "正在启动 Core 进程…")]
    [InlineData("data-root-bootstrapped", "正在启动 Core 进程…")]
    [InlineData("logging-ready", "正在启动 Core 进程…")]
    [InlineData("services-registered", "正在启动 Core 进程…")]
    [InlineData("host-built", "正在启动 Core 进程…")]
    [InlineData("middleware-mapped", "正在启动 Core 进程…")]
    [InlineData("initialized", "正在初始化本地服务…")]
    [InlineData("server-started", "正在启动本地接口…")]
    [InlineData("ready", "Core 已就绪，正在等待桌面连接…")]
    [InlineData("READY", "Core 已就绪，正在等待桌面连接…")]
    [InlineData("Server-Started", "正在启动本地接口…")]
    [InlineData("  ready  ", "Core 已就绪，正在等待桌面连接…")]
    [InlineData(null, "正在启动 Core…")]
    [InlineData("", "正在启动 Core…")]
    [InlineData("   ", "正在启动 Core…")]
    [InlineData("some-future-phase", "正在启动 Core…")]
    public void Describe_maps_phase_groups_case_insensitively(string? phase, string expected) =>
        Assert.Equal(expected, StartupPhaseActivity.Describe(phase));

    // ───────────────────────────── 辅助 ─────────────────────────────

    private static StartupPanelModel Present(StartupPanelPhase phase) =>
        StartupPanelPresenter.Present(new StartupPanelInput { Phase = phase });

    private static StartupPanelModel Present(StartupPanelInput input) =>
        StartupPanelPresenter.Present(input);

    private static void AssertSteps(
        StartupPanelModel model,
        params (string Label, StartupStepState State)[] expected)
    {
        Assert.Equal(expected.Length, model.Steps.Count);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index].Label, model.Steps[index].Label);
            Assert.Equal(expected[index].State, model.Steps[index].State);
        }
    }
}
