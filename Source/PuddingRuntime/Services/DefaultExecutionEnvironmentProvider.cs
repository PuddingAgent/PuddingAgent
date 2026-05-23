using System.Runtime.InteropServices;
using PuddingCode.Configuration;
using PuddingCode.Runtime;

namespace PuddingRuntime.Services;

/// <summary>
/// 默认执行环境提供者。
/// 包装 <see cref="StartupEnvironmentInfo"/>，在宿主机直接运行时使用。
/// 未来远程 Sandbox/容器执行场景应替换为远程环境提供者。
/// </summary>
public sealed class DefaultExecutionEnvironmentProvider : IExecutionEnvironmentProvider
{
    private readonly StartupEnvironmentInfo _startupInfo;
    private readonly PuddingDataPaths _dataPaths;
    private readonly string _fingerprint;

    public DefaultExecutionEnvironmentProvider(StartupEnvironmentInfo startupInfo, PuddingDataPaths? dataPaths = null)
    {
        _startupInfo = startupInfo;
        _dataPaths = dataPaths ?? PuddingDataPaths.FromRoot(startupInfo.DataDirectory);

        // 指纹：OS + 架构 + AppBaseDirectory + 容器状态，足够区分不同主机环境
        _fingerprint = $"{startupInfo.OsDescription}|{startupInfo.OsArchitecture}|{startupInfo.AppBaseDirectory}|{startupInfo.IsDocker}";
    }

    public string OsDescription => _startupInfo.OsDescription;
    public string OsArchitecture => _startupInfo.OsArchitecture;
    public string RuntimeVersion => _startupInfo.RuntimeVersion;
    public string AppBaseDirectory => _startupInfo.AppBaseDirectory;
    public string PathSeparator => Path.DirectorySeparatorChar.ToString();
    public bool IsContainer => _startupInfo.IsDocker;
    public string DefaultShell => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "pwsh" : "bash";
    public string EnvironmentFingerprint => _fingerprint;

    public string? GetWorkspaceRoot(string workspaceId)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
            return null;
        return _dataPaths.WorkspaceRoot(workspaceId);
    }
}
