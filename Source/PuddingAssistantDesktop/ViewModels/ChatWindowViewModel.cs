using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PuddingAssistant.Abstractions;
using PuddingAssistant.Models;
using Skills = PuddingAssistant.Skills;
using PuddingAssistant.Skills;
using PuddingAssistantDesktop.Models;

namespace PuddingAssistantDesktop.ViewModels;

/// <summary>
/// ViewModel for the compact chat window that opens beside the pudding spirit.
/// Streams LLM responses token-by-token via <see cref="ILlmGateway"/>.
/// Supports tool calling via <see cref="ISkillRegistry"/>.
/// Falls back to echo mode when no LLM is configured.
/// </summary>
public partial class ChatWindowViewModel : ViewModelBase
{
    private const string SystemPrompt =
        """
        You are Pudding (布丁), a cute desktop spirit companion.
        You are cheerful, helpful, and speak in a warm, friendly tone.
        Keep responses concise (2-4 sentences) unless the user asks for detail.
        You can use emoji sparingly. Respond in the same language the user uses.

        You have access to skills (tools) that let you interact with the user's system.
        When the user asks you to do something that requires a skill, use the appropriate tool.
        After using a tool, summarize the result in a friendly way.
        """;

    private readonly ILlmGateway? _llm;
    private readonly ISkillRegistry? _skillRegistry;
    private readonly IReadOnlyList<ITool> _tools;
    private readonly List<ChatMessage> _llmHistory = [];
    private CancellationTokenSource? _streamCts;

    /// <summary>Chat message history displayed in UI.</summary>
    public ObservableCollection<ChatEntry> Messages { get; } = [];

    /// <summary>Files attached to the next message.</summary>
    public ObservableCollection<AttachmentItem> Attachments { get; } = [];

    /// <summary>Whether any files are attached (drives UI visibility).</summary>
    public bool HasAttachments => Attachments.Count > 0;

    /// <summary>Current text in the input box.</summary>
    [ObservableProperty] private string _inputText = string.Empty;

    /// <summary>Whether the chat is waiting for a response.</summary>
    [ObservableProperty] private bool _isWaiting;

    /// <summary>Status text shown in the title bar.</summary>
    [ObservableProperty] private string _statusText = string.Empty;

    /// <summary>Fired when the user sends a message (for the spirit to react).</summary>
    public event Action<string>? MessageSent;

    /// <summary>Creates a chat VM with optional LLM gateway and skill registry.</summary>
    public ChatWindowViewModel(ILlmGateway? llm = null, ISkillRegistry? skillRegistry = null)
    {
        _llm = llm;
        _skillRegistry = skillRegistry;

        // Build tool list from skill registry for LLM function calling
        if (_skillRegistry is not null)
        {
            _tools = _skillRegistry.GetSkills(Skills.AgentRole.Spirit)
                .Select(s => (ITool)new SkillTool(s, _skillRegistry, Skills.AgentRole.Spirit))
                .ToList();
        }
        else
        {
            _tools = [];
        }

        // Build system prompt with skill context
        var fullPrompt = SystemPrompt;
        if (_tools.Count > 0)
        {
            var skillList = string.Join("\n", _tools.Select(t => $"  - {t.Name}: {t.Description}"));
            fullPrompt += $"\n\n## Available Skills ({_tools.Count})\n{skillList}";
        }

        if (_llm is not null)
        {
            _llmHistory.Add(new ChatMessage(ChatRole.System, fullPrompt));
            StatusText = $"Connected · {_tools.Count} skills";
        }
        else
        {
            StatusText = "No LLM configured";
        }

        // Welcome message with diagnostic info
        AddDiagnosticMessages();
    }

    /// <summary>Sends the current input as a user message, including any attachments.</summary>
    [RelayCommand(CanExecute = nameof(CanSend))]
    private async void Send()
    {
        var text = InputText.Trim();
        if (string.IsNullOrEmpty(text) && Attachments.Count == 0) return;

        // Build display text with attachment info
        var displayText = text;
        if (Attachments.Count > 0)
        {
            var fileList = string.Join(", ", Attachments.Select(a => a.DisplayName));
            displayText += string.IsNullOrEmpty(text)
                ? $"📎 {fileList}"
                : $"\n📎 {fileList}";
        }

        Messages.Add(new ChatEntry { Kind = ChatEntryKind.User, Content = displayText });

        // Build LLM message with attachment contents
        var llmText = text;
        if (Attachments.Count > 0)
        {
            var sb = new StringBuilder(text);
            foreach (var attachment in Attachments)
            {
                sb.AppendLine();
                sb.AppendLine($"--- Attached file: {attachment.DisplayName} ({attachment.SizeText}) ---");
                try
                {
                    var content = await System.IO.File.ReadAllTextAsync(attachment.FilePath);
                    // Limit to 8K chars to avoid blowing context
                    if (content.Length > 8192)
                        content = content[..8192] + $"\n... (truncated, {content.Length} total chars)";
                    sb.AppendLine(content);
                }
                catch
                {
                    sb.AppendLine($"[Binary file or unreadable: {attachment.Icon} {attachment.DisplayName}]");
                }
                sb.AppendLine("--- End of attached file ---");
            }
            llmText = sb.ToString();
        }

        // Clear state
        InputText = string.Empty;
        Attachments.Clear();
        OnPropertyChanged(nameof(HasAttachments));
        IsWaiting = true;

        MessageSent?.Invoke(displayText);

        if (_llm is not null)
        {
            await StreamLlmResponseAsync(llmText);
        }
        else
        {
            AddPuddingReply($"(No LLM) You said: \"{displayText}\"");
        }
    }

