using PuddingCode.Platform;

namespace PuddingCode.Platform;

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
        Role = "通用助手",
        Responsibilities = ["对话、推理、日常协调"],
        Capability = new CapabilityPolicy
        {
            AllowFileWrite = false,
            AllowShellExecution = false,
            AllowNetworkAccess = false,
        },
        Runtime = new RuntimeProfile { MaxContextTokens = 1048576 }, // Mimo v2.5 has 1M context
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
        Role = "任务助手",
        Responsibilities = ["文件操作、Shell 命令执行"],
        Capability = new CapabilityPolicy
        {
            AllowFileWrite = true,
            AllowShellExecution = true,
            AllowNetworkAccess = false,
            AllowedToolNames =
            [
                "terminal_start",
                "terminal_wait",
                "terminal_read",
                "terminal_status",
                "terminal_cancel",
                "terminal_input",
                "shell",
                "file_read",
                "list_dir",
                "file_write",
                "file_patch",
                "apply_patch",
            ],
        },
        Runtime = new RuntimeProfile { MaxContextTokens = 1048576 }, // Mimo v2.5 has 1M context
        Memory = new MemoryPolicy
        {
            EnableSessionMemory = true,
            EnableWorkspaceMemory = true,
            AllowPublicSourceWrite = false,
        },
        SystemPrompt = "You are a task-oriented agent. You can read/write files and run host commands to accomplish tasks. Prefer terminal_start/terminal_wait for long-running commands. Always ask for approval before destructive operations.",
    };

    public static readonly AgentTemplateDefinition CodeAgent = new()
    {
        TemplateId = "code-agent",
        Name = "Code Agent",
        Description = "代码执行型 Agent，支持通过 Shell 工具在宿主机执行命令。",
        TemplateType = AgentTemplateType.Task,
        Role = "代码助手",
        Responsibilities = ["代码分析、重构、实现"],
        Capability = new CapabilityPolicy
        {
            AllowFileWrite = true,
            AllowShellExecution = true,
            AllowNetworkAccess = false,
            AllowedToolNames =
            [
                "terminal_start",
                "terminal_wait",
                "terminal_read",
                "terminal_status",
                "terminal_cancel",
                "terminal_input",
                "shell",
                "file_read",
                "list_dir",
                "file_write",
                "file_patch",
                "apply_patch",
            ],
        },
        Runtime = new RuntimeProfile { MaxContextTokens = 1048576 }, // Mimo v2.5 has 1M context
        Memory = new MemoryPolicy
        {
            EnableSessionMemory = true,
            EnableWorkspaceMemory = true,
            AllowPublicSourceWrite = false,
        },
        SystemPrompt = "You are a coding agent. Prefer terminal_start/terminal_wait for builds, tests, searches, and long-running host commands. Use shell only for short bounded one-shot commands.",
    };

    public static readonly AgentTemplateDefinition WorkspaceAuditAgent = new()
    {
        TemplateId = "workspace-audit-agent",
        Name = "Workspace Audit Agent",
        Description = "审计 Agent，只读取结构化、脱敏、受限视图，可参与冻结/批准/拒绝/质询链路。",
        TemplateType = AgentTemplateType.Audit,
        Role = "审计助手",
        Responsibilities = ["审计、合规检查、安全审查"],
        Capability = new CapabilityPolicy
        {
            AllowFileWrite = false,
            AllowShellExecution = false,
            AllowNetworkAccess = false,
        },
        Runtime = new RuntimeProfile { MaxContextTokens = 1048576 }, // Mimo v2.5 has 1M context
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

    /// <summary>
    /// 模糊匹配内置模板，处理各种来源的模板 ID 格式：
    ///   - 直接 ID：code-agent, workspace-task-agent
    ///   - UI 别名：code-assistant → code-agent, task-agent → workspace-task-agent
    ///   - global: 前缀：global:code-assistant
    ///   - 复合实例 ID：default.global_code-assistant.78ff46
    /// </summary>
    public static AgentTemplateDefinition ResolveBest(string rawTemplateId)
    {
        // 1. Exact match
        var exact = FindById(rawTemplateId);
        if (exact != null) return exact;

        // 2. Strip "global:" prefix
        const string globalPrefix = "global:";
        var id = rawTemplateId.StartsWith(globalPrefix, StringComparison.OrdinalIgnoreCase)
            ? rawTemplateId[globalPrefix.Length..]
            : rawTemplateId;

        // 3. Exact match after stripping global:
        exact = FindById(id);
        if (exact != null) return exact;

        // 4. Try add/remove "workspace-" prefix
        if (!id.StartsWith("workspace-", StringComparison.OrdinalIgnoreCase))
        {
            exact = FindById($"workspace-{id}");
            if (exact != null) return exact;
        }
        if (id.StartsWith("workspace-", StringComparison.OrdinalIgnoreCase))
        {
            exact = FindById(id["workspace-".Length..]);
            if (exact != null) return exact;
        }

        // 5. Parse compound IDs like "default.global_code-assistant.78ff46"
        var coreName = ExtractTemplateCoreName(id);

        // 6. Map common aliases to built-in template IDs
        var mappedId = MapAliasToTemplateId(coreName);
        if (mappedId != null)
        {
            exact = FindById(mappedId);
            if (exact != null) return exact;
        }

        // 7. Try "workspace-" + alias
        exact = FindById($"workspace-{coreName}");
        if (exact != null) return exact;

        // 8. Contains fallback (last resort)
        return GetAll().FirstOrDefault(t =>
            t.TemplateId.Contains(id, StringComparison.OrdinalIgnoreCase));
    }

    private static string ExtractTemplateCoreName(string id)
    {
        var globalIdx = id.IndexOf("global_", StringComparison.OrdinalIgnoreCase);
        if (globalIdx >= 0)
        {
            var afterGlobal = id[(globalIdx + "global_".Length)..];
            var dotIdx = afterGlobal.IndexOf('.');
            return dotIdx >= 0 ? afterGlobal[..dotIdx] : afterGlobal;
        }

        var parts = id.Split('.');
        if (parts.Length >= 2)
        {
            return parts[1];
        }

        return id;
    }

    private static string? MapAliasToTemplateId(string alias)
    {
        return alias.ToLowerInvariant() switch
        {
            "code-assistant" => CodeAgent.TemplateId,
            "task-agent" => WorkspaceTaskAgent.TemplateId,
            "audit-agent" => WorkspaceAuditAgent.TemplateId,
            "service-agent" => WorkspaceServiceAgent.TemplateId,
            "general-assistant" => WorkspaceServiceAgent.TemplateId,
            "research-assistant" => WorkspaceServiceAgent.TemplateId,
            _ => null,
        };
    }
}
