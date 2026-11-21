using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;

namespace PuddingPlatform.Services;

/// <summary>
/// LLM 用量汇总查询（可观测性 P0-c，<b>只读</b>）。
/// <para>
/// 数据源是账本事实表 <c>TokenUsageEvents</c>（ADR-043）。刻意不用
/// <c>llm_gateway_usage_events</c>（几乎未启用）和 <c>llm_usage_daily_aggregates</c>（未在跑）。
/// </para>
/// <para>
/// <b>为什么用原生 SQL 而不是 LINQ GroupBy</b>：两个原因都是实测得到的，不是偏好。
/// ① 时间列（<c>CreatedAtUtc</c> / <c>OccurredAtUtc</c>）在 SQLite 里是 TEXT（ISO-8601 带
/// <c>+00:00</c> 偏移），EF Core 的 SQLite 提供程序不能翻译 <see cref="DateTimeOffset"/> 的
/// 比较谓词（仓库内 <see cref="TokenUsageEventRepository"/> 已记录同一限制并在注释里绕开）；
/// ② 「日」分组要求按 <b>UTC</b> 切分，而 TEXT 里保存的是带偏移的本地时间表示，
/// 必须先经 <c>julianday()</c> / <c>strftime()</c> 归一化才可能正确。
/// 因此聚合与过滤全部在 SQL 侧完成（<c>GROUP BY</c> + <c>SUM</c>），
/// 只把聚合结果（≤ limit+1 行）读进内存——绝不把 5.8 万行明细拉进进程再算。
/// </para>
/// <para>
/// 时间语义：<c>[fromUtc, toUtcExclusive)</c> 半开区间，全部按 UTC 解释。
/// </para>
/// </summary>
public static class UsageSummaryQuery
{
    /// <summary>默认返回行数上限。subagent 分组可能有上千行，不能无上限返回。</summary>
    public const int DefaultRowLimit = 200;

    /// <summary>分组维度。</summary>
    public enum Grouping
    {
        /// <summary>按 provider/model 分组，键为 "provider/model"。</summary>
        Model,

        /// <summary>按子代理分组，键为 SubAgentId（空值归入 "(none)" 即主链）。</summary>
        Subagent,

        /// <summary>按 UTC 日期分组，键为 yyyy-MM-dd。</summary>
        Day,
    }

    /// <summary>查询结果：已截断的行 + 分组总数 + 是否被截断。</summary>
    public sealed record Result(IReadOnlyList<UsageSummaryRowDto> Rows, long Total, bool Truncated);

    /// <summary>把一个分组维度翻译成 SQL 片段。所有表达式都是常量字符串，不含外部输入。</summary>
    private sealed record GroupSpec(
        string KeyExpression,
        string ProviderExpression,
        string ModelExpression,
        string GroupByExpressions,
        string OrderByClause);

    private const string ProviderExpr = "COALESCE(NULLIF(TRIM(ProviderId), ''), 'unknown')";
    private const string ModelExpr = "COALESCE(NULLIF(TRIM(ModelId), ''), 'unknown')";

    /// <summary>子代理空值的占位键：54k 条主链事件都没有 SubAgentId，丢掉它们会低估总用量。</summary>
    private const string NoSubAgentKey = "(none)";

    private static GroupSpec BuildSpec(Grouping grouping) => grouping switch
    {
        Grouping.Model => new GroupSpec(
            KeyExpression: $"{ProviderExpr} || '/' || {ModelExpr}",
            ProviderExpression: ProviderExpr,
            ModelExpression: ModelExpr,
            GroupByExpressions: $"{ProviderExpr}, {ModelExpr}",
            // 用量最大的排前面，同量按键升序，保证 LIMIT 截断结果稳定可复现。
            OrderByClause: "\"total_tokens\" DESC, \"group_key\" ASC"),

        Grouping.Subagent => new GroupSpec(
            KeyExpression: $"COALESCE(NULLIF(TRIM(SubAgentId), ''), '{NoSubAgentKey}')",
            ProviderExpression: "NULL",
            ModelExpression: "NULL",
            GroupByExpressions: $"COALESCE(NULLIF(TRIM(SubAgentId), ''), '{NoSubAgentKey}')",
            OrderByClause: "\"total_tokens\" DESC, \"group_key\" ASC"),

        // 日分组按日期升序：这是一条时间序列，按用量排序没有意义也没法读。
        Grouping.Day => new GroupSpec(
            KeyExpression: "COALESCE(strftime('%Y-%m-%d', CreatedAtUtc), 'unknown')",
            ProviderExpression: "NULL",
            ModelExpression: "NULL",
            GroupByExpressions: "COALESCE(strftime('%Y-%m-%d', CreatedAtUtc), 'unknown')",
            OrderByClause: "\"group_key\" ASC"),

        _ => throw new ArgumentOutOfRangeException(nameof(grouping), grouping, "未知分组维度"),
    };

