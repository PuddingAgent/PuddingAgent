using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PuddingCode.Configuration;

namespace PuddingCode.Runtime;

/// <summary>
/// Agent 级访问级别（用户 2026-09-19 决策：权限跟随 Agent 主体，不跟随工作区）。
/// </summary>
public enum AgentAccessLevel
{
    /// <summary>自动审批（默认）：由 PuddingAgent 自动权限审批系统逐次裁决。</summary>
    Auto = 0,

    /// <summary>
    /// 完全访问：自动审批系统直接放行（等价于原先的进程级 YOLO）。
    /// 注意语义边界 —— 它放行的是「审批与授权」，不是全部安全不变量：
    /// 角色工具白名单（暴露边界）与进程终止等宿主不变量不在放行范围内。
    /// </summary>
    Full = 1,
}

/// <summary>单个 Agent 的访问级别状态（含临时授权的到期时刻）。</summary>
public sealed record AgentAccessLevelState(
    AgentAccessLevel Level,
    DateTimeOffset? ExpiresAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string? UpdatedBy)
{
    /// <summary>缺省（无文件、读取失败、未指定 Agent）：自动审批。</summary>
    public static AgentAccessLevelState Default { get; } =
        new(AgentAccessLevel.Auto, null, DateTimeOffset.MinValue, null);

    /// <summary>
    /// 按给定时刻判定是否仍处于完全访问。临时授权到期后自动回落为 false
    /// （不需要任何人来「撤销」，因此重启/长跑都不会残留）。
    /// </summary>
    public bool IsFullAt(DateTimeOffset now) =>
        Level == AgentAccessLevel.Full
        && (ExpiresAtUtc is null || ExpiresAtUtc.Value > now);

    /// <summary>是否为「有期限的」完全访问（用于 UI 区分持久授权与 5 分钟临时授权）。</summary>
    public bool IsTemporary => Level == AgentAccessLevel.Full && ExpiresAtUtc is not null;
}

/// <summary>
/// 读写 Agent 级访问级别。存储为实例级 JSON 文件
/// <c>agents/{agentInstanceId}/access-level.json</c>（与 heartbeat.json / goal.md 同目录同构）。
/// 放在 PuddingCore 而非 PuddingRuntime：命令边界（PuddingPlatform 的 SystemCommandHandler）
/// 与执行边界（PuddingRuntime 的工具执行服务）都要用它，而 PuddingPlatform 不引用 PuddingRuntime。
/// </summary>
public interface IAgentAccessLevelService
{
    /// <summary>读取状态。未指定 Agent、文件不存在或读取失败时返回 <see cref="AgentAccessLevelState.Default"/>。</summary>
    AgentAccessLevelState Get(string? agentInstanceId);

    /// <summary>
    /// 写入状态。<paramref name="ttl"/> 非空表示临时授权（到期自动回落），
    /// <paramref name="level"/> 传 <see cref="AgentAccessLevel.Auto"/> 即撤销完全访问。
    /// </summary>
    AgentAccessLevelState Set(
        string agentInstanceId,
        AgentAccessLevel level,
        TimeSpan? ttl = null,
        string? actor = null);

    /// <summary>
    /// 求本次工具调用的**生效模式**：全局 YOLO 是运维级总开关，优先级最高；
    /// Agent 级完全访问只在其之上追加授权。Safe / EmergencyStopping 不被 Agent 级覆盖
    /// —— 那是急停态，任何 Agent 配置都不应绕过。
    /// </summary>
    RuntimeExecutionMode ResolveEffectiveMode(string? agentInstanceId, RuntimeExecutionMode globalMode);
}

/// <inheritdoc />
public sealed class AgentAccessLevelService : IAgentAccessLevelService
{
    /// <summary>UI/命令里的「完全访问（5 分钟）」临时授权时长。</summary>
    public static readonly TimeSpan TemporaryGrantDuration = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly PuddingDataPaths? _paths;
    private readonly ILogger<AgentAccessLevelService>? _logger;

