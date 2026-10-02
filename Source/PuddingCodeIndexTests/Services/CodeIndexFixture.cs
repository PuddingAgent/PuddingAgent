using PuddingCodeIndex.Storage;

namespace PuddingCodeIndexTests.Services;

/// <summary>
/// Component-local fixture: a throwaway temp root plus a real <see cref="SqliteCodeIndexStore"/>.
/// Self-contained on purpose — it uses no upper-layer type, so the index component's tests stay
/// inside the <c>PuddingCodeIndex</c> boundary (组件化交付规程 S2/S3).
/// </summary>
internal sealed class CodeIndexFixture : IDisposable
{
    private CodeIndexFixture(string root)
    {
        Root = root;
        DatabasePath = Path.Combine(root, "db", "code-index.db");
        Store = new SqliteCodeIndexStore(DatabasePath);
    }

    public string Root { get; }

    /// <summary>Path of the SQLite file behind <see cref="Store"/> (U3-G1 reads one raw column).</summary>
    public string DatabasePath { get; }

    public SqliteCodeIndexStore Store { get; }

    public static CodeIndexFixture Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "pudding-code-index-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new CodeIndexFixture(root);
    }

    public void Dispose()
    {
        // SQLite 的文件句柄（含 WAL）释放是异步的：直接删目录会随机撞上「文件被占用」，
        // 于是**测试**因为 teardown 异常而失败（实测 `CodeIndexMaintenanceServiceTests.
        // Stop_Returns_Within_The_Timeout...` 在并发跑全量时偶发）。这里重试几次；
        // 仍失败就留给系统清理 —— 一次清理竞争不该把门禁变成噪声。
        for (var attempt = 0; attempt < 6; attempt++)
        {
            if (!Directory.Exists(Root))
                return;

            try
            {
                Directory.Delete(Root, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(50 * (attempt + 1));
            }
        }
    }
}
