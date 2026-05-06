using System.Text;
using PuddingCode.Abstractions;
using PuddingCode.Agents;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingRuntime.Services.Skills;

namespace PuddingRuntime.Services;

/// <summary>
/// System Prompt 构建器：负责分层提示词拼装与用户画像读取。
/// Persona 内容按优先级：MD 文件 &gt; DB Persona 字段 &gt; 内置模板兜底。
/// </summary>
public sealed class SystemPromptBuilder
{
    private readonly IAgentTemplateProvider? _templateProvider;
    private readonly IWorkspaceProfileProvider? _workspaceProfileProvider;
    private readonly IMemoryEngine _memory;
    private readonly SkillRuntime _skillRuntime;
    private readonly AgentSkillPackageRegistry _skillPackageRegistry;
    private readonly AgentPersonaFileProvider? _personaFileProvider;
    private readonly ILogger<SystemPromptBuilder> _logger;

    public SystemPromptBuilder(
        IMemoryEngine memory,
        SkillRuntime skillRuntime,
        AgentSkillPackageRegistry skillPackageRegistry,
        ILogger<SystemPromptBuilder> logger,
        IAgentTemplateProvider? templateProvider = null,
        IWorkspaceProfileProvider? workspaceProfileProvider = null,
        AgentPersonaFileProvider? personaFileProvider = null)
    {
        _memory = memory;
        _skillRuntime = skillRuntime;
        _skillPackageRegistry = skillPackageRegistry;
        _logger = logger;
        _templateProvider = templateProvider;
        _workspaceProfileProvider = workspaceProfileProvider;
        _personaFileProvider = personaFileProvider;
    }

