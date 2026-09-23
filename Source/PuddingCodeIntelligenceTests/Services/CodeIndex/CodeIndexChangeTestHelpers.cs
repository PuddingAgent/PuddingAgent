using PuddingCodeIntelligence.Contracts;
using PuddingCodeIntelligence.Services.CodeIndex;

namespace PuddingCodeIntelligenceTests.Services.CodeIndex;

/// <summary>Throwaway directory under the temp path. Never touches the repository tree.</summary>
internal sealed class TestDirectory : IDisposable
{
    public TestDirectory()
    {
        Root = Path.Combine(Path.GetTempPath(), "pudding-u3a-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    /// <summary>Combines a workspace-relative path (either separator) with the temp root.</summary>
    public string Combine(string relativePath) =>
        Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    public void Dispose()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(Root))
                    Directory.Delete(Root, recursive: true);

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(50);
            }
        }
    }
}

/// <summary>Manually advanced clock so debounce tests never sleep on real windows.</summary>
internal sealed class MutableTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow;

    public MutableTimeProvider(DateTimeOffset? utcNow = null) => _utcNow = utcNow ?? DateTimeOffset.UnixEpoch;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);

    public void Set(DateTimeOffset utcNow) => _utcNow = utcNow;
}

/// <summary>Shared construction helpers for the change-capture tests.</summary>
internal static class CodeIndexChangeTestFactory
{
    public const string WorkspaceId = "workspace-u3a";
    public const string ScopeId = "scope-u3a";

    public static CodeIndexScope CreateScope(string rootPath) =>
        new(WorkspaceId, ScopeId, rootPath, ScopeState.Active, ScopeSource.Manual);

    public static CodeIndexScopeState CreateState() => new(WorkspaceId, ScopeId);

    public static string Combine(string rootPath, string relativePath) =>
        Path.Combine(rootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));

    public static IndexChange CreateChange(
        string rootPath,
        string relativePath,
        IndexChangeKind kind,
        long sequence,
        string? oldRelativePath = null,
        DateTimeOffset? observedAtUtc = null) =>
        new(
            WorkspaceId,
            ScopeId,
            Combine(rootPath, relativePath),
            kind,
            oldRelativePath is null ? null : Combine(rootPath, oldRelativePath),
            IsDirectory: false,
            sequence,
            observedAtUtc ?? DateTimeOffset.UnixEpoch);
}