    private bool CanSend() => (!string.IsNullOrWhiteSpace(InputText) || Attachments.Count > 0) && !IsWaiting;

    partial void OnInputTextChanged(string value) => SendCommand.NotifyCanExecuteChanged();
    partial void OnIsWaitingChanged(bool value) => SendCommand.NotifyCanExecuteChanged();

    /// <summary>Streams an LLM response token-by-token, handling tool calls and thinking chain.</summary>
    private async System.Threading.Tasks.Task StreamLlmResponseAsync(string userText)
    {
        _llmHistory.Add(new ChatMessage(ChatRole.User, userText));

        // May loop if LLM returns tool calls — execute them and let LLM see results
        const int maxToolRounds = 5;
        for (var round = 0; round < maxToolRounds; round++)
        {
            StatusText = round == 0 ? "Thinking..." : $"Tool round {round + 1}...";
            _streamCts = new CancellationTokenSource();
            var ct = _streamCts.Token;

            ChatEntry? answerEntry = null;
            var reasoningSb = new StringBuilder();
            var answerSb = new StringBuilder();

            // Tool call accumulators
            var tcIds = new Dictionary<int, string>();
            var tcNames = new Dictionary<int, StringBuilder>();
            var tcArgs = new Dictionary<int, StringBuilder>();

            var lastRenderTick = Environment.TickCount64;

            try
            {
                await foreach (var delta in _llm!.ChatStreamAsync(_llmHistory, _tools, ct))
                {
                    // Reasoning tokens
                    if (delta.ReasoningDelta is { } reasoningChunk)
                    {
                        answerEntry ??= CreateStreamingEntry();
                        reasoningSb.Append(reasoningChunk);
                        answerEntry.ReasoningContent = reasoningSb.ToString();
                        StatusText = "Reasoning...";
                    }

                    // Content tokens
                    if (delta.ContentDelta is { } chunk)
                    {
                        answerEntry ??= CreateStreamingEntry();
                        answerSb.Append(chunk);
                        answerEntry.Content = answerSb.ToString();
                        StatusText = "Responding...";
                    }

                    // Tool call deltas
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

                    // Yield to UI periodically
                    var now = Environment.TickCount64;
                    if (now - lastRenderTick >= 30)
                    {
                        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
                        lastRenderTick = Environment.TickCount64;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                if (answerEntry is not null)
                {
                    answerEntry.Content = answerSb.Length > 0 ? answerSb + " [cancelled]" : "[cancelled]";
                    answerEntry.IsStreaming = false;
                }
                StatusText = "Cancelled";
                IsWaiting = false;
                _streamCts?.Dispose();
                _streamCts = null;
                return;
            }
            catch (Exception ex)
            {
                Messages.Add(new ChatEntry { Kind = ChatEntryKind.Error, Content = $"Error: {ex.Message}" });
                StatusText = "Error";
                IsWaiting = false;
                _streamCts?.Dispose();
                _streamCts = null;
                return;
            }

            // Assemble tool calls
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

            var content = answerSb.Length > 0 ? answerSb.ToString() : null;
            var reasoning = reasoningSb.Length > 0 ? reasoningSb.ToString() : null;

            // No tool calls → final answer, done
            if (toolCalls is null or { Count: 0 })
            {
                if (answerEntry is not null)
                    answerEntry.IsStreaming = false;

                _llmHistory.Add(new ChatMessage(ChatRole.Assistant, content ?? "",
                    ReasoningContent: reasoning));
                StatusText = $"Connected · {_tools.Count} skills";
                IsWaiting = false;
                _streamCts?.Dispose();
                _streamCts = null;
                return;
            }

            // Record assistant message with tool_calls
            _llmHistory.Add(new ChatMessage(ChatRole.Assistant, content,
                ToolCalls: toolCalls, ReasoningContent: reasoning));

            // Execute each tool call
            foreach (var call in toolCalls)
            {
                // Show tool call in chat
                Messages.Add(new ChatEntry
                {
                    Kind = ChatEntryKind.ToolCall,
                    Content = $"⚙️ Calling `{call.Name}`..."
                });

                var result = await ExecuteToolAsync(call, ct);

                // Show tool result in chat
                Messages.Add(new ChatEntry
                {
                    Kind = ChatEntryKind.ToolResult,
                    Content = result.Length > 500 ? result[..500] + "..." : result
                });

                _llmHistory.Add(new ChatMessage(ChatRole.Tool, result, ToolCallId: call.Id));
            }

            _streamCts?.Dispose();
            _streamCts = null;
            // Loop back — let LLM see tool results
        }

        // Exhausted max rounds
        AddPuddingReply("⚠️ Reached maximum tool rounds. Please try again.");
        IsWaiting = false;
    }

    /// <summary>Executes a single tool call, finding the matching ITool.</summary>
    private async System.Threading.Tasks.Task<string> ExecuteToolAsync(ToolCall call, CancellationToken ct)
    {
        var tool = _tools.FirstOrDefault(t => t.Name.Equals(call.Name, StringComparison.OrdinalIgnoreCase));
        if (tool is null)
            return $"Error: unknown skill '{call.Name}'";

        try
        {
            StatusText = $"Running {call.Name}...";
            return await tool.ExecuteAsync(call.ArgumentsJson, ct);
        }
        catch (Exception ex)
        {
            return $"Error executing {call.Name}: {ex.Message}";
        }
    }

    /// <summary>Creates a new streaming answer entry and adds it to Messages.</summary>
    private ChatEntry CreateStreamingEntry()
    {
        var entry = new ChatEntry
        {
            Kind = ChatEntryKind.Answer,
            Content = "",
            IsStreaming = true
        };
        Messages.Add(entry);
        return entry;
    }

    /// <summary>Adds startup diagnostic messages showing injected context.</summary>
    private void AddDiagnosticMessages()
    {
        if (_llm is null)
        {
            Messages.Add(new ChatEntry
            {
                Kind = ChatEntryKind.System,
                Content = "🍮 Hi! Configure ~/.pudding/config.json to enable AI chat."
            });
            return;
        }

        Messages.Add(new ChatEntry
        {
            Kind = ChatEntryKind.System,
            Content = "🍮 Hi! I'm Pudding — ask me anything!"
        });

        // Skill diagnostic log
        if (_skillRegistry is not null)
        {
            var skills = _skillRegistry.GetSkills(Skills.AgentRole.Spirit);
            var groups = skills.GroupBy(s => s.Group).OrderBy(g => g.Key);
            var sb = new StringBuilder();
            sb.AppendLine($"📋 **Context Injection Report** — Role: `Spirit`");
            sb.AppendLine($"  ✅ SkillRegistry loaded: **{skills.Count}** skills as **{_tools.Count}** tools");

            foreach (var group in groups)
            {
                sb.AppendLine($"  📦 **{group.Key}**: {string.Join(", ", group.Select(s => $"`{s.Name}`"))}");
            }

            sb.AppendLine($"  📝 System prompt: {_llmHistory[0].Content?.Length ?? 0} chars");
            sb.Append("  💡 Try: \"list your skills\" or \"read a file\" to test tool calling");

            Messages.Add(new ChatEntry
            {
                Kind = ChatEntryKind.System,
                Content = sb.ToString()
            });
        }
        else
        {
            Messages.Add(new ChatEntry
            {
                Kind = ChatEntryKind.System,
                Content = "⚠️ No SkillRegistry injected — tool calling disabled."
            });
        }
    }

    /// <summary>Adds a reply from the pudding spirit (used by non-LLM sources).</summary>
    public void AddPuddingReply(string content)
    {
        Messages.Add(new ChatEntry { Kind = ChatEntryKind.Answer, Content = content });
        IsWaiting = false;
    }

    /// <summary>Adds pasted clipboard content as a message.</summary>
    public void AddClipboardContent(string description)
    {
        Messages.Add(new ChatEntry { Kind = ChatEntryKind.ToolResult, Content = $"📋 {description}" });
    }

    /// <summary>Adds a file attachment to the pending message.</summary>
    public void AddAttachment(string filePath)
    {
        // Avoid duplicates
        if (Attachments.Any(a => a.FilePath == filePath)) return;

        Attachments.Add(new AttachmentItem(filePath, RemoveAttachment));
        OnPropertyChanged(nameof(HasAttachments));
    }

    private void RemoveAttachment(AttachmentItem item)
    {
        Attachments.Remove(item);
        OnPropertyChanged(nameof(HasAttachments));
    }
}
