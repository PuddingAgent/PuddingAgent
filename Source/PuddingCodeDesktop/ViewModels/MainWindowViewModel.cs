using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Platform.Storage;
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

    // ──── Project Context ────

    [ObservableProperty] private ProjectContext? _currentProject;
    [ObservableProperty] private string _projectDisplayName = "No project opened";

    /// <summary>
    /// Set by the View (MainWindow) so the ViewModel can open platform dialogs.
    /// </summary>
    public IStorageProvider? StorageProvider { get; set; }

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

    // ──── Sidebar State (VS Code Activity Bar) ────

    /// <summary>Which sidebar panel is active: Explorer, Search, Git, or null (collapsed).</summary>
    [ObservableProperty] private string? _activeSidebarPanel = "Explorer";

    public bool IsSidebarVisible => ActiveSidebarPanel is not null;
    public bool IsSidebarExplorer => ActiveSidebarPanel == "Explorer";
    public bool IsSidebarSearch => ActiveSidebarPanel == "Search";
    public bool IsSidebarGit => ActiveSidebarPanel == "Git";

    partial void OnActiveSidebarPanelChanged(string? value)
    {
        OnPropertyChanged(nameof(IsSidebarVisible));
        OnPropertyChanged(nameof(IsSidebarExplorer));
        OnPropertyChanged(nameof(IsSidebarSearch));
        OnPropertyChanged(nameof(IsSidebarGit));
    }

    /// <summary>Which bottom panel tab is active: Chat, Output, Problems.</summary>
    [ObservableProperty] private string _activeBottomPanel = "Chat";

    public bool IsBottomChat => ActiveBottomPanel == "Chat";
    public bool IsBottomOutput => ActiveBottomPanel == "Output";
    public bool IsBottomProblems => ActiveBottomPanel == "Problems";

    /// <summary>Whether the bottom panel is visible.</summary>
    [ObservableProperty] private bool _isBottomPanelVisible = true;

    partial void OnActiveBottomPanelChanged(string value)
    {
        OnPropertyChanged(nameof(IsBottomChat));
        OnPropertyChanged(nameof(IsBottomOutput));
        OnPropertyChanged(nameof(IsBottomProblems));
        if (!IsBottomPanelVisible) IsBottomPanelVisible = true;
    }

    // Problem list for the Problems panel
    public ObservableCollection<string> Problems { get; } = new();

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

    partial void OnSelectedSwarmAgentChanged(SwarmAgent? oldValue, SwarmAgent? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
        PuddingLogger.SwarmTrace($"SelectedSwarmAgent changed: {oldValue?.Id ?? "null"} → {newValue?.Id ?? "null"}");
    }

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

    // ──── Overlay Panels ────

    /// <summary>Which detail overlay is currently open (null = none).</summary>
    [ObservableProperty] private string? _activeOverlay;

    /// <summary>The agent targeted by the current overlay.</summary>
    [ObservableProperty] private SwarmAgent? _overlayAgent;

    /// <summary>Input text for the agent chat overlay.</summary>
    [ObservableProperty] private string _overlayChatInput = "";

    /// <summary>Input text for the expanded task-assign overlay.</summary>
    [ObservableProperty] private string _overlayTaskInput = "";

    /// <summary>Input text for the expanded message overlay.</summary>
    [ObservableProperty] private string _overlayMessageInput = "";

    public bool IsModelInfoOpen => ActiveOverlay == "ModelInfo" && OverlayAgent is not null;
    public bool IsThinkingChainOpen => ActiveOverlay == "ThinkingChain" && OverlayAgent is not null;
    public bool IsAgentLogOpen => ActiveOverlay == "AgentLog" && OverlayAgent is not null;
    public bool IsAgentChatOpen => ActiveOverlay == "AgentChat" && OverlayAgent is not null;
    public bool IsTaskInputOpen => ActiveOverlay == "TaskInput" && OverlayAgent is not null;
    public bool IsMessageInputOpen => ActiveOverlay == "MessageInput" && OverlayAgent is not null;

    partial void OnActiveOverlayChanged(string? value)
    {
        OnPropertyChanged(nameof(IsModelInfoOpen));
        OnPropertyChanged(nameof(IsThinkingChainOpen));
        OnPropertyChanged(nameof(IsAgentLogOpen));
        OnPropertyChanged(nameof(IsAgentChatOpen));
        OnPropertyChanged(nameof(IsTaskInputOpen));
        OnPropertyChanged(nameof(IsMessageInputOpen));
    }

    partial void OnOverlayAgentChanged(SwarmAgent? value)
    {
        OnPropertyChanged(nameof(IsModelInfoOpen));
        OnPropertyChanged(nameof(IsThinkingChainOpen));
        OnPropertyChanged(nameof(IsAgentLogOpen));
        OnPropertyChanged(nameof(IsAgentChatOpen));
        OnPropertyChanged(nameof(IsTaskInputOpen));
        OnPropertyChanged(nameof(IsMessageInputOpen));
    }

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
        OpenEditors.Add("Program.cs");
        OpenEditors.Add("AuthService.cs");

        // Swarm demo data
        InitSwarmDemoData();

        PuddingLogger.Info($"CanSendMessage={CanSendMessage()} (IsBusy={IsBusy}, ActiveProvider={(ActiveProvider is null ? "null" : "set")})");
        PuddingLogger.Info("Initialization complete");
    }

    // ──── Open Folder ────

    /// <summary>Open a folder picker and set it as the current project workspace.</summary>
    [RelayCommand]
    private async Task OpenFolderAsync()
    {
        if (StorageProvider is null)
        {
            PuddingLogger.Warn("StorageProvider is null — cannot open folder picker");
            return;
        }

        var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Open Project Folder",
            AllowMultiple = false
        });

        if (result.Count == 0) return;

        var folder = result[0];
        var path = folder.TryGetLocalPath();
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        {
            PuddingLogger.Warn($"OpenFolder: invalid path from picker: {path}");
            return;
        }

        SetProject(path);
    }

    /// <summary>Set the project workspace to a given directory path.</summary>
    private void SetProject(string path)
    {
        CurrentProject = new ProjectContext(path);
        ProjectDisplayName = CurrentProject.Name;
        StatusText = $"Project: {CurrentProject.Name}";
        PuddingLogger.Info($"OpenFolder: project set to \"{CurrentProject.RootPath}\"");

        // Populate project file tree (shallow: top 2 levels, skip hidden/bin/obj)
        ProjectTree.Clear();
        PopulateTree(CurrentProject.RootPath, "", depth: 0, maxDepth: 2);

        AddSystemMessage($"📂 Opened: {CurrentProject.RootPath}");

        var snapshot = new GitSnapshotService(CurrentProject.RootPath);
        if (snapshot.IsGitRepo)
            AddSystemMessage("Git repo detected — auto-snapshots enabled");
    }

    private void PopulateTree(string dir, string indent, int depth, int maxDepth)
    {
        if (depth >= maxDepth) return;

        try
        {
            var entries = Directory.GetFileSystemEntries(dir)
                .Select(e => new { Path = e, Name = Path.GetFileName(e) })
                .Where(e => !e.Name.StartsWith('.') &&
                            !e.Name.Equals("bin", StringComparison.OrdinalIgnoreCase) &&
                            !e.Name.Equals("obj", StringComparison.OrdinalIgnoreCase) &&
                            !e.Name.Equals("node_modules", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => !Directory.Exists(e.Path)) // folders first
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase);

            foreach (var entry in entries)
            {
                var isDir = Directory.Exists(entry.Path);
                var icon = isDir ? "📁" : "📄";
                ProjectTree.Add($"{indent}{icon} {entry.Name}");

                if (isDir)
                    PopulateTree(entry.Path, indent + "  ", depth + 1, maxDepth);
            }
        }
        catch (UnauthorizedAccessException) { /* skip protected dirs */ }
    }

    private void InitSwarmDemoData()
    {
        PuddingLogger.SwarmInfo("Initializing Swarm demo data...");

        var leader = new SwarmAgent
        {
            Id = "leader-0",
            DisplayName = "Leader",
            Role = AgentRole.Leader,
            Status = AgentStatus.Idle,
            Model = ActiveProvider?.Model ?? "gpt-4o",
            CurrentTask = "Orchestrating swarm",
            Specialization = "Orchestrator"
        };
        var worker1 = new SwarmAgent
        {
            Id = "worker-1",
            DisplayName = "worker-auth",
            Role = AgentRole.Worker,
            Status = AgentStatus.Idle,
            Model = ActiveProvider?.Model ?? "deepseek-chat",
            ParentId = "leader-0",
            CurrentTask = "task-001: AuthService",
            Specialization = "Coder"
        };
        var worker2 = new SwarmAgent
        {
            Id = "worker-2",
            DisplayName = "worker-tests",
            Role = AgentRole.Worker,
            Status = AgentStatus.Idle,
            Model = ActiveProvider?.Model ?? "deepseek-chat",
            ParentId = "leader-0",
            CurrentTask = "task-002: Unit tests",
            Specialization = "Tester"
        };

        SwarmAgents.Add(leader);
        SwarmAgents.Add(worker1);
        SwarmAgents.Add(worker2);
        SelectedSwarmAgent = leader;

        PuddingLogger.SwarmDebug($"Created leader: {leader.Id} model={leader.Model}");
        PuddingLogger.SwarmDebug($"Created worker: {worker1.Id} model={worker1.Model} parent={worker1.ParentId}");
        PuddingLogger.SwarmDebug($"Created worker: {worker2.Id} model={worker2.Model} parent={worker2.ParentId}");

        // Populate demo logs and thinking chain
        leader.Log("Agent created as Leader.");
        leader.Log("Swarm initialized.");
        leader.AddThinkingStep("Analyze task requirements and decompose into subtasks.");
        leader.AddThinkingStep("Assign subtasks to available workers based on capability.");

        worker1.Log("Agent created as Worker.");
        worker1.AddThinkingStep("Received task-001 from Leader.");

        worker2.Log("Agent created as Worker.");
        worker2.AddThinkingStep("Received task-002 from Leader.");

        RebuildTopologyViews();

        SwarmEventLog.Add($"[{DateTime.Now:HH:mm:ss}] Swarm initialized with 1 leader + 2 workers");
        PuddingLogger.SwarmInfo($"Swarm demo initialized: {SwarmAgents.Count} agents, {LeaderAgents.Count} leaders, {WorkerAgents.Count} workers");
    }

    private void RebuildTopologyViews()
    {
        PuddingLogger.SwarmTrace("RebuildTopologyViews: rebuilding leader/worker collections");
        LeaderAgents.Clear();
        WorkerAgents.Clear();
        foreach (var a in SwarmAgents)
        {
            if (a.Role == AgentRole.Leader) LeaderAgents.Add(a);
            else WorkerAgents.Add(a);
        }
        OnPropertyChanged(nameof(HasWorkers));
        PuddingLogger.SwarmTrace($"RebuildTopologyViews: {LeaderAgents.Count} leaders, {WorkerAgents.Count} workers");
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

            PermissionGuard? guard = CurrentProject is not null
                ? new PermissionGuard(CurrentProject.RootPath)
                : null;
            registry.Register(new FileTool(CurrentProject, guard));
            registry.Register(new ShellTool(CurrentProject, guard));

            GitSnapshotService? snapshot = CurrentProject is not null
                ? new GitSnapshotService(CurrentProject.RootPath)
                : null;
            var agent = new AgentOrchestrator(gateway, registry, CurrentProject, snapshot);

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

    /// <summary>Toggle a sidebar panel; clicking the same icon again collapses the sidebar.</summary>
    [RelayCommand]
    private void ToggleSidebar(string panel)
    {
        ActiveSidebarPanel = ActiveSidebarPanel == panel ? null : panel;
        PuddingLogger.Info($"Sidebar: {ActiveSidebarPanel ?? "collapsed"}");
    }

    /// <summary>Switch to a bottom panel tab.</summary>
    [RelayCommand]
    private void SwitchBottomPanel(string panel)
    {
        if (ActiveBottomPanel == panel && IsBottomPanelVisible)
            IsBottomPanelVisible = false;
        else
        {
            ActiveBottomPanel = panel;
            IsBottomPanelVisible = true;
        }
    }

    /// <summary>Toggle bottom panel visibility.</summary>
    [RelayCommand]
    private void ToggleBottomPanel() => IsBottomPanelVisible = !IsBottomPanelVisible;

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
        PuddingLogger.SwarmInfo($"SpawnWorker: creating worker-{idx}");
        string[] specs = ["Coder", "Tester", "Researcher", "Analyst", "Reporter"];
        var spec = specs[(idx - 1) % specs.Length];
        var worker = new SwarmAgent
        {
            Id = $"worker-{idx}",
            DisplayName = $"worker-{idx}",
            Role = AgentRole.Worker,
            Status = AgentStatus.Idle,
            Model = ActiveProvider?.Model ?? "default",
            ParentId = SwarmAgents.FirstOrDefault(a => a.Role == AgentRole.Leader)?.Id,
            Specialization = spec
        };
        worker.Log($"Agent created via SpawnWorker. Specialization={spec}");
        SwarmAgents.Add(worker);
        RebuildTopologyViews();
        AddSwarmLog($"Spawned {worker.DisplayName} ({worker.Model}, {spec})");
        PuddingLogger.SwarmInfo($"SpawnWorker: {worker.Id} created, model={worker.Model}, spec={spec}, parent={worker.ParentId}");
    }

    /// <summary>Select an agent from the topology view (click on node card).</summary>
    [RelayCommand]
    private void SelectTopologyAgent(SwarmAgent? agent)
    {
        if (agent is null) return;
        PuddingLogger.SwarmTrace($"SelectTopologyAgent: {agent.Id} ({agent.DisplayName})");
        SelectedSwarmAgent = agent;
    }

    [RelayCommand]
    private void PauseAgent()
    {
        if (SelectedSwarmAgent is null) return;
        var prev = SelectedSwarmAgent.Status;
        SelectedSwarmAgent.Status = AgentStatus.Idle;
        PuddingLogger.SwarmDebug($"PauseAgent: {SelectedSwarmAgent.Id} {prev} → Idle");
        SelectedSwarmAgent.Log($"Paused by user (was {prev}).");
        AddSwarmLog($"Paused {SelectedSwarmAgent.DisplayName}");
    }

    [RelayCommand]
    private void ResumeAgent()
    {
        if (SelectedSwarmAgent is null) return;
        var prev = SelectedSwarmAgent.Status;
        SelectedSwarmAgent.Status = AgentStatus.Thinking;
        PuddingLogger.SwarmDebug($"ResumeAgent: {SelectedSwarmAgent.Id} {prev} → Thinking");
        SelectedSwarmAgent.Log($"Resumed by user (was {prev}).");
        AddSwarmLog($"Resumed {SelectedSwarmAgent.DisplayName}");
    }

    [RelayCommand]
    private void KillAgent()
    {
        if (SelectedSwarmAgent is null) return;
        var name = SelectedSwarmAgent.DisplayName;
        var id = SelectedSwarmAgent.Id;
        PuddingLogger.SwarmInfo($"KillAgent: {id} ({name}) status={SelectedSwarmAgent.Status}");
        SelectedSwarmAgent.Status = AgentStatus.Offline;
        SwarmAgents.Remove(SelectedSwarmAgent);
        SelectedSwarmAgent = SwarmAgents.FirstOrDefault();
        RebuildTopologyViews();
        AddSwarmLog($"Killed {name}");
        PuddingLogger.SwarmInfo($"KillAgent: {id} removed, agents remaining={SwarmAgents.Count}");
    }

    // ──── Right-Click Context Menu Commands ────

    [RelayCommand]
    private void ContextRestart(SwarmAgent? agent)
    {
        if (agent is null) return;
        PuddingLogger.SwarmInfo($"ContextRestart: {agent.Id} ({agent.DisplayName}) status={agent.Status}");
        agent.Status = AgentStatus.Rebuilding;
        agent.Log("Agent restarting...");
        AddSwarmLog($"🔄 Restarting {agent.DisplayName}");

        // TODO: D08 — 蜂群编排器实现后，调用 ISwarmOrchestrator.RestartAgentAsync(agent.Id)
        //       当前仅模拟状态变更。实际需清空对话历史、重新注入 System Prompt、重建 Tool 注册。
        _ = SimulateRestartAsync(agent);
    }

    private async Task SimulateRestartAsync(SwarmAgent agent)
    {
        PuddingLogger.SwarmTrace($"SimulateRestart: {agent.Id} waiting 1200ms...");
        await Task.Delay(1200);
        agent.Status = AgentStatus.Idle;
        agent.CurrentTask = null;
        agent.TokensUsed = 0;
        agent.Log("Agent restarted successfully.");
        AddSwarmLog($"✅ {agent.DisplayName} restarted");
        PuddingLogger.SwarmDebug($"SimulateRestart: {agent.Id} → Idle, tokens reset");
    }

    [RelayCommand]
    private void ContextStop(SwarmAgent? agent)
    {
        if (agent is null) return;
        var prev = agent.Status;
        PuddingLogger.SwarmInfo($"ContextStop: {agent.Id} ({agent.DisplayName}) {prev} → Idle");
        agent.Status = AgentStatus.Idle;
        agent.CurrentTask = null;
        agent.Log($"Agent stopped by user (was {prev}).");
        AddSwarmLog($"⏹ Stopped {agent.DisplayName}");
        // TODO: D08 — 需要取消 Agent 当前正在执行的 CancellationToken
    }

    [RelayCommand]
    private void ContextResume(SwarmAgent? agent)
    {
        if (agent is null) return;
        var prev = agent.Status;
        if (agent.Status is AgentStatus.Sleeping or AgentStatus.Idle or AgentStatus.Completed)
        {
            agent.Status = AgentStatus.Idle;
            agent.Log($"Agent resumed (was {prev}).");
            AddSwarmLog($"▶ Resumed {agent.DisplayName}");
            PuddingLogger.SwarmDebug($"ContextResume: {agent.Id} {prev} → Idle");
        }
        else
        {
            PuddingLogger.SwarmWarn($"ContextResume: {agent.Id} ignored, status={prev} not resumable");
        }
        // TODO: Task 09 — Agent 生命周期实现后，从休眠状态恢复需要重新加载记忆快照
    }

    [RelayCommand]
    private void ContextSleep(SwarmAgent? agent)
    {
        if (agent is null) return;
        var prev = agent.Status;
        PuddingLogger.SwarmInfo($"ContextSleep: {agent.Id} ({agent.DisplayName}) {prev} → Sleeping");
        agent.Status = AgentStatus.Sleeping;
        agent.Log($"Agent entered sleep mode (was {prev}).");
        AddSwarmLog($"😴 {agent.DisplayName} is sleeping");
        // TODO: Task 09 — Agent 生命周期实现后，休眠需持久化当前对话状态和记忆到磁盘
    }

    [RelayCommand]
    private void ContextDestroy(SwarmAgent? agent)
    {
        if (agent is null) return;
        var name = agent.DisplayName;
        var id = agent.Id;
        PuddingLogger.SwarmInfo($"ContextDestroy: {id} ({name}) status={agent.Status}, tokens={agent.TokensUsed}, msgs={agent.MessageHistory.Count}");
        agent.Status = AgentStatus.Offline;
        agent.Log("Agent destroyed.");
        SwarmAgents.Remove(agent);
        if (SelectedSwarmAgent == agent)
            SelectedSwarmAgent = SwarmAgents.FirstOrDefault();
        RebuildTopologyViews();
        AddSwarmLog($"🗑 Destroyed {name}");
        PuddingLogger.SwarmInfo($"ContextDestroy: {id} removed, agents remaining={SwarmAgents.Count}");
        // TODO: Task 09 — 销毁需清理该 Agent 的记忆文件、Git Worktree 等资源
    }

    [RelayCommand]
    private void ContextRebuild(SwarmAgent? agent)
    {
        if (agent is null) return;
        PuddingLogger.SwarmInfo($"ContextRebuild: {agent.Id} ({agent.DisplayName}) — clearing all state");
        PuddingLogger.SwarmTrace($"  Pre-rebuild stats: tokens={agent.TokensUsed}, msgs={agent.MessageHistory.Count}, chain={agent.ThinkingChain.Count}, logs={agent.AgentLogs.Count}");
        agent.Status = AgentStatus.Rebuilding;
        agent.ThinkingChain.Clear();
        agent.AgentLogs.Clear();
        agent.MessageHistory.Clear();
        agent.ActiveBubble = null;
        agent.UnreadCount = 0;
        agent.TokensUsed = 0;
        agent.Log("Agent rebuild initiated — state cleared.");
        AddSwarmLog($"🏗 Rebuilding {agent.DisplayName}");

        // TODO: D08 — 蜂群编排器实现后，重建 = 销毁 + 用同模板重新创建
        _ = SimulateRestartAsync(agent);
    }

    [RelayCommand]
    private void ContextSendMessage(SwarmAgent? agent)
    {
        if (agent is null) return;
        SelectedSwarmAgent = agent;
        OverlayAgent = agent;
        ActiveOverlay = "AgentChat";
        OverlayChatInput = "";
        PuddingLogger.SwarmDebug($"ContextSendMessage: opened chat overlay for {agent.Id} ({agent.DisplayName})");
    }

    [RelayCommand]
    private void ContextAssignTask(SwarmAgent? agent)
    {
        if (agent is null) return;
        SelectedSwarmAgent = agent;
        OpenTaskInputOverlay();
        PuddingLogger.SwarmDebug($"ContextAssignTask: opened task overlay for {agent.Id} ({agent.DisplayName})");
    }

    [RelayCommand]
    private void ContextViewModelInfo(SwarmAgent? agent)
    {
        if (agent is null) return;
        OverlayAgent = agent;
        ActiveOverlay = "ModelInfo";
        PuddingLogger.SwarmTrace($"ContextViewModelInfo: {agent.Id} model={agent.Model} role={agent.Role} status={agent.Status}");
    }

    [RelayCommand]
    private void ContextViewThinkingChain(SwarmAgent? agent)
    {
        if (agent is null) return;
        OverlayAgent = agent;
        ActiveOverlay = "ThinkingChain";
        PuddingLogger.SwarmTrace($"ContextViewThinkingChain: {agent.Id} steps={agent.ThinkingChain.Count}");
    }

    [RelayCommand]
    private void ContextViewLog(SwarmAgent? agent)
    {
        if (agent is null) return;
        OverlayAgent = agent;
        ActiveOverlay = "AgentLog";
        PuddingLogger.SwarmTrace($"ContextViewLog: {agent.Id} entries={agent.AgentLogs.Count}");
    }

    [RelayCommand]
    private void CloseOverlay()
    {
        PuddingLogger.SwarmTrace($"CloseOverlay: was={ActiveOverlay ?? "none"} agent={OverlayAgent?.Id ?? "none"}");
        ActiveOverlay = null;
        OverlayAgent = null;
        OverlayChatInput = "";
        OverlayTaskInput = "";
        OverlayMessageInput = "";
    }

    /// <summary>Open the expanded task-input overlay for the selected agent.</summary>
    [RelayCommand]
    private void OpenTaskInputOverlay()
    {
        if (SelectedSwarmAgent is null) return;
        OverlayAgent = SelectedSwarmAgent;
        OverlayTaskInput = SwarmTaskInput; // carry over any text already typed
        SwarmTaskInput = "";
        ActiveOverlay = "TaskInput";
        PuddingLogger.SwarmTrace($"OpenTaskInputOverlay: agent={OverlayAgent.Id}");
    }

    /// <summary>Open the expanded message-input overlay for the selected agent.</summary>
    [RelayCommand]
    private void OpenMessageInputOverlay()
    {
        if (SelectedSwarmAgent is null) return;
        OverlayAgent = SelectedSwarmAgent;
        OverlayMessageInput = AgentMessageInput; // carry over any text already typed
        AgentMessageInput = "";
        ActiveOverlay = "MessageInput";
        PuddingLogger.SwarmTrace($"OpenMessageInputOverlay: agent={OverlayAgent.Id}");
    }

    /// <summary>Submit the task from the expanded overlay (same logic as AssignTask).</summary>
    [RelayCommand]
    private void SubmitOverlayTask()
    {
        if (OverlayAgent is null || string.IsNullOrWhiteSpace(OverlayTaskInput)) return;
        var agent = OverlayAgent;
        var task = OverlayTaskInput.Trim();

        // Close overlay first
        OverlayTaskInput = "";
        ActiveOverlay = null;
        OverlayAgent = null;

        // Ensure the agent is selected
        SelectedSwarmAgent = agent;

        // Same logic as AssignTask
        var prev = agent.Status;
        agent.CurrentTask = task;
        agent.Status = AgentStatus.Thinking;
        agent.Log($"Task assigned: {task}");
        agent.AddThinkingStep($"Received task: {task}");
        PuddingLogger.SwarmInfo($"SubmitOverlayTask: {agent.Id} ← \"{Truncate(task, 60)}\" ({prev} → Thinking)");
        AddSwarmLog($"Assigned to {agent.DisplayName}: {task}");

        _ = SimulateTaskProcessingAsync(agent, task);
    }

    /// <summary>Submit the message from the expanded overlay (same logic as SendAgentMessage).</summary>
    [RelayCommand]
    private void SubmitOverlayMessage()
    {
        if (OverlayAgent is null || string.IsNullOrWhiteSpace(OverlayMessageInput)) return;
        var agent = OverlayAgent;
        var text = OverlayMessageInput.Trim();

        // Close overlay first
        OverlayMessageInput = "";
        ActiveOverlay = null;
        OverlayAgent = null;

        // Ensure the agent is selected
        SelectedSwarmAgent = agent;

        PuddingLogger.SwarmDebug($"SubmitOverlayMessage: user → {agent.Id}: \"{Truncate(text, 60)}\"");
        AddSwarmLog($"👤 → {agent.DisplayName}: {text}");

        var cmdBubble = new SwarmBubble
        {
            SenderId = "user",
            SenderName = "Commander",
            Content = text,
            Kind = BubbleKind.Command,
        };
        agent.PushBubble(cmdBubble);
        agent.Log($"Received message from Commander: {Truncate(text, 60)}");
        PuddingLogger.SwarmTrace($"  Bubble pushed: kind=Command, unread={agent.UnreadCount}");

        _ = SimulateAgentReplyAsync(agent, text);
    }

    /// <summary>Send a message from the overlay chat panel.</summary>
    [RelayCommand]
    private void SendOverlayChat()
    {
        if (OverlayAgent is null || string.IsNullOrWhiteSpace(OverlayChatInput)) return;

        var text = OverlayChatInput.Trim();
        var agent = OverlayAgent;

        PuddingLogger.SwarmDebug($"SendOverlayChat: user → {agent.Id}: \"{Truncate(text, 60)}\"");
        AddSwarmLog($"👤 → {agent.DisplayName}: {text}");

        var cmdBubble = new SwarmBubble
        {
            SenderId = "user",
            SenderName = "Commander",
            Content = text,
            Kind = BubbleKind.Command,
        };
        agent.PushBubble(cmdBubble);
        PuddingLogger.SwarmTrace($"  Bubble pushed to {agent.Id}: kind=Command, preview=\"{cmdBubble.Preview}\"");
        OverlayChatInput = "";

        // TODO: D03/D08 — 实际应调用 AgentOrchestrator.ProcessAsync 将消息发给该 Agent 的 LLM
        _ = SimulateAgentReplyAsync(agent, text);
    }

    [RelayCommand]
    private void AssignTask()
    {
        if (SelectedSwarmAgent is null || string.IsNullOrWhiteSpace(SwarmTaskInput)) return;
        var task = SwarmTaskInput.Trim();
        var prev = SelectedSwarmAgent.Status;
        SelectedSwarmAgent.CurrentTask = task;
        SelectedSwarmAgent.Status = AgentStatus.Thinking;
        SelectedSwarmAgent.Log($"Task assigned: {task}");
        SelectedSwarmAgent.AddThinkingStep($"Received task: {task}");
        PuddingLogger.SwarmInfo($"AssignTask: {SelectedSwarmAgent.Id} ← \"{Truncate(task, 60)}\" ({prev} → Thinking)");
        AddSwarmLog($"Assigned to {SelectedSwarmAgent.DisplayName}: {task}");
        SwarmTaskInput = "";

        // TODO: D03/D08 — 实际应调用 AgentOrchestrator.ProcessAsync 执行真实 LLM 调用
        _ = SimulateTaskProcessingAsync(SelectedSwarmAgent, task);
    }

    /// <summary>
    /// Simulate an agent processing an assigned task.
    /// Leader: plan → selective dispatch → aggregate.
    /// Worker: analyze → execute tool calls → report completion.
    /// </summary>
    private async Task SimulateTaskProcessingAsync(SwarmAgent agent, string task)
    {
        using var _ = PuddingLogger.BeginTrace(PuddingLogger.NewTraceId());
        PuddingLogger.SwarmInfo($"SimulateTaskProcessing: {agent.Id} ({agent.Role}) task=\"{Truncate(task, 50)}\"");

        if (agent.Role == AgentRole.Leader)
            await SimulateLeaderTaskAsync(agent, task);
        else
            await SimulateWorkerTaskAsync(agent, task);
    }

    /// <summary>
    /// Plan-then-Execute leader simulation (Task 17 Phase 1).
    /// Phase 1: Plan — analyze task, decompose into differentiated subtasks, match workers.
    /// Phase 2: Execute — selectively dispatch to matched workers only; idle workers stay sleeping.
    /// Phase 3: Aggregate — Leader collects results and produces final output.
    /// </summary>
    private async Task SimulateLeaderTaskAsync(SwarmAgent leader, string task)
    {
        var allWorkers = SwarmAgents.Where(a => a.Role == AgentRole.Worker).ToList();

        // ════════════════════════════════════════
        //  Phase 1: Plan
        // ════════════════════════════════════════
        PuddingLogger.SwarmDebug("── Phase 1: Planning ──");

        // S1: Semantic analysis
        leader.AddThinkingStep($"S1 语义解析: Analyzing \"{Truncate(task, 40)}\"...");
        leader.Log("Planning: analyzing task semantics...");
        await Task.Delay(800);

        var taskType = InferTaskType(task);
        leader.AddThinkingStep($"S1 结果: TaskType={taskType}");
        leader.Log($"Task type identified: {taskType}");
        PuddingLogger.SwarmDebug($"  {leader.Id}: S1 → TaskType={taskType}");

        // S2: Complexity assessment
        var complexity = allWorkers.Count switch
        {
            0 => "Trivial",
            <= 2 => "Low",
            _ => "Medium"
        };
        leader.AddThinkingStep($"S2 复杂度评估: Complexity={complexity}, Pool={allWorkers.Count} workers");
        PuddingLogger.SwarmDebug($"  {leader.Id}: S2 → Complexity={complexity}");
        await Task.Delay(500);

        if (allWorkers.Count == 0)
        {
            // No workers — leader handles it alone
            PuddingLogger.SwarmDebug($"  {leader.Id}: no workers available, self-executing");
            leader.AddThinkingStep("S3 判定: No workers available. Executing task myself.");
            leader.Log("No workers available — executing task directly.");

            leader.Status = AgentStatus.ToolExecuting;
            leader.Log("Executing tool calls...");
            await Task.Delay(2000);

            leader.TokensUsed += 456;
            leader.Status = AgentStatus.Completed;
            leader.CurrentTask = $"✅ {Truncate(task, 30)}";
            leader.Log($"Task completed (self). Tokens: {leader.TokensUsed}");
            leader.AddThinkingStep($"Task completed. Tokens used: {leader.TokensUsed}.");

            leader.PushBubble(new SwarmBubble
            {
                SenderId = leader.Id,
                SenderName = leader.DisplayName,
                Content = $"Task completed (no workers, handled directly).\n\nTask: {task}\nTokens: {leader.TokensUsed}",
                Kind = BubbleKind.Success,
            });
            AddSwarmLog($"{leader.DisplayName}: ✅ Task completed (self, {leader.TokensUsed} tokens)");
            PuddingLogger.SwarmInfo($"  {leader.Id}: self-completed, tokens={leader.TokensUsed}");
            return;
        }

        // S3: Decompose into differentiated subtasks
        var subtasks = DecomposeTask(task, taskType, allWorkers);
        leader.AddThinkingStep($"S3 子任务分解: {subtasks.Count} differentiated subtask(s)");
        foreach (var st in subtasks)
            leader.AddThinkingStep($"   → [{st.Action}] {st.Scope}");
        leader.Log($"Decomposed into {subtasks.Count} subtask(s).");
        PuddingLogger.SwarmDebug($"  {leader.Id}: S3 → {subtasks.Count} subtasks");
        await Task.Delay(600);

        // S4: Capability matching — select workers
        var assignments = MatchWorkers(subtasks, allWorkers);
        var selectedWorkers = assignments.Select(a => a.Worker).Distinct().ToList();
        var idleWorkers = allWorkers.Except(selectedWorkers).ToList();

        leader.AddThinkingStep($"S4 能力匹配: {selectedWorkers.Count} workers selected, {idleWorkers.Count} idle");
        foreach (var a in assignments)
            leader.AddThinkingStep($"   → {a.Worker.DisplayName} ({a.Worker.Specialization}) ← [{a.Subtask.Action}] {a.Subtask.Scope}");
        foreach (var w in idleWorkers)
            leader.AddThinkingStep($"   → {w.DisplayName} ({w.Specialization}) [SKIP — not needed]");
        leader.Log($"Selected {selectedWorkers.Count}/{allWorkers.Count} workers. {idleWorkers.Count} staying idle.");
        PuddingLogger.SwarmDebug($"  {leader.Id}: S4 → selected={selectedWorkers.Count}, idle={idleWorkers.Count}");
        await Task.Delay(400);

        // S5: Cost estimate
        var estimatedTokens = selectedWorkers.Count * 600;
        leader.AddThinkingStep($"S5 成本预估: ~{estimatedTokens} tokens (vs full broadcast ~{allWorkers.Count * 600})");
        PuddingLogger.SwarmDebug($"  {leader.Id}: S5 → est={estimatedTokens}, saved ~{(allWorkers.Count - selectedWorkers.Count) * 600}");
        await Task.Delay(300);

        // ════════════════════════════════════════
        //  Phase 2: Execute (selective dispatch)
        // ════════════════════════════════════════
        PuddingLogger.SwarmDebug("── Phase 2: Execute ──");
        leader.Status = AgentStatus.ToolExecuting;
        leader.CurrentTask = "Dispatching subtasks";
        leader.Log($"Dispatching {assignments.Count} subtask(s) to {selectedWorkers.Count} worker(s)...");

        // Put idle workers to sleep
        foreach (var w in idleWorkers)
        {
            if (w.Status is AgentStatus.Idle)
            {
                w.Status = AgentStatus.Sleeping;
                w.Log("Put to sleep by Leader — not needed for this task.");
                PuddingLogger.SwarmTrace($"  {w.Id}: Idle → Sleeping (not needed)");
            }
        }

        // Dispatch with differentiated subtasks
        foreach (var a in assignments)
        {
            var w = a.Worker;
            var st = a.Subtask;
            var subtaskDesc = $"[{st.Action}] {st.Scope}";

            w.CurrentTask = subtaskDesc;
            w.Status = AgentStatus.Thinking;
            w.Log($"Received from {leader.DisplayName}: {subtaskDesc}");
            w.AddThinkingStep($"ACK: received [{st.Action}] {st.Scope}");

            var cmdBubble = new SwarmBubble
            {
                SenderId = leader.Id,
                SenderName = leader.DisplayName,
                Content = $"Action: {st.Action}\nScope: {st.Scope}\n\nOriginal task: {task}",
                Kind = BubbleKind.Command,
            };
            w.PushBubble(cmdBubble);
            AddSwarmLog($"{leader.DisplayName} → {w.DisplayName}: [{st.Action}] {st.Scope}");
            PuddingLogger.SwarmInfo($"  {leader.Id} → {w.Id}: action={st.Action}, scope={st.Scope}");

            _ = SimulateWorkerTaskAsync(w, subtaskDesc);
            await Task.Delay(500);
        }

        // ════════════════════════════════════════
        //  Phase 2.5: Monitor
        // ════════════════════════════════════════
        leader.Status = AgentStatus.Idle;
        leader.CurrentTask = "Monitoring workers...";
        leader.Log($"All {assignments.Count} subtasks dispatched. Monitoring...");
        leader.AddThinkingStep($"Dispatched. Waiting for {selectedWorkers.Count} worker(s).");
        PuddingLogger.SwarmDebug($"  {leader.Id}: monitoring {selectedWorkers.Count} workers");

        // Poll until all selected workers finish
        var maxWait = 30_000;
        var elapsed = 0;
        while (elapsed < maxWait)
        {
            await Task.Delay(500);
            elapsed += 500;
            if (selectedWorkers.All(w => w.Status is AgentStatus.Completed or AgentStatus.Error or AgentStatus.Idle or AgentStatus.Offline))
                break;
        }

        // ════════════════════════════════════════
        //  Phase 3: Aggregate
        // ════════════════════════════════════════
        PuddingLogger.SwarmDebug("── Phase 3: Aggregate ──");
        leader.Status = AgentStatus.Thinking;
        leader.CurrentTask = "Aggregating results...";
        leader.Log("All workers done. Aggregating results...");
        leader.AddThinkingStep("Phase 3: Collecting and reviewing worker outputs.");

        var completedCount = selectedWorkers.Count(w => w.Status == AgentStatus.Completed);
        var errorCount = selectedWorkers.Count(w => w.Status == AgentStatus.Error);
        var workerTokens = selectedWorkers.Sum(w => w.TokensUsed);
        await Task.Delay(1000);

        // Leader reviews each result
        foreach (var w in selectedWorkers)
        {
            var passed = w.Status == AgentStatus.Completed;
            leader.AddThinkingStep($"Review {w.DisplayName}: {(passed ? "✅ PASS" : "⚠ ERROR")} ({w.TokensUsed} tokens)");
            PuddingLogger.SwarmTrace($"  {leader.Id}: review {w.Id} → {(passed ? "PASS" : "ERROR")}");
        }
        await Task.Delay(500);

        // Wake idle workers back up
        foreach (var w in idleWorkers)
        {
            if (w.Status == AgentStatus.Sleeping)
            {
                w.Status = AgentStatus.Idle;
                w.Log("Woken up — Leader task aggregation complete.");
                PuddingLogger.SwarmTrace($"  {w.Id}: Sleeping → Idle (task complete)");
            }
        }

        // Produce final summary
        leader.TokensUsed += 128; // aggregation overhead
        var totalTokens = workerTokens + leader.TokensUsed;
        var savedPct = allWorkers.Count > selectedWorkers.Count
            ? (int)((1.0 - (double)selectedWorkers.Count / allWorkers.Count) * 100)
            : 0;

        leader.Status = AgentStatus.Completed;
        leader.CurrentTask = $"✅ {Truncate(task, 30)}";
        leader.Log($"Task complete. Workers: {completedCount}/{selectedWorkers.Count} ok, {errorCount} errors. Tokens: {totalTokens}. Saved: {savedPct}%");
        leader.AddThinkingStep($"Final: {completedCount}/{selectedWorkers.Count} ok. Total={totalTokens} tokens. Saved ~{savedPct}% by smart routing.");

        var summaryBubble = new SwarmBubble
        {
            SenderId = leader.Id,
            SenderName = leader.DisplayName,
            Content = $"📊 Task Complete\n\nTask: {Truncate(task, 50)}\n"
                    + $"Workers: {completedCount} ok / {errorCount} err (activated {selectedWorkers.Count}/{allWorkers.Count})\n"
                    + $"Tokens: {totalTokens} (saved ~{savedPct}% via smart routing)\n\n"
                    + string.Join("\n", assignments.Select(a => $"• {a.Worker.DisplayName} [{a.Subtask.Action}] → {(a.Worker.Status == AgentStatus.Completed ? "✅" : "⚠")}")),
            Kind = errorCount > 0 ? BubbleKind.Blocked : BubbleKind.Success,
        };
        leader.PushBubble(summaryBubble);
        AddSwarmLog($"{leader.DisplayName}: ✅ Done — {completedCount}/{selectedWorkers.Count} ok, {totalTokens} tokens, saved {savedPct}%");
        PuddingLogger.SwarmInfo($"  {leader.Id}: task complete. selected={selectedWorkers.Count}/{allWorkers.Count}, ok={completedCount}, tokens={totalTokens}, saved={savedPct}%");
    }

    // ──── Task 17: Planning Helpers ────

    /// <summary>Simulated task type inference from keywords.</summary>
    private static string InferTaskType(string task)
    {
        var t = task.ToLowerInvariant();
        if (t.Contains("对比") || t.Contains("比较") || t.Contains("还是") || t.Contains("compare"))
            return "Comparative";
        if (t.Contains("测试") || t.Contains("test"))
            return "Testing";
        if (t.Contains("重构") || t.Contains("refactor"))
            return "Refactoring";
        if (t.Contains("实现") || t.Contains("implement") || t.Contains("开发"))
            return "Implementation";
        if (t.Contains("调研") || t.Contains("research") || t.Contains("搜索") || t.Contains("查"))
            return "Research";
        if (t.Contains("review") || t.Contains("审查"))
            return "Review";
        return "General";
    }

    /// <summary>
    /// Decompose a task into differentiated subtasks based on task type.
    /// Each subtask has a unique action + scope (never "part N/M" clones).
    /// </summary>
    private static List<Subtask> DecomposeTask(string task, string taskType, List<SwarmAgent> workers)
    {
        // Cap subtasks: never more than needed, never more than workers
        return taskType switch
        {
            "Comparative" => DecomposeComparative(task, workers.Count),
            "Testing"     => DecomposeTesting(task, workers.Count),
            "Research"    => DecomposeResearch(task, workers.Count),
            _             => DecomposeGeneral(task, workers.Count),
        };
    }

    private static List<Subtask> DecomposeComparative(string task, int maxWorkers)
    {
        // Extract comparison subjects heuristically (split on 还是/vs/比较)
        var subjects = new List<string>();
        foreach (var sep in new[] { "还是", " vs ", "比较", "对比" })
        {
            var idx = task.IndexOf(sep, StringComparison.OrdinalIgnoreCase);
            if (idx > 0)
            {
                subjects.Add(Truncate(task[..idx].TrimEnd(), 20));
                subjects.Add(Truncate(task[(idx + sep.Length)..].TrimStart(), 20));
                break;
            }
        }
        if (subjects.Count < 2)
        {
            subjects = ["Subject A", "Subject B"];
        }

        var result = new List<Subtask>();
        foreach (var subj in subjects.Take(Math.Min(subjects.Count, maxWorkers - 1)))
            result.Add(new Subtask("Research", $"调研 {subj} 的核心数据"));
        if (result.Count < maxWorkers)
            result.Add(new Subtask("Analyze", $"基于调研数据对比分析并给出结论"));
        return result;
    }

    private static List<Subtask> DecomposeTesting(string task, int maxWorkers)
    {
        var result = new List<Subtask> { new("Analyze", $"分析测试范围: {Truncate(task, 30)}") };
        if (maxWorkers >= 2) result.Add(new("Test", "编写并执行单元测试"));
        if (maxWorkers >= 3) result.Add(new("Report", "汇总测试结果并生成报告"));
        return result;
    }

    private static List<Subtask> DecomposeResearch(string task, int maxWorkers)
    {
        var result = new List<Subtask> { new("Research", $"搜集资料: {Truncate(task, 30)}") };
        if (maxWorkers >= 2) result.Add(new("Analyze", "整理和分析搜集到的资料"));
        if (maxWorkers >= 3) result.Add(new("Report", "撰写调研报告"));
        return result;
    }

    private static List<Subtask> DecomposeGeneral(string task, int maxWorkers)
    {
        // For general tasks, create at most 2 subtasks: implement + verify
        var result = new List<Subtask> { new("Implement", $"执行: {Truncate(task, 30)}") };
        if (maxWorkers >= 2) result.Add(new("Verify", "验证执行结果"));
        return result;
    }

    /// <summary>
    /// Match subtasks to workers based on specialization affinity.
    /// Returns a list of (Worker, Subtask) assignments.
    /// </summary>
    private static List<WorkerAssignment> MatchWorkers(List<Subtask> subtasks, List<SwarmAgent> workers)
    {
        var available = new List<SwarmAgent>(workers);
        var assignments = new List<WorkerAssignment>();

        foreach (var st in subtasks)
        {
            if (available.Count == 0) break;

            // Score each available worker
            var best = available
                .OrderByDescending(w => ScoreAffinity(w.Specialization, st.Action))
                .First();

            assignments.Add(new WorkerAssignment(best, st));
            available.Remove(best);
        }

        return assignments;
    }

    /// <summary>Simple affinity scoring: specialization × action compatibility.</summary>
    private static float ScoreAffinity(string specialization, string action)
    {
        return (specialization, action) switch
        {
            ("Coder", "Implement")      => 0.95f,
            ("Coder", "Analyze")        => 0.6f,
            ("Tester", "Test")          => 0.95f,
            ("Tester", "Verify")        => 0.9f,
            ("Researcher", "Research")  => 0.95f,
            ("Researcher", "Analyze")   => 0.7f,
            ("Analyst", "Analyze")      => 0.95f,
            ("Analyst", "Research")     => 0.7f,
            ("Reporter", "Report")      => 0.95f,
            ("Reporter", "Analyze")     => 0.6f,
            _ => 0.4f // General / unmatched
        };
    }

    /// <summary>A differentiated subtask with unique action + scope.</summary>
    private sealed record Subtask(string Action, string Scope);

    /// <summary>A (Worker, Subtask) assignment pair.</summary>
    private sealed record WorkerAssignment(SwarmAgent Worker, Subtask Subtask);

    private async Task SimulateWorkerTaskAsync(SwarmAgent worker, string task)
    {
        PuddingLogger.SwarmDebug($"── WorkerTask: {worker.Id} starting ──");

        // Step 1: Analyze
        worker.AddThinkingStep($"Analyzing: \"{Truncate(task, 40)}\"");
        worker.Log("Analyzing task...");
        await Task.Delay(1200);

        // Step 2: Execute
        worker.Status = AgentStatus.ToolExecuting;
        worker.Log("Executing tool calls...");
        worker.AddThinkingStep("Executing: calling tools to implement solution.");
        PuddingLogger.SwarmTrace($"  {worker.Id}: Thinking → ToolExecuting");
        await Task.Delay(1800);

        worker.TokensUsed += 389;

        // 20% chance of simulated error for realism
        if (Random.Shared.NextDouble() < 0.2)
        {
            worker.Status = AgentStatus.Error;
            worker.Log("ERROR: Build failed — attempting auto-fix.");
            worker.AddThinkingStep("Error: build failed. Attempting auto-fix...");
            PuddingLogger.SwarmWarn($"  {worker.Id}: simulated build error");

            var errBubble = new SwarmBubble
            {
                SenderId = worker.Id,
                SenderName = worker.DisplayName,
                Content = $"Build error while working on: {Truncate(task, 40)}\nAttempting auto-fix...",
                Kind = BubbleKind.Blocked,
            };
            worker.PushBubble(errBubble);
            AddSwarmLog($"{worker.DisplayName}: ⚠ Build error — retrying");
            await Task.Delay(1500);

            // Retry
            worker.Status = AgentStatus.Thinking;
            worker.Log("Retrying after fix...");
            worker.AddThinkingStep("Retry: applying fix and re-executing.");
            await Task.Delay(800);

            worker.Status = AgentStatus.ToolExecuting;
            worker.TokensUsed += 156;
            await Task.Delay(1200);
        }

        // Step 3: Complete
        worker.TokensUsed += 234;
        worker.Status = AgentStatus.Completed;
        worker.CurrentTask = $"✅ {Truncate(task, 30)}";
        worker.Log($"Task completed. Tokens: {worker.TokensUsed}");
        worker.AddThinkingStep($"Task completed. Tokens used: {worker.TokensUsed}.");

        var okBubble = new SwarmBubble
        {
            SenderId = worker.Id,
            SenderName = worker.DisplayName,
            Content = $"Task completed.\n\nTask: {Truncate(task, 50)}\nTokens: {worker.TokensUsed}\nFiles modified: {Random.Shared.Next(1, 5)}",
            Kind = BubbleKind.Success,
        };
        worker.PushBubble(okBubble);
        AddSwarmLog($"{worker.DisplayName}: ✅ Completed ({worker.TokensUsed} tokens)");
        PuddingLogger.SwarmDebug($"  {worker.Id}: completed, tokens={worker.TokensUsed}");
    }

    private void AddSwarmLog(string msg) =>
        SwarmEventLog.Add($"[{DateTime.Now:HH:mm:ss}] {msg}");

    [RelayCommand]
    private async Task SimulateSwarmAsync()
    {
        using var _ = PuddingLogger.BeginTrace(PuddingLogger.NewTraceId());
        PuddingLogger.SwarmInfo("═══ SimulateSwarm: START ═══");
        AddSwarmLog("▶ Simulation started");
        var leader = SwarmAgents.FirstOrDefault(a => a.Role == AgentRole.Leader);
        var workers = SwarmAgents.Where(a => a.Role == AgentRole.Worker).ToList();

        if (leader is null || workers.Count == 0)
        {
            PuddingLogger.SwarmWarn("SimulateSwarm: aborted — no leader or no workers");
            return;
        }

        PuddingLogger.SwarmInfo($"SimulateSwarm: leader={leader.Id}, workers={workers.Count}");

        // Phase 1: Leader planning
        PuddingLogger.SwarmDebug("── Phase 1: Leader planning ──");
        leader.Status = AgentStatus.Thinking;
        leader.CurrentTask = "Analyzing requirements...";
        leader.AddThinkingStep("Phase 1: Analyzing requirements and planning decomposition.");
        leader.Log("Thinking: analyzing requirements...");
        AddSwarmLog($"{leader.DisplayName}: Planning task decomposition");
        PuddingLogger.SwarmTrace($"  {leader.Id}: Idle → Thinking, task=\"Analyzing requirements...\"");
        await Task.Delay(1500);

        // Phase 2: Assign tasks to workers
        PuddingLogger.SwarmDebug($"── Phase 2: Dispatching {workers.Count} tasks ──");
        leader.Status = AgentStatus.ToolExecuting;
        leader.CurrentTask = "Dispatching tasks";
        leader.AddThinkingStep($"Phase 2: Dispatching {workers.Count} task(s) to workers.");
        leader.Log("Dispatching tasks to workers...");
        for (var i = 0; i < workers.Count; i++)
        {
            var w = workers[i];
            w.Status = AgentStatus.Thinking;
            w.CurrentTask = $"task-{i + 1:D3}: Implementing feature {(char)('A' + i)}";
            w.TokensUsed = 0;
            AddSwarmLog($"{leader.DisplayName} → {w.DisplayName}: {w.CurrentTask}");

            PuddingLogger.SwarmDebug($"  {leader.Id} → {w.Id}: task=\"{w.CurrentTask}\"");
            w.Log($"Received task from {leader.DisplayName}: {w.CurrentTask}");
            w.AddThinkingStep($"Received: {w.CurrentTask}");

            // Leader sends a command bubble to the worker
            var bubble = new SwarmBubble
            {
                SenderId = leader.Id,
                SenderName = leader.DisplayName,
                Content = $"Please implement feature {(char)('A' + i)}. Focus on correctness first, then optimize.",
                Kind = BubbleKind.Command,
            };
            w.PushBubble(bubble);
            PuddingLogger.SwarmTrace($"  Bubble: {leader.Id} → {w.Id}: kind=Command, preview=\"{bubble.Preview}\"");
            await Task.Delay(800);
        }

        leader.Status = AgentStatus.Idle;
        leader.CurrentTask = "Monitoring workers";
        leader.Log("All tasks dispatched. Monitoring workers.");
        PuddingLogger.SwarmDebug($"  {leader.Id}: ToolExecuting → Idle, monitoring");

        // Phase 3: Workers execute (cycle through states)
        PuddingLogger.SwarmDebug("── Phase 3: Workers executing ──");
        foreach (var w in workers)
        {
            PuddingLogger.SwarmDebug($"  {w.Id}: starting execution");
            w.Status = AgentStatus.ToolExecuting;
            w.Log("Executing tool calls...");
            w.AddThinkingStep("Executing: calling tools to implement solution.");
            AddSwarmLog($"{w.DisplayName}: Executing tool calls...");
            w.TokensUsed += 342;
            PuddingLogger.SwarmTrace($"    {w.Id}: Thinking → ToolExecuting, tokens={w.TokensUsed}");
            await Task.Delay(1200);

            // Simulate one worker hitting an error
            if (w == workers.LastOrDefault() && workers.Count > 1)
            {
                w.Status = AgentStatus.Error;
                w.CurrentTask += " ⚠ compile error";
                w.Log("ERROR: Build failed — CS0246: The type 'AuthToken' could not be found.");
                w.AddThinkingStep("Error encountered: CS0246. Attempting auto-fix...");
                PuddingLogger.SwarmWarn($"    {w.Id}: ToolExecuting → Error (compile error)");
                AddSwarmLog($"{w.DisplayName}: ❌ Build failed — retrying");

                // Error bubble
                var errBubble = new SwarmBubble
                {
                    SenderId = w.Id,
                    SenderName = w.DisplayName,
                    Content = "Build failed: CS0246 — The type 'AuthToken' could not be found.\nAttempting auto-fix...",
                    Kind = BubbleKind.Blocked,
                };
                w.PushBubble(errBubble);
                PuddingLogger.SwarmTrace($"    Bubble: {w.Id} → self: kind=Blocked, preview=\"{errBubble.Preview}\"");
                await Task.Delay(1000);

                w.Status = AgentStatus.Thinking;
                w.CurrentTask = w.CurrentTask.Replace(" ⚠ compile error", "");
                w.Log("Analyzing error, attempting fix...");
                w.AddThinkingStep("Retrying: analyzing error and applying fix.");
                PuddingLogger.SwarmDebug($"    {w.Id}: Error → Thinking (retry)");
                AddSwarmLog($"{w.DisplayName}: Analyzing error, fixing...");
                await Task.Delay(1000);

                w.Status = AgentStatus.ToolExecuting;
                w.TokensUsed += 128;
                w.Log("Re-executing tool calls after fix...");
                PuddingLogger.SwarmTrace($"    {w.Id}: Thinking → ToolExecuting (retry), tokens={w.TokensUsed}");
                await Task.Delay(800);
            }

            w.Status = AgentStatus.Completed;
            w.TokensUsed += 567;
            w.Log($"Task completed. Total tokens: {w.TokensUsed}");
            w.AddThinkingStep($"Completed. Tokens used: {w.TokensUsed}.");
            PuddingLogger.SwarmDebug($"    {w.Id}: → Completed, total tokens={w.TokensUsed}");
            AddSwarmLog($"{w.DisplayName}: ✅ Task completed ({w.TokensUsed} tokens)");

            // Success bubble
            var okBubble = new SwarmBubble
            {
                SenderId = w.Id,
                SenderName = w.DisplayName,
                Content = $"Task completed successfully.\nTokens used: {w.TokensUsed}\nFiles modified: 3",
                Kind = BubbleKind.Success,
            };
            w.PushBubble(okBubble);
            PuddingLogger.SwarmTrace($"    Bubble: {w.Id} → self: kind=Success, preview=\"{okBubble.Preview}\"");
            await Task.Delay(600);
        }

        // Phase 4: Leader summarizes
        PuddingLogger.SwarmDebug("── Phase 4: Leader reviewing ──");
        leader.Status = AgentStatus.Thinking;
        leader.CurrentTask = "Reviewing results...";
        leader.Log("Reviewing all worker outputs...");
        leader.AddThinkingStep("Phase 4: Reviewing all worker results for correctness.");
        AddSwarmLog($"{leader.DisplayName}: Reviewing all worker outputs");
        await Task.Delay(1200);

        leader.Status = AgentStatus.Completed;
        leader.CurrentTask = "All tasks completed";
        leader.Log("All tasks completed successfully.");
        leader.AddThinkingStep("All workers finished. Swarm task complete.");

        var totalTokens = SwarmAgents.Sum(a => a.TokensUsed);
        PuddingLogger.SwarmInfo($"═══ SimulateSwarm: END — total tokens={totalTokens} ═══");
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

        PuddingLogger.SwarmDebug($"SendAgentMessage: user → {agent.Id}: \"{Truncate(text, 60)}\"");

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
        agent.Log($"Received message from Commander: {Truncate(text, 60)}");
        PuddingLogger.SwarmTrace($"  Bubble pushed: kind=Command, unread={agent.UnreadCount}");
        AgentMessageInput = "";

        // Simulate the agent "receiving" and auto-replying after a short delay
        _ = SimulateAgentReplyAsync(agent, text);
    }

    private async Task SimulateAgentReplyAsync(SwarmAgent agent, string userMessage)
    {
        PuddingLogger.SwarmDebug($"SimulateAgentReply: {agent.Id} processing \"{Truncate(userMessage, 40)}\"");
        var prevStatus = agent.Status;
        agent.Status = AgentStatus.Thinking;
        agent.Log("Thinking about user message...");
        agent.AddThinkingStep($"Processing user request: \"{Truncate(userMessage, 50)}\"");
        PuddingLogger.SwarmTrace($"  {agent.Id}: {prevStatus} → Thinking");
        await Task.Delay(1500);

        var kind = BubbleKind.Info;
        var reply = $"Understood. Working on: \"{Truncate(userMessage, 40)}\"";

        // Vary the reply style for demo purposes
        if (userMessage.Contains("test", StringComparison.OrdinalIgnoreCase))
        {
            reply = $"Running tests for the requested scope. Will report results shortly.\n\nTarget: {userMessage}";
            kind = BubbleKind.Success;
            agent.AddThinkingStep("Decision: user wants tests → executing test runner.");
        }
        else if (userMessage.Contains("fix", StringComparison.OrdinalIgnoreCase) ||
                 userMessage.Contains("error", StringComparison.OrdinalIgnoreCase))
        {
            reply = $"⚠️ Analyzing the error context.\n\nI'll need to inspect the relevant files before proposing a fix.\n\nOriginal request: {userMessage}";
            kind = BubbleKind.Blocked;
            agent.AddThinkingStep("Decision: user reports error → analyzing error context.");
        }
        else if (userMessage.Contains("review", StringComparison.OrdinalIgnoreCase))
        {
            reply = $"Code review in progress.\n\nI have concerns about the current approach — will flag issues if found.\n\nScope: {userMessage}";
            kind = BubbleKind.Critique;
            agent.AddThinkingStep("Decision: user wants review → scanning code for issues.");
        }
        else
        {
            agent.AddThinkingStep("Decision: general request → acknowledging and working.");
        }

        agent.Status = AgentStatus.ToolExecuting;
        agent.Log($"Executing: preparing {kind} reply...");
        PuddingLogger.SwarmTrace($"  {agent.Id}: Thinking → ToolExecuting, reply kind={kind}");
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
        agent.Log($"Reply sent ({kind}). Tokens: +234, total={agent.TokensUsed}");
        agent.AddThinkingStep($"Reply sent. Kind={kind}. Returning to idle.");

        PuddingLogger.SwarmDebug($"  {agent.Id}: reply sent, kind={kind}, tokens={agent.TokensUsed}, → Idle");
        AddSwarmLog($"{agent.DisplayName} → 👤: {bubble.Preview}");
    }

    /// <summary>Open the full-text reader for a bubble (click on bubble).</summary>
    [RelayCommand]
    private void OpenBubbleReader(SwarmBubble? bubble)
    {
        if (bubble is null) return;
        bubble.IsRead = true;
        ReadingBubble = bubble;
        PuddingLogger.SwarmTrace($"OpenBubbleReader: sender={bubble.SenderName} kind={bubble.Kind} preview=\"{bubble.Preview}\"");

        // Find the owning agent and reduce unread
        var agent = SwarmAgents.FirstOrDefault(a => a.MessageHistory.Contains(bubble));
        if (agent is not null && agent.UnreadCount > 0)
        {
            agent.UnreadCount--;
            PuddingLogger.SwarmTrace($"  {agent.Id}: unread={agent.UnreadCount}");
        }
    }

    /// <summary>Close the message reader overlay.</summary>
    [RelayCommand]
    private void CloseReader()
    {
        PuddingLogger.SwarmTrace("CloseReader");
        ReadingBubble = null;
    }

    /// <summary>Open agent's full message history and mark all read.</summary>
    [RelayCommand]
    private void OpenAgentHistory()
    {
        if (SelectedSwarmAgent is null) return;
        PuddingLogger.SwarmDebug($"OpenAgentHistory: {SelectedSwarmAgent.Id} — marking {SelectedSwarmAgent.UnreadCount} as read");
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
