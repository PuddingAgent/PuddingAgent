using System.Text;
using PuddingCode.Abstractions;
using PuddingCode.Agents;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Tools;
using PuddingRuntime.Services.Skills;
using PuddingRuntime.Services.Tools;

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
    private readonly IMemoryLibraryConvenience? _libraryConvenience;
    private readonly SkillRuntime _skillRuntime;
    private readonly IPuddingToolRegistry? _toolRegistry;
    private readonly AgentSkillPackageRegistry _skillPackageRegistry;
    private readonly AgentPersonaFileProvider? _personaFileProvider;
    private readonly ILogger<SystemPromptBuilder> _logger;
    private readonly StartupEnvironmentInfo _env;

    public SystemPromptBuilder(
        IMemoryEngine memory,
        SkillRuntime skillRuntime,
        AgentSkillPackageRegistry skillPackageRegistry,
        ILogger<SystemPromptBuilder> logger,
        StartupEnvironmentInfo env,
        IAgentTemplateProvider? templateProvider = null,
        IWorkspaceProfileProvider? workspaceProfileProvider = null,
        AgentPersonaFileProvider? personaFileProvider = null,
        IMemoryLibraryConvenience? libraryConvenience = null,
        IPuddingToolRegistry? toolRegistry = null)
    {
        _memory = memory;
        _skillRuntime = skillRuntime;
        _toolRegistry = toolRegistry;
        _skillPackageRegistry = skillPackageRegistry;
        _logger = logger;
        _env = env;
        _templateProvider = templateProvider;
        _workspaceProfileProvider = workspaceProfileProvider;
        _personaFileProvider = personaFileProvider;
        _libraryConvenience = libraryConvenience;
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
        // 实例身份锚定：告诉 Agent 它是谁、ID 是什么、负责什么
        if (!string.IsNullOrWhiteSpace(agentInstanceId))
            sb.AppendLine($"AgentId: {agentInstanceId}");
        if (!string.IsNullOrWhiteSpace(agentTemplateId))
            sb.AppendLine($"Template: {agentTemplateId}");
        if (!string.IsNullOrWhiteSpace(template.Role))
            sb.AppendLine($"Role: {template.Role}");
        if (template.Responsibilities is { Count: > 0 })
            sb.AppendLine($"Responsibilities: {string.Join("、", template.Responsibilities)}");
        var effectiveAvatar = string.IsNullOrWhiteSpace(avatarEmoji) ? template.AvatarEmoji : avatarEmoji;
        if (!string.IsNullOrWhiteSpace(effectiveAvatar))
            sb.AppendLine($"Avatar: {effectiveAvatar}");
        // IDENTITY.md 文件内容（补充身份描述）
        if (!string.IsNullOrWhiteSpace(personaFiles?.Identity))
            sb.AppendLine(personaFiles.Identity);

        // ADR-042: 明确的身份自述 —— 帮助 Agent 在冷启动时确定自己的身份
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(template.Role))
            sb.Append($"你是 {displayName}，一名 {template.Role}");
        else
            sb.Append($"你是 {displayName}");
        if (template.Responsibilities is { Count: > 0 })
            sb.Append($"，负责 {string.Join("、", template.Responsibilities)}");
        sb.Append("。");
        sb.AppendLine("请始终以这个身份和视角处理任务。");

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

        // Voice 语音输出能力（与 ContextPipeline SKILLS 层对齐）
        sb.AppendLine();
        sb.AppendLine("Voice output:");
        sb.AppendLine("You may attach a `voice` field to messages suitable for spoken delivery.");
        sb.AppendLine("- voice.enabled: true → frontend auto-plays");
        sb.AppendLine("- voice.tts_text: optional spoken version (remove symbols, more conversational)");
        sb.AppendLine("Use for: greetings, farewells, storytelling, explanations, casual chat.");
        sb.AppendLine("Skip for: code, tables, tech specs, file paths, CLI.");

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

        // 检查深度探索结果（上次查询触发的异步 LLM 探索）
        if (_libraryConvenience is not null)
        {
            var deepResults = _libraryConvenience.GetPendingExplorations(userMessage);
            if (deepResults.Count > 0)
            {
                sb.AppendLine("[RECALLED FROM DEEP EXPLORE]");
                foreach (var dr in deepResults)
                {
                    sb.AppendLine($"- {dr.BookTitle}: {dr.Snippet}");
                }
                sb.AppendLine();
            }
        }
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

        // ── 环境感知：Agent 工作空间信息 ──
        sb.AppendLine("## 环境信息");
        sb.AppendLine($"- 操作系统: {_env.OsDescription} ({_env.OsArchitecture})");
        sb.AppendLine($"- .NET 运行时: {_env.RuntimeVersion}");
        sb.AppendLine("- 命令执行位置: 宿主机");
        sb.AppendLine($"- 程序启动时间: {_env.StartedAt:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine();
        sb.AppendLine("## 关键路径");
        sb.AppendLine($"- Pudding 程序目录: {_env.AppBaseDirectory}");
        sb.AppendLine($"- 数据根目录: {_env.DataDirectory}");
        sb.AppendLine($"- 你的工作空间: {_env.AgentsDirectory}/{{agent模板ID}}/（个人专属目录，可读写）");
        sb.AppendLine($"- 备忘录目录: {_env.MemosDirectory}/（跨 Agent 共享，睡前交接用）");
        sb.AppendLine($"- 日志目录: {_env.LogsDirectory}/");
        sb.AppendLine($"- 会话归档: {_env.SessionsDirectory}/{{日期}}/（只读历史记录）");
        sb.AppendLine($"- 配置目录: {_env.ConfDirectory}/（只读，Pudding 系统配置）");
        sb.AppendLine($"- 用户主目录: {_env.UserHomeDirectory}（宿主机的用户目录，谨慎操作）");
        sb.AppendLine();
        sb.AppendLine("你可以通过 terminal_start / terminal_wait / terminal_read / terminal_status / terminal_cancel 管理宿主机命令；通过 file_read / list_dir / file_write / file_patch / apply_patch 读写文件。");
        sb.AppendLine("长时间运行的构建、测试、搜索和服务启动应优先使用 terminal_start，然后用 terminal_wait 按 job_id 轮询输出；如果输出被截断，用 terminal_read 读取 handle/read_args 指向的缓冲输出片段；shell 仅用于短的有界一次性命令。");
        sb.AppendLine($"注意：{_env.MemosDirectory}/latest.md 是你的'睡前备忘录'，每次会话结束前应更新。");

        // ── 8. SIGIL 层：备忘录交接仪式 ──
        sb.AppendLine("--- LAYER: SIGIL ---");
        sb.AppendLine("## 备忘录交接仪式（睡前 / 唤醒）");
        sb.AppendLine();
        sb.AppendLine("你有两套记忆系统：");
        sb.AppendLine("- **记忆图书馆** = 大脑（存储原子化事实，可被 search_memory 检索）");
        sb.AppendLine("- **备忘录** = 日记本（存储完整叙事，在 data/memos/ 目录下）");
        sb.AppendLine();
        sb.AppendLine("### 睡前仪式（会话结束前必须执行）");
        sb.AppendLine("1. 写 `data/memos/{date}.md`：用最短字数交代今天最重要的 2-5 件事");
        sb.AppendLine("   - 这是\u201C昨天的你写给明天的你\u201D的信，保持叙事完整性，不拆碎");
        sb.AppendLine("   - 只记真正重要的事，不要记杂事（上下文会被填满导致遗忘）");
        sb.AppendLine("   - 确保明天的你读完后能在最短时间内想起需要做的事");
        sb.AppendLine("2. 在记忆图书馆存储几条关键指针（保存为 Book \u201C交接索引\u201D 的章节）：");
        sb.AppendLine("   - 设置 source_reference 指向 `data/memos/{date}.md`");
        sb.AppendLine("   - 这样 search_memory 能检索到，但叙事不堆进大脑");
        sb.AppendLine("3. 用 `cp data/memos/{date}.md data/memos/latest.md` 更新 latest.md");
        sb.AppendLine();
        sb.AppendLine("### 唤醒仪式（会话开始时执行）");
        sb.AppendLine("1. 读取 `data/memos/latest.md` 快速恢复上下文");
        sb.AppendLine("2. 搜索记忆图书馆的 \u201C交接索引\u201D，获取关键指针");
        sb.AppendLine("3. 在回复开头用一句话提及上次的进展（如\u201C继续昨天的任务...\u201D）");
        sb.AppendLine();
        sb.AppendLine("### 备忘录格式示例");
        sb.AppendLine("```");
        sb.AppendLine("# 2026-05-13 交接");
        sb.AppendLine();
        sb.AppendLine("## 关键进展");
        sb.AppendLine("- 完成了 XXX 功能");
        sb.AppendLine("- 修复了 YYY 问题");
        sb.AppendLine();
        sb.AppendLine("## 待办");
        sb.AppendLine("- 继续 XXX");
        sb.AppendLine();
        sb.AppendLine("## 重要提醒");
        sb.AppendLine("- ZZZ 需要注意");
        sb.AppendLine("```");
        sb.AppendLine("");
        sb.AppendLine("原则：叙事留在备忘录，线索存入图书馆，不交叉污染。");

        if (forStreaming)
        {
            sb.AppendLine("Respond directly to the user in Markdown.");
            sb.AppendLine("Do not output JSON control structures such as status/tool/meta.");
            sb.AppendLine("Use concise explanations, fenced code blocks, Markdown tables, and LaTeX when helpful.");
            sb.AppendLine("For short inline values like paths, filenames, commands, or variable names, use inline `backticks` instead of fenced code blocks.");
            if (capability?.AllowedToolNames is { Count: > 0 })
                sb.AppendLine("If a task requires tools, explain the limitation briefly instead of emitting tool-call JSON.");
        }
        else
        {
            sb.Append(BuildLoopInstructions(capability));
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
        return Task.Run(async () =>
            await BuildLayeredSystemPromptAsync(
                template,
                workspaceId,
                sessionId,
                agentTemplateId,
                userMessage,
                capability,
                agentInstanceId,
                forStreaming: true,
                CancellationToken.None)).GetAwaiter().GetResult();
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
        return Task.Run(async () =>
            await BuildLayeredSystemPromptAsync(
                template,
                workspaceId,
                sessionId,
                agentTemplateId,
                userMessage,
                capability,
                agentInstanceId,
                forStreaming: false,
                CancellationToken.None)).GetAwaiter().GetResult();
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

    private string BuildLoopInstructions(CapabilityPolicy? capability)
    {
        if (_toolRegistry is null)
            return _skillRuntime.BuildLoopInstructions(capability);

        return ToolLoopInstructionBuilder.BuildFromDescriptors(_toolRegistry.ListAvailable(capability));
    }
}
