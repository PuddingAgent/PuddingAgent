using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PuddingDesktop.Foundation;

namespace PuddingDesktop;

/// <summary>
/// DS-08 library slice: the memory page tree and the books/chapters inside it. Everything is
/// workspace + agent scoped, so switching the agent switches the whole library.
/// </summary>
public sealed partial class MainWindow
{
    private IReadOnlyList<MemoryLibrary> _mlLibraries = [];
    private IReadOnlyList<MemoryTreeNode> _mlTree = [];
    private MemoryBook? _mlBook;
    private MemoryChapter? _mlChapter;
    private IReadOnlyList<AgentInstanceSummary> _mlAgents = [];
    private bool _mlBuilt;
    private bool _mlSwitching;

    private ComboBox _mlWorkspacePicker = null!, _mlAgentPicker = null!, _mlLibraryPicker = null!;
    private ComboBox _mlParentPicker = null!, _mlNodeType = null!;
    private TextBox _mlNodeName = null!, _mlNodeSummary = null!;
    private TextBlock _mlTreeSummary = null!;
    private StackPanel _mlTreeLines = null!;
    private ComboBox _mlBookPicker = null!;
    private TextBlock _mlBookDetail = null!;
    private TextBox _mlBookTitle = null!, _mlBookSummary = null!;
    private ComboBox _mlChapterPicker = null!;
    private TextBox _mlChapterTitle = null!, _mlChapterContent = null!, _mlChapterImportance = null!;
    private TextBlock _mlChapterDetail = null!;
    private Button _mlBookArchive = null!, _mlChapterArchive = null!;
    private InfoBar _mlNotice = null!;

    private void BuildMemoryLibraryPanel()
    {
        if (_mlBuilt) return;
        _mlBuilt = true;

        _mlWorkspacePicker = new ComboBox { Header = "工作区", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_mlWorkspacePicker, "记忆所属工作区");
        _mlWorkspacePicker.SelectionChanged += async (_, _) => { if (!_mlSwitching) await LoadMemoryAgentsAsync(); };
        _mlAgentPicker = new ComboBox { Header = "Agent 实例", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_mlAgentPicker, "记忆所属 Agent");
        _mlAgentPicker.SelectionChanged += async (_, _) => { if (!_mlSwitching) await LoadMemoryLibrariesAsync(); };
        _mlLibraryPicker = new ComboBox { Header = "资料库", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_mlLibraryPicker, "选择资料库");
        _mlLibraryPicker.SelectionChanged += async (_, _) => { if (!_mlSwitching) await LoadMemoryTreeAsync(); };
        var ensureDefault = new Button { Content = "确保默认资料库" };
        ensureDefault.Click += async (_, _) => await EnsureDefaultMemoryLibraryAsync();
        var refresh = new Button { Content = "刷新" };
        refresh.Click += async (_, _) => await LoadMemoryTreeAsync();
        _mlNotice = new InfoBar { IsOpen = false, IsClosable = true };

        _mlTreeSummary = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = .8, FontSize = 12 };
        _mlTreeLines = new StackPanel { Spacing = 2 };
        _mlParentPicker = new ComboBox { Header = "父节点", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_mlParentPicker, "新节点的父节点");
        _mlNodeType = new ComboBox { Header = "节点类型", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var nodeType in MemoryLibraryText.NodeTypes)
            _mlNodeType.Items.Add(new ComboBoxItem { Content = MemoryLibraryText.DescribeNodeType(nodeType), Tag = nodeType });
        _mlNodeType.SelectedIndex = 0;
        _mlNodeName = Field("节点名称");
        _mlNodeSummary = Field("节点摘要");
        var createNode = new Button { Content = "创建节点" };
        createNode.Click += async (_, _) => await CreateMemoryNodeAsync();

