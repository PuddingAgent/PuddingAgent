using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Skills;

namespace PuddingCode.Core;

public sealed class AgentOrchestrator : IAgentOrchestrator
{
    private readonly ILlmGateway _llm;
    private readonly IToolRegistry _tools;
    private readonly IGitSnapshot? _snapshot;
    private readonly ISkillRegistry? _skillRegistry;
    private readonly AgentRole _role;
    private readonly WorkerScope? _scope;
    private readonly List<ChatMessage> _history;
    private readonly List<IAgentHook> _hooks;
    private readonly ContextBudgetOptions _contextBudget;
    private readonly IHistoryCompressor? _historyCompressor;

    /// <summary>
    /// Gets the role of this Agent instance.
    /// Default is <see cref="AgentRole.Spirit"/> for backward compatibility.
    /// </summary>
    public AgentRole Role => _role;

    /// <summary>
    /// Gets the optional Worker scope for this Agent instance.
    /// </summary>
    public WorkerScope? Scope => _scope;

    public AgentOrchestrator(
        ILlmGateway llm,
        IToolRegistry tools,
        ProjectContext? project = null,
        IGitSnapshot? snapshot = null,
        AgentRole role = AgentRole.Spirit,
        WorkerScope? scope = null,
        ISkillRegistry? skillRegistry = null,
        IEnumerable<IAgentHook>? hooks = null,
        ContextBudgetOptions? contextBudget = null,
        IHistoryCompressor? historyCompressor = null)
    {
        _llm = llm;
        _tools = tools;
        _snapshot = snapshot;
        _role = role;
        _scope = scope;
        _skillRegistry = skillRegistry;
        _hooks = hooks?.ToList() ?? [];
        _contextBudget = contextBudget ?? new ContextBudgetOptions();
        _historyCompressor = historyCompressor;

        var systemPrompt = PromptTemplateLoader.BuildSystemPrompt(role, scope, project);

        _history = [new ChatMessage(ChatRole.System, systemPrompt)];
    }

    /// <summary>
    /// Gets role-filtered tools for LLM function calling.
    /// When SkillRegistry is available, returns skills filtered by the Agent's role.
    /// Otherwise, returns all registered tools.
    /// </summary>
    private IReadOnlyList<ITool> GetRoleFilteredTools()
    {
        // If SkillRegistry is available, use role-filtered skills as tools
        if (_skillRegistry is not null)
        {
            // Create a temporary registry with role-filtered skills
            var roleTools = new List<ITool>();
            foreach (var skill in _skillRegistry.GetSkills(_role))
            {
                roleTools.Add(new SkillTool(skill, _skillRegistry, _role));
            }

            // Also include base tools (non-skill tools like FileTool, ShellTool)
            foreach (var tool in _tools.GetAllTools())
            {
                // Avoid duplicates if skill tools were already registered as base tools
                if (!roleTools.Any(t => t.Name == tool.Name))
                {
                    roleTools.Add(tool);
                }
            }

            return roleTools;
        }

        // No SkillRegistry - use all registered tools (backward compatible)
        return _tools.GetAllTools();
    }

    private const int MaxToolIterations = 20;

