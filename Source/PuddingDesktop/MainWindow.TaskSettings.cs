using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PuddingDesktop.Foundation;

namespace PuddingDesktop;

/// <summary>
/// DS-16 tasks card: the management entry — list with Core's filters and keyset paging, create, and the
/// lifecycle commands. The full board, editing, comments, reviews and events belong to a separate native work
/// page, which the card's own note requires and the page states plainly.
/// </summary>
public sealed partial class MainWindow
{
    private TaskListPage _tkPage = TaskListPage.Empty;
    private TaskFilter _tkFilter = TaskFilter.Default;
    private WorkspaceTaskItem? _tkTask;
    private IReadOnlyList<WorkspaceSummary> _tkWorkspaces = [];
    private bool _tkBuilt;
    private bool _tkSwitching;

    private ComboBox _tkWorkspace = null!, _tkStatus = null!, _tkPriority = null!, _tkCommand = null!, _tkTaskPicker = null!;
    private TextBox _tkAgent = null!, _tkReason = null!, _tkLimit = null!;
    private TextBox _tkNewTitle = null!, _tkNewDescription = null!;
    private ComboBox _tkNewPriority = null!;
    private TextBlock _tkPageText = null!, _tkFilterText = null!, _tkDetail = null!;
    private StackPanel _tkList = null!;
    private Button _tkPrevious = null!, _tkNext = null!, _tkCreate = null!, _tkRun = null!;
    private InfoBar _tkNotice = null!;