    /// <summary>
    /// 执行聚合查询。只读：仅 SELECT，不写库、不建表、不改 schema。
    /// </summary>
    /// <param name="dbFactory">短生命周期 context 工厂（平台已注册为单例）。</param>
    /// <param name="grouping">分组维度。</param>
    /// <param name="fromUtc">下界（含），null 表示不设下界。</param>
    /// <param name="toUtcExclusive">上界（不含），null 表示不设上界。</param>
    /// <param name="limit">最多返回多少行（&gt; 0）。</param>
    /// <param name="ct">取消令牌。</param>
    public static async Task<Result> ExecuteAsync(
        IDbContextFactory<PlatformDbContext> dbFactory,
        Grouping grouping,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtcExclusive,
        int limit,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        var spec = BuildSpec(grouping);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(ct);

        // 多取一行用来判断「还有更多」，与 list_dir 的 Truncated 探测同一手法。
        var rows = new List<UsageSummaryRowDto>(Math.Min(limit + 1, 64));
        await using (var cmd = CreateCommand(
            connection,
            BuildAggregateSql(spec, fromUtc.HasValue, toUtcExclusive.HasValue, limit + 1),
            fromUtc,
            toUtcExclusive))
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                rows.Add(ReadRow(reader));
        }

        var truncated = rows.Count > limit;
        if (truncated)
            rows.RemoveRange(limit, rows.Count - limit);

        // 未截断时分组总数就是返回行数，不必再扫一遍。
        var total = truncated
            ? await CountGroupsAsync(connection, spec, fromUtc, toUtcExclusive, ct)
            : rows.Count;