    /// <summary>
    /// 分层构建系统提示词。
    /// 层级：IDENTITY → SOUL → AGENTS → TOOLS → USER → MEMORY → RUNTIME
    /// </summary>
    public async Task<string> BuildLayeredSystemPromptAsync(
        AgentTemplateDefinition template,
        string? workspaceId,
        string sessionId,
        string? agentTemplateId,
        string userMessage,
        CapabilityPolicy? capability,
        string agentInstanceId,
        bool forStreaming,
        CancellationToken ct)
    {
        var sb = new StringBuilder();

        // ── Persona 优先级：MD 文件 &gt; DB &gt; 内置模板 ──
        // Step 1: 尝试从文件读取 Persona（如果目录存在）
        AgentPersonaFiles? personaFiles = null;
        if (!string.IsNullOrWhiteSpace(agentTemplateId) && _personaFileProvider is not null)
        {
            personaFiles = _personaFileProvider.Load(agentTemplateId);
        }

        // Step 2: 从 DB 读取 Persona 字段（作为文件缺失时的 fallback）
        string? dbPersonaPrompt = null;
        string? dbToolsDescription = null;
        string? dbAvatarEmoji = null;
        string? dbBootstrapTemplate = null;
        string? dbDisplayNameOverride = null;

        if (!string.IsNullOrWhiteSpace(agentTemplateId) && _templateProvider is not null)
        {
            try
            {
                var persona = await _templateProvider.GetPersonaAsync(agentTemplateId, workspaceId, ct);
                if (persona is not null)
                {
                    dbPersonaPrompt = persona.PersonaPrompt;
                    dbToolsDescription = persona.ToolsDescription;
                    dbAvatarEmoji = persona.AvatarEmoji;
                    dbBootstrapTemplate = persona.BootstrapTemplate;
                    dbDisplayNameOverride = persona.DisplayName;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[AgentExec] Load template persona DB fields failed; fallback to file/built-in. templateId={TemplateId}",
                    agentTemplateId);
            }
        }

        // Step 3: 合并优先级：文件 &gt; DB &gt; 内置模板
        var personaPrompt    = personaFiles?.Soul      ?? dbPersonaPrompt;
        var toolsDescription = personaFiles?.Tools     ?? dbToolsDescription;
        var bootstrapTemplate = personaFiles?.Bootstrap ?? dbBootstrapTemplate;
        var avatarEmoji      = dbAvatarEmoji; // Avatar 是短字符串，保留 DB 管理
        var displayNameOverride = dbDisplayNameOverride;

        // ── 1. IDENTITY 层 ──
        sb.AppendLine("--- LAYER: IDENTITY ---");
        var displayName = string.IsNullOrWhiteSpace(displayNameOverride)
            ? (string.IsNullOrWhiteSpace(template.DisplayName)
            ? template.Name
            : template.DisplayName)
            : displayNameOverride;
        if (!string.IsNullOrWhiteSpace(displayName))
            sb.AppendLine($"Name: {displayName}");
        var effectiveAvatar = string.IsNullOrWhiteSpace(avatarEmoji) ? template.AvatarEmoji : avatarEmoji;
        if (!string.IsNullOrWhiteSpace(effectiveAvatar))
            sb.AppendLine($"Avatar: {effectiveAvatar}");
        // IDENTITY.md 文件内容（补充身份描述）
        if (!string.IsNullOrWhiteSpace(personaFiles?.Identity))
            sb.AppendLine(personaFiles.Identity);

        // ── 2. SOUL 层 ──
        sb.AppendLine("--- LAYER: SOUL ---");
        var effectivePersona = string.IsNullOrWhiteSpace(personaPrompt) ? template.PersonaPrompt : personaPrompt;
        if (!string.IsNullOrWhiteSpace(effectivePersona))
            sb.AppendLine(effectivePersona);

        // ── 3. AGENTS 层 ──
        sb.AppendLine("--- LAYER: AGENTS ---");
        // AGENTS.md 如果存在，覆盖 SystemPrompt
        if (!string.IsNullOrWhiteSpace(personaFiles?.Agents))
            sb.AppendLine(personaFiles.Agents);
        else
            sb.AppendLine(template.SystemPrompt ?? "You are a helpful assistant.");
        if (!string.IsNullOrWhiteSpace(bootstrapTemplate))
        {
            sb.AppendLine("Bootstrap:");
            sb.AppendLine(bootstrapTemplate);
        }

        // ── 4. TOOLS 层 ──
        sb.AppendLine("--- LAYER: TOOLS ---");
        // TOOLS.md 如果存在，覆盖 DB ToolsDescription
        if (!string.IsNullOrWhiteSpace(personaFiles?.Tools))
            sb.AppendLine(personaFiles.Tools);
        else if (!string.IsNullOrWhiteSpace(toolsDescription))
            sb.AppendLine(toolsDescription);
        else if (!string.IsNullOrWhiteSpace(template.ToolsDescription))
            sb.AppendLine(template.ToolsDescription);

        var pkgs = _skillPackageRegistry.Get(agentInstanceId);
        if (pkgs.Count > 0)
        {
            sb.AppendLine("Available Skill Packages:");
            foreach (var pkg in pkgs)
            {
                sb.Append($"- **{pkg.Name}** (`/skills/{pkg.SkillPackageId}/`)");
                if (!string.IsNullOrWhiteSpace(pkg.Description))
                    sb.Append($": {pkg.Description}");
                sb.AppendLine();
            }
        }

        // ── 5. USER 层 ──
        sb.AppendLine("--- LAYER: USER ---");
        // USER.md 文件优先
        if (!string.IsNullOrWhiteSpace(personaFiles?.User))
            sb.AppendLine(personaFiles.User);
        else
        {
            var userProfile = await LoadWorkspaceUserProfileAsync(workspaceId, ct);
            if (!string.IsNullOrWhiteSpace(userProfile))
                sb.AppendLine(userProfile);
        }

        // ── 6. MEMORY 层 ──
        sb.AppendLine("--- LAYER: MEMORY ---");
        if (template.Memory?.EnableSessionMemory == true
         || template.Memory?.EnableWorkspaceMemory == true)
        {
            var memCtx = await _memory.RecallWithIntentAsync(
                userMessage,
                workspaceId ?? string.Empty,
                agentInstanceId,
                sessionId,
                maxTokens: 2000,
                ct);
            if (!string.IsNullOrWhiteSpace(memCtx))
                sb.AppendLine(memCtx);
        }

        // ── 7. RUNTIME 层 ──
        sb.AppendLine("--- LAYER: RUNTIME ---");
        sb.AppendLine($"Date: {DateTimeOffset.Now:yyyy-MM-dd}");
        sb.AppendLine($"Session: {sessionId}");

        if (forStreaming)
        {
            sb.AppendLine("Respond directly to the user in Markdown.");
            sb.AppendLine("Do not output JSON control structures such as status/tool/meta.");
            sb.AppendLine("Use concise explanations, fenced code blocks, Markdown tables, and LaTeX when helpful.");
            if (capability?.AllowedToolNames is { Count: > 0 })
                sb.AppendLine("If a task requires tools, explain the limitation briefly instead of emitting tool-call JSON.");
        }
        else
        {
            sb.Append(_skillRuntime.BuildLoopInstructions(capability));
        }

        return sb.ToString();
    }

    /// <summary>
    /// 旧同步版本（流式模式）系统提示词构建。
    /// </summary>
    public string BuildStreamingSystemPrompt(
        AgentTemplateDefinition template,
        string? workspaceId,
        string sessionId,
        string? agentTemplateId,
        string userMessage,
        CapabilityPolicy? capability,
        string agentInstanceId)
    {
        return BuildLayeredSystemPromptAsync(
            template,
            workspaceId,
            sessionId,
            agentTemplateId,
            userMessage,
            capability,
            agentInstanceId,
            forStreaming: true,
            CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>
    /// 旧同步版本（结构化 Loop 模式）系统提示词构建。
    /// </summary>
    public string BuildSystemPrompt(
        AgentTemplateDefinition template,
        string? workspaceId,
        string sessionId,
        string? agentTemplateId,
        string userMessage,
        CapabilityPolicy? capability,
        string agentInstanceId)
    {
        return BuildLayeredSystemPromptAsync(
            template,
            workspaceId,
            sessionId,
            agentTemplateId,
            userMessage,
            capability,
            agentInstanceId,
            forStreaming: false,
            CancellationToken.None).GetAwaiter().GetResult();
    }

    public async Task<string?> LoadWorkspaceUserProfileAsync(string? workspaceId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || _workspaceProfileProvider is null)
            return null;

        try
        {
            return await _workspaceProfileProvider.GetWorkspaceUserProfileAsync(workspaceId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "[AgentExec] Load workspace user profile failed; fallback to empty USER layer. workspace={Workspace}",
                workspaceId);
            return null;
        }
    }
}
