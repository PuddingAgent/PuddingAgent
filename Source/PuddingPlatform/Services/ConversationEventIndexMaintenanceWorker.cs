using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services;

/// <summary>
/// 启动后在后台补齐 <c>conversation_events</c> 的查询索引——G4 读放大修复的可执行入口。
/// <para>
/// <b>为什么需要它</b>：这些索引按设计<b>不在启动期同步创建</b>（370 万行建索引有耗时与磁盘成本），
/// 但 <see cref="ConversationReadModelSchemaBootstrapper.EnsureConversationEventIndexesAsync"/>
/// 此前<b>没有任何生产调用点</b> ⇒ 已有库永远拿不到索引，线上一直全表扫
/// （2026-10-28 实测造成约 800 MiB/s 持续逻辑读）。
/// </para>
/// <para>
/// <b>行为</b>：启动后等待 <see cref="InitialDelay"/>（让宿主先稳定、不与启动争 IO），
/// 再逐条检查 <c>sqlite_master</c>（微秒级），缺哪个补哪个；全部就绪则直接结束。
/// 本进程内只尝试一次；<c>CREATE INDEX IF NOT EXISTS</c> 保证失败后下次启动可安全重试。
/// </para>
/// <para>
/// <b>安全边界</b>：不阻塞启动；异常只记日志不外抛（建索引失败不该让宿主起不来）；
/// 可用配置 <c>ConversationEventIndexMaintenance:Enabled=false</c> 整体关闭。
/// </para>
/// </summary>
public sealed class ConversationEventIndexMaintenanceWorker(
    IDbContextFactory<PlatformDbContext> dbFactory,
    IConfiguration configuration,
    ILogger<ConversationEventIndexMaintenanceWorker> logger) : BackgroundService
{
    internal const string EnabledConfigurationKey = "ConversationEventIndexMaintenance:Enabled";

    /// <summary>启动后延迟多久再动手；避开启动高峰，避免与首屏查询/JIT 争 IO。</summary>
    internal static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var enabled = configuration.GetValue(EnabledConfigurationKey, true);

        // 入口即记 Information：这是判别「worker 根本没被调用」与「被调用但卡在后面的 DB 调用」的唯一凭据。
        // 2026-10-28 实测：只有结束态日志时，宿主启动后数分钟内这两种情形在日志里完全同形。
        logger.LogInformation(
            "[ConversationEventIndex] worker entered; first index check in {DelaySeconds}s ({Key}={Enabled})",
            InitialDelay.TotalSeconds,
            EnabledConfigurationKey,
            enabled);

        if (!enabled)
        {
            // 与结束态日志同理：宿主默认过滤 Debug，静默禁用会被误读成「worker 没实现」。
            logger.LogInformation(
                "[ConversationEventIndex] Disabled by configuration ({Key}=false)",
                EnabledConfigurationKey);
            return;
        }

        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);
            var result = await ConversationReadModelSchemaBootstrapper
                .EnsureConversationEventIndexesAsync(db, logger, stoppingToken);

            if (result.CreatedIndexes.Count > 0)
            {
                logger.LogInformation(
                    "[ConversationEventIndex] Created {Count} missing index(es): {Indexes}",
                    result.CreatedIndexes.Count,
                    string.Join(", ", result.CreatedIndexes));
            }
            else
            {
                // 刻意用 Information 而不是 Debug：宿主默认过滤 Debug（实测日志里 [DBG] 计数恒为 0），
                // 而这一行是运维唯一能确认「worker 真的跑了、索引真的齐了」的凭据。
                // 每进程仅一行，不构成噪声。
                logger.LogInformation(
                    "[ConversationEventIndex] All {Count} conversation_events indexes already present",
                    result.AlreadyPresentIndexes.Count);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 宿主正在停止，建索引可能被中断；CREATE INDEX IF NOT EXISTS 保证下次启动可重试。
            logger.LogInformation(
                "[ConversationEventIndex] Interrupted by host shutdown; will retry on next start");
        }
        catch (Exception ex)
        {
            // 刻意不抛：索引缺失只是性能问题，不该让宿主启动失败。
            logger.LogWarning(
                ex,
                "[ConversationEventIndex] Maintenance failed; host continues. "
                + "conversation_events queries keep full-scanning until this succeeds.");
        }
    }
}
