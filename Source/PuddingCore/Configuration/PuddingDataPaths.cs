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
}