    public AgentAccessLevelService(
        PuddingDataPaths? paths = null,
        ILogger<AgentAccessLevelService>? logger = null)
    {
        _paths = paths;
        _logger = logger;
    }

    public AgentAccessLevelState Get(string? agentInstanceId)
    {
        if (string.IsNullOrWhiteSpace(agentInstanceId) || _paths is null)
            return AgentAccessLevelState.Default;

        try
        {
            var file = _paths.AgentInstanceAccessLevelFile(agentInstanceId);
            if (!File.Exists(file))
                return AgentAccessLevelState.Default;

            var dto = JsonSerializer.Deserialize<AccessLevelFile>(File.ReadAllText(file), JsonOptions);
            if (dto is null)
                return AgentAccessLevelState.Default;

            var level = string.Equals(dto.Level, "full", StringComparison.OrdinalIgnoreCase)
                ? AgentAccessLevel.Full
                : AgentAccessLevel.Auto;

            return new AgentAccessLevelState(
                level,
                dto.ExpiresAtUtc,
                dto.UpdatedAtUtc ?? DateTimeOffset.MinValue,
                dto.UpdatedBy);
        }
        catch (Exception ex)
        {
            // 读取失败按**最小权限**回落：宁可退回自动审批，也不因 IO 故障意外放开。
            _logger?.LogWarning(
                ex,
                "[AgentAccessLevel] Read failed, falling back to auto. agent={Agent}",
                agentInstanceId);
            return AgentAccessLevelState.Default;
        }
    }

    public AgentAccessLevelState Set(
        string agentInstanceId,
        AgentAccessLevel level,
        TimeSpan? ttl = null,
        string? actor = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentInstanceId);
        if (_paths is null)
            throw new InvalidOperationException("Agent access level requires PuddingDataPaths to be configured.");

        var now = DateTimeOffset.UtcNow;
        var expiresAt = level == AgentAccessLevel.Full && ttl is { } duration && duration > TimeSpan.Zero
            ? now.Add(duration)
            : (DateTimeOffset?)null;

        var file = _paths.AgentInstanceAccessLevelFile(agentInstanceId);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);

        var payload = new AccessLevelFile
        {
            Level = level == AgentAccessLevel.Full ? "full" : "auto",
            ExpiresAtUtc = expiresAt,
            UpdatedAtUtc = now,
            UpdatedBy = actor,
        };

        // 原子替换，避免读到半截 JSON（执行服务每次工具调用都会读这个文件）。
        var temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(payload, JsonOptions));
        File.Move(temp, file, overwrite: true);

        _logger?.LogInformation(
            "[AgentAccessLevel] Set agent={Agent} level={Level} expiresAt={ExpiresAt} actor={Actor}",
            agentInstanceId,
            payload.Level,
            expiresAt?.ToString("O") ?? "(persistent)",
            actor ?? "(unknown)");

        return new AgentAccessLevelState(level, expiresAt, now, actor);
    }

    public RuntimeExecutionMode ResolveEffectiveMode(string? agentInstanceId, RuntimeExecutionMode globalMode)
    {
        if (globalMode == RuntimeExecutionMode.Yolo)
            return RuntimeExecutionMode.Yolo;

        // Safe / EmergencyStopping 是急停态：Agent 级配置不得覆盖。
        if (globalMode is RuntimeExecutionMode.Safe or RuntimeExecutionMode.EmergencyStopping)
            return globalMode;

        return Get(agentInstanceId).IsFullAt(DateTimeOffset.UtcNow)
            ? RuntimeExecutionMode.Yolo
            : globalMode;
    }

    private sealed class AccessLevelFile
    {
        [JsonPropertyName("level")]
        public string? Level { get; set; }

        [JsonPropertyName("expiresAtUtc")]
        public DateTimeOffset? ExpiresAtUtc { get; set; }

        [JsonPropertyName("updatedAtUtc")]
        public DateTimeOffset? UpdatedAtUtc { get; set; }

        [JsonPropertyName("updatedBy")]
        public string? UpdatedBy { get; set; }
    }
}
