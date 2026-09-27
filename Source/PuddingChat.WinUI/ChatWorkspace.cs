using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace PuddingChat.WinUI;

/// <summary>Role-first native workspace. All continuations return to the UI thread; Core owns execution.</summary>
public sealed partial class ChatWorkspace : UserControl, IDisposable
{
    private readonly IChatClient _client;
    private readonly SpeechPlaybackSession? _speech;
    private readonly Uri? _origin;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ChatSelection _state = new();
    private CancellationTokenSource? _selection;
    private CancellationTokenSource? _follow;
    private string? _followSession;
    private readonly ComboBox _workspaces = new() { Header = "工作空间", HorizontalAlignment = HorizontalAlignment.Stretch, DisplayMemberPath = "Name" };
    private readonly ListView _roles = new() { SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = true };
    private readonly VirtualTranscript _transcript = new();
    private readonly ScrollViewer _scroll;
    private readonly TextBox _search = new() { PlaceholderText = "搜索角色或职责" };
    private readonly TextBlock _searchEmpty = new() { Text = "没有匹配的角色", Visibility = Visibility.Collapsed, Opacity = .65 };
    private readonly Button _latest = new() { Content = "↓ 回到最新消息", HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 12), Visibility = Visibility.Collapsed };
    private readonly Dictionary<RoleKey, ReadingBookmark> _reading = [];
    private ReadingBookmark? _restoreBookmark;
    private ReadingPosition? _restoreReading;
    private bool _filtering;
    private ActiveRun? _liveSnapshot;
    private readonly Dictionary<string, ProcessItem> _liveEvents = [];
    private TranscriptItem? _livePanel;
    private readonly TextBlock _title = new() { Text = "选择角色，开始工作", FontSize = 24, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _subtitle = new() { FontSize = 12, Opacity = .65, TextWrapping = TextWrapping.Wrap };
    private readonly InfoBar _notice = new() { IsOpen = true, IsClosable = false, Title = "正在加载工作空间", Message = "正在读取本机角色与主会话。" };
    private readonly Grid _chat = new() { RowSpacing = 16, Padding = new Thickness(24) };
    private readonly Button _refresh = new() { Content = "刷新角色" };
    private readonly Button _older = new() { Content = "加载更早的消息", HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TranscriptItem _olderItem;
    private bool _loadingHistory;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly Dictionary<string, RoleAvatarCard> _cards = [];
    private readonly Dictionary<string, (ChatMessage Message, TranscriptItem Item)> _messageCards = [];
    private readonly ColumnDefinition _navigationColumn = new() { Width = new GridLength(248) };
    private readonly Button _roleMenu = new() { Content = "角色", Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Top };
    private Flyout _roleFlyout = new() { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft };
    private double _preferredNavigationWidth = 248;
    private bool _compactNavigation;
    private Grid? _navigation;
    private bool _disposed, _busy, _refreshing, _changingDraft, _connected;
    private bool _active = true;
    private bool _addingImages;
    private bool _loadingWorkspaces;
    private bool _inspectorOpen;
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
    public void SetActive(bool active)
    {
        _active = active;
        if (active && _connected && !_disposed) _timer.Start(); else _timer.Stop();
        if (!active) { _roleFlyout.Hide(); _speech?.Stop(); }
    }

    public void SetNavigationWidth(double width)
    {
        if (!double.IsFinite(width) || width < 0) throw new ArgumentOutOfRangeException(nameof(width));
        _preferredNavigationWidth = width;
        UpdateNavigationLayout();
    }

    private void UpdateNavigationLayout()
    {
        if (_disposed || _navigation is null || Content is not Grid root || ActualWidth <= 0) return;
        var compact = _preferredNavigationWidth == 0 || ActualWidth < _preferredNavigationWidth + 520;
        if (compact != _compactNavigation)
        {
            _roleFlyout.Hide();
            if (compact)
            {
                root.Children.Remove(_navigation);
                // A flyout that lost its content while closing may retain its old popup lifecycle.
                // Reuse navigation controls, but give each compact-layout epoch a fresh popup host.
                _roleFlyout = new Flyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft, Content = _navigation };
                _roleMenu.Flyout = _roleFlyout;
            }
            else { _roleFlyout.Content = null; root.Children.Insert(0, _navigation); }
            _compactNavigation = compact;
        }
        _navigationColumn.Width = new GridLength(compact ? 0 : _preferredNavigationWidth);
        _navigation.Width = compact ? Math.Max(240, Math.Min(320, ActualWidth - 48)) : double.NaN;
        _navigation.Height = compact ? Math.Max(360, ActualHeight - 96) : double.NaN;
        _roleMenu.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        _chat.Padding = new Thickness(ActualWidth < 520 ? 12 : 24);
    }

    public ChatWorkspace(IChatClient client, Uri? origin = null, ISpeechAudioPlayer? speechPlayer = null)
    {
        _client = client; _origin = origin;
        if (client is IChatSpeechClient speech) _speech = new(speech, speechPlayer ?? new NativeSpeechAudioPlayer());
        _olderItem = new("history-loader", _older, _ => _older) { IsAnchor = false };
        var root = new Grid(); root.ColumnDefinitions.Add(_navigationColumn);
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var navigation = Surfaces.Navigation(); navigation.Padding = new Thickness(16); navigation.RowSpacing = 16;
        _navigation = navigation;
        navigation.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        navigation.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        navigation.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var head = new StackPanel { Spacing = 12 };
        head.Children.Add(new TextBlock { Text = "我的角色", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        head.Children.Add(_workspaces); head.Children.Add(_search); head.Children.Add(_searchEmpty); head.Children.Add(_refresh); navigation.Children.Add(head);
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
        if (_client is IConfigurationClient)
        {
            var models = new Button { Content = "模型与密钥", HorizontalAlignment = HorizontalAlignment.Stretch };
            var editRole = new Button { Content = "编辑当前角色", HorizontalAlignment = HorizontalAlignment.Stretch };
            models.Click += async (_, _) => { models.IsEnabled = false; try { await GuardAsync(() => OpenConfigurationAsync(false)); } finally { models.IsEnabled = true; } };
            editRole.Click += async (_, _) => { editRole.IsEnabled = false; try { await GuardAsync(() => OpenConfigurationAsync(true)); } finally { editRole.IsEnabled = true; } };
            footer.Children.Add(models); footer.Children.Add(editRole);
        }
        footer.Children.Add(administration); footer.Children.Add(settings); footer.Children.Add(runtime); Grid.SetRow(footer, 2); navigation.Children.Add(footer); root.Children.Add(navigation);
        _chat.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _chat.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _chat.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _chat.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new StackPanel { Spacing = 6 }; heading.Children.Add(_title); heading.Children.Add(_subtitle);
        var headingRow = new Grid { ColumnSpacing = 12 };
        headingRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headingRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _roleMenu.Flyout = _roleFlyout;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_roleMenu, "打开角色与工作空间导航");
        Grid.SetColumn(heading, 1); headingRow.Children.Add(_roleMenu); headingRow.Children.Add(heading); _chat.Children.Add(headingRow);
        Grid.SetRow(_notice, 1); _chat.Children.Add(_notice);
        _scroll = new ScrollViewer { Content = _transcript.View, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var viewport = new Grid(); viewport.Children.Add(_scroll); viewport.Children.Add(_latest);
        Grid.SetRow(viewport, 2); _chat.Children.Add(viewport);
        _transcript.View.MaxWidth = 900; _transcript.View.HorizontalAlignment = HorizontalAlignment.Stretch;
        Composer.MaxWidth = 900; Composer.HorizontalAlignment = HorizontalAlignment.Stretch;
        _latest.Click += (_, _) => ScrollToLatest();
        _older.Click += async (_, _) => await GuardAsync(LoadOlderAsync);
        _scroll.ViewChanged += (_, _) => _latest.Visibility = _scroll.ScrollableHeight - _scroll.VerticalOffset < 80 ? Visibility.Collapsed : Visibility.Visible;
        _search.TextChanged += (_, _) => ApplyRoleFilter();
        Grid.SetRow(Composer, 3); _chat.Children.Add(Composer);
        Grid.SetColumn(_chat, 1); root.Children.Add(_chat);
        Content = root; UpdateComposer();
        SizeChanged += (_, _) => UpdateNavigationLayout();
        _refresh.Click += async (_, _) => await GuardAsync(LoadWorkspacesAsync);
        _workspaces.SelectionChanged += async (_, _) => { if (!_loadingWorkspaces) await GuardAsync(RefreshRolesAsync); };
        _roles.SelectionChanged += async (_, _) =>
        {
            if (!_filtering && _roles.SelectedItem is RoleAvatarCard card && _workspaces.SelectedItem is Workspace workspace)
            {
                _roleFlyout.Hide();
                if (_compactNavigation) Composer.FocusEditor();
                await GuardAsync(() => SelectRoleAsync(workspace.WorkspaceId, card.Agent));
            }
        };
        _roles.ItemClick += (_, _) => { _roleFlyout.Hide(); if (_compactNavigation) Composer.FocusEditor(); };
        Composer.DraftChanged += (_, _) => { if (!_changingDraft) _state.Draft = Composer.Draft; UpdateComposer(); };
        Composer.SendRequested += async (_, _) => await GuardAsync(SendAsync);
        Composer.AttachRequested += async (_, _) => await GuardAsync(PickImagesAsync);
        Composer.AttachFileRequested += async (_, _) => await GuardAsync(PickTextFilesAsync);
        Composer.RemoveFileRequested += id => { _state.RemoveFile(id); Composer.SetFiles(_state.Files); UpdateComposer(); };
        Composer.ImportImagesAsync = data => AddImageBatchAsync((import, ct) => NativeImageTransfer.ReadAsync(data, import, ct));
        Composer.ImportFilesAsync = ImportDroppedFilesAsync;
        Composer.RemoveImageRequested += id => { _state.RemoveImage(id); Composer.SetImages(_state.Images); UpdateComposer(); };
        Composer.CancelRequested += async (_, _) => await GuardAsync(CancelAsync);
        _timer.Tick += async (_, _) => { if (_active && IsLoaded) await GuardAsync(RefreshAsync); };
        Loaded += async (_, _) => { await GuardAsync(InitializeAsync); if (_active && _connected && !_disposed) _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
    }
    private Action<string>? InspectionHandler(RoleKey role, string parentSession, CancellationToken ct)
        => _client is not ISubAgentInspectionClient ? null : async runId => await GuardAsync(async () =>
        {
            if (_inspectorOpen || _disposed || ct.IsCancellationRequested || SelectedRole != role
                || CurrentConversation?.MainSessionId != parentSession) return;
            _inspectorOpen = true;
            try
            {
                using var view = new SubAgentInspector(new(role, parentSession, runId), (ISubAgentInspectionClient)_client)
                    { Width = 650, Height = 500 };
                var dialog = new ContentDialog { Title = "子代理运行", Content = view, CloseButtonText = "关闭", XamlRoot = XamlRoot };
                using var registration = ct.Register(() => DispatcherQueue.TryEnqueue(() => { view.Dispose(); dialog.Hide(); }));
                dialog.Opened += async (_, _) => await view.LoadAsync();
                await dialog.ShowAsync();
            }
            finally { _inspectorOpen = false; }
        });
    private async Task OpenConfigurationAsync(bool roleEditor)
    {
        if (_client is not IConfigurationClient config) return;
        var selected = _state.Role;
        UserControl form; Func<CancellationToken, Task<bool>> save;
        if (roleEditor)
        {
            if (selected is null || _client is not IWorkspaceSetupClient models)
                throw new InvalidOperationException("请先选择要编辑的角色。");
            var roleForm = new RoleConfigurationForm(config, models, selected);
            await roleForm.LoadAsync(_lifetime.Token); form = roleForm; save = roleForm.SaveAsync;
        }
        else
        {
            var providerForm = new ProviderConfigurationForm(config);
            await providerForm.LoadAsync(_lifetime.Token); form = providerForm; save = providerForm.SaveAsync;
        }
        if (_disposed) return;
        var dialog = new ContentDialog { Title = roleEditor ? "角色设置" : "模型与密钥", Content = form,
            PrimaryButtonText = "保存", CloseButtonText = "取消", XamlRoot = XamlRoot };
        var saving = false; var saved = false;
        dialog.Closing += (_, args) => { if (saving && !_disposed) args.Cancel = true; };
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral(); saving = true; dialog.IsPrimaryButtonEnabled = false;
            try { saved = await save(_lifetime.Token); args.Cancel = !saved; }
            catch (OperationCanceledException) { args.Cancel = true; }
            finally { saving = false; dialog.IsPrimaryButtonEnabled = true; deferral.Complete(); }
        };
        using var registration = _lifetime.Token.Register(() => DispatcherQueue.TryEnqueue(dialog.Hide));
        await dialog.ShowAsync();
        if (_disposed || !saved || !roleEditor) return;
        await RefreshRolesAsync();
        if (!_disposed && selected is not null && _cards.TryGetValue(selected.AgentId, out var card)) _roles.SelectedItem = card;
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
            _workspaceGeneration++; RememberReading(); _selection?.Cancel(); _state.Select(null); _agent = null; UpdateComposer();
            _transcript.Clear(); _messageCards.Clear(); _roles.Items.Clear(); _cards.Clear(); SetDraft();
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
        RememberReading(); _selection?.Cancel(); _state.Select(null); _agent = null; _transcript.Clear(); SetDraft(); UpdateComposer();
        _roles.Items.Clear(); _cards.Clear();
        var agents = await _client.GetAgentsAsync(workspace.WorkspaceId, _lifetime.Token);
        if (_disposed || generation != _workspaceGeneration) return;
        foreach (var agent in agents)
        { var card = new RoleAvatarCard(agent, _origin); _cards.Add(agent.AgentId, card); }
        ApplyRoleFilter();
        _notice.IsOpen = true; _notice.Title = agents.Length == 0 ? "暂无角色" : "选择一位角色";
        _notice.Message = agents.Length == 0 ? "当前工作空间没有角色。角色配置仍由 Core 管理。" : "角色承担工作，主会话保留上下文。";
        await RefreshStatusesAsync(workspace.WorkspaceId, generation, _lifetime.Token);
    }
    public async Task SelectRoleAsync(string workspace, Agent agent)
    {
        _speech?.Stop();
        RememberReading();
        _selection?.Cancel(); _selection?.Dispose(); _selection = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var role = new RoleKey(workspace, agent.AgentId);
        _restoreBookmark = _reading.GetValueOrDefault(role);
        _restoreReading = _restoreBookmark?.Position ?? ReadingPosition.Latest;
        _follow?.Cancel(); _follow?.Dispose(); _follow = null; _followSession = null;
        _state.Select(role); _agent = agent; _livePanel = null; _liveSnapshot = null; _liveEvents.Clear();
        _messageCards.Clear();
        _title.Text = agent.Label; _subtitle.Text = agent.Description ?? "与角色的工作会话";
        _transcript.Clear(); SetDraft(); UpdateComposer();
        _notice.IsOpen = true; _notice.Title = "正在读取主会话"; _notice.Message = "";
        var generation = _state.Generation; var token = _selection.Token;
        try { await RefreshConversationAsync(generation, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    private async Task FollowConversationAsync(IConversationChanges changes, long generation, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && generation == _state.Generation && _state.Conversation is { } current && _state.Role is { } role)
        {
            await changes.WaitForChangeAsync(role, current.MainSessionId, current.EventCursor, ct);
            // Coalesce token bursts; only one projection request is ever outstanding per subscription.
            await Task.Delay(40, ct);
            if (generation != _state.Generation || _disposed) return;
            await _conversationReads.WaitAsync(ct);
            try
            {
                current = _state.Conversation!;
                if (_client is IConversationActivity activity && current?.ActiveRun is { } run
                    && run.OutputSnapshot.Window is { } window)
                {
                    var page = await activity.ReadActivityAsync(role,
                        new(current.MainSessionId, run.RunId, window.TurnId, current.EventCursor), ct);
                    if (_disposed || generation != _state.Generation) return;
                    if (ConversationActivity.Apply(current, page) is { } updated)
                    { RenderConversation(generation, role, updated, ct); continue; }
                }
                await RefreshConversationCoreAsync(generation, ct);
            }
            finally { _conversationReads.Release(); }
        }
    }
    private async Task RefreshAsync()
    {
        if (_refreshing || !_connected || _disposed) return;
        _refreshing = true;
        try
        {
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
    private readonly SemaphoreSlim _conversationReads = new(1, 1);
    public async Task LoadOlderAsync()
    {
        if (_loadingHistory || _client is not IConversationHistory history || _state.Role is not { } role
            || _state.Conversation is not { OlderCursor: { } before } current || _selection is null) return;
        var generation = _state.Generation; var ct = _selection.Token;
        _loadingHistory = true; _older.IsEnabled = false; _older.Content = "正在加载…";
        try
        {
            await _conversationReads.WaitAsync(ct);
            try
            {
                var page = await history.ReadHistoryAsync(role, current.MainSessionId, before, ct).WaitAsync(ct);
                if (_disposed || generation != _state.Generation) return;
                var position = ReadingPosition.Capture(MessageGeometry(), _scroll.VerticalOffset, _scroll.ScrollableHeight, false);
                if (!_state.PrependHistory(generation, page)) return;
                _restoreReading = position;
                RenderConversation(generation, role, _state.Conversation!, ct);
            }
            finally { _conversationReads.Release(); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { _loadingHistory = false; _older.IsEnabled = true; _older.Content = "加载更早的消息"; }
    }
    private async Task RefreshConversationAsync(long generation, CancellationToken ct)
    {
        await _conversationReads.WaitAsync(ct);
        try { await RefreshConversationCoreAsync(generation, ct); }
        finally { _conversationReads.Release(); }
    }
    private async Task RefreshConversationCoreAsync(long generation, CancellationToken ct)
    {
        if (_state.Role is not { } role) return;
        var snapshot = await _client.GetConversationAsync(role, _state.Conversation?.EventCursor, ct).WaitAsync(ct);
        if (_disposed || snapshot is null || generation != _state.Generation) return;
        while (_client is IConversationActivity activity && snapshot.ActiveRun is { } run && run.OutputSnapshot.Window is { } window)
        {
            var retry = false;
            // Recover the entire active turn to a fixed committed ceiling before going live.
            var restored = snapshot with { EventCursor = 0, ActiveRun = run with {
                OutputSnapshot = new("", [], window with { ThroughSequence = 0 }) } };
            while (true)
            {
                var page = await activity.ReadActivityAsync(role,
                    new(snapshot.MainSessionId, run.RunId, window.TurnId, restored.EventCursor, snapshot.EventCursor, Replay: true), ct);
                if (_disposed || generation != _state.Generation) return;
                if (ConversationActivity.Apply(restored, page) is not { } applied)
                {
                    snapshot = await _client.GetConversationAsync(role, null, ct).WaitAsync(ct)
                        ?? throw new InvalidOperationException("会话已变化，请重新选择角色。");
                    retry = true;
                    break;
                }
                restored = applied;
                if (!page.HasMore) { snapshot = restored; break; }
            }
            if (!retry) break;
        }
        if (_restoreBookmark is { } bookmark)
        {
            _restoreReading = bookmark.PositionFor(snapshot);
            if (_client is IConversationHistory history && bookmark.NeedsHistory(snapshot))
            {
                _notice.Title = "正在恢复上次阅读位置";
                if (!_state.Apply(generation, snapshot)) return;
                while (bookmark.NeedsHistory(_state.Conversation!))
                {
                    var current = _state.Conversation!;
                    var page = await history.ReadHistoryAsync(role, current.MainSessionId, current.OlderCursor!, ct).WaitAsync(ct);
                    if (_disposed || generation != _state.Generation) return;
                    if (!_state.PrependHistory(generation, page))
                        throw new InvalidOperationException("历史记录游标已变化，请重新选择角色。");
                }
                snapshot = _state.Conversation!;
            }
            if (_disposed || generation != _state.Generation) return;
            _restoreBookmark = null;
        }
        RenderConversation(generation, role, snapshot, ct);
    }
    private void RenderConversation(long generation, RoleKey role, Conversation snapshot, CancellationToken ct)
    {
        if (_disposed || !_state.Apply(generation, snapshot)) return;
        snapshot = _state.Conversation!; // Apply may retain previously loaded history.
        // A session rotation cancels the old follower, not the new cards or subscription.
        ct = _selection?.Token ?? ct;
        var position = _restoreReading ?? CaptureReading(); _restoreReading = null;
        var desired = new List<TranscriptItem>();
        if (_client is IConversationHistory && snapshot.OlderCursor is not null) desired.Add(_olderItem);
        foreach (var message in snapshot.Messages)
        {
            var id = message.MessageId;
            var running = snapshot.ActiveRun is { } active && message.RunId == active.RunId && message.Role != "user" ? active : null;
            var rendered = running is null ? message : message with { Content = running.OutputSnapshot.Markdown,
                Status = running.StatusText, ProcessItems = running.OutputSnapshot.ProcessItems };
            if (message.Role != "user" && message.RunId is not null && message.RunId == _liveSnapshot?.RunId)
                rendered = rendered with { ProcessItems = ChatSelection.Ordered(_liveEvents.Values.Concat(rendered.ProcessItems)).ToArray() };
            _messageCards.TryGetValue(id, out var previous);
            if (!SameMessage(previous.Message, rendered))
            {
                var item = previous.Item;
                if (item is null)
                {
                    var state = new MessageViewState();
                    item = new TranscriptItem(id, rendered,
                        row => new MessageCard((ChatMessage)row.Data, () => _client.GetProcessAsync(role, id, ct),
                            _client as IImageAttachmentClient, role.WorkspaceId, ct, state, InspectionHandler(role, snapshot.MainSessionId, ct), _speech, role),
                        (view, data) => ((MessageCard)view).Update((ChatMessage)data));
                }
                else item.Update(rendered);
                _messageCards[id] = (rendered, item);
            }
            desired.Add(_messageCards[id].Item);
        }
        foreach (var id in _messageCards.Keys.Except(snapshot.Messages.Select(m => m.MessageId)).ToArray()) _messageCards.Remove(id);
        if (snapshot.ActiveRun is { } run && !snapshot.Messages.Any(m => m.RunId == run.RunId && m.Role != "user"))
        {
            if (_livePanel is null || _liveSnapshot != run)
            {
                if (_liveSnapshot?.RunId != run.RunId) { _liveEvents.Clear(); _livePanel = null; }
                foreach (var item in run.OutputSnapshot.ProcessItems) _liveEvents[item.Id] = item;
                var output = new OutputSnapshot(run.OutputSnapshot.Markdown, _liveEvents.Values.ToArray());
                if (_livePanel is null)
                {
                    var expansions = new Dictionary<string, bool>();
                    var flowWindow = new FlowWindow();
                    _livePanel = new($"run:{run.RunId}", output, row => {
                        var view = new TurnContentView(expansions, flowWindow) { Padding = new Thickness(20), InspectDelegation = InspectionHandler(role, snapshot.MainSessionId, ct),
                            Images = _client is IImageAttachmentClient images ? new MarkdownImageContext(images, role.WorkspaceId, ct) : null };
                        var data = (OutputSnapshot)row.Data; view.Update(data.ProcessItems, data.Markdown); return view;
                    }, (view, data) => { var value = (OutputSnapshot)data; ((TurnContentView)view).Update(value.ProcessItems, value.Markdown); });
                }
                else _livePanel.Update(output);
                _liveSnapshot = run;
            }
            desired.Add(_livePanel);

        }
        // Stable data rows keep realized cards; the factory only builds controls near the viewport.
        _transcript.SetItems(desired);
        _notice.IsOpen = snapshot.Messages.Length == 0 && snapshot.ActiveRun is null;
        _notice.Title = "开始新的工作"; _notice.Message = "向这位角色描述任务，消息将进入其主会话。";
        _subtitle.Text = _cards.GetValueOrDefault(role.AgentId)?.Agent.Description ?? "与角色的工作会话";
        if (snapshot.ActiveRun is { } statusRun)
            _subtitle.Text += $" · {statusRun.StatusText}" + (statusRun.OutputSnapshot.Window?.HasMoreBefore == true ? " · 正在补齐执行轨迹…" : "");
        UpdateComposer();
        if (_client is IConversationChanges changes && !string.IsNullOrEmpty(snapshot.MainSessionId) && _followSession != snapshot.MainSessionId)
        {
            _follow?.Cancel(); _follow?.Dispose();
            _follow = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _followSession = snapshot.MainSessionId;
            _ = GuardAsync(() => FollowConversationAsync(changes, generation, _follow.Token));
        }
        _scroll.UpdateLayout();
        if (!_disposed && generation == _state.Generation)
            _transcript.Restore(_scroll, position);
    }
    private static bool SameMessage(ChatMessage? before, ChatMessage after) => before is not null
        && before == after with { ProcessItems = before.ProcessItems, ContentParts = before.ContentParts }
        && before.ProcessItems.SequenceEqual(after.ProcessItems)
        && (before.ContentParts ?? []).SequenceEqual(after.ContentParts ?? []);
    private async Task PickImagesAsync()
    {
        if (_client is not IImageAttachmentClient || _state.Role is null || _addingImages) return;
        var generation = _state.Generation;
        var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(XamlRoot.ContentIslandEnvironment.AppWindowId)
        { CommitButtonText = "添加图片", FileTypeFilter = { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp" } };
        var files = await picker.PickMultipleFilesAsync().AsTask(_lifetime.Token);
        if (_disposed || generation != _state.Generation) return;
        await AddImagesAsync(files.Select(f => f.Path));
    }
    public Task AddImagesAsync(IEnumerable<string> paths) => AddImageBatchAsync((import, _) => import(paths.ToArray()));
    private async Task PickTextFilesAsync()
    {
        if (_state.Role is null || _addingImages || _busy) return;
        var generation = _state.Generation;
        var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(XamlRoot.ContentIslandEnvironment.AppWindowId)
        { CommitButtonText = "添加文本文件", FileTypeFilter = { "*" } };
        var files = await picker.PickMultipleFilesAsync().AsTask(_lifetime.Token);
        if (_disposed || generation != _state.Generation) return;
        await AddTextFilesAsync(files.Select(f => f.Path));
    }
    public async Task AddTextFilesAsync(IEnumerable<string> paths)
    {
        if (_state.Role is not { } role || _addingImages || _busy || _agent is not { IsEnabled: true, IsFrozen: false }) return;
        var selected = paths.Take(TextFileContexts.MaxFiles + 1).ToArray();
        if (selected.Length + _state.FilesFor(role).Count > TextFileContexts.MaxFiles)
            throw new ArgumentException("每条消息最多添加 8 个文本文件。");
        _addingImages = true; UpdateComposer();
        try
        {
            var files = new List<TextFileContext>();
            foreach (var path in selected)
                files.Add(await Task.Run(() => TextFileContexts.ReadAsync(path, _lifetime.Token), _lifetime.Token));
            if (_disposed) return;
            // Batch import is atomic; failures keep the old draft, and late completion belongs to the captured role.
            _state.AddFiles(role, files);
            if (_state.Role == role) Composer.SetFiles(_state.Files);
        }
        finally { _addingImages = false; if (!_disposed) UpdateComposer(); }
    }
    private async Task AddImageBatchAsync(Func<Func<IReadOnlyList<string>, Task>, CancellationToken, Task> read)
    {
        if (_client is not IImageAttachmentClient images || _state.Role is not { } role || _addingImages || _busy
            || _agent is not { IsEnabled: true, IsFrozen: false }) return;
        _addingImages = true; UpdateComposer();
        try
        {
            // Capture the role and reserve the composer before resolving asynchronous clipboard/drop data.
            await read(async files =>
            {
                if (files.Count + _state.ImagesFor(role).Count > images.MaxImagesPerMessage)
                    throw new InvalidOperationException($"每条消息最多添加 {images.MaxImagesPerMessage} 张图片。");
                foreach (var path in files)
                {
                    _lifetime.Token.ThrowIfCancellationRequested();
                    var image = await images.ImportImageAsync(role, path, _lifetime.Token);
                    if (_disposed) return;
                    _state.AddImage(role, image);
                    if (_state.Role == role) Composer.SetImages(_state.Images);
                }
            }, _lifetime.Token);
        }
        finally { _addingImages = false; if (!_disposed) UpdateComposer(); }
    }
    public async Task SendAsync()
    {
        if (_busy || _addingImages || !_connected || _agent is not { IsEnabled: true, IsFrozen: false } agent || _state.Role is not { } role) return;
        _busy = true; UpdateComposer();
        var generation = _state.Generation;
        var capturedDraft = _state.Draft;
        var capturedImages = _state.Images;
        var capturedFiles = _state.Files;
        // Sending has a lifetime independent of selection. Its receipt belongs to the captured role.
        try
        {
            var pending = _state.Pending;
            if (pending is null)
            {
                var session = await _client.EnsureSessionAsync(role, agent, _lifetime.Token);
                if (_disposed || generation != _state.Generation) return;
                pending = _state.Prepare(session, capturedDraft, capturedImages, capturedFiles);
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
    private void SetDraft() { _changingDraft = true; try { Composer.Draft = _state.Draft; Composer.SetImages(_state.Images); Composer.SetFiles(_state.Files); } finally { _changingDraft = false; } }
    private void UpdateComposer()
    {
        Composer.SetAttachmentAvailability(!_busy && !_addingImages && _client is IImageAttachmentClient && _agent is { IsEnabled: true, IsFrozen: false });
        Composer.SetFileAvailability(!_busy && !_addingImages && _agent is { IsEnabled: true, IsFrozen: false });
        Composer.SetContext(_agent?.Label, _agent is { IsEnabled: true, IsFrozen: false });
        Composer.SetAvailability(_connected && !_busy && !_addingImages && _agent is { IsEnabled: true, IsFrozen: false }
        && (_state.Pending is not null || !string.IsNullOrWhiteSpace(_state.Draft) || _state.Images.Count > 0 || _state.Files.Count > 0), ChatSelection.ActiveTurn(_state.Conversation) is not null, _state.Pending is not null);
    }
    public int VisibleRoleCount => _roles.Items.Count;
    public void SetRoleFilter(string text) { _search.Text = text; ApplyRoleFilter(); }
    private void ApplyRoleFilter()
    {
        _filtering = true;
        try
        {
            _roles.Items.Clear();
            var query = _search.Text.Trim();
            foreach (var card in _cards.Values)
                if (query.Length == 0 || card.Agent.Label.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || card.Agent.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) == true) _roles.Items.Add(card);
            if (_state.Role is { } role && _cards.TryGetValue(role.AgentId, out var selected) && _roles.Items.Contains(selected)) _roles.SelectedItem = selected;
            _searchEmpty.Visibility = _cards.Count > 0 && _roles.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _filtering = false; }
    }
    private MessageBounds[] MessageGeometry() => _transcript.Geometry();
    private ReadingPosition CaptureReading() => ReadingPosition.Capture(MessageGeometry(), _scroll.VerticalOffset, _scroll.ScrollableHeight);
    private void RememberReading()
    {
        // A cancelled restoration must not replace the saved bookmark with an empty viewport.
        if (_restoreBookmark is null && _state.Role is { } role && _state.Conversation is { } conversation)
        {
            var position = CaptureReading();
            _reading[role] = new(conversation.MainSessionId, position,
                conversation.Messages.FirstOrDefault(m => m.MessageId == position.MessageId)?.CreatedAt);
        }
    }
    public void ScrollToLatest()
    {
        _transcript.Restore(_scroll, ReadingPosition.Latest);
        if (_state.Role is { } role && _state.Conversation is { } conversation)
            _reading[role] = new(conversation.MainSessionId, ReadingPosition.Latest, null);
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _timer.Stop(); _lifetime.Cancel(); _selection?.Cancel();
        _roleFlyout.Hide();
        _selection?.Dispose(); _follow?.Cancel(); _follow?.Dispose(); _speech?.Dispose(); _client.Dispose(); _lifetime.Dispose();
    }
}
