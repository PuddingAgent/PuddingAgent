using PuddingCode.Platform;

namespace PuddingAgent;

/// <summary>
/// 内置 Agent 模板注册表——提供首批默认模板。
/// </summary>
public static class BuiltInAgentTemplates
{
    public static readonly AgentTemplateDefinition WorkspaceServiceAgent = new()
    {
        TemplateId = "workspace-service-agent",
        Name = "Workspace Service Agent",
        Description = "通用服务 Agent，处理 Workspace 内的日常对话与任务。",
        TemplateType = AgentTemplateType.Service,
        Capability = new CapabilityPolicy
        {
            AllowFileWrite = false,
            AllowShellExecution = false,
            AllowNetworkAccess = false,
        },
        Runtime = new RuntimeProfile
        {
            MaxContextTokens = 8192,
            MaxTurnsPerSession = 100,
            SessionTimeout = TimeSpan.FromHours(1),
        },
        Memory = new MemoryPolicy
        {
            EnableSessionMemory = true,
            EnableWorkspaceMemory = true,
            AllowPublicSourceWrite = false,
        },
        SystemPrompt = "You are a helpful assistant in the Pudding Agent Network. Answer questions accurately and concisely.",
    };

    public static readonly AgentTemplateDefinition WorkspaceTaskAgent = new()
    {
        TemplateId = "workspace-task-agent",
        Name = "Workspace Task Agent",
        Description = "任务型 Agent，可执行文件操作和受限 Shell 命令。",
        TemplateType = AgentTemplateType.Task,
        Capability = new CapabilityPolicy
        {
            AllowFileWrite = true,
            AllowShellExecution = true,
            AllowNetworkAccess = false,
            AllowedToolNames = ["bash", "file_read", "file_write"],
        },
        Runtime = new RuntimeProfile
        {
            MaxContextTokens = 16384,
            MaxTurnsPerSession = 200,
            SessionTimeout = TimeSpan.FromHours(2),
        },
        Memory = new MemoryPolicy
        {
            EnableSessionMemory = true,
            EnableWorkspaceMemory = true,
            AllowPublicSourceWrite = false,
        },
        SystemPrompt = "You are a task-oriented agent. You can read/write files and run shell commands to accomplish tasks. Always ask for approval before destructive operations.",
    };

    public static readonly AgentTemplateDefinition CodeAgent = new()
    {
        TemplateId = "code-agent",
        Name = "Code Agent",
        Description = "代码执行型 Agent，支持通过 bash 工具在隔离容器内执行命令。",
        TemplateType = AgentTemplateType.Task,
        Capability = new CapabilityPolicy
        {
            AllowFileWrite = true,
            AllowShellExecution = true,
            AllowNetworkAccess = false,
            AllowedToolNames = ["bash", "file_read", "file_write"],
        },
        Runtime = new RuntimeProfile
        {
            MaxContextTokens = 16384,
            MaxTurnsPerSession = 200,
            SessionTimeout = TimeSpan.FromHours(2),
        },
        Memory = new MemoryPolicy
        {
            EnableSessionMemory = true,
            EnableWorkspaceMemory = true,
            AllowPublicSourceWrite = false,
        },
        SystemPrompt = "You are a coding agent. Use bash tool when needed to inspect files, run commands, and complete coding tasks safely in sandbox.",
    };

    public static readonly AgentTemplateDefinition WorkspaceAuditAgent = new()
    {
        TemplateId = "workspace-audit-agent",
        Name = "Workspace Audit Agent",
        Description = "审计 Agent，只读取结构化、脱敏、受限视图，可参与冻结/批准/拒绝/质询链路。",
        TemplateType = AgentTemplateType.Audit,
        Capability = new CapabilityPolicy
        {
            AllowFileWrite = false,
            AllowShellExecution = false,
            AllowNetworkAccess = false,
        },
        Runtime = new RuntimeProfile
        {
            MaxContextTokens = 4096,
            MaxTurnsPerSession = 50,
            SessionTimeout = TimeSpan.FromMinutes(30),
        },
        Memory = new MemoryPolicy
        {
            EnableSessionMemory = true,
            EnableWorkspaceMemory = false,
            AllowPublicSourceWrite = false,
        },
        SystemPrompt = "You are an audit agent. You can only read structured, desensitized, and restricted views. You participate in freeze, approval, rejection, and inquiry workflows.",
    };

    /// <summary>获取所有内置模板。</summary>
    public static IReadOnlyList<AgentTemplateDefinition> GetAll() =>
        [WorkspaceServiceAgent, WorkspaceTaskAgent, CodeAgent, WorkspaceAuditAgent];

    /// <summary>按 ID 查找内置模板。</summary>
    public static AgentTemplateDefinition? FindById(string templateId) =>
        GetAll().FirstOrDefault(t => t.TemplateId == templateId);
}
