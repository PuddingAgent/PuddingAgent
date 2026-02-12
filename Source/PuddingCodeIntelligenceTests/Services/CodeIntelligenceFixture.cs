using PuddingCodeIntelligence.Services;
using PuddingCodeIntelligence.Storage;

namespace PuddingCodeIntelligenceTests.Services;

internal sealed class CodeIntelligenceFixture : IDisposable
{
    private CodeIntelligenceFixture(string root)
    {
        Root = root;
        Store = new SqliteCodeIndexStore(Path.Combine(root, "db", "code-index.db"));
    }

    public string Root { get; }

    public SqliteCodeIndexStore Store { get; }

    public CodeProjectRegistry CreateRegistry()
    {
        var resolver = new DefaultCodeWorkspaceResolver(Store);
        return new CodeProjectRegistry(Store, resolver);
    }

    public static CodeIntelligenceFixture Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "pudding-code-intelligence-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new CodeIntelligenceFixture(root);
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
            Directory.Delete(Root, recursive: true);
    }
}
