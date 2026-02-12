using System.Runtime.InteropServices;

namespace PuddingRuntime.Services;

/// <summary>
/// 启动环境信息：程序启动时一次性采集，注入到 Agent 系统提示词中。
/// 帮助 Agent 理解"我是谁、我在哪里、我有什么工具"。
/// </summary>
public sealed class StartupEnvironmentInfo
{
    public string OsDescription { get; }
    public string OsArchitecture { get; }
    public string RuntimeVersion { get; }
    public string AppBaseDirectory { get; }
    public string DataDirectory { get; }
    public string AgentsDirectory { get; }
    public string MemosDirectory { get; }
    public string LogsDirectory { get; }
    public string SessionsDirectory { get; }
    public string ConfDirectory { get; }
    public string UserHomeDirectory { get; }
    public DateTimeOffset StartedAt { get; }

    public StartupEnvironmentInfo()
    {
        OsDescription = RuntimeInformation.OSDescription;
        OsArchitecture = RuntimeInformation.OSArchitecture.ToString();
        RuntimeVersion = Environment.Version.ToString();
        AppBaseDirectory = AppContext.BaseDirectory;

        // 工作目录结构
        DataDirectory = Path.Combine(AppBaseDirectory, "data");
        AgentsDirectory = Path.Combine(DataDirectory, "agents");
        MemosDirectory = Path.Combine(DataDirectory, "memos");
        LogsDirectory = Path.Combine(DataDirectory, "logs");
        SessionsDirectory = Path.Combine(LogsDirectory, "sessions");
        ConfDirectory = Path.Combine(DataDirectory, "conf");

        UserHomeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        StartedAt = DateTimeOffset.UtcNow;

        // 确保目录存在
        Directory.CreateDirectory(AgentsDirectory);
        Directory.CreateDirectory(MemosDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(SessionsDirectory);
        Directory.CreateDirectory(ConfDirectory);
    }
}
