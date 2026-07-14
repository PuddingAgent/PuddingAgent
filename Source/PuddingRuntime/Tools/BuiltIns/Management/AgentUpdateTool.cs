using System.Text.Json;
using PuddingCode.Agents;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// 让 Agent 更新自己的 Persona 文件（实例级别，不影响共享同模板的其他 Agent）。
/// 写入 data/agents/{agentInstanceId}/persona/{LAYER}.md。
/// </summary>
// [Tool(  // 临时移除：ContextPipeline 构造函数 30+ 参数导致 DI 首次解析阻塞
//     id: "agent_update",
//     name: "Update agent persona",
//     description: "Update this agent's own persona files...",
//     category: ToolCategory.Orchestration,
//     permission: ToolPermissionLevel.High,
//     safety: ToolSafetyFlags.Destructive | ToolSafetyFlags.RequiresFileWrite)]
public sealed class AgentUpdateTool : PuddingToolBase<AgentUpdateArgs>
{
    private readonly AgentPersonaFileProvider _personaFileProvider;
    // private readonly ContextPipeline _contextPipeline;
    private readonly ILogger<AgentUpdateTool> _logger;

    private static readonly Dictionary<string, string> LayerToFileName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["soul"] = "SOUL.md",
        ["tools"] = "TOOLS.md",
        ["agents"] = "AGENTS.md",
        ["bootstrap"] = "BOOTSTRAP.md",
        ["memory"] = "MEMORY.md",
        ["identity"] = "IDENTITY.md",
        ["user"] = "USER.md",
    };

    public AgentUpdateTool(
        AgentPersonaFileProvider personaFileProvider,
        // ContextPipeline contextPipeline,
        ILogger<AgentUpdateTool> logger)
    {
        _personaFileProvider = personaFileProvider;
        // _contextPipeline = contextPipeline;
        _logger = logger;
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        AgentUpdateArgs args, ToolExecutionContext context, CancellationToken ct)
    {
        var layer = args.Layer?.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(layer))
            return ToolExecutionResult.Fail("layer 参数是必填的。支持: soul, tools, agents, bootstrap, memory, identity, user, all.");

        var agentInstanceId = context.AgentInstanceId;
        if (string.IsNullOrWhiteSpace(agentInstanceId))
            return ToolExecutionResult.Fail("无法获取 Agent 实例 ID，操作被拒绝。");

        var written = new List<string>();
        var errors = new List<string>();

        try
        {
            if (layer == "all")
            {
                WriteIfNotEmpty(agentInstanceId, args.Soul, "SOUL.md", written);
                WriteIfNotEmpty(agentInstanceId, args.Tools, "TOOLS.md", written);
                WriteIfNotEmpty(agentInstanceId, args.Agents, "AGENTS.md", written);
                WriteIfNotEmpty(agentInstanceId, args.Bootstrap, "BOOTSTRAP.md", written);
                WriteIfNotEmpty(agentInstanceId, args.Memory, "MEMORY.md", written);
                WriteIfNotEmpty(agentInstanceId, args.Identity, "IDENTITY.md", written);
            }
            else if (LayerToFileName.TryGetValue(layer, out var fileName))
            {
                var content = layer switch
                {
                    "soul" => args.Content ?? args.Soul,
                    "tools" => args.Content ?? args.Tools,
                    "agents" => args.Content ?? args.Agents,
                    "bootstrap" => args.Content ?? args.Bootstrap,
                    "memory" => args.Content ?? args.Memory,
                    "identity" => args.Content ?? args.Identity,
                    "user" => args.Content,
                    _ => args.Content,
                };

                if (string.IsNullOrWhiteSpace(content))
                    return ToolExecutionResult.Fail($"layer '{layer}' 需要 content 参数（或对应的具名参数）。");

                WriteOne(agentInstanceId, fileName, content, written);
            }
            else
            {
                return ToolExecutionResult.Fail(
                    $"未知的 layer '{layer}'。支持: {string.Join(", ", LayerToFileName.Keys)}, all.");
            }

            // 刷新静态上下文缓存，使下一轮对话加载新的 Persona
            // _contextPipeline.InvalidateSession(context.SessionId);
            _logger.LogInformation("[AgentUpdate] Updated {Count} persona files for agent={Agent} session={Session}",
                written.Count, agentInstanceId, context.SessionId);

            return ToolExecutionResult.Ok(JsonSerializer.Serialize(new
            {
                updated_files = written,
                agent = agentInstanceId,
                next_turn_effective = true,
                note = "Persona 已更新。下一轮对话将使用新配置。"
            }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[AgentUpdate] Failed agent={Agent}", agentInstanceId);
            return ToolExecutionResult.Fail($"更新失败: {ex.Message}");
        }
    }

    private void WriteOne(string agentInstanceId, string fileName, string content, List<string> written)
    {
        _personaFileProvider.Write(agentInstanceId, fileName, content);
        written.Add(fileName);
    }

    private void WriteIfNotEmpty(string agentInstanceId, string? content, string fileName, List<string> written)
    {
        if (string.IsNullOrWhiteSpace(content)) return;
        WriteOne(agentInstanceId, fileName, content, written);
    }
}

public sealed record AgentUpdateArgs
{
    [ToolParam("Which layer to update: soul, tools, agents, bootstrap, memory, identity, user, or all.")]
    public string? Layer { get; init; }

    [ToolParam("New Markdown content (for single-layer update).")]
    public string? Content { get; init; }

    [ToolParam("SOUL.md content (use with layer=all).")]
    public string? Soul { get; init; }

    [ToolParam("TOOLS.md content (use with layer=all).")]
    public string? Tools { get; init; }

    [ToolParam("AGENTS.md content (use with layer=all).")]
    public string? Agents { get; init; }

    [ToolParam("BOOTSTRAP.md content (use with layer=all).")]
    public string? Bootstrap { get; init; }

    [ToolParam("MEMORY.md content (use with layer=all).")]
    public string? Memory { get; init; }

    [ToolParam("IDENTITY.md content (use with layer=all).")]
    public string? Identity { get; init; }
}
