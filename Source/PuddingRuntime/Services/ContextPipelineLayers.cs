using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using PuddingCode.Abstractions;
using PuddingCode.Agents;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Observability;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingCode.Tools;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntime.Services;

// ═══════════════════════════════════════════════════════════════
// ContextPipeline — Layer Providers (L0-L6)
// Extracted from ContextPipeline.cs to eliminate god-class (P0 audit #9).
// ═══════════════════════════════════════════════════════════════

public sealed partial class ContextPipeline
{
    // ═══════════════════════════════════════════════════════════════
    // L0: 静态上下文
    // ═══════════════════════════════════════════════════════════════

    private async Task<string> GetOrBuildStaticLayerAsync(ContextRequest request, CancellationToken ct)
    {
        if (_staticCache.TryGetValue(request.SessionId, out var cached)
            && cached.TemplateId == request.AgentTemplateId)
        {
            return cached.Content;
        }

        var content = await BuildStaticLayerAsync(request, ct);
        _staticCache[request.SessionId] = new StaticContextCache
        {
            TemplateId = request.AgentTemplateId,
            Content = content,
        };
        return content;
    }

    /// <summary>
    /// L0-AGENTS-ROSTER 的 session 冻结版：名册内容随工作区 Agent 生成/结束而变化
    /// （活跃子代理委派期间反复改写），而它位于 system 消息前部，任何字节变化都会使
    /// 整个后续历史失去前缀缓存。冻结首组装字节，进程重启或 InvalidateSession 后刷新；
    /// 会话内新 Agent 由 subagent_result 消息自行介绍，名册陈旧一个会话周期可接受。
    /// </summary>
    private async Task<string> GetOrBuildWorkspaceAgentsLayerAsync(ContextRequest request, CancellationToken ct)
    {
        if (_workspaceAgentsCache.TryGetValue(request.SessionId, out var cached))
            return cached;

        var content = _workspaceAgentsContextBuilder is null
            ? "--- LAYER: WORKSPACE AGENTS ---\n(No workspace agents available.)\n"
            : await _workspaceAgentsContextBuilder.BuildAsync(request.WorkspaceId, "default", ct);
        _workspaceAgentsCache[request.SessionId] = content;
        return content;
    }

    private async Task<string> BuildStaticLayerAsync(ContextRequest request, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var template = request.Template;

        // Persona 优先级：实例 MD 文件 > 模板 MD 文件 > DB > 内置模板
        AgentPersonaFiles? personaFiles = null;
        if (!string.IsNullOrWhiteSpace(request.AgentTemplateId) && _personaFileProvider is not null)
            personaFiles = _personaFileProvider.Load(request.AgentTemplateId, request.PersistentAgentInstanceId);

        // 实例级 persona（Admin 写入 AgentInstanceRoot/{agentId}/）优先级最高。
        var instancePersona = LoadInstancePersonaFiles(request.PersistentAgentInstanceId);

        // 实例 manifest.json 的 systemPrompt 字段（Admin「系统提示词」框）— 实例级最高优先级指令。
        var manifestSystemPrompt = LoadInstanceManifestSystemPrompt(request.PersistentAgentInstanceId);

        string? dbPersonaPrompt = null;
        string? dbToolsDescription = null;
        string? dbAvatarEmoji = null;
        string? dbBootstrapTemplate = null;
        string? dbDisplayNameOverride = null;

        if (!string.IsNullOrWhiteSpace(request.AgentTemplateId) && _templateProvider is not null)
        {
            try
            {
                var persona = await _templateProvider.GetPersonaAsync(
                    request.AgentTemplateId, request.WorkspaceId, ct);
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
                    "[ContextPipeline] Load persona DB failed templateId={Id}", request.AgentTemplateId);
            }
        }

        var personaPrompt = instancePersona?.Soul ?? personaFiles?.Soul ?? dbPersonaPrompt;
        var toolsDescription = instancePersona?.Tools ?? personaFiles?.Tools ?? dbToolsDescription;
        var bootstrapTemplate = instancePersona?.Bootstrap ?? personaFiles?.Bootstrap ?? dbBootstrapTemplate;

