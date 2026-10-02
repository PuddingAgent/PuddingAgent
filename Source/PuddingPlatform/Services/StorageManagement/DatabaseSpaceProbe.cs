using Microsoft.Data.Sqlite;

namespace PuddingPlatform.Services.StorageManagement;

/// <summary>单张表的占用。</summary>
/// <param name="Table">B 树名（表名**或索引名** —— dbstat 按 B 树逐条列出）。</param>
/// <param name="Bytes">该 B 树页占用字节数。</param>
/// <param name="Pages">页数。</param>
/// <param name="OwnerTable">
/// 该 B 树**归属的表**（经 <c>sqlite_master.tbl_name</c> 解析）：索引占用应计入其所属表的数据类，
/// 否则「索引特别多的表」会被明显低估。解析不到时为 <c>null</c>。
/// </param>
public sealed record DatabaseTableSpace(string Table, long Bytes, long Pages, string? OwnerTable = null);

/// <summary>数据库文件占用报告。</summary>
/// <param name="DatabaseFile">数据库主文件绝对路径。</param>
/// <param name="FileBytes">主文件 + WAL + SHM 的字节数（用户看到的"这个库占多少"就是这个数）。</param>
/// <param name="PageSize">SQLite 页大小。</param>
/// <param name="PageCount">SQLite 页数。</param>
/// <param name="PerTableAvailable">是否拿到了**按表**明细（依赖 SQLite 的 dbstat 虚表）。</param>
/// <param name="Tables">按占用降序的表明细；<paramref name="PerTableAvailable"/> 为 false 时为空。</param>
public sealed record DatabaseSpaceReport(
    string DatabaseFile,
    long FileBytes,
    long PageSize,
    long PageCount,
    bool PerTableAvailable,
    IReadOnlyList<DatabaseTableSpace> Tables)
{
    /// <summary>按表明细的字节合计（<paramref name="PerTableAvailable"/> 为 false 时为 0）。</summary>
    public long TablesBytes => Tables.Sum(table => table.Bytes);
}

/// <summary>
/// 数据库**按表占用**探针（ADR-076 存储管理页"显示不同数据的占比"的数据源）。
///
/// <para>
/// 实现选择：优先用 SQLite 的 <c>dbstat</c> 虚表 —— 它按 B 树页统计，**毫秒级**、不扫行、
/// 不干扰运行中的 Core。SQLite 的 dbstat 是编译期选项，不是所有构型都有，因此：
/// 拿不到时就**如实**把 <see cref="DatabaseSpaceReport.PerTableAvailable"/> 置为 false 并只回文件级总量
/// （**不猜**每个表多少 —— 猜出来的占比会让用户按错误依据清理）。
/// </para>
/// <para>
/// 只读打开（<c>Mode=ReadOnly</c>）：本探针**绝不**创建文件、绝不改库、绝不给运行中的 Core 加锁写。
/// </para>
/// <para>
/// 文件级总量统计**主文件 + <c>-wal</c> + <c>-shm</c>**：Pudding 的平台库在多写场景下 WAL 可以很大，
/// 只报主文件会让用户看到的总量明显偏小。
/// </para>
/// </summary>
public sealed class DatabaseSpaceProbe
{
    /// <summary>
    /// 测量一个数据库文件。
    /// </summary>
    /// <param name="databasePath">数据库主文件绝对路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>报告；文件不存在时返回 <c>null</c>（调用方据此显示"库不存在"，而不是报 0）。</returns>
    public async Task<DatabaseSpaceReport?> MeasureAsync(
        string databasePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        if (!File.Exists(databasePath))
        {
            return null;
        }

        var fileBytes = SidelongFileBytes(databasePath);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var pageSize = await ScalarAsync(connection, "PRAGMA page_size;", cancellationToken).ConfigureAwait(false);
        var pageCount = await ScalarAsync(connection, "PRAGMA page_count;", cancellationToken).ConfigureAwait(false);

        var (perTableAvailable, tables) = await TryReadPerTableAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        return new DatabaseSpaceReport(
            DatabaseFile: Path.GetFullPath(databasePath),
            FileBytes: fileBytes,
            PageSize: pageSize,
            PageCount: pageCount,
            PerTableAvailable: perTableAvailable,
            Tables: tables);
    }

    /// <summary>
    /// 按表明细。dbstat 不可用时返回 <c>(false, [])</c> —— **不抛、不降级成猜测**。
    /// </summary>
    private static async Task<(bool Available, IReadOnlyList<DatabaseTableSpace> Tables)> TryReadPerTableAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        try
        {
            // 先用 sqlite_master 建「B 树名 → 所属表」映射：对表而言 tbl_name == name，
            // 对索引而言 tbl_name 就是它服务的表 —— 于是索引占用能正确归到表。
            var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            await using (var mapCommand = connection.CreateCommand())
            {
                mapCommand.CommandText = "SELECT name, tbl_name FROM sqlite_master WHERE name IS NOT NULL;";
                await using var mapReader = await mapCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await mapReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (!mapReader.IsDBNull(0) && !mapReader.IsDBNull(1))
                    {
                        owners[mapReader.GetString(0)] = mapReader.GetString(1);
                    }
                }
            }

            var tables = new List<DatabaseTableSpace>();
            await using var command = connection.CreateCommand();
            // dbstat 逐条 B 树列出 (name, pgsize)：一张表一行（索引各自成行）。
            command.CommandText = "SELECT name, SUM(pgsize), COUNT(*) FROM dbstat GROUP BY name;";

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var name = reader.IsDBNull(0) ? "(unknown)" : reader.GetString(0);
                var bytes = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
                var pages = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
                var owner = owners.TryGetValue(name, out var ownerTable) ? ownerTable : null;
                tables.Add(new DatabaseTableSpace(name, bytes, pages, owner));
            }

            tables.Sort(static (left, right) => right.Bytes.CompareTo(left.Bytes));
            return (true, tables);
        }
        catch (SqliteException)
        {
            // 该 SQLite 构型没有 dbstat 虚表（编译期选项）⇒ 如实报告"拿不到按表明细"。
            return (false, []);
        }
    }

    /// <summary>主文件 + WAL + SHM 的字节合计。</summary>
    private static long SidelongFileBytes(string databasePath)
    {
        long total = 0;
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var info = new FileInfo(databasePath + suffix);
            if (info.Exists)
            {
                total += info.Length;
            }
        }

        return total;
    }

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
}
