using System.Data.Common;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace PuddingPlatform.Data.Diagnostics;

/// <summary>
/// EF Core 命令拦截器：把每次 SQL 执行喂给 <see cref="SqlStatementMetrics"/>。
/// </summary>
/// <remarks>
/// <para>
/// 它回答的问题：<b>哪条 SQL 被跑了几百万次</b>。文件级观测（句柄 / IO 计数器 / ETW）
/// 只能告诉你"在读哪个文件"，这层才能告诉你是"哪条查询在读它"。追
/// <c>pudding_platform.db</c> 上 4KiB 级页读风暴时，缺的正是这一层。
/// </para>
/// <para>
/// <b>无状态设计</b>：耗时直接取 <see cref="CommandEventData.StartTime"/>，不自己保存
/// "命令开始时刻"。理由是自存时刻要么用 <c>ConditionalWeakTable</c> 兜住生命周期、要么就得
/// 容忍字典泄漏；而 EF 已经把开始时刻随事件传进来了，用它既没有生命周期问题，也没有
/// 并发键冲突问题。
/// </para>
/// <para>
/// <b>只记录不阻断</b>：所有回调都原样放行结果，不修改 SQL、不修改参数、不吞异常。
/// 统计层绝不能改变被观测者的行为，否则它自己就成了新的故障源。
/// </para>
/// <para>
/// <b>行数只在不撒谎时记录</b>：<c>ReaderExecuted</c> 拿到的 reader 还没被消费，
/// 此刻的行数是未知的，所以查询类传 <c>null</c> 而不是猜一个值；只有
/// <c>Scalar</c>（有值即 1 行）与 <c>NonQuery</c>（有确切影响行数）才记数。
/// </para>
/// </remarks>
internal sealed class SqlStatementMetricsInterceptor : DbCommandInterceptor
{
    private readonly SqlStatementMetrics _metrics;

    /// <summary>用给定的统计容器创建拦截器。</summary>
    /// <param name="metrics">目标统计容器（由调用方持有，便于外部读取快照）。</param>
    internal SqlStatementMetricsInterceptor(SqlStatementMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        _metrics = metrics;
    }

    // ── Reader（查询）───────────────────────────────────────────────────

    /// <inheritdoc />
    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
        => result;   // 不阻断、不改写

    /// <inheritdoc />
    public override DbDataReader ReaderExecuted(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result)
    {
        Record(command, eventData, rowCount: null);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(result);

    /// <inheritdoc />
    public override ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        Record(command, eventData, rowCount: null);
        return ValueTask.FromResult(result);
    }

    // ── Scalar（取单值）─────────────────────────────────────────────────

    /// <inheritdoc />
    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result)
        => result;

    /// <inheritdoc />
    public override object? ScalarExecuted(
        DbCommand command,
        CommandExecutedEventData eventData,
        object? result)
    {
        // 有非空结果说明至少存在一行。
        Record(command, eventData, rowCount: result is null ? null : 1);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(result);

    /// <inheritdoc />
    public override ValueTask<object?> ScalarExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        object? result,
        CancellationToken cancellationToken = default)
    {
        Record(command, eventData, rowCount: result is null ? null : 1);
        return ValueTask.FromResult(result);
    }

    // ── NonQuery（写）───────────────────────────────────────────────────

    /// <inheritdoc />
    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result)
        => result;

    /// <inheritdoc />
    public override int NonQueryExecuted(
        DbCommand command,
        CommandExecutedEventData eventData,
        int result)
    {
        Record(command, eventData, rowCount: result);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(result);

    /// <inheritdoc />
    public override ValueTask<int> NonQueryExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        Record(command, eventData, rowCount: result);
        return ValueTask.FromResult(result);
    }

    private void Record(DbCommand command, CommandEventData eventData, int? rowCount)
    {
        // 耗时取 EF 随事件带来的开始时刻；负值（时钟回拨）由指标层夹到 0。
        var elapsed = DateTimeOffset.UtcNow - eventData.StartTime;

        // 单位是 TimeSpan tick（100ns），与 Stopwatch tick 不同；指标层只做累加与比较，
        // 不解释绝对时间，所以单位一致的内部约定即可。
        _metrics.Record(command.CommandText, elapsed.Ticks, rowCount);
    }
}
