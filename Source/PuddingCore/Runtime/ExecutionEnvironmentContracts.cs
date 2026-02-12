namespace PuddingCode.Runtime;

/// <summary>
/// 执行环境信息提供者。
/// 描述 Agent 当前运行环境的元数据，用于注入到上下文管道。
/// </summary>
public interface IExecutionEnvironmentProvider
{
    /// <summary>操作系统描述（如 "Microsoft Windows 10.0.22631"）。</summary>
    string OsDescription { get; }

    /// <summary>操作系统架构（如 "X64"、"Arm64"）。</summary>
    string OsArchitecture { get; }

    /// <summary>.NET 运行时版本号。</summary>
    string RuntimeVersion { get; }

    /// <summary>App 基目录。</summary>
    string AppBaseDirectory { get; }

    /// <summary>Agent 工作空间根目录（WorkspaceId → WorkspaceRoot 映射）。</summary>
    string? GetWorkspaceRoot(string workspaceId);

    /// <summary>路径分隔符，用于 Agent 文件操作提示。</summary>
    string PathSeparator { get; }

    /// <summary>当前版本固定为宿主执行，返回 false。</summary>
    bool IsContainer { get; }

    /// <summary>默认 shell 名称（如 "pwsh"、"bash"）。</summary>
    string DefaultShell { get; }

    /// <summary>环境指纹，用于缓存键区分。</summary>
    string EnvironmentFingerprint { get; }
}
