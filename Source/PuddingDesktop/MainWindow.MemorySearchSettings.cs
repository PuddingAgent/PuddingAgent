using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PuddingDesktop.Foundation;

namespace PuddingDesktop;

/// <summary>
/// DS-08 search slice: full-text search over an agent's memory plus an inspector that shows the chapter's
/// metadata, its source references and its graph pointers, and can locate the hit in the library tab.
/// </summary>
public sealed partial class MainWindow
{
    private IReadOnlyList<MemorySearchHit> _msHits = [];
    private MemorySearchHit? _msHit;
    private MemoryChapter? _msChapter;
    private bool _msBuilt;
    private bool _msSwitching;

    private ComboBox _msWorkspacePicker = null!, _msAgentPicker = null!, _msTopK = null!, _msHitPicker = null!;
    private TextBox _msQuery = null!;
    private TextBlock _msSummary = null!, _msHitDetail = null!, _msMetadata = null!, _msSourcesSummary = null!, _msPointersSummary = null!;
    private StackPanel _msSources = null!, _msPointers = null!;
    private TextBox _msOwnerType = null!, _msOwnerId = null!, _msSourceType = null!, _msSourceId = null!;
    private ComboBox _msAgents = null!;
    private InfoBar _msNotice = null!;

    private void BuildMemorySearchPanel()
    {
        if (_msBuilt) return;
        _msBuilt = true;

        _msWorkspacePicker = new ComboBox { Header = "工作区", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_msWorkspacePicker, "搜索所属工作区");
        _msWorkspacePicker.SelectionChanged += async (_, _) => { if (!_msSwitching) await LoadMemorySearchAgentsAsync(); };
        _msAgentPicker = new ComboBox { Header = "Agent 实例", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_msAgentPicker, "搜索所属 Agent");
        _msQuery = Field("搜索词");
        _msTopK = new ComboBox { Header = "返回条数", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var size in MemoryLibraryText.SearchTopKSizes)
            _msTopK.Items.Add(new ComboBoxItem { Content = size.ToString(), Tag = size });
        _msTopK.SelectedIndex = 1;
        var search = new Button { Content = "搜索" };
        search.Click += async (_, _) => await RunMemorySearchAsync();
        _msSummary = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = .8, FontSize = 12 };
        _msHitPicker = new ComboBox { Header = "结果", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_msHitPicker, "选择搜索结果");
        _msHitPicker.SelectionChanged += async (_, _) => { if (!_msSwitching) await LoadMemoryInspectorAsync(); };
        _msHitDetail = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };

        _msMetadata = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        _msOwnerType = Field("sources ownerType");
        _msOwnerId = Field("sources ownerId");
        _msSourceType = Field("pointers sourceType");
        _msSourceId = Field("pointers sourceId");
        var inspect = new Button { Content = "查询来源与指针" };
        inspect.Click += async (_, _) => await LoadMemoryInspectorAsync();
        var locate = new Button { Content = "在资料库中打开" };
        locate.Click += async (_, _) => await LocateMemoryHitAsync();
        _msSourcesSummary = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = .8, FontSize = 12 };
        _msSources = new StackPanel { Spacing = 2 };
        _msPointersSummary = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = .8, FontSize = 12 };
        _msPointers = new StackPanel { Spacing = 2 };
        _msAgents = _msAgentPicker;
        _msNotice = new InfoBar { IsOpen = false, IsClosable = true };

        MemorySearchSettings.Content = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(0, 16, 8, 16),
            Children =
            {
                Card("记忆搜索", MemoryLibraryText.SearchNotice,
                    _msWorkspacePicker, _msAgentPicker, _msQuery, _msTopK, Row(search), _msSummary,
                    _msHitPicker, _msHitDetail),
                Card("检查器", MemoryLibraryText.InspectorNotice,
                    _msMetadata, _msOwnerType, _msOwnerId, _msSourceType, _msSourceId, Row(inspect, locate),
                    _msSourcesSummary, _msSources, _msPointersSummary, _msPointers),
                _msNotice
            }
        };
        SetMemorySearchEnabled(false);
    }

    internal async Task LoadMemorySearchAgentsAsync()
    {
        if (!_msBuilt) return;
        try
        {
            if (AgentSelected(_msWorkspacePicker) is not { } workspaceId)
            {
                _msSwitching = true;
                try
                {
                    var workspaces = await _agentDirectory.ListWorkspacesAsync();
                    _msWorkspacePicker.Items.Clear();
                    foreach (var workspace in workspaces)
                        _msWorkspacePicker.Items.Add(new ComboBoxItem
                        {
                            Content = $"{workspace.Name}（{workspace.WorkspaceId}）", Tag = workspace.WorkspaceId
                        });
                    _msWorkspacePicker.SelectedIndex = workspaces.Count > 0 ? 0 : -1;
                }
                finally { _msSwitching = false; }
                if (AgentSelected(_msWorkspacePicker) is not { } selected) { _msNotice.IsOpen = false; return; }
                workspaceId = selected;
            }

            var agents = await _agentDirectory.ListInstancesAsync(workspaceId);
            _msSwitching = true;
            try
            {
                var previous = AgentSelected(_msAgentPicker);
                _msAgentPicker.Items.Clear();
                foreach (var agent in agents)
                    _msAgentPicker.Items.Add(new ComboBoxItem
                    {
                        Content = $"{agent.DisplayName}（{agent.AgentId}）", Tag = agent.AgentId
                    });
                var index = agents.ToList().FindIndex(agent => agent.AgentId == previous);
                _msAgentPicker.SelectedIndex = agents.Count > 0 ? Math.Max(0, index) : -1;
            }
            finally { _msSwitching = false; }
            SetMemorySearchEnabled(true);
            _msNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportMemorySearchFailure(exception); }
    }

    private async Task RunMemorySearchAsync()
    {
        var workspaceId = AgentSelected(_msWorkspacePicker);
        var agentId = AgentSelected(_msAgentPicker);
        var errors = MemoryLibraryText.ValidateSearch(workspaceId ?? "", agentId ?? "", _msQuery.Text);
        if (errors.Count > 0) { ShowNotice(_msNotice, InfoBarSeverity.Warning, "请先修正表单", string.Join(" ", errors)); return; }

        try
        {
            var topK = _msTopK.SelectedItem is ComboBoxItem { Tag: int size } ? size : 20;
            _msHits = await _memoryLibrary.SearchAsync(workspaceId!, agentId!, _msQuery.Text.Trim(), topK);
            _msSwitching = true;
            try
            {
                _msHitPicker.Items.Clear();
                foreach (var hit in _msHits)
                    _msHitPicker.Items.Add(new ComboBoxItem
                    {
                        Content = $"{hit.BookTitle} · 分数 {hit.ScoreText} · {hit.ChapterId}", Tag = hit.ChapterId
                    });
                _msHitPicker.SelectedIndex = _msHits.Count > 0 ? 0 : -1;
            }
            finally { _msSwitching = false; }
            _msSummary.Text = _msHits.Count == 0
                ? $"「{_msQuery.Text.Trim()}」没有匹配结果。"
                : $"命中 {_msHits.Count} 条（Core 全文检索，按 Core 返回的顺序与分数）。";
            await LoadMemoryInspectorAsync();
            _msNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportMemorySearchFailure(exception); }
    }

    private async Task LoadMemoryInspectorAsync()
    {
        _msHit = AgentSelected(_msHitPicker) is { } chapterId
            ? _msHits.FirstOrDefault(hit => hit.ChapterId == chapterId)
            : null;
        _msHitDetail.Text = _msHit is null
            ? (_msHits.Count == 0 ? "请先搜索。" : "请选择一条结果。")
            : $"{_msHit.BookTitle}（Book {_msHit.BookId}）\n章节 {_msHit.ChapterId} · 分数 {_msHit.ScoreText}\n\n{_msHit.Snippet}";

        if (_msHit is null)
        {
            _msChapter = null;
            _msMetadata.Text = "请选择一条结果以查看元数据。";
            _msSources.Children.Clear();
            _msPointers.Children.Clear();
            _msSourcesSummary.Text = "";
            _msPointersSummary.Text = "";
            return;
        }

        // A hit carries the chapter id, so the inspector fills both of Core's key pairs from it.
        _msOwnerType.Text = "chapter";
        _msOwnerId.Text = _msHit.ChapterId;
        _msSourceType.Text = "chapter";
        _msSourceId.Text = _msHit.ChapterId;
        await LoadMemoryInspectionAsync();
    }

    private async Task LoadMemoryInspectionAsync()
    {
        if (AgentSelected(_msWorkspacePicker) is not { } workspaceId || AgentSelected(_msAgentPicker) is not { } agentId)
        {
            WarnMemorySearch("请先选择工作区与 Agent");
            return;
        }
        var ownerErrors = MemoryLibraryText.ValidateOwner(_msOwnerType.Text, _msOwnerId.Text, "来源 owner");
        var sourceErrors = MemoryLibraryText.ValidateOwner(_msSourceType.Text, _msSourceId.Text, "指针 source");
        if (ownerErrors.Count > 0 || sourceErrors.Count > 0)
        {
            ShowNotice(_msNotice, InfoBarSeverity.Warning, "请先修正表单", string.Join(" ", ownerErrors.Concat(sourceErrors)));
            return;
        }

        try
        {
            await LoadChapterMetadataAsync(workspaceId, agentId);
            var sources = await _memoryLibrary.ListSourcesAsync(workspaceId, agentId,
                _msOwnerType.Text.Trim(), _msOwnerId.Text.Trim());
            _msSources.Children.Clear();
            _msSourcesSummary.Text = sources.Count == 0 ? "没有来源引用。" : $"来源引用 {sources.Count} 条";
            foreach (var source in sources)
                _msSources.Children.Add(Muted($"{source.Display} · {source.TargetRange}" +
                    (source.Description.Length == 0 ? "" : $" · {source.Description}") +
                    $" · {source.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}"));

            var pointers = await _memoryLibrary.ListPointersAsync(workspaceId, agentId,
                _msSourceType.Text.Trim(), _msSourceId.Text.Trim());
            _msPointers.Children.Clear();
            var outgoing = pointers.Count(pointer => pointer.Direction == "outgoing");
            _msPointersSummary.Text = pointers.Count == 0
                ? "没有关联指针。"
                : $"指针 {pointers.Count} 条（出边 {outgoing} · 反链 {pointers.Count - outgoing}）";
            foreach (var pointer in pointers)
                _msPointers.Children.Add(Muted(pointer.Display +
                    (pointer.Description.Length == 0 ? "" : $" · {pointer.Description}")));
            _msNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportMemorySearchFailure(exception); }
    }

    private async Task LoadChapterMetadataAsync(string workspaceId, string agentId)
    {
        _msChapter = null;
        if (_msHit is null) { _msMetadata.Text = "没有可显示的元数据。"; return; }
        var book = await _memoryLibrary.ReadBookAsync(workspaceId, agentId, _msHit.BookId);
        _msChapter = book?.Chapters.FirstOrDefault(chapter => chapter.ChapterId == _msHit.ChapterId);
        _msMetadata.Text = _msChapter is null
            ? $"{_msHit.BookTitle} · 章节 {_msHit.ChapterId}（元数据需要 Book 可读；该 Book 当前不可读或章节已被归档/移除）"
            : $"Book {_msHit.BookTitle}（{_msHit.BookId}）· 状态 {book!.Status}\n" +
              $"章节 {_msChapter.Title} · 类型 {_msChapter.ContentType} · {_msChapter.ImportanceText} 重要度\n" +
              $"更新 {_msChapter.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm} · 内容 {_msChapter.Content.Length} 字符";
    }

    private async Task LocateMemoryHitAsync()
    {
        if (_msHit is null) { WarnMemorySearch("请先选择一条搜索结果"); return; }
        // 定位 = 把资料库页签切到该 Book：先确保资料库页签已加载，再选中 Book。
        OpenSettingsCategory("memory", "library");
        await LoadMemoryAgentsAsync();
        _mlBookPicker.SelectedItem = _mlBookPicker.Items
            .Cast<ComboBoxItem>().FirstOrDefault(item => (string?)item.Tag == _msHit.BookId);
        if (_mlBookPicker.SelectedItem is null)
        {
            ShowNotice(_msNotice, InfoBarSeverity.Informational, "该 Book 不在当前资料库的页面树里",
                "它可能属于另一个资料库或被归档；资料库页签已为你打开。");
            return;
        }
        ShowNotice(_msNotice, InfoBarSeverity.Success, "已在资料库中定位", $"资料库页签已切到 Book {_msHit.BookId}。");
    }

    private void WarnMemorySearch(string message) =>
        ShowNotice(_msNotice, InfoBarSeverity.Warning, "请先选择", message);

    private void ReportMemorySearchFailure(Exception exception)
    {
        App.WriteDiagnostic(exception);
        var unavailable = exception is SettingsUnavailableException;
        var (severity, title, message) = exception switch
        {
            SettingsUnavailableException reason => (InfoBarSeverity.Informational, "Core 未就绪",
                reason.Message + " 该分类仍可浏览；表单已禁用，未写入任何内容。"),
            UnauthorizedAccessException denied => (InfoBarSeverity.Warning, "Core 拒绝访问该资料库", denied.Message),
            ArgumentException argument => (InfoBarSeverity.Warning, "请求被 Core 拒绝", argument.Message),
            InvalidOperationException invalid => (InfoBarSeverity.Warning, "Core 拒绝了该操作", invalid.Message),
            _ => (InfoBarSeverity.Error, "操作失败", "请查看诊断日志后重试。")
        };
        SetMemorySearchEnabled(!unavailable);
        ShowNotice(_msNotice, severity, title, message);
    }

    private void SetMemorySearchEnabled(bool enabled)
    {
        foreach (var control in new Control[]
        {
            _msWorkspacePicker, _msAgentPicker, _msQuery, _msTopK, _msHitPicker,
            _msOwnerType, _msOwnerId, _msSourceType, _msSourceId
        }) control.IsEnabled = enabled;
    }
}
