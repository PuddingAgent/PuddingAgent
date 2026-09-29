using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using PuddingPlatform.Data;
using PuddingPlatform.Services.Execution;
using System.Text.Json;

// Deliberately does not build/start Host: no workers, schedules, connectors or model calls.
if (args.Length != 1) throw new ArgumentException("Pass the isolated platform database snapshot path.");
var path = Path.GetFullPath(args[0]);
var allowedRoot = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "temp", "test-out")) + Path.DirectorySeparatorChar;
if (!path.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
    throw new InvalidOperationException("Schema probe accepts only existing snapshots under repository temp/test-out.");
await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
await connection.OpenAsync();
await using var db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlite(connection).Options);
await ExecutionRunSchemaBootstrapper.EnsureCreatedAsync(db);
await ExecutionRunSchemaBootstrapper.EnsureCreatedAsync(db);
var failures = new List<string>();
var checkedTables = 0;
static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
foreach (var group in db.Model.GetEntityTypes().Where(e => e.GetTableName() is not null).GroupBy(e => e.GetTableName()!))
{
    var mappedColumns = group.SelectMany(e => e.GetProperties().Select(p => p.GetColumnName(StoreObjectIdentifier.Table(group.Key, e.GetSchema()))))
        .Where(c => c is not null).Select(c => c!).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var columns = mappedColumns.Select(c => "snapshot." + Quote(c));
    await using var command = connection.CreateCommand();
    command.CommandText = $"SELECT {string.Join(',', columns)} FROM {Quote(group.Key)} AS snapshot LIMIT 1";
    try { await using var reader = await command.ExecuteReaderAsync(); await reader.ReadAsync(); checkedTables++; }
    catch (SqliteException ex) { failures.Add($"{group.Key}: {ex.SqliteErrorCode}: {ex.Message}"); }
    await using var schema = connection.CreateCommand();
    schema.CommandText = $"PRAGMA table_info({Quote(group.Key)})";
    await using var schemaReader = await schema.ExecuteReaderAsync();
    while (await schemaReader.ReadAsync())
    {
        var name = schemaReader.GetString(1);
        if (!mappedColumns.Contains(name) && schemaReader.GetInt32(3) != 0 && schemaReader.IsDBNull(4) && schemaReader.GetInt32(5) == 0)
            failures.Add($"{group.Key}.{name}: extra NOT NULL column has no default; old-model inserts need a migration");
    }
}
Console.WriteLine(JsonSerializer.Serialize(new { checkedTables, failures, snapshotOnly = true, workersStarted = false }));
return failures.Count == 0 ? 0 : 1;
