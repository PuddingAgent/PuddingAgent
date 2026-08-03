using Microsoft.Extensions.Logging.Abstractions;
using PuddingPlatform.Services;

namespace PuddingPlatformTests;

/// <summary>
/// 复制 agent-avatars.json 到测试输出目录的 Config/ 下，
/// 然后创建 AgentAvatarCatalog。
/// </summary>
public sealed class AvatarCatalogTestFixture : IDisposable
{
    private readonly string _originalJsonPath;

    public AgentAvatarCatalog Catalog { get; }

    public AvatarCatalogTestFixture()
    {
        // Ensure Config/agent-avatars.json exists in test output dir
        var targetDir = Path.Combine(AppContext.BaseDirectory, "Config");
        Directory.CreateDirectory(targetDir);

        _originalJsonPath = Path.Combine(targetDir, "agent-avatars.json");

        if (!File.Exists(_originalJsonPath))
        {
            // Copy from source project
            var source = FindJsonSourcePath();
            File.Copy(source, _originalJsonPath, overwrite: true);
        }

        Catalog = new AgentAvatarCatalog(NullLogger<AgentAvatarCatalog>.Instance);
    }

    public void Dispose()
    {
        try { File.Delete(_originalJsonPath); }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// The canonical source copy lives in Source/PuddingHost/Config;
    /// Source/PuddingAgent/Config is kept as a fallback for older layouts.
    /// </summary>
    private static readonly string[] CandidateRelativePaths =
    {
        Path.Combine("Source", "PuddingHost", "Config", "agent-avatars.json"),
        Path.Combine("Source", "PuddingAgent", "Config", "agent-avatars.json"),
    };

    private static string FindJsonSourcePath()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            foreach (var relative in CandidateRelativePaths)
            {
                var candidate = Path.Combine(current.FullName, relative);
                if (File.Exists(candidate))
                    return candidate;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate agent-avatars.json under Source/PuddingHost/Config or Source/PuddingAgent/Config");
    }
}
