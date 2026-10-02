using PuddingCode.Configuration;
using Serilog.Events;

namespace PuddingCode.Observability;

/// <summary>
/// 日志级别的**持久化 + 运行时生效**（Debug 开关的服务端真源）。
///
/// <para>
/// 为什么需要"持久化"这一半：只切 <see cref="PuddingLogLevelSwitch"/> 的话，进程一重启就回到默认级别，
/// 用户会以为"我开过 Debug"却找不到日志。落盘文件是 <c>&lt;DataRoot&gt;/config/logging.json</c>，
/// 形如 <c>{"Logging":{"Level":"Debug"}}</c> —— 与 <c>PuddingLoggingBootstrapper</c> 读的键
/// <c>Logging:Level</c> **同一个形状**，因此重启后自然生效（该文件也进启动配置链，手改亦可）。
/// </para>
/// <para>
/// 写入用 <see cref="AtomicFileWriter"/>（临时文件 + fsync + 替换）：半写文件不能成为下一次启动的级别来源。
/// </para>
/// <para>
/// <b>顺序约定</b>：**先落盘、后生效**。反过来的话落盘失败会出现"运行级别已放宽、但重启后不是"这种
/// 用户无法理解的状态；先落盘则失败时运行级别保持不变，接口如实报错。
/// </para>
/// </summary>
public sealed class PuddingLogLevelStore(string filePath)
{
    /// <summary>配置节名（与 bootstrapper 读取的 <c>Logging:Level</c> 对齐）。</summary>
    public const string SectionName = "Logging";

    /// <summary>配置键名。</summary>
    public const string LevelKeyName = "Level";

    /// <summary>可切换的级别（顺序由宽松到严格，供界面直接展示）。</summary>
    public static readonly IReadOnlyList<string> SupportedLevels =
        ["Verbose", "Debug", "Information", "Warning", "Error", "Fatal"];

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>配置文件绝对路径（<c>&lt;DataRoot&gt;/config/logging.json</c>）。</summary>
    public string FilePath { get; } = string.IsNullOrWhiteSpace(filePath)
        ? throw new ArgumentException("日志级别配置文件路径不能为空。", nameof(filePath))
        : filePath;

    /// <summary>当前**实际生效**的级别（读开关，不读文件 —— 文件是意图，开关是事实）。</summary>
    public LogEventLevel Current => PuddingLogLevelSwitch.Instance.MinimumLevel;

    /// <summary>当前是否处于会放大日志量的档位（Verbose/Debug）——界面据此提示"记得关"。</summary>
    public bool IsVerboseEnabled => Current is LogEventLevel.Verbose or LogEventLevel.Debug;

    /// <summary>
    /// 切换级别并落盘。无法识别的级别名 ⇒ 返回 <c>false</c>，且**既不落盘也不改变运行级别**
    /// （fail closed：绝不猜测用户想开哪一档）。
    /// </summary>
    /// <param name="level">目标级别名（大小写不敏感，支持别名）。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<bool> TrySetAsync(string? level, CancellationToken ct = default)
    {
        if (!PuddingLogLevelSwitch.TryParse(level, out var parsed))
        {
            return false;
        }

        // 规范化成 Serilog 的正式名字，避免文件里留下 "info"/"dbg" 这类别名（下次读回时仍能解析，
        // 但人看配置文件时应看到标准值）。
        var canonical = parsed.ToString();

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var payload = new Dictionary<string, Dictionary<string, string>>
            {
                [SectionName] = new() { [LevelKeyName] = canonical },
            };

            await AtomicFileWriter.WriteJsonAsync(FilePath, payload, options: null, ct).ConfigureAwait(false);
            PuddingLogLevelSwitch.Instance.MinimumLevel = parsed;
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 同步版：供**启动路径**使用（Serilog 装配是同步的，那里不该出现 async-over-sync）。
    /// 文件缺失/损坏/值无法识别 ⇒ 保持 <paramref name="fallback"/>，不抛异常。
    /// </summary>
    /// <param name="fallback">文件不可用时的级别（由配置/环境变量解析而来）。</param>
    public LogEventLevel ApplyPersistedLevel(LogEventLevel fallback)
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var payload = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(
                    File.ReadAllText(FilePath),
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (payload is not null
                    && payload.TryGetValue(SectionName, out var section)
                    && section.TryGetValue(LevelKeyName, out var value)
                    && PuddingLogLevelSwitch.TryParse(value, out var parsed))
                {
                    PuddingLogLevelSwitch.Instance.MinimumLevel = parsed;
                    return parsed;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            // 读不到就沿用 fallback：日志级别的持久化损坏不该阻止进程启动。
        }

        PuddingLogLevelSwitch.Instance.MinimumLevel = fallback;
        return fallback;
    }

    /// <summary>
    /// 异步版：供接口/测试使用。语义与 <see cref="ApplyPersistedLevel"/> 相同。
    /// </summary>
    public async Task<LogEventLevel> ApplyPersistedLevelAsync(
        LogEventLevel fallback,
        CancellationToken ct = default)
    {
        try
        {
            var persisted = await AtomicFileWriter
                .ReadJsonAsync<Dictionary<string, Dictionary<string, string>>>(FilePath, options: null, ct)
                .ConfigureAwait(false);

            if (persisted is not null
                && persisted.TryGetValue(SectionName, out var section)
                && section.TryGetValue(LevelKeyName, out var value)
                && PuddingLogLevelSwitch.TryParse(value, out var parsed))
            {
                PuddingLogLevelSwitch.Instance.MinimumLevel = parsed;
                return parsed;
            }
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            // 读不到就沿用 fallback：日志级别的持久化损坏不该阻止进程启动。
        }

        PuddingLogLevelSwitch.Instance.MinimumLevel = fallback;
        return fallback;
    }
}
