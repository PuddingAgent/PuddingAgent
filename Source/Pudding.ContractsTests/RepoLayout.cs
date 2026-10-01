namespace Pudding.ContractsTests;

/// <summary>测试工程的仓库根定位：从输出目录向上找到解决方案文件。</summary>
internal static class RepoLayout
{
    public static string Root { get; } = FindRoot();

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PuddingAgentNetwork.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException(
            $"Repository root (PuddingAgentNetwork.slnx) not found above {AppContext.BaseDirectory}.");
    }
}
