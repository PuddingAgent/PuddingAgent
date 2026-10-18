namespace PuddingDesktop.Foundation;

using System.Globalization;

/// <summary>启动面板所处的启动阶段（Desktop 启动协调器状态映射而来）。</summary>
public enum StartupPanelPhase
{
    Unknown,
    Idle,
    Starting,
    Cancelling,
    WorkbenchLoading,
    Ready,
    RestartScheduled,
    CoreFailed,
    WorkbenchFailed,
    CircuitOpen,
    NeedsDataRoot,
    InvalidConfiguration,
    DebugFailed,
}

/// <summary>启动面板的语义色调（由 UI 层映射到具体画笔）。</summary>
public enum StartupPanelTone
{
    Normal,
    Busy,
    Warning,
    Error,
    Success,
}

/// <summary>启动面板主按钮的动作（由 UI 层映射到具体命令）。</summary>
public enum StartupPanelAction
{
    None,
    StartCore,
    CancelStartup,
    Retry,
    OpenSettings,

    /// <summary>
    /// 保留动作：宿主主按钮可直达运行中心。当前 <see cref="StartupPanelPresenter"/> 的
    /// 逐相位映射不产出该动作（运行中心入口走 RuntimeLink），保留以维持宿主动作枚举完整。
    /// </summary>
    OpenRuntimeCenter,
}

/// <summary>启动步骤的四种状态。</summary>
public enum StartupStepState
{
    Pending,
    Active,
    Done,
    Failed,
}

/// <summary>启动步骤：固定文案 + 当前状态。</summary>
public sealed record StartupPanelStep(string Label, StartupStepState State);

/// <summary>
/// 启动面板的一次渲染输入：Desktop 只提供原始状态，文案/可见性/色调全部由
/// <see cref="StartupPanelPresenter"/> 纯映射得出（单一真源，UI 层不再自行判断）。
/// </summary>
public sealed record StartupPanelInput
{
    public StartupPanelPhase Phase { get; init; } = StartupPanelPhase.Unknown;
    public TimeSpan Elapsed { get; init; }
    public TimeSpan? CoreStartupDuration { get; init; }
    public string? Error { get; init; }
    public string? Activity { get; init; }
    public string? Detail { get; init; }
    public string? Notice { get; init; }
    public string? CoreAddress { get; init; }
    public int? RestartAttempt { get; init; }
    public int? RestartMaxAttempts { get; init; }
    public TimeSpan? NextRestartIn { get; init; }
}

/// <summary>
/// 启动面板的完整渲染模型。全部 required：调用方只按字段落 UI，
/// 不再自行组合「什么时候显示什么」，避免同一状态在启动/重试/恢复三条路径上文案漂移。
/// </summary>
public sealed record StartupPanelModel
{
    public required bool PanelVisible { get; init; }
    public required string Title { get; init; }
    public required StartupPanelTone Tone { get; init; }
    public required bool RingVisible { get; init; }
    public required bool ErrorIconVisible { get; init; }
    public required string PhaseText { get; init; }
    public required bool PhaseVisible { get; init; }
    public required IReadOnlyList<StartupPanelStep> Steps { get; init; }
    public required bool StepsVisible { get; init; }
    public required string MetaText { get; init; }
    public required bool MetaVisible { get; init; }
    public required bool MetaIsWarning { get; init; }
    public required string DetailText { get; init; }
    public required bool DetailVisible { get; init; }
    public required string ErrorText { get; init; }
    public required bool ErrorVisible { get; init; }
    public required string HintText { get; init; }
    public required bool HintVisible { get; init; }
    public required bool PrimaryVisible { get; init; }
    public required string PrimaryText { get; init; }
    public required StartupPanelAction PrimaryAction { get; init; }
    public required string RuntimeLinkText { get; init; }
    public required bool RuntimeLinkVisible { get; init; }
    public required bool SettingsLinkVisible { get; init; }
    public required bool RawErrorLinkVisible { get; init; }
    public required string RawErrorText { get; init; }
    public required string StatusBarText { get; init; }
    public required StartupPanelTone StatusBarTone { get; init; }
}

/// <summary>
/// 启动面板文案/可见性/色调的唯一真源（设计规格：启动、取消、自动恢复、失败等
/// 所有路径共用一张映射表）。
/// <para>
/// 纯逻辑、无 UI/进程依赖，可在 Foundation 边界内单测。未在逐相位规格中特别规定的
/// 可选字段统一取安全空值（<c>false</c> / <c>""</c> / <see cref="StartupPanelTone.Normal"/> /
/// <see cref="StartupPanelAction.None"/> / 空 Steps）；但「有内容就必须可见」的结构字段
/// （PanelVisible / PhaseVisible / StepsVisible）随内容置位，避免出现有文案却被隐藏的不可用界面。
/// </para>
/// </summary>
public static class StartupPanelPresenter
{
    /// <summary>超过该秒数视为「比平时更久」，切到慢启动文案并显示最新输出。</summary>
    public const int SlowStartupSeconds = 15;