        // L0: IDENTITY
        sb.AppendLine("--- LAYER: IDENTITY ---");
        var displayName = string.IsNullOrWhiteSpace(dbDisplayNameOverride)
            ? (string.IsNullOrWhiteSpace(template.DisplayName) ? template.Name : template.DisplayName)
            : dbDisplayNameOverride;
        if (!string.IsNullOrWhiteSpace(displayName))
            sb.AppendLine($"Name: {displayName}");
        if (!string.IsNullOrWhiteSpace(request.AgentInstanceId))
            sb.AppendLine($"AgentId: {request.AgentInstanceId}");
        if (!string.IsNullOrWhiteSpace(request.AgentTemplateId))
            sb.AppendLine($"Template: {request.AgentTemplateId}");
        if (!string.IsNullOrWhiteSpace(template.Role))
            sb.AppendLine($"Role: {template.Role}");
        if (template.Responsibilities is { Count: > 0 })
            sb.AppendLine($"Responsibilities: {string.Join("、", template.Responsibilities)}");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(template.Role))
            sb.Append($"你是 {displayName}，一名 {template.Role}");
        else
            sb.Append($"你是 {displayName}");
        if (template.Responsibilities is { Count: > 0 })
            sb.Append($"，负责 {string.Join("、", template.Responsibilities)}");
        sb.Append("。");
        sb.AppendLine("请始终以这个身份和视角处理任务。");

        var effectiveAvatar = string.IsNullOrWhiteSpace(dbAvatarEmoji) ? template.AvatarEmoji : dbAvatarEmoji;
        if (!string.IsNullOrWhiteSpace(effectiveAvatar))
            sb.AppendLine($"Avatar: {effectiveAvatar}");
        if (!string.IsNullOrWhiteSpace(instancePersona?.Identity))
            sb.AppendLine(instancePersona.Identity);
        else if (!string.IsNullOrWhiteSpace(personaFiles?.Identity))
            sb.AppendLine(personaFiles.Identity);

        // L0: SOUL
        sb.AppendLine("--- LAYER: SOUL ---");
        var effectivePersona = string.IsNullOrWhiteSpace(personaPrompt) ? template.PersonaPrompt : personaPrompt;
        if (!string.IsNullOrWhiteSpace(effectivePersona))
            sb.AppendLine(effectivePersona);

        // L0: AGENTS
        sb.AppendLine("--- LAYER: AGENTS ---");
        if (!string.IsNullOrWhiteSpace(manifestSystemPrompt))
        {
            // Admin「系统提示词」框内容 — 最高优先级，追加在 AGENTS 层最前。
            sb.AppendLine(manifestSystemPrompt);
            sb.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(instancePersona?.Agents))
            sb.AppendLine(instancePersona.Agents);
        else if (!string.IsNullOrWhiteSpace(personaFiles?.Agents))
            sb.AppendLine(personaFiles.Agents);
        else
            sb.AppendLine(template.SystemPrompt ?? "You are a helpful assistant.");
        if (!string.IsNullOrWhiteSpace(bootstrapTemplate))
        {
            sb.AppendLine("Bootstrap:");
            sb.AppendLine(bootstrapTemplate);
        }

        // L0: SECURITY-GUIDE — 权限申请与熔断恢复指引
        sb.AppendLine("--- LAYER: SECURITY-GUIDE ---");
        sb.AppendLine("When a tool call is rejected (error contains \"permission\", \"not allowed\", or \"rejected\"):");
        sb.AppendLine("1. STOP immediately — do NOT retry the same tool blindly.");
        sb.AppendLine("2. Call request_tool_approval(tool_id=\"...\", purpose=\"...\") to request one-time authorization.");
        sb.AppendLine("   The system may auto-approve (implicit approval) if the request matches safety rules.");
        sb.AppendLine("3. If approval is denied, try a different approach that uses allowed tools.");
        sb.AppendLine("4. Repeated failures trigger session fuse (session becomes Faulted).");
        sb.AppendLine("5. If fuse triggers, the user can send /resume to recover the session.");

        return sb.ToString();
    }

    /// <summary>
    /// 读取实例级 persona 文件（Admin 写入 AgentInstanceRoot/{agentId}/ 的 SOUL/AGENTS/TOOLS/BOOTSTRAP/IDENTITY/MEMORY.md）。
    /// 实例级自定义的权威来源，优先级高于模板级 persona 与 DB 字段。
    /// </summary>
    private AgentPersonaFiles? LoadInstancePersonaFiles(string? agentInstanceId)
    {
        if (string.IsNullOrWhiteSpace(agentInstanceId))
            return null;

        var dir = _dataPaths.AgentInstanceRoot(agentInstanceId);
        if (!Directory.Exists(dir))
            return null;

        return new AgentPersonaFiles
        {
            Soul = ReadPersonaFile(dir, "SOUL.md"),
            Agents = ReadPersonaFile(dir, "AGENTS.md"),
            Tools = ReadPersonaFile(dir, "TOOLS.md"),
            Bootstrap = ReadPersonaFile(dir, "BOOTSTRAP.md"),
            Identity = ReadPersonaFile(dir, "IDENTITY.md"),
            Memory = ReadPersonaFile(dir, "MEMORY.md"),
        };
    }

    /// <summary>
    /// 读取实例 manifest.json 的 systemPrompt 字段（Admin「系统提示词」文本框写入）。
    /// 用 JsonDocument 直接解析，避免 PuddingRuntime 引用 PuddingHost 的 AgentInstanceManifest 类型。
    /// </summary>
    private string? LoadInstanceManifestSystemPrompt(string? agentInstanceId)
    {
        if (string.IsNullOrWhiteSpace(agentInstanceId))
            return null;

        var manifestPath = Path.Combine(_dataPaths.AgentInstanceRoot(agentInstanceId), "manifest.json");
        if (!File.Exists(manifestPath))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (doc.RootElement.TryGetProperty("systemPrompt", out var el)
                && el.ValueKind == JsonValueKind.String)
            {
                return el.GetString();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[ContextPipeline] Load manifest systemPrompt failed agent={Agent}", agentInstanceId);
        }
        return null;
    }

    private static string? ReadPersonaFile(string dir, string fileName)
    {
        var path = Path.Combine(dir, fileName);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    // ═══════════════════════════════════════════════════════════════
    // L0-ENVIRONMENT: 运行环境
    // ═══════════════════════════════════════════════════════════════

    private string GetOrBuildEnvironmentLayer(ContextRequest request)
    {
        var envCacheKey = $"{request.WorkspaceId}:{_envProvider.EnvironmentFingerprint}";
        if (_envCache.TryGetValue(envCacheKey, out var cached))
            return cached.Content;

        var content = BuildEnvironmentLayer(request);
        _envCache[envCacheKey] = new EnvironmentLayerCache { Content = content };
        _logger.LogDebug(
            "[ContextPipeline:L0-ENVIRONMENT] Built env layer fingerprint={Fingerprint} workspace={Workspace}",
            _envProvider.EnvironmentFingerprint, request.WorkspaceId);
        return content;
    }

    private string BuildEnvironmentLayer(ContextRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine("--- LAYER: ENVIRONMENT ---");
        sb.AppendLine($"OS: {_envProvider.OsDescription} {_envProvider.OsArchitecture}");
        sb.AppendLine($"Runtime: .NET {_envProvider.RuntimeVersion}");
        sb.AppendLine($"PathSeparator: {_envProvider.PathSeparator}");
        sb.AppendLine($"Shell: {_envProvider.DefaultShell}");
        sb.AppendLine($"Container: {(_envProvider.IsContainer ? "true" : "false")}");

        return sb.ToString();
    }

    /// <summary>
    /// 构建 INBOUND-MESSAGE-CONTEXT 层：当 Agent 收到其他 Agent 的消息时，
    /// 明确告知发送方身份和消息意图，防止身份混淆。
    /// ADR-042: Agent 身份锚定。
    /// </summary>
    private static string BuildInboundMessageContextLayer(ContextRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.InboundSourceId)
            || !string.Equals(request.InboundSourceKind, "agent", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        var senderName = request.InboundSourceName ?? request.InboundSourceId;

        return $"""
            --- LAYER: INBOUND-MESSAGE-CONTEXT ---
            你收到了一条来自其他 Agent 的内部消息。
            发送方: {senderName} (agent:{request.InboundSourceId})

            请根据你的角色和职责，判断如何回应这条消息。
            ---
            """;
    }

    private string BuildWorkspaceEnvironmentLayer(ContextRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine("--- LAYER: WORKSPACE ENVIRONMENT ---");
        var workspaceRoot = _envProvider.GetWorkspaceRoot(request.WorkspaceId);
        if (workspaceRoot is not null)
        {
            sb.AppendLine($"WorkspaceRoot: {workspaceRoot}");
        }
        else
        {
            sb.AppendLine("WorkspaceRoot: unavailable");
        }

        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════════
    // L1: 动态工具
    // ═══════════════════════════════════════════════════════════════

    private Task<string> BuildToolsLayerAsync(ContextRequest request, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("--- LAYER: TOOLS ---");

        if (_toolRegistry is not null)
        {
            var descriptors = _toolRegistry.ListAvailable(request.Capability, request.WorkspaceId);
            // P0-5 step 4a: L1 索引文本优先从 session 已加载工具集合（append-only）生成，
            // 可见工具 = Core ∪ Loaded，与 ToolExposurePlanner.CreatePlan 的可见集语义一致
            // （Core ∪ loaded ∪ committed 不收缩），消除每轮从实时 registry 全量重建导致
            // 的 system prompt 前缀漂移。LoadedToolIds 为 null/空时保持全量行为（向后兼容）。
            IReadOnlyList<ToolDescriptor> deferredDescriptors = [];
            if (request.LoadedToolIds is { Count: > 0 })
            {
                var visible = descriptors
                    .Where(d => ToolExposurePlanner.CoreToolIds.Contains(d.ToolId)
                        || request.LoadedToolIds.Contains(d.ToolId))
                    .ToList();
                // 2026-08-22 冗余治理：未装载工具必须以名字索引出现（只列 id，不带 schema），
                // 否则 Agent 无法知道有哪些延迟能力、只能盲调 search_tools。
                deferredDescriptors = descriptors
                    .Where(d => !ToolExposurePlanner.CoreToolIds.Contains(d.ToolId)
                        && !request.LoadedToolIds.Contains(d.ToolId))
                    .OrderBy(d => d.ToolId, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                descriptors = visible;
            }
            AppendToolDescriptorList(sb, descriptors, request);
            if (deferredDescriptors.Count > 0)
            {
                sb.AppendLine("Deferred tools (not loaded yet; call `search_tools` with a tool id above to load its full definition):");
                sb.AppendLine(string.Join(", ", deferredDescriptors.Select(d => $"`{d.ToolId}`")));
            }
            return Task.FromResult(sb.ToString());
        }

        var availableSkills = _skillRuntime.GetAvailableSkills(request.Capability);
        var skillsList = availableSkills.ToList();
        if (skillsList.Count > 0)
        {
            sb.AppendLine("Available tools (use via function calling):");
            var defaultSkills = skillsList.Where(s => s.PermissionLevel == ToolPermissionLevel.Low).ToList();
            var mediumSkills = skillsList.Where(s => s.PermissionLevel == ToolPermissionLevel.Medium).ToList();
            var highSkills = skillsList.Where(s => s.PermissionLevel == ToolPermissionLevel.High).ToList();

            if (defaultSkills.Count > 0)
            {
                sb.Append("  [内置] ");
                sb.AppendLine(string.Join(", ", defaultSkills.Select(s => $"`{s.SkillId}`")));
            }
            if (mediumSkills.Count > 0)
            {
                sb.Append("  [默认授权] ");
                sb.AppendLine(string.Join(", ", mediumSkills.Select(s => $"`{s.SkillId}`")));
            }
            if (highSkills.Count > 0)
            {
                sb.Append("  [需显式授权] ");
                sb.AppendLine(string.Join(", ", highSkills.Select(s => $"`{s.SkillId}`")));
            }
            sb.AppendLine($"Total: {skillsList.Count} tools available.");
        }
        else
        {
            sb.AppendLine("(No tools available with current capability policy.)");
        }

        sb.AppendLine("Memory tool hint: use `search_memory` when you need to recall user facts from memory library; use `query_session_logs` for paged message transcripts by default, and raw event actions only for diagnostics.");
        if (ShouldShowSubAgentHint(request))
            AppendMandatoryDelegationPolicy(sb);

        return Task.FromResult(sb.ToString());
    }

    private static void AppendToolDescriptorList(StringBuilder sb, IReadOnlyList<ToolDescriptor> descriptors, ContextRequest request)
    {
        var visibleDescriptors = ShouldShowSubAgentHint(request)
            ? descriptors
            : descriptors
                .Where(d => !string.Equals(d.ToolId, "spawn_sub_agent", StringComparison.OrdinalIgnoreCase))
                .ToList();

        if (visibleDescriptors.Count > 0)
        {
            sb.AppendLine("Available tools (grouped by category, use via function calling):");
            var categoryOrder = new[]
            {
                ToolCategory.FileSystem,
                ToolCategory.Query,
                ToolCategory.Execute,
                ToolCategory.Memory,
                ToolCategory.Messaging,
                ToolCategory.Orchestration,
                ToolCategory.Network,
                ToolCategory.Security,
                ToolCategory.Shell,
                ToolCategory.General,
            };
            foreach (var category in categoryOrder)
            {
                var tools = visibleDescriptors
                    .Where(d => d.Category == category)
                    .OrderBy(d => d.ToolId, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (tools.Count == 0)
                    continue;
                sb.Append("  [");
                sb.Append(category);
                sb.Append("] ");
                sb.AppendLine(string.Join(", ", tools.Select(t => $"`{t.ToolId}`")));
            }
            sb.AppendLine($"Total: {visibleDescriptors.Count} tools available.");
        }
        else
        {
            sb.AppendLine("(No tools available with current capability policy.)");
        }

        sb.AppendLine("Memory tool hint: use `search_memory` when you need to recall user facts from memory library; use `query_session_logs` for paged message transcripts by default, and raw event actions only for diagnostics.");
        if (ShouldShowSubAgentHint(request))
            AppendMandatoryDelegationPolicy(sb);
    }

    private static void AppendMandatoryDelegationPolicy(StringBuilder sb)
    {
        sb.AppendLine("Mandatory delegation policy (`smart_*` / `spawn_sub_agent`):");
        sb.AppendLine("- Before the first tool call, you MUST classify the request as `Direct` or `Delegated`. This is an execution requirement.");
        sb.AppendLine("- You MUST choose `Delegated` when the work is expected to require more than 3 tool calls, spans multiple files or sources, contains independent workstreams, or the user explicitly requests delegation.");
        sb.AppendLine("- In `Delegated` mode, you MUST call the matching visible `smart_explore`, `smart_research`, `smart_plan`, `smart_review`, `smart_develop`, `smart_test`, `smart_deploy`, or `spawn_sub_agent` within the first 3 tool calls.");
        sb.AppendLine("- If no matching delegation tool is visible, you MUST report the capability blocker and MUST NOT silently perform the delegated workload with low-level tools.");
        sb.AppendLine("- Every delegated task MUST be atomic and MUST define its scope, success criteria, and output contract. The parent Agent MUST retain planning, bounded verification, integration, and final judgment.");
        sb.AppendLine("- After delegation, you MUST NOT repeat the same exploration, research, implementation, review, or testing with low-level tools. Bounded integration and verification are limited to at most 3 low-level tool calls.");
        sb.AppendLine("- You MUST choose `Direct` only when no `Delegated` condition applies and the work can finish within 3 tool calls, requires real-time user interaction, or depends on the parent Agent's judgment.");
    }

    private static bool ShouldShowSubAgentHint(ContextRequest request)
    {
        if (!request.SessionId.Contains("-sub-", StringComparison.OrdinalIgnoreCase))
            return true;

        if (request.AllowSubDelegation != true)
            return false;

        var depth = Math.Max(0, request.DelegationDepth ?? 0);
        var maxDepth = request.MaxDelegationDepth ?? 1;
        return depth < maxDepth;
    }

    // ═══════════════════════════════════════════════════════════════
    // L2: 动态 Skills
    // ═══════════════════════════════════════════════════════════════

    private async Task<string> BuildSkillsLayerAsync(ContextRequest request, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("--- LAYER: SKILLS ---");

        var availableSkills = _skillRuntime.GetAvailableSkills(request.Capability);
        var pkgs = _skillPackageRegistry.Get(request.AgentInstanceId);

        if (availableSkills.Count > 0)
        {
            // Function schemas and the compact L1 tool index are authoritative. Repeating
            // every tool description here added thousands of stable-but-low-value tokens
            // to every request and defeated deferred tool discovery.
            sb.AppendLine($"Callable tool/skill count: {availableSkills.Count}. Use the visible function schemas; use `search_tools` for deferred capabilities.");
        }

        if (pkgs.Count > 0)
        {
            sb.AppendLine("Additional skill packages loaded:");
            foreach (var pkg in pkgs)
                sb.AppendLine($"- {pkg.Name} (v{pkg.Version}): {pkg.Description ?? ""}");
        }

        var runtimeSkillCount = await AppendRuntimeSkillIndexAsync(sb, request.PersistentAgentInstanceId, ct);

        if (availableSkills.Count == 0 && pkgs.Count == 0 && runtimeSkillCount == 0)
            sb.AppendLine("(No skills or skill packages loaded.)");

        SystemPromptBuilder.AppendVoiceOutputProtocol(sb);
        SystemPromptBuilder.AppendAudioInputProtocol(sb);
        SystemPromptBuilder.AppendImageOutputProtocol(sb);

        return sb.ToString();
    }

    private async Task<int> AppendRuntimeSkillIndexAsync(
        StringBuilder sb,
        string agentInstanceId,
        CancellationToken ct)
    {
        if (_agentSkillFileService is null || string.IsNullOrWhiteSpace(agentInstanceId))
            return 0;

        try
        {
            var index = await _agentSkillFileService.GetIndexAsync(agentInstanceId, ct);
            var skills = index.Skills
                .Where(skill => skill.Enabled)
                .OrderBy(skill => skill.SkillId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(skill => skill.Version, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (skills.Count == 0)
                return 0;

            sb.AppendLine("Runtime-private SKILL index:");
            sb.AppendLine("Use `agent_skill` with action=read_file when a listed SKILL is relevant and you need its full instructions.");
            foreach (var skill in skills)
            {
                // 2026-08-22 冗余治理：索引行只保留定位所需的最小信息（skillId + 首句摘要 +
                // 有限 tags/keywords）。完整说明由 agent_skill 渐进加载；此前每条 ~1.2K 字符的
                // 索引把该层推到 29K 字符，占每次调用上下文的 25%。
                var parts = new List<string>
                {
                    $"`{skill.SkillId}`",
                    CompactSummary(skill.Summary),
                };
                if (skill.Tags.Count > 0)
                    parts.Add($"tags={string.Join(", ", skill.Tags.Take(4))}");
                if (skill.Keywords.Count > 0)
                    parts.Add($"keywords={string.Join(", ", skill.Keywords.Take(6))}");

                sb.Append("- ");
                sb.AppendLine(string.Join(" | ", parts));
            }

            return skills.Count;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "[ContextPipeline:L2-SKILLS] Failed to load runtime SKILL index agent={AgentInstanceId}",
                agentInstanceId);
            return 0;
        }
    }

    /// <summary>
    /// 技能索引行的摘要压缩：取首个完整句（。/./!/？），上限 100 字符；
    /// 完整说明由 agent_skill 渐进加载，索引只负责让模型判断相关性。
    /// </summary>
    private static string CompactSummary(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
            return "(no summary)";

        var text = summary.Trim();
        var cut = text.IndexOfAny(['。', '.', '！', '!', '？', '?']);
        if (cut > 0)
            text = text[..(cut + 1)];
        return text.Length <= 100 ? text : text[..100] + "…";
    }

    // ═══════════════════════════════════════════════════════════════
    // L3: 用户偏好
    // ═══════════════════════════════════════════════════════════════

    private async Task<string> GetOrBuildUserProfileAsync(ContextRequest request, CancellationToken ct)
    {
        var cacheKey = $"user_profile:{request.SessionId}";
        if (_memCache.TryGetValue<string>(cacheKey, out var cached) && cached is not null)
            return cached;

        var profile = await _promptBuilder.LoadWorkspaceUserProfileAsync(request.WorkspaceId, ct);
        var result = new StringBuilder();
        result.AppendLine("--- LAYER: USER ---");
        if (!string.IsNullOrWhiteSpace(profile))
            result.AppendLine(profile);
        else
            result.AppendLine("(No user profile configured.)");

        var content = result.ToString();
        _memCache.Set(cacheKey, content, MemCacheExpiration);
        return content;
    }

    // ═══════════════════════════════════════════════════════════════
    // L3-USER-PREFERENCES: 用户偏好预取（Prefetch）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 从记忆图书馆预取用户偏好并注入 System Prompt。
    /// 按 workspace 缓存 30s；任何失败都降级为 null（不阻塞上下文组装）。
    /// </summary>
    private async Task<string?> GetOrBuildUserPreferencesAsync(ContextRequest request, CancellationToken ct)
    {
        if (_userPreferenceService is null || string.IsNullOrWhiteSpace(request.WorkspaceId))
            return null;

        var cacheKey = $"user_prefs:{request.WorkspaceId}";
        if (_memCache.TryGetValue<string>(cacheKey, out var cached) && cached is not null)
            return cached;

        try
        {
            var prefs = await _userPreferenceService.LoadPreferencesAsync(
                request.WorkspaceId,
                maxItems: 20,
                ct);
            if (!string.IsNullOrWhiteSpace(prefs))
                _memCache.Set(cacheKey, prefs, MemCacheExpiration);
            return prefs;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[ContextPipeline] User preference prefetch failed workspace={Workspace}",
                request.WorkspaceId);
            return null;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // L4: 重要记忆（importance > 0.8）
    // ═══════════════════════════════════════════════════════════════

    private async Task<string> GetOrBuildPinnedMemoryAsync(ContextRequest request, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("--- LAYER: PINNED ---");

        // ── 第一步：尝试读 Important_memory.md（主路径）──
        if (!string.IsNullOrWhiteSpace(request.PersistentAgentInstanceId) && _importantMemory is not null)
        {
            var content = _importantMemory.ReadOrNull(request.PersistentAgentInstanceId);
            if (string.IsNullOrWhiteSpace(content))
            {
                await _importantMemory.EnsureInitializedAsync(request.PersistentAgentInstanceId, ct);
                content = _importantMemory.ReadOrNull(request.PersistentAgentInstanceId);
            }

            if (!string.IsNullOrWhiteSpace(content))
            {
                sb.AppendLine("[IMPORTANT MEMORIES]");
                sb.Append(content);
                var result = sb.ToString();
                _memCache.Set($"pinned:{request.WorkspaceId}", result, MemCacheExpiration);
                return result;
            }
        }

        // ── 第二步：回退到搜索（完全不变）──
        if (_libraryConvenience is null || string.IsNullOrWhiteSpace(request.WorkspaceId))
        {
            sb.AppendLine("(No pinned memory.)");
            return sb.ToString();
        }

        var cacheKey = $"pinned:{request.WorkspaceId}";
        if (_memCache.TryGetValue<string>(cacheKey, out var cached) && cached is not null)
            return cached;

        try
        {
            var results = await _libraryConvenience.SmartSearchAsync(
                "important critical key",
                topK: 5,
                ct);

            _logger.LogDebug(
                "[ContextPipeline:Pinned] workspace={Workspace} results={Count} cache={Cached}",
                request.WorkspaceId, results.Count, cached is not null);

            if (results.Count > 0)
            {
                sb.AppendLine("[IMPORTANT MEMORIES]");
                foreach (var r in results)
                {
                    sb.AppendLine($"- {r.BookTitle}: {r.Snippet}");
                }
            }
            else
            {
                sb.AppendLine("(No pinned memories.)");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[ContextPipeline] Pinned memory recall failed workspace={Workspace}", request.WorkspaceId);
            sb.AppendLine("(Memory recall unavailable.)");
        }

        var cachedContent = sb.ToString();
        _memCache.Set(cacheKey, cachedContent, MemCacheExpiration);
        return cachedContent;
    }

    // ═══════════════════════════════════════════════════════════════
    // L5: 近期历史
    // ═══════════════════════════════════════════════════════════════

    private string BuildRecentHistoryLayer(
        ContextRequest request,
        ContextPipelineCompactionLevel compactionLevel,
        int budgetTokens)
    {
        var sb = new StringBuilder();
        sb.AppendLine("--- LAYER: RECENT ---");

        if (request.SessionHistory is not { Count: > 0 })
        {
            if (request.IsFirstMessage && !string.IsNullOrWhiteSpace(request.PersistentAgentInstanceId))
            {
                var prefilled = TryBuildColdStartRecent(request, budgetTokens);
                if (!string.IsNullOrWhiteSpace(prefilled))
                    return $"{sb}--- RECENT DAYS ---\n{prefilled}";
            }

            sb.AppendLine("(No recent history.)");
            return sb.ToString();
        }

        var history = request.SessionHistory;
        var usedTokens = 0;
        var budgetChars = budgetTokens * 4;
        string? lastHeartbeatContent = null;

        switch (compactionLevel)
        {
            case ContextPipelineCompactionLevel.None:
                for (int i = Math.Max(0, history.Count - DefaultRecentMessageCount); i < history.Count; i++)
                {
                    var msg = history[i];
                    if (IsHeartbeatContent(msg.Content))
                    {
                        if (lastHeartbeatContent == msg.Content)
                            continue;
                        lastHeartbeatContent = msg.Content;
                    }
                    var entry = FormatHistoryEntry(msg);
                    if (usedTokens + entry.Length > budgetChars && i < history.Count - 2)
                        break;
                    sb.Append(entry);
                    usedTokens += entry.Length;
                }
                break;

            case ContextPipelineCompactionLevel.Gentle:
                var gentleKeep = Math.Min(15, history.Count);
                for (int i = history.Count - gentleKeep; i < history.Count; i++)
                {
                    var entry = FormatHistoryEntry(history[i]);
                    if (usedTokens + entry.Length > budgetChars && i < history.Count - 2)
                        break;
                    sb.Append(entry);
                    usedTokens += entry.Length;
                }
                if (history.Count > gentleKeep && usedTokens < budgetChars * 0.7)
                {
                    var olderSummary = SummarizeOlderHistory(history.Take(history.Count - gentleKeep).ToList());
                    sb.AppendLine($"[SUMMARY] {olderSummary}");
                }
                break;

            case ContextPipelineCompactionLevel.Aggressive:
                var aggressiveKeep = Math.Min(3, history.Count);
                for (int i = history.Count - aggressiveKeep; i < history.Count; i++)
                {
                    sb.Append(FormatHistoryEntry(history[i]));
                }
                if (history.Count > aggressiveKeep)
                {
                    var olderSummary = SummarizeOlderHistory(history.Take(history.Count - aggressiveKeep).ToList());
                    sb.AppendLine($"[SUMMARY] {olderSummary}");
                }
                break;
        }

        return sb.ToString();
    }

    /// <summary>
    /// 冷启动时从 Agent 私有消息日志目录读取最近 3 天的剥离后对话，
    /// 注入 L5-RECENT 层，使 Agent 在首次消息时感知最近几天的对话脉络。
    /// </summary>
    private string? TryBuildColdStartRecent(
        ContextRequest request,
        int budgetTokens)
    {
        const int maxRecentDays = 3;
        var budgetChars = budgetTokens * 4;
        var logsRoot = _dataPaths.AgentInstanceMessageLogsRoot(request.PersistentAgentInstanceId);

        var sb = new StringBuilder();
        var totalChars = 0;
        var today = DateTimeOffset.Now.Date;

        for (int dayOffset = 1; dayOffset <= maxRecentDays; dayOffset++)
        {
            if (totalChars >= budgetChars) break;

            var day = today.AddDays(-dayOffset);
            var dayDir = Path.Combine(logsRoot, day.ToString("yyyy-MM-dd"));
            if (!Directory.Exists(dayDir)) continue;

            var files = Directory.GetFiles(dayDir, "*.md")
                .OrderByDescending(f => f)
                .ToList();

            sb.AppendLine($"--- Day {day:yyyy-MM-dd} ---");

            foreach (var file in files)
            {
                if (totalChars >= budgetChars)
                {
                    sb.AppendLine("... (truncated, use query_session_logs for more)");
                    break;
                }

                try
                {
                    var raw = File.ReadAllText(file);
                    var stripped = MessageLogStripper.Strip(raw);
                    if (string.IsNullOrWhiteSpace(stripped)) continue;

                    sb.AppendLine(stripped);
                    sb.AppendLine("---");
                    totalChars += stripped.Length;
                }
                catch (IOException ex)
                {
                    _logger.LogDebug(ex, "[ContextPipeline] Skip unreadable log file {File}", file);
                }
            }
        }

        var result = sb.ToString().Trim();
        return result.Length > 0 ? result : null;
    }

    private async Task<string> BuildLegacyAgentLogRecallLayerAsync(
        ContextRequest request,
        CancellationToken ct)
    {
        if (_agentLogRecallService is null
            || string.IsNullOrWhiteSpace(request.PersistentAgentInstanceId)
            || string.IsNullOrWhiteSpace(request.UserMessage))
        {
            return string.Empty;
        }

        var recall = await _agentLogRecallService.RecallAsync(
            new AgentLogRecallRequest(request.PersistentAgentInstanceId, request.UserMessage),
            ct);

        if (recall.RecentFiveDaysMessages.Count == 0
            && recall.RecentDailySummaries.Count == 0
            && recall.RecentThirtyDaysMessages.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.AppendLine("--- LAYER: RECALLED ---");
        sb.AppendLine("[AGENT LOG RECALL]");

        if (recall.RecentFiveDaysMessages.Count > 0)
        {
            sb.AppendLine("Recent 5 days message logs:");
            foreach (var match in recall.RecentFiveDaysMessages)
                sb.AppendLine($"- {match.Day} {match.RelativePath}:{match.LineNumber}: {match.Snippet}");
        }

        if (recall.RecentThirtyDaysMessages.Count > 0)
        {
            sb.AppendLine("Recent 30 days message logs:");
            foreach (var match in recall.RecentThirtyDaysMessages)
                sb.AppendLine($"- {match.Day} {match.RelativePath}:{match.LineNumber}: {match.Snippet}");
        }

        if (recall.RecentDailySummaries.Count > 0)
        {
            sb.AppendLine("Recent 180 days daily summaries:");
            foreach (var match in recall.RecentDailySummaries)
                sb.AppendLine($"- {match.Day} {match.RelativePath}:{match.LineNumber}: {match.Snippet}");
        }

        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════════
    // RUNTIME 层
    // ═══════════════════════════════════════════════════════════════

    private void AppendRuntimeLayer(StringBuilder sb, ContextRequest request)
    {
        sb.AppendLine("--- LAYER: RUNTIME ---");

        if (request.ForStreaming)
        {
            sb.AppendLine("Respond directly to the user in Markdown.");
            sb.AppendLine("Do not output JSON control structures such as status/tool/meta.");
            sb.AppendLine("Use concise explanations, fenced code blocks, Markdown tables, and LaTeX when helpful.");
            sb.AppendLine("For short inline values like paths, filenames, commands, or variable names, use inline `backticks` instead of fenced code blocks.");
            if (request.Capability?.AllowedToolNames is { Count: > 0 })
                sb.AppendLine("If a task requires tools, explain the limitation briefly instead of emitting tool-call JSON.");
        }
        else
        {
            sb.Append(BuildLoopInstructions(request.Capability, request.WorkspaceId, request));
        }
    }

    private string BuildLoopInstructions(CapabilityPolicy? capability, string workspaceId, ContextRequest request)
    {
        if (_toolRegistry is null)
            return _skillRuntime.BuildLoopInstructions(capability);

        var registryTools = _toolRegistry.ListAvailable(capability, workspaceId);

        // 前缀稳定：Available Tools 清单只列可见集（Core ∪ loaded），与 L1-TOOLS 索引和
        // tools 参数口径一致。全注册表清单会把 registry/MCP 变化放大成 RUNTIME 层字节
        // 漂移，破坏稳定前缀缓存。LoadedToolIds 为 null（非 Agent 路径）保持旧行为。
        if (request.LoadedToolIds is { } loaded)
        {
            var visibleIds = new HashSet<string>(ToolExposurePlanner.CoreToolIds, StringComparer.OrdinalIgnoreCase);
            visibleIds.UnionWith(loaded);
            var visibleTools = registryTools
                .Where(tool => visibleIds.Contains(tool.ToolId))
                .ToList();
            if (visibleTools.Count > 0)
                return ToolLoopInstructionBuilder.BuildFromDescriptors(visibleTools);
        }

        return ToolLoopInstructionBuilder.BuildFromDescriptors(registryTools);
    }

    // ═══════════════════════════════════════════════════════════════
    // L5 helper methods: FormatHistoryEntry, IsHeartbeatContent, SummarizeOlderHistory
    // Used by BuildRecentHistoryLayer.
    // ═══════════════════════════════════════════════════════════════

    private static string FormatHistoryEntry(ChatMessage msg)
    {
        if (IsHeartbeatContent(msg.Content))
        {
            return $"[System:heartbeat]: 系统心跳（已忽略重复内容）\n";
        }

        var role = msg.Role switch
        {
            _ when msg.Role == ChatRole.User => "User",
            _ when msg.Role == ChatRole.Assistant => "Assistant",
            _ when msg.Role == ChatRole.System => "System",
            _ => msg.Role.ToString(),
        };
        var text = TruncateText(msg.Content ?? "", 800);
        return $"[{role}]: {text}\n";
    }

    private static bool IsHeartbeatContent(string? content) =>
        content is not null && content.Contains("── 系统心跳 ──", StringComparison.Ordinal);

    private static string SummarizeOlderHistory(List<ChatMessage> olderMessages)
    {
        if (olderMessages.Count == 0) return "No prior history.";
        var userMsgs = olderMessages.Count(m => m.Role == ChatRole.User);
        var assistantMsgs = olderMessages.Count(m => m.Role == ChatRole.Assistant);
        return $"Earlier conversation: {userMsgs} user messages, {assistantMsgs} assistant replies. Topics: general discussion.";
    }
}
