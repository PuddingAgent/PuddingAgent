using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using PuddingCode.Abstractions;
using PuddingCode.Models;

namespace PuddingCode.Core;

public sealed class AgentOrchestrator : IAgentOrchestrator
{
    private readonly ILlmGateway _llm;
    private readonly IToolRegistry _tools;
    private readonly IGitSnapshot? _snapshot;
    private readonly List<ChatMessage> _history;

    public AgentOrchestrator(
        ILlmGateway llm,
        IToolRegistry tools,
        ProjectContext? project = null,
        IGitSnapshot? snapshot = null)
    {
        _llm = llm;
        _tools = tools;
        _snapshot = snapshot;

        var systemPrompt = """
            You are PuddingCode, an AI programming assistant.
            Use the provided tools to help the user with coding tasks.
            Always use tools when the user asks to read files, write files, or run commands.
            After using a tool, summarize the result for the user.
            """;

        if (project is not null)
        {
            systemPrompt += $"""


            Current project: {project.Name}
            Project root: {project.RootPath}
            All relative file paths are resolved from the project root.
            When using the file tool, use paths relative to the project root.
            When using the shell tool, commands run in the project root by default.
            """;
        }

        _history = [new ChatMessage(ChatRole.System, systemPrompt)];
    }

    public async IAsyncEnumerable<AgentEvent> ProcessAsync(
        string userInput,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        _history.Add(new ChatMessage(ChatRole.User, userInput));

        while (true)
        {
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
                    await foreach (var delta in _llm.ChatStreamAsync(
                        _history, _tools.GetAllTools(), ct))
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
                _history.Add(new ChatMessage(ChatRole.Assistant, answer,
                    ReasoningContent: reasoning));
                yield return new AnswerEvent(answer);
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
                yield return new ToolCallEvent(call.Name, call.ArgumentsJson);

                var (result, evt) = await ExecuteToolSafe(call, ct);
                yield return evt;

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
}