    private const string IdlePhaseText = "独立 Core 尚未运行。启动后即可进入工作台；日志与排障在「运行中心」。";
    private const string StartingPhaseText = "正在启动 Core 进程…";

    private static readonly StartupPanelModel Empty = new()
    {
        PanelVisible = false,
        Title = "",
        Tone = StartupPanelTone.Normal,
        RingVisible = false,
        ErrorIconVisible = false,
        PhaseText = "",
        PhaseVisible = false,
        Steps = Array.Empty<StartupPanelStep>(),
        StepsVisible = false,
        MetaText = "",
        MetaVisible = false,
        MetaIsWarning = false,
        DetailText = "",
        DetailVisible = false,
        ErrorText = "",
        ErrorVisible = false,
        HintText = "",
        HintVisible = false,
        PrimaryVisible = false,
        PrimaryText = "",
        PrimaryAction = StartupPanelAction.None,
        RuntimeLinkText = "",
        RuntimeLinkVisible = false,
        SettingsLinkVisible = false,
        RawErrorLinkVisible = false,
        RawErrorText = "",
        StatusBarText = "",
        StatusBarTone = StartupPanelTone.Normal,
    };

    /// <summary>把一次启动状态快照映射为完整的面板模型。</summary>
    public static StartupPanelModel Present(StartupPanelInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var seconds = FormatSeconds(input.Elapsed);
        var activity = NullIfWhiteSpace(input.Activity);
        var notice = NullIfWhiteSpace(input.Notice);
        var detail = NullIfWhiteSpace(input.Detail);
        var address = NullIfWhiteSpace(input.CoreAddress);
        var rawError = NullIfWhiteSpace(input.Error);
        var errorDescription = StartupErrorText.Describe(input.Error);

        return input.Phase switch
        {
            StartupPanelPhase.Unknown => Unknown(),
            StartupPanelPhase.Idle => Idle(),
            StartupPanelPhase.Starting => Starting(),
            StartupPanelPhase.Cancelling => Cancelling(),
            StartupPanelPhase.WorkbenchLoading => WorkbenchLoading(),
            StartupPanelPhase.Ready => Ready(),
            StartupPanelPhase.RestartScheduled => RestartScheduled(),
            StartupPanelPhase.CoreFailed => CoreFailed(),
            StartupPanelPhase.WorkbenchFailed => WorkbenchFailed(),
            StartupPanelPhase.CircuitOpen => CircuitOpen(),
            StartupPanelPhase.NeedsDataRoot => NeedsDataRoot(),
            StartupPanelPhase.InvalidConfiguration => InvalidConfiguration(),
            StartupPanelPhase.DebugFailed => DebugFailed(),
            // 非法枚举值按「还没读到启动状态」处理：不抛异常，面板始终可用。
            _ => Unknown(),
        };

        StartupPanelModel Unknown() => Empty with
        {
            PanelVisible = true,
            Title = "准备你的工作台",
            Tone = StartupPanelTone.Busy,
            RingVisible = true,
            PhaseText = "正在读取启动配置…",
            PhaseVisible = true,
            StatusBarText = "Core · 准备中",
            StatusBarTone = StartupPanelTone.Busy,
        };

        StartupPanelModel Idle() => Empty with
        {
            PanelVisible = true,
            Title = "准备你的工作台",
            PhaseText = notice ?? IdlePhaseText,
            PhaseVisible = true,
            PrimaryVisible = true,
            PrimaryText = "启动 Core",
            PrimaryAction = StartupPanelAction.StartCore,
            RuntimeLinkText = "运行中心",
            RuntimeLinkVisible = true,
            SettingsLinkVisible = true,
            StatusBarText = "Core · 已停止    尚未连接",
        };

        StartupPanelModel Starting()
        {
            var slow = input.Elapsed.TotalSeconds >= SlowStartupSeconds;
            // 「正在启动 Core 进程…」是进程刚起来、还没进入 Host 初始化时的阶段文案：
            // 此时第一步仍在进行中；只有出现更靠后的阶段（数据/服务/接口）才把它标成完成。
            var processStarted = activity is not null
                && !string.Equals(activity, StartupPhaseActivity.ProcessActivity, StringComparison.Ordinal);
            var steps = processStarted
                ? BuildSteps(StartupStepState.Done, StartupStepState.Active, StartupStepState.Pending)
                : BuildSteps(StartupStepState.Active, StartupStepState.Pending, StartupStepState.Pending);
            var slowDetail = slow ? detail : null;
            return Empty with
            {
                PanelVisible = true,
                Title = "正在启动 Core",
                Tone = slow ? StartupPanelTone.Warning : StartupPanelTone.Busy,
                RingVisible = true,
                PhaseText = activity ?? StartingPhaseText,
                PhaseVisible = true,
                Steps = steps,
                StepsVisible = true,
                MetaVisible = true,
                MetaText = slow
                    ? $"已用 {seconds} 秒 · 比平时更久，可能正在升级数据或等待磁盘"
                    : $"已用 {seconds} 秒 · 通常需要 4–8 秒",
                MetaIsWarning = slow,
                DetailText = slowDetail is null ? "" : $"最新输出 · {slowDetail}",
                DetailVisible = slowDetail is not null,
                PrimaryVisible = true,
                PrimaryText = "取消启动",
                PrimaryAction = StartupPanelAction.CancelStartup,
                RuntimeLinkText = "查看日志",
                RuntimeLinkVisible = true,
                StatusBarText = $"Core · 启动中    已用 {seconds} 秒 · 尚未连接",
                StatusBarTone = StartupPanelTone.Busy,
            };
        }

        StartupPanelModel Cancelling() => Empty with
        {
            PanelVisible = true,
            Title = "正在停止 Core",
            Tone = StartupPanelTone.Warning,
            RingVisible = true,
            PhaseText = "正在取消启动，等待 Core 进程退出…",
            PhaseVisible = true,
            MetaVisible = true,
            MetaText = "通常几秒内完成",
            PrimaryVisible = false,
            RuntimeLinkText = "查看日志",
            RuntimeLinkVisible = true,
            StatusBarText = "Core · 停止中    正在取消启动",
            StatusBarTone = StartupPanelTone.Warning,
        };

        StartupPanelModel WorkbenchLoading() => Empty with
        {
            PanelVisible = true,
            Title = "正在加载工作台",
            Tone = StartupPanelTone.Busy,
            RingVisible = true,
            PhaseText = "Core 已就绪，正在打开 Web 工作台…",
            PhaseVisible = true,
            Steps = BuildSteps(StartupStepState.Done, StartupStepState.Done, StartupStepState.Active),
            StepsVisible = true,
            MetaVisible = true,
            MetaText = input.CoreStartupDuration is { } coreStartup
                ? $"Core 启动耗时 {FormatSeconds(coreStartup)} 秒 · 首次加载界面可能稍慢"
                : "首次加载界面可能稍慢",
            PrimaryVisible = false,
            RuntimeLinkText = "查看日志",
            RuntimeLinkVisible = true,
            StatusBarText = address is null
                ? "Core · 运行中    正在加载工作台"
                : $"Core · 运行中    {address} · 正在加载工作台",
            StatusBarTone = StartupPanelTone.Busy,
        };

        StartupPanelModel Ready() => Empty with
        {
            PanelVisible = false,
            Tone = StartupPanelTone.Success,
            StatusBarText = address is null ? "Core · 运行中" : $"Core · 运行中    {address}",
            StatusBarTone = StartupPanelTone.Success,
        };

        StartupPanelModel RestartScheduled()
        {
            var countdown = input.NextRestartIn is { } remaining
                ? $"Core 意外退出，将在 {CeilingSeconds(remaining)} 秒后自动恢复。"
                : "Core 意外退出，正在等待自动恢复。";
            var metaVisible = input.RestartAttempt.HasValue && input.RestartMaxAttempts.HasValue;
            var metaText = metaVisible
                ? string.Create(CultureInfo.InvariantCulture,
                    $"第 {input.RestartAttempt!.Value}/{input.RestartMaxAttempts!.Value} 次自动恢复")
                : "";
            return Empty with
            {
                PanelVisible = true,
                Title = "等待自动重启",
                RingVisible = true,
                PhaseText = countdown,
                PhaseVisible = true,
                Steps = BuildSteps(StartupStepState.Done, StartupStepState.Failed, StartupStepState.Pending),
                StepsVisible = true,
                MetaText = metaText,
                MetaVisible = metaVisible,
                PrimaryVisible = true,
                PrimaryText = "立即重试",
                PrimaryAction = StartupPanelAction.Retry,
                RuntimeLinkText = "运行中心",
                RuntimeLinkVisible = true,
                StatusBarText = "Core · 等待重启    尚未连接",
                Tone = StartupPanelTone.Warning,
                StatusBarTone = StartupPanelTone.Warning,
            };
        }

        StartupPanelModel CoreFailed() => Empty with
        {
            PanelVisible = true,
            Title = "Core 启动失败",
            ErrorIconVisible = true,
            Steps = BuildSteps(StartupStepState.Done, StartupStepState.Failed, StartupStepState.Pending),
            StepsVisible = true,
            ErrorText = errorDescription,
            ErrorVisible = true,
            HintVisible = true,
            HintText = "请检查端口是否被占用、数据目录是否可写、Core 路径是否正确；也可以到运行中心查看完整日志。",
            PrimaryVisible = true,
            PrimaryText = "重试",
            PrimaryAction = StartupPanelAction.Retry,
            RuntimeLinkText = "运行中心",
            RuntimeLinkVisible = true,
            SettingsLinkVisible = true,
            RawErrorLinkVisible = rawError is not null,
            RawErrorText = input.Error ?? "",
            StatusBarText = "Core · 启动失败    尚未连接",
            Tone = StartupPanelTone.Error,
            StatusBarTone = StartupPanelTone.Error,
        };

        StartupPanelModel WorkbenchFailed() => Empty with
        {
            PanelVisible = true,
            Title = "工作台加载失败",
            ErrorIconVisible = true,
            Steps = BuildSteps(StartupStepState.Done, StartupStepState.Done, StartupStepState.Failed),
            StepsVisible = true,
            ErrorText = "Core 正在运行，但 Web 工作台没有加载出来。",
            ErrorVisible = true,
            HintVisible = true,
            HintText = "可以重试加载界面；如果反复失败，请到运行中心查看日志。",
            PrimaryVisible = true,
            PrimaryText = "重试界面",
            PrimaryAction = StartupPanelAction.Retry,
            RuntimeLinkText = "运行中心",
            RuntimeLinkVisible = true,
            RawErrorLinkVisible = rawError is not null,
            RawErrorText = input.Error ?? "",
            StatusBarText = address is null
                ? "Core · 运行中 · 界面加载失败"
                : $"Core · 运行中    {address} · 界面加载失败",
            Tone = StartupPanelTone.Error,
            StatusBarTone = StartupPanelTone.Error,
        };

        StartupPanelModel CircuitOpen() => Empty with
        {
            PanelVisible = true,
            Title = "自动恢复已暂停",
            ErrorIconVisible = true,
            Steps = BuildSteps(StartupStepState.Done, StartupStepState.Failed, StartupStepState.Pending),
            StepsVisible = true,
            ErrorText = "Core 在短时间内连续失败，自动恢复已暂停。",
            ErrorVisible = true,
            HintVisible = true,
            HintText = "请检查端口、数据目录或 Core 路径，然后手动重试。",
            PrimaryVisible = true,
            PrimaryText = "重试",
            PrimaryAction = StartupPanelAction.Retry,
            RuntimeLinkText = "运行中心",
            RuntimeLinkVisible = true,
            SettingsLinkVisible = true,
            RawErrorLinkVisible = rawError is not null,
            RawErrorText = input.Error ?? "",
            StatusBarText = "Core · 恢复已暂停",
            Tone = StartupPanelTone.Error,
            StatusBarTone = StartupPanelTone.Error,
        };

        StartupPanelModel NeedsDataRoot() => Empty with
        {
            PanelVisible = true,
            Title = "需要先完成启动设置",
            PhaseText = "还没有设置数据目录。设置好数据目录与 Core 路径后即可启动 Core。",
            PhaseVisible = true,
            PrimaryVisible = true,
            PrimaryText = "打开启动设置",
            PrimaryAction = StartupPanelAction.OpenSettings,
            RuntimeLinkText = "运行中心",
            RuntimeLinkVisible = true,
            StatusBarText = "Core · 待配置    尚未连接",
            Tone = StartupPanelTone.Warning,
            StatusBarTone = StartupPanelTone.Warning,
        };

        StartupPanelModel InvalidConfiguration() => Empty with
        {
            PanelVisible = true,
            Title = "启动配置有误",
            PhaseText = errorDescription,
            PhaseVisible = true,
            HintVisible = true,
            HintText = "请到「启动设置」修正后重试。",
            PrimaryVisible = true,
            PrimaryText = "打开启动设置",
            PrimaryAction = StartupPanelAction.OpenSettings,
            RuntimeLinkText = "运行中心",
            RuntimeLinkVisible = true,
            RawErrorLinkVisible = rawError is not null,
            RawErrorText = input.Error ?? "",
            StatusBarText = "Core · 配置异常    尚未连接",
            Tone = StartupPanelTone.Warning,
            StatusBarTone = StartupPanelTone.Warning,
        };

        StartupPanelModel DebugFailed() => Empty with
        {
            PanelVisible = true,
            Title = "调试组件启动失败",
            ErrorIconVisible = true,
            Steps = BuildSteps(StartupStepState.Done, StartupStepState.Failed, StartupStepState.Pending),
            StepsVisible = true,
            ErrorText = errorDescription,
            ErrorVisible = true,
            HintVisible = true,
            HintText = "请检查调试设置，或到运行中心查看日志。",
            PrimaryVisible = true,
            PrimaryText = "重试",
            PrimaryAction = StartupPanelAction.Retry,
            RuntimeLinkText = "运行中心",
            RuntimeLinkVisible = true,
            SettingsLinkVisible = true,
            RawErrorLinkVisible = rawError is not null,
            RawErrorText = input.Error ?? "",
            StatusBarText = "Core · 调试失败",
            Tone = StartupPanelTone.Error,
            StatusBarTone = StartupPanelTone.Error,
        };
    }

