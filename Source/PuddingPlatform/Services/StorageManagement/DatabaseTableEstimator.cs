using System.Text;
using Microsoft.Data.Sqlite;

namespace PuddingPlatform.Services.StorageManagement;

/// <summary>单张表的占用估算。</summary>
/// <param name="Table">表名。</param>
/// <param name="RowCount">行数（**精确**：来自 <c>COUNT(*)</c>）。</param>
/// <param name="SampledRows">参与平均行长抽样的行数（0 表示空表）。</param>
/// <param name="Bytes">估算占用 = 平均行长 × 行数。</param>
public sealed record DatabaseTableEstimate(string Table, long RowCount, int SampledRows, long Bytes);

/// <summary>
/// **无 dbstat 时的按表占用估算器**（本仓库的 SQLite 没有 dbstat 虚表，2026-10-03 实测确认）。
///
/// <para>
/// 方法：每表取**精确行数**（<c>COUNT(*)</c>）＋对**有界样本**（默认 200 行）逐列求字节长度，
/// 相乘得到估算占用。行数是精确的，只有"平均行长"来自抽样 —— 因此整表的数字是估算，
/// 但**类与类之间的相对占比**仍然可用（同一把尺子量所有表）。
/// </para>
/// <para>
/// 已知偏差（必须知道，否则会误读数字）：
/// ① **索引占用不计入**（dbstat 才有按 B 树的明细）⇒ 索引特别多的表会被低估；
/// ② 样本取的是**前 N 行**，宽窄分布不均的表会有偏差；行数越少偏差越小；
/// ③ 空表返回 0（不是"未知"）。
/// </para>
/// <para>
/// 开销：每表一次 <c>COUNT(*)</c> 扫描 —— 在 3M 行级表上约几十到几百毫秒，因此**只适合显式动作**
/// （用户点"查看占用"），不适合放进轮询热路径。只读打开（<c>Mode=ReadOnly</c>），不加写锁、不建库。
/// </para>
/// </summary>
public sealed class DatabaseTableEstimator
{
    /// <summary>默认抽样行数：够稳定又不至于把大表读穿。</summary>
    public const int DefaultSampleRows = 200;

    /// <summary>估算一个数据库里的全部表。</summary>
    /// <param name="databasePath">数据库主文件路径。</param>
    /// <param name="sampleRows">每表抽样行数（&gt;0）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<IReadOnlyList<DatabaseTableEstimate>> EstimateAsync(
        string databasePath,
        int sampleRows = DefaultSampleRows,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRows, 1);

        if (!File.Exists(databasePath))
        {
            return [];
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var tables = new List<DatabaseTableEstimate>();
        foreach (var table in await ListTablesAsync(connection, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var rowCount = await ScalarAsync(connection, $"SELECT COUNT(*) FROM {Quote(table)};", cancellationToken)
                .ConfigureAwait(false);
            if (rowCount <= 0)
            {
                tables.Add(new DatabaseTableEstimate(table, 0, 0, 0));
                continue;
            }

            var (sampledRows, sampledBytes) = await SampleAsync(
                connection, table, sampleRows, cancellationToken).ConfigureAwait(false);
            var averageRowBytes = sampledRows == 0 ? 0 : sampledBytes / sampledRows;
            tables.Add(new DatabaseTableEstimate(table, rowCount, sampledRows, averageRowBytes * rowCount));
        }

        tables.Sort(static (left, right) => right.Bytes.CompareTo(left.Bytes));
        return tables;
    }

    /// <summary>把估算结果转换成占位符（<c>Pages = 0</c>：估算方法无法把页归给某张表）。</summary>
    /// <remarks>
    /// 存在的意义：让 Map 的输入形状与 dbstat 路径一致 —— 归并器因此不需要知道数据来源。
    /// <c>OwnerTable</c> 恒等于表名：本方法不做索引归属（索引占用未计入，见类文档的已知偏差 ①）。
    /// </remarks>
    public static IReadOnlyList<DatabaseTableSpace> ToTableSpaces(IReadOnlyList<DatabaseTableEstimate> estimates)
        => estimates
            .Select(estimate => new DatabaseTableSpace(
                estimate.Table, estimate.Bytes, Pages: 0, OwnerTable: estimate.Table))
            .ToArray();

    private static async Task<IReadOnlyList<string>> ListTablesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var tables = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!reader.IsDBNull(0))
            {
                tables.Add(reader.GetString(0));
            }
        }

        return tables;
    }

    /// <summary>抽样求"样本总字节数"：字符串按 UTF-8 字节、blob 按长度、数字固定 8 字节、NULL 计 1 字节。</summary>
    private static async Task<(int Rows, long Bytes)> SampleAsync(
        SqliteConnection connection,
        string table,
        int sampleRows,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {Quote(table)} LIMIT {sampleRows};";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var rows = 0;
        long bytes = 0;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows++;
            for (var i = 0; i < reader.FieldCount; i++)
            {
                bytes += reader.IsDBNull(i) ? 1 : FieldBytes(reader.GetValue(i));
            }
        }

        return (rows, bytes);
    }

    private static long FieldBytes(object value) => value switch
    {
        string text => Encoding.UTF8.GetByteCount(text),
        byte[] blob => blob.Length,
        _ => 8,
    };

    private static async Task<long> ScalarAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null || value is DBNull ? 0 : Convert.ToInt64(value);
    }

    /// <summary>标识符引用：表名来自 sqlite_master，仍需转义双引号以防名字里带引号。</summary>
    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";
}
