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
        Store = new SqliteCodeIndexStore(Path.Combine(root, "db", "code-index.db"));
    }

    public string Root { get; }

    public SqliteCodeIndexStore Store { get; }

    public static CodeIndexFixture Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "pudding-code-index-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new CodeIndexFixture(root);
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
            Directory.Delete(Root, recursive: true);
    }
}
