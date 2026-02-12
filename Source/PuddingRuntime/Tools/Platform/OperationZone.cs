using PuddingCode.Configuration;

namespace PuddingRuntime.Services.Tools;

/// <summary>文件/命令操作的安全区域分级。</summary>
public enum OperationZone
{
    /// <summary>工作区内操作 — 完全透明，无需审批。</summary>
    Workspace,
    /// <summary>Agent 私有目录操作 — 需提供 reason。</summary>
    AgentPrivate,
    /// <summary>工作区外操作 — 需要显式审批。</summary>
    External,
}

/// <summary>基于路径或命令判断操作安全区域。</summary>
public static class OperationZoneClassifier
{
    /// <summary>判断给定路径属于哪个安全区域。</summary>
    public static OperationZone ClassifyPath(
        string path,
        PuddingDataPaths dataPaths,
        string workspaceId,
        string agentInstanceId)
    {
        var fullPath = NormalizePath(path);

        var workspaceRoot = Path.Combine(dataPaths.WorkspacesRoot, workspaceId);
        if (IsInsideDirectory(fullPath, workspaceRoot))
            return OperationZone.Workspace;

        var agentRoot = dataPaths.AgentInstanceRoot(agentInstanceId);
        if (IsInsideDirectory(fullPath, agentRoot))
            return OperationZone.AgentPrivate;

        return OperationZone.External;
    }

    /// <summary>判断 shell 命令的安全区域。工作区内安全命令走 Workspace，否则走 External。</summary>
    public static OperationZone ClassifyShellCommand(
        string command,
        string? workingDirectory,
        PuddingDataPaths dataPaths,
        string workspaceId,
        string agentInstanceId)
    {
        // 先按工作目录判断
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            var zone = ClassifyPath(workingDirectory, dataPaths, workspaceId, agentInstanceId);
            if (zone == OperationZone.Workspace)
            {
                // 在 workspace 内执行且是安全命令 → 放行
                if (IsSafeCommand(command))
                    return OperationZone.Workspace;
            }
            if (zone == OperationZone.AgentPrivate)
                return OperationZone.AgentPrivate;
            return OperationZone.External;
        }

        // 没有 working_directory，默认视为外部
        return OperationZone.External;
    }

    /// <summary>判断命令是否属于"安全"类别（只读或工作区构建类）。</summary>
    public static bool IsSafeCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return false;

        var firstWord = command.TrimStart().Split(' ', 2)[0];
        var exe = Path.GetFileNameWithoutExtension(firstWord);

        // 这些命令在工作区内执行是安全的（不涉及系统修改、网络攻击面）
        return exe.Equals("git", StringComparison.OrdinalIgnoreCase)
            || exe.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            || exe.Equals("ls", StringComparison.OrdinalIgnoreCase)
            || exe.Equals("dir", StringComparison.OrdinalIgnoreCase)
            || exe.Equals("cat", StringComparison.OrdinalIgnoreCase)
            || exe.Equals("type", StringComparison.OrdinalIgnoreCase)
            || exe.Equals("echo", StringComparison.OrdinalIgnoreCase)
            || exe.Equals("pwd", StringComparison.OrdinalIgnoreCase)
            || exe.Equals("cd", StringComparison.OrdinalIgnoreCase)
            || exe.Equals("mkdir", StringComparison.OrdinalIgnoreCase)
            || exe.Equals("rmdir", StringComparison.OrdinalIgnoreCase)
            || exe.Equals("rm", StringComparison.OrdinalIgnoreCase)
            || exe.Equals("cp", StringComparison.OrdinalIgnoreCase)
            || exe.Equals("copy", StringComparison.OrdinalIgnoreCase)
            || exe.Equals("mv", StringComparison.OrdinalIgnoreCase)
            || exe.Equals("move", StringComparison.OrdinalIgnoreCase)
            || exe.Equals("find", StringComparison.OrdinalIgnoreCase)
            || exe.Equals("grep", StringComparison.OrdinalIgnoreCase)
            || exe.Equals("which", StringComparison.OrdinalIgnoreCase)
            || exe.Equals("where", StringComparison.OrdinalIgnoreCase)
            || exe.Equals("python", StringComparison.OrdinalIgnoreCase)
            || exe.Equals("python3", StringComparison.OrdinalIgnoreCase)
            || exe.Equals("node", StringComparison.OrdinalIgnoreCase)
            || exe.Equals("npm", StringComparison.OrdinalIgnoreCase)
            || exe.Equals("pnpm", StringComparison.OrdinalIgnoreCase)
            || exe.Equals("yarn", StringComparison.OrdinalIgnoreCase)
            || exe.Equals("npx", StringComparison.OrdinalIgnoreCase)
            || exe.Equals("docker", StringComparison.OrdinalIgnoreCase)
            || exe.Equals("curl", StringComparison.OrdinalIgnoreCase)
            || exe.Equals("wget", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path;
        }
    }

    private static bool IsInsideDirectory(string fullPath, string root)
    {
        root = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullPath.Equals(root, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
