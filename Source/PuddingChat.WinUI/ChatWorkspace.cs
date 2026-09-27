using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace PuddingChat.WinUI;

/// <summary>Role-first native workspace. All continuations return to the UI thread; Core owns execution.</summary>
public sealed class ChatWorkspace : UserControl, IDisposable
{
    private readonly IChatClient _client;
    private readonly Uri? _origin;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ChatSelection _state = new();
    private CancellationTokenSource? _selection;
    private readonly ComboBox _workspaces = new() { Header = "工作空间", HorizontalAlignment = HorizontalAlignment.Stretch, DisplayMemberPath = "Name" };
    private readonly ListView _roles = new() { SelectionMode = ListViewSelectionMode.Single };
    private readonly StackPanel _messages = new() { Spacing = 8 };
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _title = new() { Text = "选择角色，开始工作", FontSize = 24, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _subtitle = new() { FontSize = 12, Opacity = .65, TextWrapping = TextWrapping.Wrap };
    private readonly InfoBar _notice = new() { IsOpen = true, IsClosable = false, Title = "正在加载工作空间", Message = "正在读取本机角色与主会话。" };
    private readonly Grid _chat = new() { RowSpacing = 16, Padding = new Thickness(24) };
    private readonly Button _refresh = new() { Content = "刷新角色" };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Dictionary<string, RoleAvatarCard> _cards = [];
    private readonly Dictionary<string, (string Signature, MessageCard Card)> _messageCards = [];
    private readonly ColumnDefinition _navigationColumn = new() { Width = new GridLength(248) };
    private Grid? _navigation;
    private bool _disposed, _busy, _refreshing, _changingDraft, _connected;
    private bool _active = true;
    private bool _loadingWorkspaces;
    private Task? _initialization;
    private long _workspaceGeneration;
    private Agent? _agent;
    public ChatComposer Composer { get; } = new();
    public RoleKey? SelectedRole => _state.Role;
    public Conversation? CurrentConversation => _state.Conversation;
    public int RoleCount => _cards.Count;
    public event EventHandler? SettingsRequested;
    public event EventHandler? RuntimeRequested;
    public event EventHandler? AdministrationRequested;
    public void SetActive(bool active) { _active = active; if (active && _connected && !_disposed) _timer.Start(); else _timer.Stop(); }

    public void SetNavigationWidth(double width)
    {
        _navigationColumn.Width = new GridLength(width);
        if (_navigation is not null) _navigation.Visibility = width > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public ChatWorkspace(IChatClient client, Uri? origin = null)
    {
        _client = client; _origin = origin;
        var root = new Grid(); root.ColumnDefinitions.Add(_navigationColumn);
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var navigation = Surfaces.Navigation(); navigation.Padding = new Thickness(16); navigation.RowSpacing = 16;
        _navigation = navigation;
        navigation.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        navigation.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        navigation.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var head = new StackPanel { Spacing = 12 };
        head.Children.Add(new TextBlock { Text = "我的角色", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        head.Children.Add(_workspaces); head.Children.Add(_refresh); navigation.Children.Add(head);
        Grid.SetRow(_roles, 1); navigation.Children.Add(_roles);
        var footer = new StackPanel { Spacing = 8 };
        var settings = new Button { Content = "设置", HorizontalAlignment = HorizontalAlignment.Stretch };
        var runtime = new Button { Content = "运行中心", HorizontalAlignment = HorizontalAlignment.Stretch };
        var administration = new Button { Content = "高级管理（Web）", HorizontalAlignment = HorizontalAlignment.Stretch };
        settings.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        runtime.Click += (_, _) => RuntimeRequested?.Invoke(this, EventArgs.Empty);
        administration.Click += (_, _) => AdministrationRequested?.Invoke(this, EventArgs.Empty);
        if (_client is IWorkspaceSetupClient)
        {
            var setup = new Button { Content = "创建工作空间与角色", HorizontalAlignment = HorizontalAlignment.Stretch };
            setup.Click += async (_, _) =>
            {
                setup.IsEnabled = false;
                try { await GuardAsync(OpenSetupAsync); }
                finally { setup.IsEnabled = true; }
            };
            footer.Children.Add(setup);
        }
        footer.Children.Add(administration); footer.Children.Add(settings); footer.Children.Add(runtime); Grid.SetRow(footer, 2); navigation.Children.Add(footer); root.Children.Add(navigation);
        _chat.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _chat.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _chat.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _chat.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new StackPanel { Spacing = 6 }; heading.Children.Add(_title); heading.Children.Add(_subtitle); _chat.Children.Add(heading);
        Grid.SetRow(_notice, 1); _chat.Children.Add(_notice);
        _scroll = new ScrollViewer { Content = _messages, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(_scroll, 2); _chat.Children.Add(_scroll);
        Grid.SetRow(Composer, 3); _chat.Children.Add(Composer);
        Grid.SetColumn(_chat, 1); root.Children.Add(_chat);
        Content = root; UpdateComposer();
        _refresh.Click += async (_, _) => await GuardAsync(LoadWorkspacesAsync);
        _workspaces.SelectionChanged += async (_, _) => { if (!_loadingWorkspaces) await GuardAsync(RefreshRolesAsync); };
        _roles.SelectionChanged += async (_, _) =>
        {
            if (_roles.SelectedItem is RoleAvatarCard card && _workspaces.SelectedItem is Workspace workspace)
                await GuardAsync(() => SelectRoleAsync(workspace.WorkspaceId, card.Agent));
        };
        Composer.DraftChanged += (_, _) => { if (!_changingDraft) _state.Draft = Composer.Draft; UpdateComposer(); };
        Composer.SendRequested += async (_, _) => await GuardAsync(SendAsync);
        Composer.CancelRequested += async (_, _) => await GuardAsync(CancelAsync);
        _timer.Tick += async (_, _) => { if (_active && IsLoaded) await GuardAsync(RefreshAsync); };
        Loaded += async (_, _) => { await GuardAsync(InitializeAsync); if (_active && _connected && !_disposed) _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
    }
    private async Task OpenSetupAsync()
    {
        if (_client is not IWorkspaceSetupClient setupClient) return;
        var form = new WorkspaceSetupForm(setupClient);
        await form.LoadAsync(_lifetime.Token);
        if (_disposed) return;
        var dialog = new ContentDialog { Title = "准备编码工作空间", Content = form,
            PrimaryButtonText = "创建或打开", CloseButtonText = "取消", XamlRoot = XamlRoot };
        WorkspaceSetupResult? created = null;
        var saving = false;
        dialog.Closing += (_, args) => { if (saving && !_disposed) args.Cancel = true; };
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            saving = true; dialog.IsPrimaryButtonEnabled = false;
            try { created = await form.SubmitAsync(_lifetime.Token); args.Cancel = created is null; }
            catch (OperationCanceledException) { args.Cancel = true; }
            finally { saving = false; dialog.IsPrimaryButtonEnabled = true; deferral.Complete(); }
        };
        using var registration = _lifetime.Token.Register(() => DispatcherQueue.TryEnqueue(dialog.Hide));
        await dialog.ShowAsync();
        if (_disposed || created is null) return;
        await LoadWorkspacesAsync();
        if (_disposed) return;
        _loadingWorkspaces = true;
        try { _workspaces.SelectedItem = ((Workspace[])_workspaces.ItemsSource).First(w => w.WorkspaceId == created.WorkspaceId); }
        finally { _loadingWorkspaces = false; }
        await RefreshRolesAsync();
        if (!_disposed && _cards.TryGetValue(created.AgentId, out var card)) _roles.SelectedItem = card;
    }
    private async Task GuardAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (_disposed) return;
            _notice.IsOpen = true; _notice.Severity = InfoBarSeverity.Error; _notice.Title = "操作未完成";
            _notice.Message = exception is HttpRequestException or InvalidOperationException ? exception.Message : "请重试，或在运行中心检查 Core。";
        }
    }
    public Task InitializeAsync()
    {
        if (_initialization is null || _initialization.IsFaulted || _initialization.IsCanceled)
            _initialization = LoadWorkspacesAsync();
        return _initialization;
    }
    private async Task LoadWorkspacesAsync()
    {
        if (_disposed || _loadingWorkspaces) return;
        _loadingWorkspaces = true; _refresh.IsEnabled = false;
        try
        {
            var workspaces = await _client.GetWorkspacesAsync(_lifetime.Token);
            if (_disposed) return;
            _connected = true;
            _workspaceGeneration++; _selection?.Cancel(); _state.Select(null); _agent = null; UpdateComposer();
            _messages.Children.Clear(); _messageCards.Clear(); _roles.Items.Clear(); _cards.Clear(); SetDraft();
            _workspaces.ItemsSource = workspaces;
            _notice.IsOpen = true; _notice.Severity = InfoBarSeverity.Informational; _notice.Title = "选择角色";
            _notice.Message = workspaces.Length == 0 ? "暂无工作空间，请点击左侧“创建工作空间与角色”。" : "角色的主会话、草稿和运行状态会在这里展示。";
            if (workspaces.Length > 0) _workspaces.SelectedIndex = 0;
            await RefreshRolesAsync();
            if (_active && IsLoaded) _timer.Start();
            UpdateComposer();
        }
        finally { _loadingWorkspaces = false; if (!_disposed) _refresh.IsEnabled = true; }
    }
    private async Task RefreshRolesAsync()
    {
        if (!_connected || _workspaces.SelectedItem is not Workspace workspace) return;
        var generation = ++_workspaceGeneration;
        _selection?.Cancel(); _state.Select(null); _agent = null; _messages.Children.Clear(); SetDraft(); UpdateComposer();
        _roles.Items.Clear(); _cards.Clear();
        var agents = await _client.GetAgentsAsync(workspace.WorkspaceId, _lifetime.Token);
        if (_disposed || generation != _workspaceGeneration) return;
        foreach (var agent in agents)
        { var card = new RoleAvatarCard(agent, _origin); _cards.Add(agent.AgentId, card); _roles.Items.Add(card); }
        _notice.IsOpen = true; _notice.Title = agents.Length == 0 ? "暂无角色" : "选择一位角色";
        _notice.Message = agents.Length == 0 ? "当前工作空间没有角色。角色配置仍由 Core 管理。" : "角色承担工作，主会话保留上下文。";
        await RefreshStatusesAsync(workspace.WorkspaceId, generation, _lifetime.Token);
    }
    public async Task SelectRoleAsync(string workspace, Agent agent)
    {
        _selection?.Cancel(); _selection?.Dispose(); _selection = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _state.Select(new(workspace, agent.AgentId)); _agent = agent;
        _messageCards.Clear();
        _title.Text = agent.Label; _subtitle.Text = $"{workspace} / {agent.AgentId} · {agent.Description}";
        _messages.Children.Clear(); SetDraft(); UpdateComposer();
        _notice.IsOpen = true; _notice.Title = "正在读取主会话"; _notice.Message = "";
        await RefreshConversationAsync(_state.Generation, _selection.Token);
    }
    private async Task RefreshAsync()
    {
        if (_refreshing || !_connected || _disposed) return;
        _refreshing = true;
        try
        {
            if (_state.Role is not null && _selection is { } selection) await RefreshConversationAsync(_state.Generation, selection.Token);
            if (_workspaces.SelectedItem is Workspace workspace) await RefreshStatusesAsync(workspace.WorkspaceId, _workspaceGeneration, _lifetime.Token);
        }
        finally { _refreshing = false; }
    }
    private async Task RefreshStatusesAsync(string workspace, long generation, CancellationToken ct)
    {
        var statuses = await _client.GetStatusesAsync(workspace, ct);
        if (_disposed || generation != _workspaceGeneration) return;
        foreach (var status in statuses) if (_cards.TryGetValue(status.AgentId, out var card)) card.SetStatus(status);
    }
    private async Task RefreshConversationAsync(long generation, CancellationToken ct)
    {
        if (_state.Role is not { } role) return;
        var snapshot = await _client.GetConversationAsync(role, _state.Conversation?.EventCursor, ct);
        if (_disposed || snapshot is null || !_state.Apply(generation, snapshot)) return;
        var followBottom = _scroll.ScrollableHeight - _scroll.VerticalOffset < 80;
        _messages.Children.Clear();
        foreach (var message in snapshot.Messages)
        {
            var id = message.MessageId;
            var running = snapshot.ActiveRun is { } active && message.RunId == active.RunId && message.Role != "user" ? active : null;
            var rendered = running is null ? message : message with { Content = running.OutputSnapshot.Markdown,
                Status = running.StatusText, ProcessItems = running.OutputSnapshot.ProcessItems };
            var signature = System.Text.Json.JsonSerializer.Serialize(rendered);
            _messageCards.TryGetValue(id, out var previous);
            if (previous.Signature != signature)
            {
                var card = new MessageCard(rendered, running is null ? () => _client.GetProcessAsync(role, id, ct) : null);
                card.IsProcessExpanded = previous.Card?.IsProcessExpanded ?? false;
                _messageCards[id] = (signature, card);
            }
            _messages.Children.Add(_messageCards[id].Card);
        }
        foreach (var id in _messageCards.Keys.Except(snapshot.Messages.Select(m => m.MessageId)).ToArray()) _messageCards.Remove(id);
        if (snapshot.ActiveRun is { } run && !snapshot.Messages.Any(m => m.RunId == run.RunId && m.Role != "user"))
        {
            var live = new StackPanel { Spacing = 12, Padding = new Thickness(20) };
            live.Children.Add(new TextBlock { Text = $"{run.StatusText} · {run.Summary}", TextWrapping = TextWrapping.Wrap });
            // Canonical events carry their original sequence, tool-call identity and status.
            MessageCard.RenderProcess(live, run.OutputSnapshot.ProcessItems);
            if (run.OutputSnapshot.Window?.HasMoreBefore == true) live.Children.Add(new TextBlock { Text = "仅显示当前执行事件窗口。", Opacity = .6 });
            if (!string.IsNullOrEmpty(run.OutputSnapshot.Markdown)) live.Children.Add(MessageCard.RenderText(run.OutputSnapshot.Markdown));
            _messages.Children.Add(live);
        }
        _notice.IsOpen = snapshot.Messages.Length == 0 && snapshot.ActiveRun is null;
        _notice.Title = "开始新的工作"; _notice.Message = "向这位角色描述任务，消息将进入其主会话。";
        _subtitle.Text = $"{role.WorkspaceId} / {role.AgentId} · 最近 {snapshot.Messages.Length} 条消息 · {snapshot.MainSessionId}";
        _timer.Interval = TimeSpan.FromSeconds(snapshot.ActiveRun is null ? 4 : 1);
        UpdateComposer();
        if (followBottom) DispatcherQueue.TryEnqueue(() => { if (!_disposed && generation == _state.Generation) _scroll.ChangeView(null, _scroll.ScrollableHeight, null, true); });
    }
    public async Task SendAsync()
    {
        if (_busy || !_connected || _agent is not { IsEnabled: true, IsFrozen: false } agent || _state.Role is not { } role) return;
        _busy = true; UpdateComposer();
        var generation = _state.Generation;
        var capturedDraft = _state.Draft;
        // Sending has a lifetime independent of selection. Its receipt belongs to the captured role.
        try
        {
            var pending = _state.Pending;
            if (pending is null)
            {
                var session = await _client.EnsureSessionAsync(role, agent, _lifetime.Token);
                if (_disposed || generation != _state.Generation) return;
                pending = _state.Prepare(session, capturedDraft);
            }
            try { await _client.SendAsync(pending, _lifetime.Token); }
            catch (ArgumentException) { _state.Reject(pending); throw; }
            if (_disposed) return;
            _state.Accept(pending);
            if (generation == _state.Generation)
            {
                SetDraft(); _notice.IsOpen = true; _notice.Title = "消息已接收";
                _notice.Severity = InfoBarSeverity.Informational; _notice.Message = "Core 已受理；执行进度以会话回执为准。";
                await RefreshConversationAsync(generation, _selection!.Token);
            }
        }
        finally { _busy = false; if (!_disposed) UpdateComposer(); }
    }
    public async Task CancelAsync()
    {
        if (_state.Role is not { } role || _state.Conversation is not { } conversation || ChatSelection.ActiveTurn(conversation) is not { } turn) return;
        await _client.CancelAsync(role.WorkspaceId, conversation.MainSessionId, turn, _lifetime.Token);
        if (_disposed || _state.Role != role) return;
        _notice.IsOpen = true; _notice.Title = "已请求停止"; _notice.Message = "等待 Core 确认取消结果。";
    }
    private void SetDraft() { _changingDraft = true; try { Composer.Draft = _state.Draft; } finally { _changingDraft = false; } }
    private void UpdateComposer() => Composer.SetAvailability(_connected && !_busy && _agent is { IsEnabled: true, IsFrozen: false }
        && (_state.Pending is not null || !string.IsNullOrWhiteSpace(_state.Draft)), ChatSelection.ActiveTurn(_state.Conversation) is not null, _state.Pending is not null);
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _timer.Stop(); _lifetime.Cancel(); _selection?.Cancel();
        _selection?.Dispose(); _client.Dispose(); _lifetime.Dispose();
    }
}
