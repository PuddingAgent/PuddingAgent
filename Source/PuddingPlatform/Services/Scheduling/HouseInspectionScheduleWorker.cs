using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PuddingCode.Configuration;
using PuddingCode.Tasks;
using PuddingPlatform.Data;
using PuddingPlatform.Services.Tasks;
using PuddingScheduling;

namespace PuddingPlatform.Services.Scheduling;

public sealed class HouseInspectionScheduleOptions
{
    public const string SectionName = "AutomationSchedules:HouseInspection";
    public bool Enabled { get; set; }
    public string WorkspaceId { get; set; } = "";
    public string? PreferredAgentId { get; set; }
    public string Cron { get; set; } = "0 8 * * *";
    public string TimeZoneId { get; set; } = "Asia/Shanghai";
}

/// <summary>Creates inspection cards for the existing task dispatcher.</summary>
public sealed class HouseInspectionScheduleWorker(
    IDbContextFactory<PlatformDbContext> databaseFactory,
    IOptions<HouseInspectionScheduleOptions> options,
    PuddingDataPaths paths,
    TimeProvider clock,
    ILogger<HouseInspectionScheduleWorker> logger) : BackgroundService
{
    private static readonly Guid PilotScheduleId = new("d73dbe48-a2e2-4c82-bdab-25745db152f2");
    private const string Handler = "inspect_house";
    private const string Title = "每日只读巡检";
    private readonly HouseInspectionScheduleOptions _settings = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.Enabled)
            return;

        var initialized = false;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15), clock);
        do
        {
            try
            {
                if (!initialized)
                {
                    await InitializeAsync(stoppingToken);
                    initialized = true;
                }

                foreach (var result in await ScanAsync(stoppingToken))
                {
                    if (result.Error is { } error)
                        logger.LogWarning(error, "Inspection schedule {ScheduleId} could not create a task", result.ScheduleId);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                logger.LogWarning(error, "Inspection schedule scan failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task InitializeAsync(CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(_settings.WorkspaceId);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(_settings.TimeZoneId);
        var cron = CronExpression.Parse(_settings.Cron);
        var next = cron.NextOccurrence(zone, clock.GetUtcNow())
            ?? throw new InvalidOperationException("The inspection schedule has no upcoming occurrence.");
        await using var db = await databaseFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.Workspaces.AnyAsync(w => w.WorkspaceId == _settings.WorkspaceId && w.IsEnabled && !w.IsFrozen, cancellationToken))
            throw new InvalidOperationException("The configured inspection workspace is unavailable.");

        await db.Database.OpenConnectionAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();
        await SqliteScheduleStore.EnsureSchemaAsync(connection, cancellationToken);
        var store = new SqliteScheduleStore(connection, WriteTaskAsync);
        await store.SyncDefinitionAsync(new AutomationScheduleRecord(
            PilotScheduleId, Title, cron.Source, zone.Id, Handler, TaskBinding(),
            CatchUpPolicy.Skip, true, next, null, null), cancellationToken);
    }

    internal async Task<IReadOnlyList<ScheduleTickResult>> ScanAsync(CancellationToken cancellationToken)
    {
        await using var db = await databaseFactory.CreateDbContextAsync(cancellationToken);
        await db.Database.OpenConnectionAsync(cancellationToken);
        var store = new SqliteScheduleStore(db.Database.GetDbConnection(), WriteTaskAsync);
        return await new ScheduleTickService(store, clock).RunDueAsync(16, cancellationToken);
    }

    private async Task<string> WriteTaskAsync(
        AutomationScheduleRecord schedule,
        ScheduleFireKey fire,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (schedule.Id != PilotScheduleId || schedule.Handler != Handler || schedule.PayloadJson != TaskBinding())
            throw new InvalidOperationException("The inspection task binding no longer matches its schedule.");

        var sqliteTransaction = (SqliteTransaction)transaction;
        var connection = sqliteTransaction.Connection!;
        await using var eligibility = connection.CreateCommand();
        eligibility.Transaction = sqliteTransaction;
        eligibility.CommandText = """
            SELECT 1 FROM Workspaces WHERE WorkspaceId = $workspace AND IsEnabled = 1 AND IsFrozen = 0
            """;
        eligibility.Parameters.AddWithValue("$workspace", _settings.WorkspaceId);
        if (await eligibility.ExecuteScalarAsync(cancellationToken) is null)
            throw new InvalidOperationException("The configured inspection workspace is unavailable.");

        var script = Path.Combine(AppContext.BaseDirectory, "maintenance", "inspect-house.ps1");
        var command = $"pwsh -NoProfile -File {QuoteArgument(script)} -DataRoot {QuoteArgument(paths.DataRoot)} -SummaryOnly";
        var task = SqliteWorkspaceTaskStore.PrepareTask(new CreateTaskRequest
        {
            WorkspaceId = _settings.WorkspaceId,
            Title = Title,
            Description = $"""
                执行本实例的只读运行巡检（计划时间 {fire.FireTimeUtc:O}）。
                ```powershell
                {command}
                ```
                """,
            AcceptanceCriteria = "在任务结果中简述数据库体量、队列滞留、用量聚合延迟和重复错误数量，标出异常或无法读取的指标。避免引用日志原文和凭据。",
            Origin = TaskOrigin.AutomationSchedule,
            PreferredAgentId = _settings.PreferredAgentId,
            AutoDispatchEnabled = !string.IsNullOrWhiteSpace(_settings.PreferredAgentId),
        }, clock.GetUtcNow());
        await SqliteWorkspaceTaskStore.WriteCreatedTaskAsync(task, sqliteTransaction, cancellationToken);
        return task.TaskId;
    }

    private string TaskBinding() => JsonSerializer.Serialize(new { _settings.WorkspaceId, _settings.PreferredAgentId });

    private static string QuoteArgument(string argument) => $"'{argument.Replace("'", "''")}'";
}