    private static IReadOnlyList<StartupPanelStep> BuildSteps(
        StartupStepState process,
        StartupStepState initializing,
        StartupStepState workbench) =>
        new StartupPanelStep[]
        {
            new("启动进程", process),
            new("初始化服务", initializing),
            new("打开界面", workbench),
        };

    /// <summary>秒数 1 位小数、InvariantCulture（如 <c>4.2</c>）；单位「秒」由各文案统一拼接。</summary>
    private static string FormatSeconds(TimeSpan elapsed) =>
        elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture);

    /// <summary>倒计时向上取整到整秒（不足 1 秒按 1 秒，剩余为负按 0 秒兜底）。</summary>
    private static int CeilingSeconds(TimeSpan remaining) =>
        remaining <= TimeSpan.Zero ? 0 : (int)Math.Ceiling(remaining.TotalSeconds);

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>
/// 启动错误 → 面板可读文案。规则按「具体到通用」的顺序匹配，保证超时/健康检查/
/// 找不到可执行文件这类可行动错误不会被「含中文就原样显示」的兜底规则抢先。
/// </summary>
public static class StartupErrorText
{
    private const string UnknownError = "Core 启动失败：没有拿到具体错误信息。";
    private const string GenericError = "Core 启动失败：请查看运行中心日志了解详细原因。";
    private const int OriginalTextLimit = 160;