    private void BuildTaskPanel()
    {
        if (_tkBuilt) return;
        _tkBuilt = true;

        _tkWorkspace = new ComboBox { Header = "工作区（Core 的任务查询必填）", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_tkWorkspace, "选择任务工作区");
        _tkWorkspace.SelectionChanged += async (_, _) => { if (!_tkSwitching) await QueryTasksAsync(resetPage: true); };
        _tkStatus = new ComboBox { Header = "状态", HorizontalAlignment = HorizontalAlignment.Stretch };
        _tkStatus.Items.Add(new ComboBoxItem { Content = "全部状态", Tag = "" });
        foreach (var status in TaskText.Statuses)
            _tkStatus.Items.Add(new ComboBoxItem { Content = TaskText.DescribeStatus(status), Tag = status });
        _tkStatus.SelectedIndex = 0;
        _tkPriority = new ComboBox { Header = "优先级", HorizontalAlignment = HorizontalAlignment.Stretch };
        _tkPriority.Items.Add(new ComboBoxItem { Content = "全部优先级", Tag = "" });
        foreach (var priority in TaskText.Priorities)
            _tkPriority.Items.Add(new ComboBoxItem { Content = TaskText.DescribePriority(priority), Tag = priority });
        _tkPriority.SelectedIndex = 0;
        _tkAgent = Field("Agent ID（按指派筛选 / 指派命令必填）");
        _tkLimit = Field("每页条数", "1–500");
        _tkLimit.Text = "50";
        var query = new Button { Content = "查询" };
        query.Click += async (_, _) => await QueryTasksAsync(resetPage: true);
        var clear = new Button { Content = "清空筛选" };
        clear.Click += async (_, _) =>
        {
            _tkFilter = TaskFilter.Default with { WorkspaceId = AgentSelected(_tkWorkspace) ?? "" };
            ApplyTaskFilterToForm();
            await QueryTasksAsync(resetPage: true);
        };
        _tkPrevious = new Button { Content = "重置到第一页" };
        _tkPrevious.Click += async (_, _) => await QueryTasksAsync(resetPage: true);
        _tkNext = new Button { Content = "下一页（游标）" };
        _tkNext.Click += async (_, _) => await QueryTasksAsync(resetPage: false, cursor: _tkPage.NextCursor);
        _tkPageText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        _tkFilterText = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = .8, FontSize = 12 };
        _tkList = new StackPanel { Spacing = 2 };
        _tkTaskPicker = new ComboBox { Header = "选择任务（命令作用于所选任务）", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_tkTaskPicker, "选择任务");
        _tkTaskPicker.SelectionChanged += (_, _) => { if (!_tkSwitching) ApplyTaskSelection(); };
        _tkDetail = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 12 };

        _tkCommand = new ComboBox { Header = "生命周期命令", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var kind in Enum.GetValues<TaskCommandKind>())
            _tkCommand.Items.Add(new ComboBoxItem { Content = TaskText.DescribeCommand(kind), Tag = kind.ToString() });
        _tkCommand.SelectedIndex = 0;
        _tkReason = Field("原因（随命令写入任务事件）");
        _tkRun = new Button { Content = "执行命令" };
        _tkRun.Click += async (_, _) => await RunTaskCommandAsync();

        _tkNewTitle = Field("新任务标题");
        _tkNewDescription = Field("新任务描述");
        _tkNewPriority = new ComboBox { Header = "新任务优先级", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var priority in TaskText.Priorities)
            _tkNewPriority.Items.Add(new ComboBoxItem { Content = TaskText.DescribePriority(priority), Tag = priority });
        _tkNewPriority.SelectedIndex = 3;
        _tkCreate = new Button { Content = "创建任务" };
        _tkCreate.Click += async (_, _) => await CreateTaskAsync();
        _tkNotice = new InfoBar { IsOpen = false, IsClosable = true };

        TasksSettings.Content = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(0, 16, 8, 16),
            Children =
            {
                Card("任务管理入口", TaskText.VersionNotice + " " + TaskText.HierarchyNotice + " " + TaskText.CursorNotice + " " +
                    TaskText.WorkPageNotice,
                    _tkWorkspace, _tkStatus, _tkPriority, _tkAgent, _tkLimit, Row(query, clear),
                    _tkFilterText, _tkPageText, Row(_tkPrevious, _tkNext), _tkList, _tkTaskPicker, _tkDetail),
                Card("生命周期命令", "命令都带读到的版本；版本过期会被拒绝而不是覆盖。",
                    _tkCommand, _tkReason, Row(_tkRun)),
                Card("创建任务", "创建后的任务从 Backlog 开始，后续状态迁移由 Core 的状态机决定。",
                    _tkNewTitle, _tkNewDescription, _tkNewPriority, Row(_tkCreate)),
                _tkNotice
            }
        };
        SetTaskEnabled(false);
    }

    internal async Task QueryTasksAsync(bool resetPage, string? cursor = null)
    {
        if (!_tkBuilt) return;
        SetTaskEnabled(false);
        try
        {
            // 工作区列表来自 Core：Core 未就绪时这里会抛出，界面据此说「Core 未就绪」而不是抱怨筛选条件。
            await EnsureTaskWorkspacesAsync();
        }
        catch (Exception exception) { ReportTaskFailure(exception); return; }

        var filter = ReadTaskFilter() with
        {
            Cursor = resetPage ? "" : (cursor ?? _tkFilter.Cursor),
            // 工作区来自选择器而不是文本框。
            WorkspaceId = AgentSelected(_tkWorkspace) ?? "",
        };
        var errors = TaskText.Validate(filter);
        if (errors.Count > 0)
        {
            ShowNotice(_tkNotice, InfoBarSeverity.Warning, "请先修正筛选条件", string.Join(" ", errors));
            return;
        }
        _tkFilter = TaskText.Normalize(filter);
        SetTaskEnabled(false);
        try
        {
            _tkPage = await _tasks.ListAsync(_tkFilter);
            _tkFilterText.Text = "筛选：" + _tkFilter.DescribeText;
            _tkPageText.Text = _tkPage.PageText;
            _tkList.Children.Clear();
            if (_tkPage.Items.Count == 0) _tkList.Children.Add(Muted("没有匹配的任务。"));
            foreach (var task in _tkPage.Items) _tkList.Children.Add(Muted(task.LineText));

            var previous = _tkTask?.TaskId;
            _tkSwitching = true;
            try
            {
                _tkTaskPicker.Items.Clear();
                foreach (var task in _tkPage.Items)
                    _tkTaskPicker.Items.Add(new ComboBoxItem { Content = $"{task.DisplayTitle} · v{task.Version}", Tag = task.TaskId });
                var index = _tkPage.Items.ToList().FindIndex(task => task.TaskId == previous);
                _tkTaskPicker.SelectedIndex = _tkPage.Items.Count > 0 ? Math.Max(0, index) : -1;
            }
            finally { _tkSwitching = false; }
            ApplyTaskSelection();
            SetTaskEnabled(true);
            _tkNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportTaskFailure(exception); }
    }

    private async Task EnsureTaskWorkspacesAsync()
    {
        if (_tkWorkspaces.Count > 0) return;
        _tkWorkspaces = await _workspaces.ListAsync();
        _tkSwitching = true;
        try
        {
            var previous = AgentSelected(_tkWorkspace);
            _tkWorkspace.Items.Clear();
            foreach (var workspace in _tkWorkspaces)
                _tkWorkspace.Items.Add(new ComboBoxItem
                {
                    Content = $"{workspace.WorkspaceId} · {workspace.StateText}", Tag = workspace.WorkspaceId
                });
            var index = _tkWorkspaces.ToList().FindIndex(item => item.WorkspaceId == previous);
            _tkWorkspace.SelectedIndex = _tkWorkspaces.Count > 0 ? Math.Max(0, index) : -1;
            if (_tkFilter.WorkspaceId.Length == 0 && _tkWorkspaces.Count > 0)
                _tkFilter = _tkFilter with { WorkspaceId = _tkWorkspaces[0].WorkspaceId };
        }
        finally { _tkSwitching = false; }
    }

    private void ApplyTaskSelection()
    {
        _tkTask = AgentSelected(_tkTaskPicker) is { } taskId
            ? _tkPage.Items.FirstOrDefault(task => task.TaskId == taskId)
            : null;
        _tkDetail.Text = _tkTask is { } task
            ? task.DetailText
            : (_tkPage.Items.Count == 0 ? "请先查询任务。" : "请选择一个任务。");
    }

    private TaskFilter ReadTaskFilter() => new(
        _tkFilter.WorkspaceId, AgentSelected(_tkStatus) ?? "", AgentSelected(_tkPriority) ?? "",
        _tkAgent.Text, _tkFilter.Cursor, int.TryParse(_tkLimit.Text.Trim(), out var size) ? size : _tkFilter.Limit);

    private void ApplyTaskFilterToForm()
    {
        _tkStatus.SelectedIndex = 0;
        _tkPriority.SelectedIndex = 0;
        _tkAgent.Text = _tkFilter.AgentId;
        _tkLimit.Text = _tkFilter.Limit.ToString();
    }

    private async Task CreateTaskAsync()
    {
        var create = new TaskCreate(AgentSelected(_tkWorkspace) ?? "", _tkNewTitle.Text, _tkNewDescription.Text,
            AgentSelected(_tkNewPriority) ?? "p3");
        var errors = TaskText.Validate(create);
        if (errors.Count > 0) { ShowNotice(_tkNotice, InfoBarSeverity.Warning, "请先修正表单", string.Join(" ", errors)); return; }
        await RunTaskAsync("任务已创建", "新任务从 Backlog 开始。",
            async () =>
            {
                await _tasks.CreateAsync(create);
                _tkNewTitle.Text = "";
                _tkNewDescription.Text = "";
                await QueryTasksAsync(resetPage: true);
            });
    }

    private async Task RunTaskCommandAsync()
    {
        if (_tkTask is not { } task) { WarnTask("请先选择任务"); return; }
        var kind = Enum.TryParse<TaskCommandKind>(AgentSelected(_tkCommand), out var parsed)
            ? parsed
            : TaskCommandKind.Cancel;
        var request = new TaskCommandRequest(task.WorkspaceId, task.TaskId, task.Version, _tkAgent.Text, _tkReason.Text);
        var errors = TaskText.Validate(kind, request);
        if (errors.Count > 0) { ShowNotice(_tkNotice, InfoBarSeverity.Warning, "请先修正表单", string.Join(" ", errors)); return; }
        var confirm = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = TaskText.DescribeCommand(kind),
            Content = $"将对「{task.DisplayTitle}」（v{task.Version}）执行：{TaskText.DescribeCommand(kind)}。" +
                      (kind is TaskCommandKind.Cancel or TaskCommandKind.Archive ? " " + TaskText.HierarchyNotice : ""),
            PrimaryButtonText = "执行", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        await RunTaskAsync($"{TaskText.DescribeCommand(kind)}已完成", "任务版本已前进；列表已刷新。",
            async () => { await _tasks.RunCommandAsync(kind, request); await QueryTasksAsync(resetPage: true); });
    }

    private void WarnTask(string message) => ShowNotice(_tkNotice, InfoBarSeverity.Warning, "请先选择", message);

    private async Task RunTaskAsync(string title, string message, Func<Task> action)
    {
        SetTaskEnabled(false);
        try
        {
            await action();
            SetTaskEnabled(true);
            ShowNotice(_tkNotice, InfoBarSeverity.Success, title, message);
        }
        catch (Exception exception) { ReportTaskFailure(exception); }
    }

    private void ReportTaskFailure(Exception exception)
    {
        App.WriteDiagnostic(exception);
        var unavailable = exception is SettingsUnavailableException;
        var (severity, title, message) = exception switch
        {
            SettingsUnavailableException reason => (InfoBarSeverity.Informational, "Core 未就绪",
                reason.Message + " 该分类仍可浏览；表单已禁用，未写入任何内容。"),
            SettingsConflictException conflict => (InfoBarSeverity.Warning, "任务版本冲突，已阻止覆盖", conflict.Message),
            ArgumentException argument => (InfoBarSeverity.Warning, "请求被 Core 拒绝", argument.Message),
            InvalidOperationException invalid => (InfoBarSeverity.Warning, "Core 拒绝了该操作", invalid.Message),
            _ => (InfoBarSeverity.Error, "操作失败", "请查看诊断日志后重试。")
        };
        SetTaskEnabled(!unavailable);
        ShowNotice(_tkNotice, severity, title, message);
    }

    private void SetTaskEnabled(bool enabled)
    {
        foreach (var control in new Control[]
        {
            _tkWorkspace, _tkStatus, _tkPriority, _tkAgent, _tkLimit, _tkTaskPicker, _tkCommand, _tkReason,
            _tkNewTitle, _tkNewDescription, _tkNewPriority
        }) control.IsEnabled = enabled;
        foreach (var button in new[] { _tkPrevious, _tkNext, _tkCreate, _tkRun }) button.IsEnabled = enabled;
    }
}
