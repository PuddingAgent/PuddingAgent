namespace PuddingCode.Configuration;

public sealed record PuddingDataPaths
{
    public required string DataRoot { get; init; }

    public string ConfigRoot => Path.Combine(DataRoot, "config");
    public string AgentTemplatesRoot => Path.Combine(DataRoot, "agent-templates");
    public string AgentInstancesRoot => Path.Combine(DataRoot, "agents");
    public string WorkspacesRoot => Path.Combine(DataRoot, "workspaces");
    public string LogsRoot => Path.Combine(DataRoot, "logs");
    public string SystemLogsRoot => Path.Combine(LogsRoot, "system");
    public string DiagnosticsLogsRoot => Path.Combine(LogsRoot, "diagnostics");
    public string SessionLogsRoot => Path.Combine(LogsRoot, "sessions");
    public string RuntimeRoot => Path.Combine(DataRoot, "runtime");
    public string RuntimeTracesRoot => Path.Combine(RuntimeRoot, "traces");
    public string EventQueueRoot => Path.Combine(RuntimeRoot, "event-queue");
    public string MemoryRoot => Path.Combine(DataRoot, "memory");
    public string DatabasesRoot => Path.Combine(DataRoot, "databases");
    public string BackupsRoot => Path.Combine(DataRoot, "backups");
    public string TempRoot => Path.Combine(DataRoot, "tmp");
    public string PluginsRoot => Path.Combine(DataRoot, "plugins");

    public static PuddingDataPaths FromRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("Data root cannot be empty.", nameof(root));