        return new Result(rows, total, truncated);
    }

    /// <summary>聚合主查询：一条 SELECT，过滤 + 分组 + 汇总全在 SQL 侧。</summary>
    private static string BuildAggregateSql(
        GroupSpec spec,
        bool hasFrom,
        bool hasTo,
        int rowLimit)
    {
        var sb = new StringBuilder();
        sb.AppendLine("SELECT");
        sb.AppendLine($"    {spec.KeyExpression} AS \"group_key\",");
        sb.AppendLine($"    {spec.ProviderExpression} AS \"provider_id\",");
        sb.AppendLine($"    {spec.ModelExpression} AS \"model_id\",");
        sb.AppendLine("    COUNT(*) AS \"request_count\",");
        sb.AppendLine("    COALESCE(SUM(PromptTokens), 0) AS \"prompt_tokens\",");
        sb.AppendLine("    COALESCE(SUM(CompletionTokens), 0) AS \"completion_tokens\",");
        sb.AppendLine("    COALESCE(SUM(TotalTokens), 0) AS \"total_tokens\",");
        sb.AppendLine("    COALESCE(SUM(CacheHitTokens), 0) AS \"cache_hit_tokens\",");
        sb.AppendLine("    COALESCE(SUM(CacheMissTokens), 0) AS \"cache_miss_tokens\",");
        sb.AppendLine("    COALESCE(SUM(CacheEligibleTokens), 0) AS \"cache_eligible_tokens\",");
        sb.AppendLine("    COALESCE(SUM(InputCost), 0) AS \"input_cost\",");
        sb.AppendLine("    COALESCE(SUM(OutputCost), 0) AS \"output_cost\",");
        sb.AppendLine("    COALESCE(SUM(CacheHitCost), 0) AS \"cache_hit_cost\",");
        sb.AppendLine("    COALESCE(SUM(TotalCost), 0) AS \"total_cost\",");
        // 成本「登记缺失」的判据：这条请求有 token 却没有任何成本。零 token 的请求不算缺失。
        sb.AppendLine("    COALESCE(SUM(CASE WHEN TotalCost = 0 AND TotalTokens > 0 THEN 1 ELSE 0 END), 0) AS \"unknown_cost_request_count\"");
        sb.AppendLine("FROM TokenUsageEvents");
        AppendWhereClause(sb, hasFrom, hasTo);
        sb.AppendLine($"GROUP BY {spec.GroupByExpressions}");
        sb.AppendLine($"ORDER BY {spec.OrderByClause}");
        // rowLimit 是服务端常量（DefaultRowLimit + 1），不是用户输入；SQLite 的 LIMIT 不接受参数绑定以外的表达式。
        sb.AppendLine(CultureInfo.InvariantCulture, $"LIMIT {rowLimit}");
        return sb.ToString();
    }

    /// <summary>截断时才算分组总数：包一层子查询，仍是 SQL 侧完成。</summary>
    private static async Task<long> CountGroupsAsync(
        DbConnection connection,
        GroupSpec spec,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtcExclusive,
        CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("SELECT COUNT(*) FROM (");
        sb.AppendLine("    SELECT 1 FROM TokenUsageEvents");
        AppendWhereClause(sb, fromUtc.HasValue, toUtcExclusive.HasValue);
        sb.AppendLine($"    GROUP BY {spec.GroupByExpressions}");
        sb.Append(')');

        await using var cmd = CreateCommand(connection, sb.ToString(), fromUtc, toUtcExclusive);
        var raw = await cmd.ExecuteScalarAsync(ct);
        return raw is null or DBNull ? 0 : Convert.ToInt64(raw, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 时间过滤：把 TEXT 列经 <c>strftime()</c> 归一化成固定宽度的 <b>UTC</b> 文本
    /// （<c>yyyy-MM-dd HH:mm:ss</c>）再做逐字符比较。
    /// <para>
    /// 为什么不是直接比较原始 TEXT：列里的表示带偏移且小数秒尾零被裁掉
    /// （实测同一列既有 32 字符也有 33 字符），逐字符比较在整秒边界上会错。
    /// </para>
    /// <para>
    /// 为什么不用 <c>julianday()</c>：它把时间折算成浮点儒略日，比较变成浮点比较，
    /// 在整秒/整日边界上引入舍入（与列里的精确文本表示不是一一映射）。
    /// 归一化后的固定宽度文本比较是精确的字典序，没有舍入。
    /// </para>
    /// <para>
    /// 分辨率到秒：入参本身是秒级（ISO 日期/日期时间），小数秒一律截断；
    /// 代价是放弃 <c>IX_TokenUsageEvents_OccurredAtUtc</c> 索引走全表扫描——
    /// 实测 5.8 万行全表 SUM 约 100 ms，聚合本来就要读全量，可接受。
    /// </para>
    /// </summary>
    private static void AppendWhereClause(StringBuilder sb, bool hasFrom, bool hasTo)
    {
        if (!hasFrom && !hasTo)
            return;

        sb.Append("WHERE ");
        if (hasFrom)
            sb.Append("strftime('%Y-%m-%d %H:%M:%S', CreatedAtUtc) >= @fromUtc ");
        if (hasFrom && hasTo)
            sb.Append("AND ");
        if (hasTo)
            sb.Append("strftime('%Y-%m-%d %H:%M:%S', CreatedAtUtc) < @toUtc ");
        sb.AppendLine();
    }

    private static DbCommand CreateCommand(
        DbConnection connection,
        string sql,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtcExclusive)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        // 生产库实测全表聚合约 100 ms；给足余量但不无限等待（挂住的管理端请求比报错更糟）。
        cmd.CommandTimeout = 120;

        if (fromUtc.HasValue)
            AddBoundaryParameter(cmd, "@fromUtc", fromUtc.Value);
        if (toUtcExclusive.HasValue)
            AddBoundaryParameter(cmd, "@toUtc", toUtcExclusive.Value);

        return cmd;
    }

    /// <summary>
    /// 传字符串而不是 <see cref="DateTimeOffset"/>：<c>julianday()</c> 只认 ISO-8601 文本，
    /// 且这样与列里保存的格式（yyyy-MM-dd HH:mm:ss）逐字一致，不依赖提供程序的类型协商。
    /// </summary>
    private static void AddBoundaryParameter(DbCommand cmd, string name, DateTimeOffset value)
    {
        var parameter = cmd.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = DbType.String;
        // 固定宽度、无小数秒：与 WHERE 里 strftime('%Y-%m-%d %H:%M:%S', ...) 的输出逐字同形。
        parameter.Value = value
            .ToUniversalTime()
            .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        cmd.Parameters.Add(parameter);
    }

    /// <summary>
    /// 读取一行聚合结果（列序由 <see cref="BuildAggregateSql"/> 固定）。
    /// 成本列在 SQLite 里是 REAL（列声明 decimal(18,10) 具有 NUMERIC 亲和性），
    /// 求和后按 6 位小数收敛，与既有 StatsApiController 的口径一致。
    /// </summary>
    private static UsageSummaryRowDto ReadRow(DbDataReader reader)
    {
        var key = reader.IsDBNull(0) ? "unknown" : reader.GetString(0);
        var providerId = reader.IsDBNull(1) ? null : reader.GetString(1);
        var modelId = reader.IsDBNull(2) ? null : reader.GetString(2);
        var requestCount = reader.GetInt64(3);
        var promptTokens = reader.GetInt64(4);
        var completionTokens = reader.GetInt64(5);
        var totalTokens = reader.GetInt64(6);
        var cacheHitTokens = reader.GetInt64(7);
        var cacheMissTokens = reader.GetInt64(8);
        var cacheEligibleTokens = reader.GetInt64(9);
        var inputCost = ReadCost(reader, 10);
        var outputCost = ReadCost(reader, 11);
        var cacheHitCost = ReadCost(reader, 12);
        var totalCost = ReadCost(reader, 13);
        var unknownCostRequestCount = reader.GetInt64(14);

        // 加权命中率：分母为 0 时是「算不出」，返回 null 而不是 0——
        // 0 表示「命中率为 0%」，null 表示「没有可算的样本」，两者在报表上不能混。
        double? cacheHitRate = cacheEligibleTokens > 0
            ? Math.Round((double)cacheHitTokens / cacheEligibleTokens, 6, MidpointRounding.AwayFromZero)
            : null;

        // 有 token 却没有任何成本 ⇒ 登记缺失，不是免费。
        var costKnown = !(totalCost == 0m && totalTokens > 0);

        return new UsageSummaryRowDto(
            Key: key,
            ProviderId: providerId,
            ModelId: modelId,
            RequestCount: requestCount,
            PromptTokens: promptTokens,
            CompletionTokens: completionTokens,
            TotalTokens: totalTokens,
            CacheHitTokens: cacheHitTokens,
            CacheMissTokens: cacheMissTokens,
            CacheHitRate: cacheHitRate,
            InputCost: inputCost,
            OutputCost: outputCost,
            CacheHitCost: cacheHitCost,
            TotalCost: totalCost,
            CostKnown: costKnown,
            UnknownCostRequestCount: unknownCostRequestCount);
    }

    /// <summary>
    /// 成本列读取：SQLite 的 SUM 结果可能是 REAL（正常）或 INTEGER（全零时），
    /// 统一转 double 再收敛到 decimal，避免 GetDouble/GetDecimal 直接抛。
    /// </summary>
    private static decimal ReadCost(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return 0m;

        var raw = reader.GetValue(ordinal);
        var value = raw switch
        {
            double d => d,
            float f => f,
            long l => l,
            int i => i,
            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => Convert.ToDouble(raw, CultureInfo.InvariantCulture),
        };

        return Math.Round((decimal)value, 6, MidpointRounding.AwayFromZero);
    }
}
