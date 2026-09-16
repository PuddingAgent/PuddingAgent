using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services.Todo;

/// <summary>
/// 设计 2026-09-16 §3（TD-1）：todo_lists / todo_items 两表的幂等 SQLite schema bootstrap。
/// <para>
/// 与 <see cref="Goals.GoalSchemaBootstrapper"/> 同风格：EF EnsureCreated 覆盖全新库，
/// 本 bootstrap 覆盖已有库（CREATE TABLE IF NOT EXISTS + 幂等索引）。列名与
/// <see cref="Data.Entities.TodoListEntity"/> / <see cref="Data.Entities.TodoItemEntity"/>
/// 的 [Column] 严格一致。状态存 wire 字符串、时间存 DateTimeOffset（TEXT）、列名 snake_case。
/// </para>
/// </summary>
public static class TodoSchemaBootstrapper
{
    private static readonly string[] Ddl =
    [
        // ── todo_lists（设计 2026-09-16 §3）─────────────────────
        """
        CREATE TABLE IF NOT EXISTS todo_lists (
            list_id         TEXT    NOT NULL,
            scope_kind      TEXT    NOT NULL,
            scope_id        TEXT    NOT NULL,
            title           TEXT,
            revision        INTEGER NOT NULL DEFAULT 0,
            created_at_utc  TEXT    NOT NULL,
            updated_at_utc  TEXT    NOT NULL,
            archived_at_utc TEXT,
            PRIMARY KEY (list_id)
        );
        """,
        // (scope_kind, scope_id) 唯一定位一个列表；todo_write 全量替换的目标。
        "CREATE UNIQUE INDEX IF NOT EXISTS UX_todo_lists_scope ON todo_lists(scope_kind, scope_id);",

        // ── todo_items（设计 2026-09-16 §3）─────────────────────
        """
        CREATE TABLE IF NOT EXISTS todo_items (
            item_id          TEXT    NOT NULL,
            list_id          TEXT    NOT NULL,
            slug             TEXT    NOT NULL,
            title            TEXT    NOT NULL,
            status           TEXT    NOT NULL DEFAULT 'pending',
            note             TEXT,
            evidence_ref     TEXT,
            blocked_reason   TEXT,
            order_index      INTEGER NOT NULL DEFAULT 0,
            started_at_utc   TEXT,
            completed_at_utc TEXT,
            PRIMARY KEY (item_id)
        );
        """,
        // slug 由写方提供、列表内唯一（全量替换 diff 的键）。
        "CREATE UNIQUE INDEX IF NOT EXISTS UX_todo_items_list_slug ON todo_items(list_id, slug);",
        "CREATE INDEX IF NOT EXISTS IX_todo_items_list_order ON todo_items(list_id, order_index);",
    ];

    public static async Task EnsureCreatedAsync(
        PlatformDbContext db,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        if (!db.Database.IsSqlite())
        {
            return;
        }

        foreach (var ddl in Ddl)
        {
            try
            {
                await db.Database.ExecuteSqlRawAsync(ddl, ct);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(
                    ex,
                    "[TodoSchema] SQLite schema bootstrap failed: {Ddl}",
                    ddl[..Math.Min(ddl.Length, 96)]);
                throw;
            }
        }
    }
}
