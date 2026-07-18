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

    private static string FindJsonSourcePath()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(
                current.FullName,
                "Source",
                "PuddingAgent",
                "Config",
                "agent-avatars.json");
            if (File.Exists(candidate))
                return candidate;

            current = current.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate Source/PuddingAgent/Config/agent-avatars.json");
    }
}