    public async IAsyncEnumerable<AgentEvent> ProcessAsync(
        string userInput,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        _history.Add(new ChatMessage(ChatRole.User, userInput));

        var iterations = 0;
        while (true)
        {
            if (iterations++ >= MaxToolIterations)
            {
                yield return new ErrorEvent($"Tool call loop exceeded {MaxToolIterations} iterations. Stopping to prevent infinite loop.");
                yield break;
            }

            if (_historyCompressor is not null)
                await ContextBudgetManager.CompressAndTrimAsync(_history, _contextBudget, _historyCompressor, ct);
            else
                ContextBudgetManager.TrimInPlace(_history, _contextBudget);
            yield return new ThinkingEvent("Calling LLM...");

            // Use a Channel so the SSE reader (in try/catch) can push events
            // while we yield them in real-time (outside try/catch).
            var channel = Channel.CreateUnbounded<AgentEvent>();
            var contentBuf = new StringBuilder();
            var reasoningBuf = new StringBuilder();
            var tcIds = new Dictionary<int, string>();
            var tcNames = new Dictionary<int, StringBuilder>();
            var tcArgs = new Dictionary<int, StringBuilder>();
            string? streamError = null;

            // Producer: read SSE stream, push events into channel
            _ = Task.Run(async () =>
            {
                try
                {
                    // Use role-filtered tools when SkillRegistry is available
                    var tools = GetRoleFilteredTools();
                    await foreach (var delta in _llm.ChatStreamAsync(
                        _history, tools, ct))
                    {
                        if (delta.ReasoningDelta is not null)
                        {
                            reasoningBuf.Append(delta.ReasoningDelta);
                            await channel.Writer.WriteAsync(
                                new ReasoningEvent(delta.ReasoningDelta), ct);
                        }

                        if (delta.ContentDelta is not null)
                        {
                            contentBuf.Append(delta.ContentDelta);
                            await channel.Writer.WriteAsync(
                                new StreamingAnswerEvent(delta.ContentDelta), ct);
                        }

                        if (delta.ToolCallIndex is { } idx)
                        {
                            if (delta.ToolCallId is not null)
                                tcIds[idx] = delta.ToolCallId;
                            if (delta.ToolCallNameDelta is not null)
                            {
                                if (!tcNames.ContainsKey(idx))
                                    tcNames[idx] = new StringBuilder();
                                tcNames[idx].Append(delta.ToolCallNameDelta);
                            }
                            if (delta.ToolCallArgsDelta is not null)
                            {
                                if (!tcArgs.ContainsKey(idx))
                                    tcArgs[idx] = new StringBuilder();
                                tcArgs[idx].Append(delta.ToolCallArgsDelta);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    streamError = $"LLM request failed: {ex.Message}";
                }
                finally
                {
                    channel.Writer.Complete();
                }
            }, ct);

            // Consumer: yield events in real-time as they arrive
            await foreach (var evt in channel.Reader.ReadAllAsync(ct))
                yield return evt;

            if (streamError is not null)
            {
                yield return new ErrorEvent(streamError);
                yield break;
            }

            // ── Assemble accumulated response ──
            var content = contentBuf.Length > 0 ? contentBuf.ToString() : null;
            var reasoning = reasoningBuf.Length > 0 ? reasoningBuf.ToString() : null;

            List<ToolCall>? toolCalls = null;
            if (tcIds.Count > 0)
            {
                toolCalls = [];
                foreach (var idx in tcIds.Keys.OrderBy(k => k))
                {
                    toolCalls.Add(new ToolCall(
                        tcIds.GetValueOrDefault(idx, $"tc_{idx}"),
                        tcNames.TryGetValue(idx, out var n) ? n.ToString() : "",
                        tcArgs.TryGetValue(idx, out var a) ? a.ToString() : "{}"));
                }
            }

            // No tool calls → final answer
            if (toolCalls is null or { Count: 0 })
            {
                var answer = content ?? "";
                await FireHookAsync(h => h.OnPreReplyAsync(answer, ct), ct);
                _history.Add(new ChatMessage(ChatRole.Assistant, answer,
                    ReasoningContent: reasoning));
                yield return new AnswerEvent(answer);
                await FireHookAsync(h => h.OnPostReplyAsync(answer, ct), ct);
                yield break;
            }

            // Record assistant message with tool_calls + reasoning_content
            _history.Add(new ChatMessage(ChatRole.Assistant, content,
                ToolCalls: toolCalls,
                ReasoningContent: reasoning));

            // Auto-snapshot before tool execution (D06)
            if (_snapshot is { IsGitRepo: true })
            {
                var toolNames = string.Join("+", toolCalls.Select(c => c.Name));
                await _snapshot.CreateSnapshotAsync($"pre-tool: {toolNames}", ct);
            }

            // Execute each tool call
            foreach (var call in toolCalls)
            {
                await FireHookAsync(h => h.OnPreToolCallAsync(call, ct), ct);
                yield return new ToolCallEvent(call.Name, call.ArgumentsJson);

                var (result, evt) = await ExecuteToolSafe(call, ct);
                yield return evt;
                await FireHookAsync(h => h.OnPostToolCallAsync(call, result, evt is ErrorEvent, ct), ct);

                _history.Add(new ChatMessage(ChatRole.Tool, result, ToolCallId: call.Id));
            }

            // Loop back — let LLM see tool results
        }
    }

    private async Task<(string Result, AgentEvent Event)> ExecuteToolSafe(
        ToolCall call, CancellationToken ct)
    {
        var tool = _tools.GetTool(call.Name);
        if (tool is null)
        {
            var msg = $"Error: unknown tool '{call.Name}'";
            return (msg, new ErrorEvent(msg));
        }

        try
        {
            var result = await tool.ExecuteAsync(call.ArgumentsJson, ct);
            return (result, new ToolResultEvent(call.Name, result));
        }
        catch (Exception ex)
        {
            var msg = $"Error executing tool '{call.Name}': {ex.Message}";
            return (msg, new ErrorEvent(msg));
        }
    }

    private async Task FireHookAsync(Func<IAgentHook, Task> invoke, CancellationToken ct)
    {
        foreach (var hook in _hooks)
        {
            try
            {
                await invoke(hook);
            }
            catch
            {
                // Hook failures should not break the coding loop.
            }
        }
    }
}