        return new PuddingDataPaths
        {
            DataRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        };
    }

    public string SystemConfigFile(string fileName) =>
        Path.Combine(ConfigRoot, fileName);

    public string AgentTemplateRoot(string templateId) =>
        Path.Combine(AgentTemplatesRoot, templateId);

    public string AgentTemplateFile(string templateId, string fileName) =>
        Path.Combine(AgentTemplateRoot(templateId), fileName);

    public string AgentInstanceRoot(string agentInstanceId) =>
        Path.Combine(AgentInstancesRoot, agentInstanceId);

    public string AgentInstanceConfigRoot(string agentInstanceId) =>
        Path.Combine(AgentInstanceRoot(agentInstanceId), "config");

    public string AgentInstanceConfigFile(string agentInstanceId, string fileName) =>
        Path.Combine(AgentInstanceConfigRoot(agentInstanceId), fileName);

    public string AgentInstanceWorkspaceRoot(string agentInstanceId) =>
        Path.Combine(AgentInstanceRoot(agentInstanceId), "workspace");

    public string AgentInstanceStateRoot(string agentInstanceId) =>
        Path.Combine(AgentInstanceRoot(agentInstanceId), "state");

    public string AgentInstanceLogsRoot(string agentInstanceId) =>
        Path.Combine(AgentInstanceRoot(agentInstanceId), "logs");

    public string AgentInstanceMessageLogsRoot(string agentInstanceId) =>
        Path.Combine(AgentInstanceLogsRoot(agentInstanceId), "messages");

    public string AgentInstanceMessageLogDayRoot(string agentInstanceId, string day) =>
        Path.Combine(AgentInstanceMessageLogsRoot(agentInstanceId), day);

    public string AgentInstanceMessageLogJsonlFile(string agentInstanceId, string day, string sessionId) =>
        Path.Combine(AgentInstanceMessageLogDayRoot(agentInstanceId, day), $"{sessionId}.jsonl");

    public string AgentInstanceMessageLogMarkdownFile(string agentInstanceId, string day, string sessionId) =>
        Path.Combine(AgentInstanceMessageLogDayRoot(agentInstanceId, day), $"{sessionId}.md");

    public string AgentInstanceRawLogsRoot(string agentInstanceId) =>
        Path.Combine(AgentInstanceLogsRoot(agentInstanceId), "raw");

    public string AgentInstanceRawLogDayRoot(string agentInstanceId, string day) =>
        Path.Combine(AgentInstanceRawLogsRoot(agentInstanceId), day);

    public string AgentInstanceRawLogJsonlFile(string agentInstanceId, string day, string sessionId) =>
        Path.Combine(AgentInstanceRawLogDayRoot(agentInstanceId, day), $"{sessionId}.jsonl");

    public string AgentInstanceMemoryRoot(string agentInstanceId) =>
        Path.Combine(AgentInstanceRoot(agentInstanceId), "memory");

    public string AgentInstanceDailySummaryRoot(string agentInstanceId) =>
        Path.Combine(AgentInstanceMemoryRoot(agentInstanceId), "daily");

    public string AgentInstanceContentSummaryFile(string agentInstanceId) =>
        Path.Combine(AgentInstanceMemoryRoot(agentInstanceId), "content.md");

    public string AgentInstanceMemoryIndexFile(string agentInstanceId) =>
        Path.Combine(AgentInstanceMemoryRoot(agentInstanceId), "index.json");

    /// <summary>
    /// 获取 Agent 的精选重要记忆文件路径。
    /// 文件：agents/{agentInstanceId}/memory/important_memory.md
    /// </summary>
    public string AgentInstanceImportantMemoryFile(string agentInstanceId) =>
        Path.Combine(AgentInstanceMemoryRoot(agentInstanceId), "important_memory.md");

    /// <summary>
    /// Session 压缩摘要根目录。
    /// 路径：agents/{agentInstanceId}/memory/session-summaries/
    /// </summary>
    public string AgentInstanceSessionSummaryRoot(string agentInstanceId) =>
        Path.Combine(AgentInstanceMemoryRoot(agentInstanceId), "session-summaries");

    /// <summary>
    /// 指定日期的 Session 摘要目录。
    /// 路径：agents/{agentInstanceId}/memory/session-summaries/{date}/
    /// </summary>
    public string AgentInstanceSessionSummaryDayRoot(string agentInstanceId, string date) =>
        Path.Combine(AgentInstanceSessionSummaryRoot(agentInstanceId), date);

    /// <summary>
    /// 单条 Session 摘要文件。
    /// 路径：agents/{agentInstanceId}/memory/session-summaries/{date}/{sequence}.summary.md
    /// </summary>
    public string AgentInstanceSessionSummaryFile(string agentInstanceId, string date, string sequence) =>
        Path.Combine(AgentInstanceSessionSummaryDayRoot(agentInstanceId, date), $"{sequence}.summary.md");

    public string WorkspaceRoot(string workspaceId) =>
        Path.Combine(WorkspacesRoot, workspaceId);

    public string WorkspaceAgentRoot(string workspaceId, string agentInstanceId) =>
        Path.Combine(WorkspaceRoot(workspaceId), "agents", agentInstanceId);

    public string WorkspaceAgentRefFile(string workspaceId, string agentInstanceId) =>
        Path.Combine(WorkspaceAgentRoot(workspaceId, agentInstanceId), "ref.json");

    // 子代理单次运行归档根
    public string SubAgentRunRoot(string workspaceId, string agentInstanceId, string runId) =>
        Path.Combine(WorkspaceAgentRoot(workspaceId, agentInstanceId), "runs", runId);

    // 子代理权限配置
    public string WorkspaceAgentPermissionsFile(string workspaceId, string agentInstanceId) =>
        Path.Combine(WorkspaceAgentRoot(workspaceId, agentInstanceId), "permissions.json");

    public string ComponentLogsRoot(string component) =>
        Path.Combine(LogsRoot, "components", component);

    public string ComponentLogFile(string component, string baseName) =>
        Path.Combine(ComponentLogsRoot(component), $"{baseName}-.log");

    public string TerminalLogsRoot => Path.Combine(LogsRoot, "components", "terminal");

    public string ErrorLogFile => Path.Combine(LogsRoot, "error", "pudding-error");

    public string SystemLogFile => Path.Combine(LogsRoot, "system", "pudding");
}