    /// <summary>把原始错误文本映射成一句可展示的中文说明（永不返回 null/空白）。</summary>
    public static string Describe(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return UnknownError;

        // 旧文案里的「系统设置」已改名为「启动设置」：原生错误直接透传时也要纠正称呼。
        var normalized = error.Replace("「系统设置」", "「启动设置」", StringComparison.Ordinal);

        if (normalized.Contains("did not emit a Ready signal or renew its startup lease", StringComparison.Ordinal))
            return "Core 启动超时：在设定时间内没有收到就绪信号。";
        if (normalized.Contains("did not emit a Ready signal within the bounded", StringComparison.Ordinal))
            return "Core 启动超时：超过最长等待时间仍未就绪。";
        if (normalized.Contains("did not become healthy", StringComparison.Ordinal))
            return "Core 已启动，但健康检查没有通过。";
        if (normalized.Contains("before startup completed", StringComparison.Ordinal))
            return "Core 进程在启动完成前退出了（常见原因：端口被占用、数据目录被占用或配置错误）。";
        if (normalized.Contains("PuddingAgent.exe", StringComparison.Ordinal))
            return "找不到 Core 可执行文件：请检查启动设置中的 Core 路径。";
        if (normalized.Contains("Core 进程意外退出", StringComparison.Ordinal))
            return "Core 进程意外退出，请重试或查看运行中心日志。";
        if (normalized.Contains("熔断", StringComparison.Ordinal))
            return "Core 在短时间内连续失败，自动恢复已暂停。";

        // 原生中文错误原样展示（去空白）；过长时截断，避免撑爆面板。
        if (ContainsCjk(normalized))
        {
            var trimmed = normalized.Trim();
            return trimmed.Length > OriginalTextLimit ? trimmed[..OriginalTextLimit] + "…" : trimmed;
        }

        return GenericError;
    }

    /// <summary>是否含任意 CJK 统一表意文字（\u4e00~\u9fff）。</summary>
    private static bool ContainsCjk(string text)
    {
        foreach (var ch in text)
        {
            if (ch is >= '\u4e00' and <= '\u9fff') return true;
        }
        return false;
    }
}