        _mlBookPicker = new ComboBox { Header = "Book", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_mlBookPicker, "选择 Book");
        _mlBookPicker.SelectionChanged += async (_, _) => { if (!_mlSwitching) await LoadMemoryBookAsync(); };
        _mlBookDetail = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 12 };
        _mlBookTitle = Field("Book 标题");
        _mlBookSummary = Field("Book 摘要");
        var saveBook = new Button { Content = "保存 Book" };
        saveBook.Click += async (_, _) => await SaveMemoryBookAsync();
        var createBook = new Button { Content = "按当前标题新建 Book" };
        createBook.Click += async (_, _) => await CreateMemoryBookAsync();
        _mlBookArchive = new Button { Content = "归档 Book" };
        _mlBookArchive.Click += async (_, _) => await ArchiveMemoryBookAsync();

        _mlChapterPicker = new ComboBox { Header = "章节（分页显示）", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_mlChapterPicker, "选择章节");
        _mlChapterPicker.SelectionChanged += (_, _) => { if (!_mlSwitching) ApplyMemoryChapterSelection(); };
        _mlChapterDetail = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 12 };
        _mlChapterTitle = Field("章节标题");
        _mlChapterContent = new TextBox
        {
            Header = "章节内容", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 120,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        _mlChapterImportance = Field("重要度 0~1", "留空按 0（未标注）处理");
        var saveChapter = new Button { Content = "保存章节" };
        saveChapter.Click += async (_, _) => await SaveMemoryChapterAsync();
        var createChapter = new Button { Content = "新建章节" };
        createChapter.Click += async (_, _) => await CreateMemoryChapterAsync();
        _mlChapterArchive = new Button { Content = "归档章节" };
        _mlChapterArchive.Click += async (_, _) => await ArchiveMemoryChapterAsync();

        MemoryLibrarySettings.Content = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(0, 16, 8, 16),
            Children =
            {
                Card("作用范围", MemoryLibraryText.ScopeNotice, _mlWorkspacePicker, _mlAgentPicker, _mlLibraryPicker,
                    Row(ensureDefault, refresh)),
                Card("页面树", "树由 Core 返回，界面按层级缩进显示，不重排父子关系。",
                    _mlTreeSummary, _mlTreeLines,
                    _mlParentPicker, _mlNodeType, _mlNodeName, _mlNodeSummary, Row(createNode)),
                Card("Book 与章节", MemoryLibraryText.ArchiveNotice,
                    _mlBookPicker, _mlBookDetail, _mlBookTitle, _mlBookSummary,
                    Row(saveBook, createBook, _mlBookArchive),
                    _mlChapterPicker, _mlChapterDetail, _mlChapterTitle, _mlChapterContent, _mlChapterImportance,
                    Row(saveChapter, createChapter, _mlChapterArchive)),
                _mlNotice
            }
        };
        SetMemoryLibraryEnabled(false);
    }

    internal async Task LoadMemoryAgentsAsync()
    {
        if (!_mlBuilt) return;
        try
        {
            if (AgentSelected(_mlWorkspacePicker) is not { } workspaceId)
            {
                _mlSwitching = true;
                try
                {
                    var workspaces = await _agentDirectory.ListWorkspacesAsync();
                    _mlWorkspacePicker.Items.Clear();
                    foreach (var workspace in workspaces)
                        _mlWorkspacePicker.Items.Add(new ComboBoxItem
                        {
                            Content = $"{workspace.Name}（{workspace.WorkspaceId}）", Tag = workspace.WorkspaceId
                        });
                    _mlWorkspacePicker.SelectedIndex = workspaces.Count > 0 ? 0 : -1;
                }
                finally { _mlSwitching = false; }
                if (AgentSelected(_mlWorkspacePicker) is not { } selected) { _mlNotice.IsOpen = false; return; }
                workspaceId = selected;
            }

            _mlAgents = await _agentDirectory.ListInstancesAsync(workspaceId);
            _mlSwitching = true;
            try
            {
                var previous = AgentSelected(_mlAgentPicker);
                _mlAgentPicker.Items.Clear();
                foreach (var agent in _mlAgents)
                    _mlAgentPicker.Items.Add(new ComboBoxItem
                    {
                        Content = $"{agent.DisplayName}（{agent.AgentId}）", Tag = agent.AgentId
                    });
                var index = _mlAgents.ToList().FindIndex(agent => agent.AgentId == previous);
                _mlAgentPicker.SelectedIndex = _mlAgents.Count > 0 ? Math.Max(0, index) : -1;
            }
            finally { _mlSwitching = false; }
            await LoadMemoryLibrariesAsync();
        }
        catch (Exception exception) { ReportMemoryLibraryFailure(exception); }
    }

    private async Task LoadMemoryLibrariesAsync()
    {
        if (AgentSelected(_mlWorkspacePicker) is not { } workspaceId || AgentSelected(_mlAgentPicker) is not { } agentId)
        {
            _mlTreeSummary.Text = "请先选择工作区与 Agent。";
            _mlTreeLines.Children.Clear();
            return;
        }
        try
        {
            _mlLibraries = await _memoryLibrary.ListLibrariesAsync(workspaceId, agentId);
            _mlSwitching = true;
            try
            {
                var previous = AgentSelected(_mlLibraryPicker);
                _mlLibraryPicker.Items.Clear();
                foreach (var library in _mlLibraries)
                    _mlLibraryPicker.Items.Add(new ComboBoxItem
                    {
                        Content = $"{library.Name}（{library.LibraryId}）", Tag = library.LibraryId
                    });
                var index = _mlLibraries.ToList().FindIndex(library => library.LibraryId == previous);
                _mlLibraryPicker.SelectedIndex = _mlLibraries.Count > 0 ? Math.Max(0, index) : -1;
            }
            finally { _mlSwitching = false; }
            await LoadMemoryTreeAsync();
        }
        catch (Exception exception) { ReportMemoryLibraryFailure(exception); }
    }

    private async Task EnsureDefaultMemoryLibraryAsync()
    {
        if (AgentSelected(_mlWorkspacePicker) is not { } workspaceId || AgentSelected(_mlAgentPicker) is not { } agentId)
        {
            WarnMemoryLibrary("请先选择工作区与 Agent");
            return;
        }
        await RunMemoryLibraryAsync("默认资料库已就绪", "Core 返回了该 Agent 的默认资料库（已存在则复用）。",
            async () => { await _memoryLibrary.EnsureDefaultLibraryAsync(workspaceId, agentId); await LoadMemoryLibrariesAsync(); });
    }

    private async Task LoadMemoryTreeAsync()
    {
        if (AgentSelected(_mlWorkspacePicker) is not { } workspaceId || AgentSelected(_mlAgentPicker) is not { } agentId
            || AgentSelected(_mlLibraryPicker) is not { } libraryId)
        {
            _mlTreeLines.Children.Clear();
            _mlTreeSummary.Text = _mlLibraries.Count == 0 ? "该 Agent 还没有资料库；可先「确保默认资料库」。" : "请选择资料库。";
            FillMemoryNodePickers();
            return;
        }
        try
        {
            _mlTree = await _memoryLibrary.ReadTreeAsync(workspaceId, agentId, libraryId);
            var lines = MemoryLibraryText.RenderTree(_mlTree);
            _mlTreeLines.Children.Clear();
            if (lines.Count == 0) _mlTreeLines.Children.Add(Muted("该资料库还没有节点。"));
            foreach (var line in lines) _mlTreeLines.Children.Add(Muted(line));
            _mlTreeSummary.Text = $"节点 {MemoryLibraryText.CountNodes(_mlTree)} 个 · " +
                                  $"其中 Book 节点 {MemoryLibraryText.Flatten(_mlTree).Count(node => node.HasBook)} 个";
            FillMemoryNodePickers();
            SetMemoryLibraryEnabled(true);
            _mlNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportMemoryLibraryFailure(exception); }
    }

    private void FillMemoryNodePickers()
    {
        var flat = MemoryLibraryText.Flatten(_mlTree);
        _mlSwitching = true;
        try
        {
            var previousParent = AgentSelected(_mlParentPicker);
            _mlParentPicker.Items.Clear();
            _mlParentPicker.Items.Add(new ComboBoxItem { Content = "（根节点）", Tag = "" });
            foreach (var node in flat)
                _mlParentPicker.Items.Add(new ComboBoxItem { Content = $"{node.TypeText} · {node.Title}", Tag = node.Id });
            var parentIndex = flat.ToList().FindIndex(node => node.Id == previousParent);
            _mlParentPicker.SelectedIndex = parentIndex >= 0 ? parentIndex + 1 : 0;

            var previousBook = AgentSelected(_mlBookPicker);
            _mlBookPicker.Items.Clear();
            foreach (var node in flat.Where(node => node.HasBook))
                _mlBookPicker.Items.Add(new ComboBoxItem
                {
                    Content = $"{node.Title} · {node.Status}", Tag = node.BookId
                });
            var bookIndex = flat.Where(node => node.HasBook).ToList().FindIndex(node => node.BookId == previousBook);
            _mlBookPicker.SelectedIndex = _mlBookPicker.Items.Count > 0 ? Math.Max(0, bookIndex) : -1;
        }
        finally { _mlSwitching = false; }
    }

    private async Task LoadMemoryBookAsync()
    {
        _mlBook = null;
        _mlChapter = null;
        _mlSwitching = true;
        try
        {
            var workspaceId = AgentSelected(_mlWorkspacePicker);
            var agentId = AgentSelected(_mlAgentPicker);
            var bookId = AgentSelected(_mlBookPicker);
            if (string.IsNullOrEmpty(workspaceId) || string.IsNullOrEmpty(agentId) || string.IsNullOrEmpty(bookId))
            {
                _mlBookDetail.Text = _mlBookPicker.Items.Count == 0
                    ? "该资料库还没有挂载 Book 的节点；可先用「按当前标题新建 Book」。"
                    : "请选择一个 Book。";
                _mlChapterPicker.Items.Clear();
                _mlChapterTitle.Text = "";
                _mlChapterContent.Text = "";
                _mlChapterImportance.Text = "";
                _mlChapterDetail.Text = "";
                _mlBookTitle.Text = "";
                _mlBookSummary.Text = "";
                return;
            }

            _mlBook = await _memoryLibrary.ReadBookAsync(workspaceId, agentId, bookId);
            if (_mlBook is null)
            {
                _mlBookDetail.Text = "该 Book 已不存在，请刷新。";
                return;
            }
            _mlBookTitle.Text = _mlBook.Title;
            _mlBookSummary.Text = _mlBook.Summary;
            _mlBookDetail.Text =
                $"{_mlBook.Title}（{_mlBook.BookId}）· 状态 {_mlBook.Status} · 章节 {_mlBook.Chapters.Count} 个\n" +
                (_mlBook.Summary.Length == 0 ? "（没有摘要）" : _mlBook.Summary) + "\n" + MemoryLibraryText.ArchiveNotice;

            _mlChapterPicker.Items.Clear();
            foreach (var chapter in _mlBook.Chapters)
                _mlChapterPicker.Items.Add(new ComboBoxItem
                {
                    Content = $"{chapter.Title} · {chapter.ImportanceText} · {chapter.Preview}", Tag = chapter.ChapterId
                });
            _mlChapterPicker.SelectedIndex = _mlBook.Chapters.Count > 0 ? 0 : -1;
            ApplyMemoryChapterSelection();
        }
        catch (Exception exception) { ReportMemoryLibraryFailure(exception); }
        finally { _mlSwitching = false; }
    }

    private void ApplyMemoryChapterSelection()
    {
        _mlChapter = AgentSelected(_mlChapterPicker) is { } chapterId
            ? _mlBook?.Chapters.FirstOrDefault(chapter => chapter.ChapterId == chapterId)
            : null;
        _mlChapterTitle.Text = _mlChapter?.Title ?? "";
        _mlChapterContent.Text = _mlChapter?.Content ?? "";
        _mlChapterImportance.Text = _mlChapter is null
            ? ""
            : _mlChapter.Importance.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        _mlChapterDetail.Text = _mlChapter is null
            ? (_mlBook is null ? "请先选择 Book。" : "该 Book 还没有章节；可新建一个。")
            : $"{_mlChapter.ChapterId} · 类型 {_mlChapter.ContentType} · {_mlChapter.ImportanceText} 重要度 · " +
              $"更新 {_mlChapter.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm}";
        _mlBookArchive.IsEnabled = _mlBook is not null;
        _mlChapterArchive.IsEnabled = _mlChapter is not null;
    }

    private async Task CreateMemoryNodeAsync()
    {
        if (AgentSelected(_mlWorkspacePicker) is not { } workspaceId || AgentSelected(_mlAgentPicker) is not { } agentId
            || AgentSelected(_mlLibraryPicker) is not { } libraryId)
        {
            WarnMemoryLibrary("请先选择工作区、Agent 与资料库");
            return;
        }
        var create = new MemoryTreeNodeCreate(workspaceId, agentId, libraryId, AgentSelected(_mlParentPicker) ?? "",
            _mlNodeName.Text.Trim(), _mlNodeSummary.Text.Trim(), AgentSelected(_mlNodeType) ?? "Page");
        var errors = MemoryLibraryText.Validate(create);
        if (errors.Count > 0) { ShowNotice(_mlNotice, InfoBarSeverity.Warning, "请先修正表单", string.Join(" ", errors)); return; }
        await RunMemoryLibraryAsync("节点已创建", "节点已挂在所选父节点下；树由 Core 重新返回。",
            async () =>
            {
                await _memoryLibrary.CreateTreeNodeAsync(create);
                _mlNodeName.Text = "";
                _mlNodeSummary.Text = "";
                await LoadMemoryTreeAsync();
            });
    }

    private async Task CreateMemoryBookAsync()
    {
        if (AgentSelected(_mlWorkspacePicker) is not { } workspaceId || AgentSelected(_mlAgentPicker) is not { } agentId
            || AgentSelected(_mlLibraryPicker) is not { } libraryId)
        {
            WarnMemoryLibrary("请先选择工作区、Agent 与资料库");
            return;
        }
        var create = new MemoryBookCreate(workspaceId, agentId, libraryId, AgentSelected(_mlParentPicker) ?? "",
            _mlBookTitle.Text.Trim(), _mlBookSummary.Text.Trim());
        var errors = MemoryLibraryText.Validate(create);
        if (errors.Count > 0) { ShowNotice(_mlNotice, InfoBarSeverity.Warning, "请先修正表单", string.Join(" ", errors)); return; }
        await RunMemoryLibraryAsync("Book 已创建", "Book 已挂到所选节点（根节点时只创建 Book）。",
            async () => { await _memoryLibrary.CreateBookAsync(create); await LoadMemoryTreeAsync(); });
    }

    private async Task SaveMemoryBookAsync()
    {
        if (_mlBook is not { } book) { WarnMemoryLibrary("请先选择 Book"); return; }
        var edit = new MemoryBookEdit(book.WorkspaceId, AgentSelected(_mlAgentPicker) ?? "", book.BookId,
            _mlBookTitle.Text.Trim(), _mlBookSummary.Text.Trim());
        var errors = MemoryLibraryText.Validate(edit);
        if (errors.Count > 0) { ShowNotice(_mlNotice, InfoBarSeverity.Warning, "请先修正表单", string.Join(" ", errors)); return; }
        await RunMemoryLibraryAsync("Book 已保存", "只更新标题与摘要；章节内容不变。",
            async () => { await _memoryLibrary.UpdateBookAsync(edit); await LoadMemoryBookAsync(); });
    }

    private async Task ArchiveMemoryBookAsync()
    {
        if (_mlBook is not { } book) { WarnMemoryLibrary("请先选择 Book"); return; }
        if (AgentSelected(_mlAgentPicker) is not { } agentId) { WarnMemoryLibrary("请先选择 Agent"); return; }
        if (!await ConfirmMemoryArchiveAsync("归档 Book", book.Title, "章节内容会保留，只是状态变为已归档。")) return;
        await RunMemoryLibraryAsync("Book 已归档", MemoryLibraryText.ArchiveNotice,
            async () => { await _memoryLibrary.ArchiveBookAsync(book.WorkspaceId, agentId, book.BookId); await LoadMemoryBookAsync(); });
    }

    private async Task CreateMemoryChapterAsync()
    {
        if (_mlBook is not { } book) { WarnMemoryLibrary("请先选择 Book"); return; }
        if (AgentSelected(_mlAgentPicker) is not { } agentId) { WarnMemoryLibrary("请先选择 Agent"); return; }
        var importance = MemoryLibraryText.ParseImportance(_mlChapterImportance.Text);
        if (importance is null)
        {
            ShowNotice(_mlNotice, InfoBarSeverity.Warning, "重要度不是数字", "请填 0 到 1 之间的数字，或留空表示未标注。");
            return;
        }
        var create = new MemoryChapterCreate(book.WorkspaceId, agentId, book.BookId, _mlChapterTitle.Text.Trim(),
            _mlChapterContent.Text, importance.Value);
        var errors = MemoryLibraryText.Validate(create);
        if (errors.Count > 0) { ShowNotice(_mlNotice, InfoBarSeverity.Warning, "请先修正表单", string.Join(" ", errors)); return; }
        await RunMemoryLibraryAsync("章节已创建", "章节已加入该 Book。",
            async () => { await _memoryLibrary.CreateChapterAsync(create); await LoadMemoryBookAsync(); });
    }

    private async Task SaveMemoryChapterAsync()
    {
        if (_mlChapter is not { } chapter) { WarnMemoryLibrary("请先选择章节"); return; }
        if (AgentSelected(_mlAgentPicker) is not { } agentId) { WarnMemoryLibrary("请先选择 Agent"); return; }
        var importance = MemoryLibraryText.ParseImportance(_mlChapterImportance.Text);
        if (importance is null)
        {
            ShowNotice(_mlNotice, InfoBarSeverity.Warning, "重要度不是数字", "请填 0 到 1 之间的数字，或留空表示未标注。");
            return;
        }
        var edit = new MemoryChapterEdit(_mlBook?.WorkspaceId ?? "", agentId, chapter.ChapterId,
            _mlChapterTitle.Text.Trim(), _mlChapterContent.Text, importance.Value);
        var errors = MemoryLibraryText.Validate(edit);
        if (errors.Count > 0) { ShowNotice(_mlNotice, InfoBarSeverity.Warning, "请先修正表单", string.Join(" ", errors)); return; }
        await RunMemoryLibraryAsync("章节已保存", "标题、内容与重要度已更新。",
            async () => { await _memoryLibrary.UpdateChapterAsync(edit); await LoadMemoryBookAsync(); });
    }

    private async Task ArchiveMemoryChapterAsync()
    {
        if (_mlChapter is not { } chapter || _mlBook is not { } book) { WarnMemoryLibrary("请先选择章节"); return; }
        if (AgentSelected(_mlAgentPicker) is not { } agentId) { WarnMemoryLibrary("请先选择 Agent"); return; }
        if (!await ConfirmMemoryArchiveAsync("归档章节", chapter.Title, "章节内容会保留，只是状态变为已归档。")) return;
        await RunMemoryLibraryAsync("章节已归档", MemoryLibraryText.ArchiveNotice,
            async () => { await _memoryLibrary.ArchiveChapterAsync(book.WorkspaceId, agentId, chapter.ChapterId); await LoadMemoryBookAsync(); });
    }

    private async Task<bool> ConfirmMemoryArchiveAsync(string title, string name, string consequence)
    {
        var confirm = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = title,
            Content = $"将归档 {name}。{consequence}归档不是删除，内容仍保留。",
            PrimaryButtonText = "归档", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
        };
        return await confirm.ShowAsync() == ContentDialogResult.Primary;
    }

    private void WarnMemoryLibrary(string message) =>
        ShowNotice(_mlNotice, InfoBarSeverity.Warning, "请先选择", message);

    private async Task RunMemoryLibraryAsync(string title, string message, Func<Task> action)
    {
        SetMemoryLibraryEnabled(false);
        try
        {
            await action();
            SetMemoryLibraryEnabled(true);
            ShowNotice(_mlNotice, InfoBarSeverity.Success, title, message);
        }
        catch (Exception exception) { ReportMemoryLibraryFailure(exception); }
    }

    private void ReportMemoryLibraryFailure(Exception exception)
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
        SetMemoryLibraryEnabled(!unavailable);
        ShowNotice(_mlNotice, severity, title, message);
    }

    private void SetMemoryLibraryEnabled(bool enabled)
    {
        foreach (var control in new Control[]
        {
            _mlWorkspacePicker, _mlAgentPicker, _mlLibraryPicker, _mlParentPicker, _mlNodeType, _mlNodeName,
            _mlNodeSummary, _mlBookPicker, _mlBookTitle, _mlBookSummary, _mlChapterPicker, _mlChapterTitle,
            _mlChapterContent, _mlChapterImportance
        }) control.IsEnabled = enabled;
        if (!enabled)
        {
            _mlBookArchive.IsEnabled = false;
            _mlChapterArchive.IsEnabled = false;
            return;
        }
        _mlBookArchive.IsEnabled = _mlBook is not null;
        _mlChapterArchive.IsEnabled = _mlChapter is not null;
    }
}
