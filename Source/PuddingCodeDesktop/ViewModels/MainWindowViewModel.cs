using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PuddingCode.Core;
using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingCodeDesktop.Models;
using PuddingCodeDesktop.Services;

namespace PuddingCodeDesktop.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    // ──── Infrastructure ────
    private readonly DesktopConfig _config;
    private readonly HttpClient _httpClient = new();
    private CancellationTokenSource? _agentCts;

    // ──── View Switching ────

    [ObservableProperty] private bool _isEditorView = true;
    [ObservableProperty] private bool _isSwarmView;

    public IBrush EditorButtonBg => IsEditorView ? Brushes.White : Brushes.Transparent;
    public IBrush SwarmButtonBg => IsSwarmView ? Brushes.White : Brushes.Transparent;
    public FontWeight EditorButtonWeight => IsEditorView ? FontWeight.Bold : FontWeight.Normal;
    public FontWeight SwarmButtonWeight => IsSwarmView ? FontWeight.Bold : FontWeight.Normal;

    partial void OnIsEditorViewChanged(bool value)
    {
        OnPropertyChanged(nameof(EditorButtonBg));
        OnPropertyChanged(nameof(EditorButtonWeight));
    }

    partial void OnIsSwarmViewChanged(bool value)
    {
        OnPropertyChanged(nameof(SwarmButtonBg));
        OnPropertyChanged(nameof(SwarmButtonWeight));
    }

    // ──── Observable properties ────

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendMessageCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "Ready";

    // Providers
    public ObservableCollection<ProviderInfo> Providers { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendMessageCommand))]
    private ProviderInfo? _activeProvider;

    // Chat (Editor view)
    public ObservableCollection<ChatEntry> ChatMessages { get; } = new();
    [ObservableProperty] private string _userInput = "";

    // Log
    public ObservableCollection<string> LogEntries { get; } = new();

    // Tasks (Editor view)
    public ObservableCollection<string> Tasks { get; } = new();
    [ObservableProperty] private string? _selectedTask;

    // Editors
    public ObservableCollection<string> OpenEditors { get; } = new();
    [ObservableProperty] private int _selectedEditorIndex;

    // Project tree
    public ObservableCollection<string> ProjectTree { get; } = new();

    // ──── Swarm State ────

    public ObservableCollection<SwarmAgent> SwarmAgents { get; } = new();
    [ObservableProperty] private SwarmAgent? _selectedSwarmAgent;
    [ObservableProperty] private string _swarmTaskInput = "";
    public ObservableCollection<string> SwarmEventLog { get; } = new();

    /// <summary>Leader agents for the topology center display.</summary>
    public ObservableCollection<SwarmAgent> LeaderAgents { get; } = new();
    /// <summary>Worker agents for the topology center display.</summary>
    public ObservableCollection<SwarmAgent> WorkerAgents { get; } = new();
    public bool HasWorkers => WorkerAgents.Count > 0;

    // ──── Bubble / Message Reader ────

    /// <summary>The bubble currently expanded in the reader panel.</summary>
    [ObservableProperty] private SwarmBubble? _readingBubble;

    /// <summary>Whether the message reader overlay is open.</summary>
    public bool IsReaderOpen => ReadingBubble is not null;

    /// <summary>Input text for sending a message to the selected agent.</summary>
    [ObservableProperty] private string _agentMessageInput = "";

    partial void OnReadingBubbleChanged(SwarmBubble? value)
        => OnPropertyChanged(nameof(IsReaderOpen));

    // ──── Constructor ────

    public MainWindowViewModel()
    {
        PuddingLogger.EntryLogged += OnLogEntry;

        PuddingLogger.Info("=== PuddingCode Desktop starting ===");
        PuddingLogger.Info($"Log file: {PuddingLogger.LogFilePath}");

        PuddingLogger.Info($"Loading config from: {DesktopConfigLoader.DefaultPath}");
        _config = DesktopConfigLoader.Load();
        PuddingLogger.Info($"Config loaded: {_config.Providers.Count} provider(s), active={_config.ActiveProvider ?? "(none)"}");

        foreach (var p in _config.Providers)
        {
            PuddingLogger.Info($"  Provider: id={p.Id}, model={p.Model}, endpoint={p.Endpoint}");
            Providers.Add(p);
        }

        ActiveProvider = Providers.FirstOrDefault(p => p.Id == _config.ActiveProvider)
                         ?? Providers.FirstOrDefault();

        PuddingLogger.Info($"ActiveProvider resolved to: {ActiveProvider?.Id ?? "NULL"}");

        AddSystemMessage("PuddingCode Desktop v0.1.0");
        if (Providers.Count == 0)
            AddSystemMessage("No providers configured. Run PuddingCodeCLI first to set up ~/.pudding/config.json");
        else
        {
            AddSystemMessage($"Provider: {ActiveProvider?.Id} ({ActiveProvider?.Model})");
            AddSystemMessage($"Log: {PuddingLogger.LogFilePath}");
        }

        // Editor scaffolding
        Tasks.Add("task-001: Implement AuthService.LoginAsync");
        Tasks.Add("task-002: Add unit tests for TokenManager");
        ProjectTree.Add("src/");
        ProjectTree.Add("tests/");
        OpenEditors.Add("Program.cs");
        OpenEditors.Add("AuthService.cs");

        // Swarm demo data
        InitSwarmDemoData();

        PuddingLogger.Info($"CanSendMessage={CanSendMessage()} (IsBusy={IsBusy}, ActiveProvider={(ActiveProvider is null ? "null" : "set")})");
        PuddingLogger.Info("Initialization complete");
    }

    private void InitSwarmDemoData()
    {
        var leader = new SwarmAgent
        {
            Id = "leader-0",
            DisplayName = "Leader",
            Role = AgentRole.Leader,
            Status = AgentStatus.Idle,
            Model = ActiveProvider?.Model ?? "gpt-4o",
            CurrentTask = "Orchestrating swarm"
        };
        var worker1 = new SwarmAgent
        {
            Id = "worker-1",
            DisplayName = "worker-auth",
            Role = AgentRole.Worker,
            Status = AgentStatus.Idle,
            Model = ActiveProvider?.Model ?? "deepseek-chat",
            ParentId = "leader-0",
            CurrentTask = "task-001: AuthService"
        };
        var worker2 = new SwarmAgent
        {
            Id = "worker-2",
            DisplayName = "worker-tests",
            Role = AgentRole.Worker,
            Status = AgentStatus.Idle,
            Model = ActiveProvider?.Model ?? "deepseek-chat",
            ParentId = "leader-0",
            CurrentTask = "task-002: Unit tests"
        };

        SwarmAgents.Add(leader);
        SwarmAgents.Add(worker1);
        SwarmAgents.Add(worker2);
        SelectedSwarmAgent = leader;

        RebuildTopologyViews();

        SwarmEventLog.Add($"[{DateTime.Now:HH:mm:ss}] Swarm initialized with 1 leader + 2 workers");
    }

    private void RebuildTopologyViews()
    {
        LeaderAgents.Clear();
        WorkerAgents.Clear();
        foreach (var a in SwarmAgents)
        {
            if (a.Role == AgentRole.Leader) LeaderAgents.Add(a);
            else WorkerAgents.Add(a);
        }
        OnPropertyChanged(nameof(HasWorkers));
    }

    // ──── Commands ────

    [RelayCommand(CanExecute = nameof(CanSendMessage))]
    private async Task SendMessageAsync()
    {
        PuddingLogger.Info(">>> SendMessageAsync invoked");

        if (string.IsNullOrWhiteSpace(UserInput))
        {
            PuddingLogger.Warn("UserInput is empty, aborting");
            return;
        }

        if (ActiveProvider is null)
        {
            PuddingLogger.Error("ActiveProvider is null, aborting");
            AddChat(ChatEntryKind.Error, "No provider configured. Run PuddingCodeCLI to set up.");
            return;
        }

        var input = UserInput.Trim();
        UserInput = "";
        IsBusy = true;
        StatusText = "Agent thinking...";

        PuddingLogger.Info($"User input: \"{input}\"");
        AddChat(ChatEntryKind.User, input);

        try
        {
            var provider = ActiveProvider;
            PuddingLogger.Info($"Using provider: id={provider.Id}, model={provider.Model}, endpoint={provider.Endpoint}");

            var options = new LlmOptions(
                provider.Endpoint,
                provider.ApiKey,
                provider.Model,
                provider.Temperature,
                provider.MaxTokens);

            PuddingLogger.Debug($"LlmOptions: endpoint={options.Endpoint}, model={options.Model}, temp={options.Temperature}, maxTokens={options.MaxTokens}");

            var gateway = new OpenAiLlmGateway(_httpClient, options);
            var registry = new ToolRegistry();
            registry.Register(new FileTool());
            registry.Register(new ShellTool());
            var agent = new AgentOrchestrator(gateway, registry);

            _agentCts = new CancellationTokenSource();

            PuddingLogger.Info("Starting agent.ProcessAsync...");
            var eventCount = 0;

            // Track current streaming entries for in-place append
            ChatEntry? currentReasoning = null;
            ChatEntry? currentAnswer = null;

            await foreach (var evt in agent.ProcessAsync(input, _agentCts.Token))
            {
                eventCount++;

                switch (evt)
                {
                    case ThinkingEvent e:
                        PuddingLogger.Info($"  Thinking: {e.Thought}");
                        DispatchAddChat(ChatEntryKind.Thinking, e.Thought);
                        currentReasoning = null;
                        currentAnswer = null;
                        break;

                    case ReasoningEvent e:
                        // Append delta to existing reasoning entry (stream in-place)
                        if (currentReasoning is null)
                        {
                            currentReasoning = new ChatEntry
                            {
                                Kind = ChatEntryKind.Reasoning,
                                Content = e.Delta,
                                IsStreaming = true
                            };
                            DispatchAction(() => ChatMessages.Add(currentReasoning));
                            PuddingLogger.Debug("  Reasoning: started");
                        }
                        else
                        {
                            var r = currentReasoning;
                            var d = e.Delta;
                            DispatchAction(() =>
                            {
                                r.Content += d;
                                RefreshLastChat();
                            });
                        }
                        break;

                    case StreamingAnswerEvent e:
                        // Finalize reasoning entry if still open
                        if (currentReasoning is { IsStreaming: true })
                        {
                            currentReasoning.IsStreaming = false;
                            PuddingLogger.Debug($"  Reasoning: done ({currentReasoning.Content.Length} chars)");
                        }

                        // Append delta to existing answer entry (stream in-place)
                        if (currentAnswer is null)
                        {
                            currentAnswer = new ChatEntry
                            {
                                Kind = ChatEntryKind.Answer,
                                Content = e.Delta,
                                IsStreaming = true
                            };
                            DispatchAction(() => ChatMessages.Add(currentAnswer));
                            PuddingLogger.Debug("  Answer: streaming started");
                        }
                        else
                        {
                            var a = currentAnswer;
                            var d = e.Delta;
                            DispatchAction(() =>
                            {
                                a.Content += d;
                                RefreshLastChat();
                            });
                        }
                        break;

                    case ToolCallEvent e:
                        FinalizeStreaming(ref currentReasoning, ref currentAnswer);
                        PuddingLogger.Info($"  ToolCall: {e.ToolName}");
                        DispatchAddChat(ChatEntryKind.ToolCall,
                            $"{e.ToolName}({Truncate(e.Arguments, 120)})", e.ToolName);
                        break;

                    case ToolResultEvent e:
                        PuddingLogger.Info($"  ToolResult: {e.ToolName} ({e.Result.Length} chars)");
                        DispatchAddChat(ChatEntryKind.ToolResult,
                            Truncate(e.Result, 500), e.ToolName);
                        break;

                    case AnswerEvent e:
                        FinalizeStreaming(ref currentReasoning, ref currentAnswer);
                        PuddingLogger.Info($"  Answer: {Truncate(e.Content, 200)}");
                        // If we already streamed the answer, just finalize; otherwise add new
                        if (currentAnswer is null)
                            DispatchAddChat(ChatEntryKind.Answer, e.Content);
                        break;

                    case ErrorEvent e:
                        FinalizeStreaming(ref currentReasoning, ref currentAnswer);
                        PuddingLogger.Error($"  AgentError: {e.Message}");
                        DispatchAddChat(ChatEntryKind.Error, e.Message);
                        break;
                }
            }

            FinalizeStreaming(ref currentReasoning, ref currentAnswer);

            PuddingLogger.Info($"Agent finished. Total events: {eventCount}");
        }
        catch (OperationCanceledException)
        {
            PuddingLogger.Warn("Agent cancelled by user");
            DispatchAddChat(ChatEntryKind.System, "Cancelled.");
        }
        catch (Exception ex)
        {
            PuddingLogger.Error("SendMessageAsync failed", ex);
            DispatchAddChat(ChatEntryKind.Error, $"Error: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            StatusText = "Ready";
            _agentCts = null;
            PuddingLogger.Info("<<< SendMessageAsync complete");
        }
    }

    private bool CanSendMessage() => !IsBusy && ActiveProvider is not null;

    [RelayCommand]
    private void CancelAgent()
    {
        PuddingLogger.Info("CancelAgent invoked");
        _agentCts?.Cancel();
    }

    // ──── View Switching ────

    [RelayCommand]
    private void SwitchToEditor()
    {
        IsEditorView = true;
        IsSwarmView = false;
        PuddingLogger.Info("Switched to Editor view");
    }

    [RelayCommand]
    private void SwitchToSwarm()
    {
        IsEditorView = false;
        IsSwarmView = true;
        PuddingLogger.Info("Switched to Swarm view");
    }

    // ──── Editor Commands ────

    [RelayCommand]
    private void CreateTask() => Tasks.Add($"task-{Tasks.Count + 1:D3}: New Task");

    // ──── Swarm Commands ────

    [RelayCommand]
    private void SpawnSwarm()
    {
        PuddingLogger.Info("SpawnSwarm (stub)");
        AddSwarmLog("Swarm spawn requested (not yet implemented)");
    }

    [RelayCommand]
    private void SpawnWorker()
    {
        var idx = SwarmAgents.Count(a => a.Role == AgentRole.Worker) + 1;
        var worker = new SwarmAgent
        {
            Id = $"worker-{idx}",
            DisplayName = $"worker-{idx}",
            Role = AgentRole.Worker,
            Status = AgentStatus.Idle,
            Model = ActiveProvider?.Model ?? "default",
            ParentId = SwarmAgents.FirstOrDefault(a => a.Role == AgentRole.Leader)?.Id
        };
        SwarmAgents.Add(worker);
        RebuildTopologyViews();
        AddSwarmLog($"Spawned {worker.DisplayName} ({worker.Model})");
        PuddingLogger.Info($"Spawned worker: {worker.Id}");
    }

    [RelayCommand]
    private void PauseAgent()
    {
        if (SelectedSwarmAgent is null) return;
        SelectedSwarmAgent.Status = AgentStatus.Idle;
        AddSwarmLog($"Paused {SelectedSwarmAgent.DisplayName}");
    }

    [RelayCommand]
    private void ResumeAgent()
    {
        if (SelectedSwarmAgent is null) return;
        SelectedSwarmAgent.Status = AgentStatus.Thinking;
        AddSwarmLog($"Resumed {SelectedSwarmAgent.DisplayName}");
    }

    [RelayCommand]
    private void KillAgent()
    {
        if (SelectedSwarmAgent is null) return;
        var name = SelectedSwarmAgent.DisplayName;
        SelectedSwarmAgent.Status = AgentStatus.Offline;
        SwarmAgents.Remove(SelectedSwarmAgent);
        SelectedSwarmAgent = SwarmAgents.FirstOrDefault();
        RebuildTopologyViews();
        AddSwarmLog($"Killed {name}");
        PuddingLogger.Info($"Killed agent: {name}");
    }

    [RelayCommand]
    private void AssignTask()
    {
        if (SelectedSwarmAgent is null || string.IsNullOrWhiteSpace(SwarmTaskInput)) return;
        var task = SwarmTaskInput.Trim();
        SelectedSwarmAgent.CurrentTask = task;
        SelectedSwarmAgent.Status = AgentStatus.Thinking;
        AddSwarmLog($"Assigned to {SelectedSwarmAgent.DisplayName}: {task}");
        SwarmTaskInput = "";
    }

    private void AddSwarmLog(string msg) =>
        SwarmEventLog.Add($"[{DateTime.Now:HH:mm:ss}] {msg}");

    [RelayCommand]
    private async Task SimulateSwarmAsync()
    {
        AddSwarmLog("▶ Simulation started");
        var leader = SwarmAgents.FirstOrDefault(a => a.Role == AgentRole.Leader);
        var workers = SwarmAgents.Where(a => a.Role == AgentRole.Worker).ToList();

        if (leader is null || workers.Count == 0) return;

        // Phase 1: Leader planning
        leader.Status = AgentStatus.Thinking;
        leader.CurrentTask = "Analyzing requirements...";
        AddSwarmLog($"{leader.DisplayName}: Planning task decomposition");
        await Task.Delay(1500);

        // Phase 2: Assign tasks to workers
        leader.Status = AgentStatus.ToolExecuting;
        leader.CurrentTask = "Dispatching tasks";
        for (var i = 0; i < workers.Count; i++)
        {
            var w = workers[i];
            w.Status = AgentStatus.Thinking;
            w.CurrentTask = $"task-{i + 1:D3}: Implementing feature {(char)('A' + i)}";
            w.TokensUsed = 0;
            AddSwarmLog($"{leader.DisplayName} → {w.DisplayName}: {w.CurrentTask}");

            // Leader sends a command bubble to the worker
            w.PushBubble(new SwarmBubble
            {
                SenderId = leader.Id,
                SenderName = leader.DisplayName,
                Content = $"Please implement feature {(char)('A' + i)}. Focus on correctness first, then optimize.",
                Kind = BubbleKind.Command,
            });
            await Task.Delay(800);
        }

        leader.Status = AgentStatus.Idle;
        leader.CurrentTask = "Monitoring workers";

        // Phase 3: Workers execute (cycle through states)
        foreach (var w in workers)
        {
            w.Status = AgentStatus.ToolExecuting;
            AddSwarmLog($"{w.DisplayName}: Executing tool calls...");
            w.TokensUsed += 342;
            await Task.Delay(1200);

            // Simulate one worker hitting an error
            if (w == workers.LastOrDefault() && workers.Count > 1)
            {
                w.Status = AgentStatus.Error;
                w.CurrentTask += " ⚠ compile error";
                AddSwarmLog($"{w.DisplayName}: ❌ Build failed — retrying");

                // Error bubble
                w.PushBubble(new SwarmBubble
                {
                    SenderId = w.Id,
                    SenderName = w.DisplayName,
                    Content = "Build failed: CS0246 — The type 'AuthToken' could not be found.\nAttempting auto-fix...",
                    Kind = BubbleKind.Blocked,
                });
                await Task.Delay(1000);

                w.Status = AgentStatus.Thinking;
                w.CurrentTask = w.CurrentTask.Replace(" ⚠ compile error", "");
                AddSwarmLog($"{w.DisplayName}: Analyzing error, fixing...");
                await Task.Delay(1000);

                w.Status = AgentStatus.ToolExecuting;
                w.TokensUsed += 128;
                await Task.Delay(800);
            }

            w.Status = AgentStatus.Completed;
            w.TokensUsed += 567;
            AddSwarmLog($"{w.DisplayName}: ✅ Task completed ({w.TokensUsed} tokens)");

            // Success bubble
            w.PushBubble(new SwarmBubble
            {
                SenderId = w.Id,
                SenderName = w.DisplayName,
                Content = $"Task completed successfully.\nTokens used: {w.TokensUsed}\nFiles modified: 3",
                Kind = BubbleKind.Success,
            });
            await Task.Delay(600);
        }

        // Phase 4: Leader summarizes
        leader.Status = AgentStatus.Thinking;
        leader.CurrentTask = "Reviewing results...";
        AddSwarmLog($"{leader.DisplayName}: Reviewing all worker outputs");
        await Task.Delay(1200);

        leader.Status = AgentStatus.Completed;
        leader.CurrentTask = "All tasks completed";
        AddSwarmLog("✅ Swarm simulation complete");
    }

    // ──── Bubble / Message Commands ────

    /// <summary>Send a user message to the selected agent (Commander → Agent).</summary>
    [RelayCommand]
    private void SendAgentMessage()
    {
        if (SelectedSwarmAgent is null || string.IsNullOrWhiteSpace(AgentMessageInput)) return;

        var text = AgentMessageInput.Trim();
        var agent = SelectedSwarmAgent;

        // Log the user→agent message
        AddSwarmLog($"👤 → {agent.DisplayName}: {text}");

        // Create the command bubble on the agent
        var cmdBubble = new SwarmBubble
        {
            SenderId = "user",
            SenderName = "Commander",
            Content = text,
            Kind = BubbleKind.Command,
        };
        agent.PushBubble(cmdBubble);
        AgentMessageInput = "";

        // Simulate the agent "receiving" and auto-replying after a short delay
        _ = SimulateAgentReplyAsync(agent, text);
    }

    private async Task SimulateAgentReplyAsync(SwarmAgent agent, string userMessage)
    {
        agent.Status = AgentStatus.Thinking;
        await Task.Delay(1500);

        var kind = BubbleKind.Info;
        var reply = $"Understood. Working on: \"{Truncate(userMessage, 40)}\"";

        // Vary the reply style for demo purposes
        if (userMessage.Contains("test", StringComparison.OrdinalIgnoreCase))
        {
            reply = $"Running tests for the requested scope. Will report results shortly.\n\nTarget: {userMessage}";
            kind = BubbleKind.Success;
        }
        else if (userMessage.Contains("fix", StringComparison.OrdinalIgnoreCase) ||
                 userMessage.Contains("error", StringComparison.OrdinalIgnoreCase))
        {
            reply = $"⚠️ Analyzing the error context.\n\nI'll need to inspect the relevant files before proposing a fix.\n\nOriginal request: {userMessage}";
            kind = BubbleKind.Blocked;
        }
        else if (userMessage.Contains("review", StringComparison.OrdinalIgnoreCase))
        {
            reply = $"Code review in progress.\n\nI have concerns about the current approach — will flag issues if found.\n\nScope: {userMessage}";
            kind = BubbleKind.Critique;
        }

        agent.Status = AgentStatus.ToolExecuting;
        await Task.Delay(800);

        var bubble = new SwarmBubble
        {
            SenderId = agent.Id,
            SenderName = agent.DisplayName,
            Content = reply,
            Kind = kind,
        };
        agent.PushBubble(bubble);
        agent.TokensUsed += 234;
        agent.Status = AgentStatus.Idle;

        AddSwarmLog($"{agent.DisplayName} → 👤: {bubble.Preview}");
    }

    /// <summary>Open the full-text reader for a bubble (click on bubble).</summary>
    [RelayCommand]
    private void OpenBubbleReader(SwarmBubble? bubble)
    {
        if (bubble is null) return;
        bubble.IsRead = true;
        ReadingBubble = bubble;

        // Find the owning agent and reduce unread
        var agent = SwarmAgents.FirstOrDefault(a => a.MessageHistory.Contains(bubble));
        if (agent is not null && agent.UnreadCount > 0)
            agent.UnreadCount--;
    }

    /// <summary>Close the message reader overlay.</summary>
    [RelayCommand]
    private void CloseReader() => ReadingBubble = null;

    /// <summary>Open agent's full message history and mark all read.</summary>
    [RelayCommand]
    private void OpenAgentHistory()
    {
        if (SelectedSwarmAgent is null) return;
        SelectedSwarmAgent.MarkAllRead();
        // Show the most recent message if available
        if (SelectedSwarmAgent.MessageHistory.Count > 0)
            ReadingBubble = SelectedSwarmAgent.MessageHistory[^1];
    }

    // ──── Helpers ────

    /// <summary>Add chat entry — safe to call from any thread.</summary>
    private void DispatchAddChat(ChatEntryKind kind, string content, string? toolName = null)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            AddChat(kind, content, toolName);
        }
        else
        {
            Dispatcher.UIThread.Post(() => AddChat(kind, content, toolName));
        }
    }

    private void AddChat(ChatEntryKind kind, string content, string? toolName = null)
    {
        ChatMessages.Add(new ChatEntry
        {
            Kind = kind,
            Content = content,
            ToolName = toolName
        });
    }

    private void AddSystemMessage(string msg) => AddChat(ChatEntryKind.System, msg);

    private void OnLogEntry(string entry)
    {
        if (Dispatcher.UIThread.CheckAccess())
            LogEntries.Add(entry);
        else
            Dispatcher.UIThread.Post(() => LogEntries.Add(entry));
    }

    /// <summary>Dispatch an arbitrary action to the UI thread.</summary>
    private static void DispatchAction(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }

    /// <summary>
    /// Force the ItemsControl to re-render the last chat entry.
    /// We do this by removing and re-adding it (simplest approach for ObservableCollection).
    /// </summary>
    private void RefreshLastChat()
    {
        if (ChatMessages.Count == 0) return;
        var last = ChatMessages[^1];
        ChatMessages[^1] = last;  // triggers CollectionChanged Replace
    }

    private void FinalizeStreaming(ref ChatEntry? reasoning, ref ChatEntry? answer)
    {
        if (reasoning is { IsStreaming: true })
        {
            reasoning.IsStreaming = false;
            PuddingLogger.Debug($"  Reasoning: finalized ({reasoning.Content.Length} chars)");
        }
        if (answer is { IsStreaming: true })
        {
            answer.IsStreaming = false;
            PuddingLogger.Debug($"  Answer: finalized ({answer.Content.Length} chars)");
        }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
