using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace PuddingPlatform.Services;

/// <summary>
/// usage 账本写入的 SQLite 唯一约束判定（S01-A）。
/// 唯一索引是 source 幂等的最终保证：事务内检查负责常规路径，
/// 本判定负责兜住绕过检查窗口的并发重复插入。
/// </summary>
internal static class UsageWriteConflict
{
    private const int SqliteConstraintError = 19;
    private const int SqliteConstraintUnique = 2067;
    private const int SqliteConstraintPrimaryKey = 1555;

    public static bool IsUniqueSourceConflict(DbUpdateException exception)
    {
        if (exception.InnerException is not SqliteException sqliteException)
            return false;
        return sqliteException.SqliteErrorCode == SqliteConstraintError
            && sqliteException.SqliteExtendedErrorCode
                is SqliteConstraintUnique
                or SqliteConstraintPrimaryKey;
    }
}
