using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PuddingDesktop.Foundation;

namespace PuddingDesktop;

/// <summary>
/// DS-14 subagent-runs card: run listing with Core's filters and paging, the archive-backed detail
/// (task, output, profiles, trace, counts, degradation) and the event list.
/// </summary>
public sealed partial class MainWindow
{
    private SubAgentRunPage _saPage = SubAgentRunPage.Empty;
    private SubAgentRunFilter _saFilter = SubAgentRunFilter.Default;
    private SubAgentRunDetail? _saDetail;
    private SubAgentRun? _saRun;
    private bool _saBuilt;

    private TextBox _saParentSession = null!, _saWorkspace = null!, _saAgent = null!, _saLimit = null!;
    private ComboBox _saStatus = null!;
    private TextBlock _saPageText = null!, _saFilterText = null!, _saDetailText = null!;
    private StackPanel _saList = null!, _saEvents = null!;
    private ComboBox _saRunPicker = null!;
    private Button _saPrevious = null!, _saNext = null!;
    private InfoBar _saNotice = null!;

    private void BuildSubAgentRunPanel()
    {
        if (_saBuilt) return;
        _saBuilt = true;

        _saParentSession = Field("父会话 ID", "Core 侧条件");
        _saWorkspace = Field("工作区", "Core 侧条件");
        _saAgent = Field("Agent 实例 ID", "Core 侧条件");
        _saStatus = new ComboBox { Header = "状态", HorizontalAlignment = HorizontalAlignment.Stretch };
        _saStatus.Items.Add(new ComboBoxItem { Content = "全部状态", Tag = "" });
        foreach (var status in SubAgentRunText.Statuses)
            _saStatus.Items.Add(new ComboBoxItem { Content = SubAgentRunText.DescribeStatus(status), Tag = status });
        _saStatus.SelectedIndex = 0;
        _saLimit = Field("每页条数", "1–500（与 Core 一致）");
        _saLimit.Text = "20";
        var query = new Button { Content = "查询" };
        query.Click += async (_, _) => await QuerySubAgentRunsAsync(resetPage: true);
        var clear = new Button { Content = "清空筛选" };
        clear.Click += async (_, _) =>
        {
            _saFilter = SubAgentRunFilter.Default;
            ApplySubAgentFilterToForm();
            await QuerySubAgentRunsAsync(resetPage: true);
        };
        _saPrevious = new Button { Content = "上一页" };
        _saPrevious.Click += async (_, _) => await QuerySubAgentRunsAsync(resetPage: false, offset: _saPage.Offset - _saPage.Limit);
        _saNext = new Button { Content = "下一页" };
        _saNext.Click += async (_, _) => await QuerySubAgentRunsAsync(resetPage: false, offset: _saPage.Offset + _saPage.Limit);
        _saPageText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        _saFilterText = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = .8, FontSize = 12 };
        _saList = new StackPanel { Spacing = 2 };
        _saRunPicker = new ComboBox { Header = "查看哪次运行的详情", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_saRunPicker, "选择子代理运行");
        _saRunPicker.SelectionChanged += async (_, _) => await LoadSubAgentDetailAsync();
        _saDetailText = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 12 };
        _saEvents = new StackPanel { Spacing = 2 };
        _saNotice = new InfoBar { IsOpen = false, IsClosable = true };

        SubAgentRunsSettings.Content = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(0, 16, 8, 16),
            Children =
            {
                Card("子代理运行", SubAgentRunText.SubSessionNotice + " " + SubAgentRunText.DegradedNotice,
                    _saParentSession, _saWorkspace, _saAgent, _saStatus, _saLimit, Row(query, clear),
                    _saFilterText, _saPageText, Row(_saPrevious, _saNext), _saList),
                Card("运行详情与事件", SubAgentRunText.PayloadNotice,
                    _saRunPicker, _saDetailText, _saEvents),
                _saNotice
            }
        };
        SetSubAgentRunEnabled(false);
    }

    internal async Task QuerySubAgentRunsAsync(bool resetPage, int offset = 0)
    {
        if (!_saBuilt) return;
        var filter = ReadSubAgentFilter() with { Offset = resetPage ? 0 : Math.Max(0, offset) };
        var errors = SubAgentRunText.Validate(filter);
        if (errors.Count > 0)
        {
            ShowNotice(_saNotice, InfoBarSeverity.Warning, "请先修正筛选条件", string.Join(" ", errors));
            return;
        }
        _saFilter = SubAgentRunText.Normalize(filter);
        SetSubAgentRunEnabled(false);
        try
        {
            _saPage = await _subAgentRuns.ListAsync(_saFilter);
            _saFilterText.Text = "筛选：" + _saFilter.DescribeText;
            _saPageText.Text = _saPage.PageText;
            _saList.Children.Clear();
            if (_saPage.Items.Count == 0)
                _saList.Children.Add(Muted("没有匹配的子代理运行。"));

            var previous = _saRun?.RunId;
            _saRunPicker.Items.Clear();
            foreach (var run in _saPage.Items)
            {
                _saList.Children.Add(Muted(
                    $"{run.IdentityText} · {run.CountsText} · 子会话 {run.SubSessionId} · " +
                    $"父会话 {run.ParentSessionId} · Agent {run.AgentInstanceId}" +
                    (run.HasError ? $" · 错误 {run.ErrorMessage}" : "")));
                _saRunPicker.Items.Add(new ComboBoxItem { Content = run.IdentityText, Tag = run.RunId });
            }
            var index = _saPage.Items.ToList().FindIndex(run => run.RunId == previous);
            _saRunPicker.SelectedIndex = _saPage.Items.Count > 0 ? Math.Max(0, index) : -1;

            SetSubAgentRunEnabled(true);
            if (_saPage.Items.Count > 0) await LoadSubAgentDetailAsync();
            else
            {
                _saRun = null;
                _saDetail = null;
                _saDetailText.Text = "请先选择一次运行。";
                _saEvents.Children.Clear();
            }
            _saNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportSubAgentRunFailure(exception); }
    }

    private async Task LoadSubAgentDetailAsync()
    {
        if (!_saBuilt) return;
        var runId = AgentSelected(_saRunPicker);
        if (string.IsNullOrEmpty(runId))
        {
            _saRun = null;
            _saDetail = null;
            _saDetailText.Text = "请先选择一次运行。";
            _saEvents.Children.Clear();
            return;
        }
        try
        {
            _saRun = _saPage.Items.FirstOrDefault(run => run.RunId == runId);
            _saDetail = await _subAgentRuns.GetAsync(runId);
            _saEvents.Children.Clear();
            if (_saDetail is null)
            {
                _saDetailText.Text = $"Core 没有 {runId} 的归档（可能已被清理）。";
            }
            else
            {
                var detail = _saDetail;
                _saDetailText.Text =
                    $"{detail.Summary.IdentityText} · {detail.Summary.CountsText}\n" +
                    $"时间 {detail.Summary.TimeText}\n" +
                    $"子会话 {detail.Summary.SubSessionId} · 父会话 {detail.Summary.ParentSessionId} · " +
                    $"工作区 {detail.Summary.WorkspaceId} · Agent {detail.Summary.AgentInstanceId}\n" +
                    $"任务 {detail.TaskText}\n" +
                    $"LLM {detail.ProfileText}\n" +
                    $"Trace {detail.TraceText}\n" +
                    $"归档 事件 {detail.EventCount} · 工具 {detail.ToolCallCount}" +
                    (detail.IsDegraded ? $"\n⚠ {detail.DegradedText}" : "") +
                    (detail.Summary.HasError ? $"\n错误 {detail.Summary.ErrorMessage}" : "") +
                    $"\n输出：{detail.OutputText}";

                var events = await _subAgentRuns.ListEventsAsync(runId, 100, 0);
                if (events.Count == 0)
                    _saEvents.Children.Add(Muted("该归档没有事件。"));
                foreach (var item in events)
                    _saEvents.Children.Add(Muted(
                        $"{item.Timestamp} · {item.EventType} · {item.PayloadSize} 字节" +
                        (item.PayloadPreview.Length == 0 ? "" : $"\n{item.PayloadPreview}")));
            }
            _saNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportSubAgentRunFailure(exception); }
    }

    private SubAgentRunFilter ReadSubAgentFilter() => new(
        _saParentSession.Text, _saWorkspace.Text, _saAgent.Text, AgentSelected(_saStatus) ?? "",
        int.TryParse(_saLimit.Text.Trim(), out var limit) ? limit : _saFilter.Limit, _saFilter.Offset);

    private void ApplySubAgentFilterToForm()
    {
        _saParentSession.Text = _saFilter.ParentSessionId;
        _saWorkspace.Text = _saFilter.WorkspaceId;
        _saAgent.Text = _saFilter.AgentInstanceId;
        _saStatus.SelectedIndex = 0;
        _saLimit.Text = _saFilter.Limit.ToString();
    }

    private void ReportSubAgentRunFailure(Exception exception)
    {
        App.WriteDiagnostic(exception);
        var unavailable = exception is SettingsUnavailableException;
        var (severity, title, message) = exception switch
        {
            SettingsUnavailableException reason => (InfoBarSeverity.Informational, "Core 未就绪",
                reason.Message + " 该分类仍可浏览；查询已禁用，未读取任何内容。"),
            ArgumentException argument => (InfoBarSeverity.Warning, "请求被 Core 拒绝", argument.Message),
            InvalidOperationException invalid => (InfoBarSeverity.Warning, "Core 拒绝了该操作", invalid.Message),
            _ => (InfoBarSeverity.Error, "查询失败", "请查看诊断日志后重试。")
        };
        SetSubAgentRunEnabled(!unavailable);
        ShowNotice(_saNotice, severity, title, message);
    }

    private void SetSubAgentRunEnabled(bool enabled)
    {
        foreach (var control in new Control[]
        {
            _saParentSession, _saWorkspace, _saAgent, _saStatus, _saLimit, _saRunPicker
        }) control.IsEnabled = enabled;
        if (!enabled)
        {
            _saPrevious.IsEnabled = false;
            _saNext.IsEnabled = false;
            return;
        }
        _saPrevious.IsEnabled = _saPage.CanGoBack;
        _saNext.IsEnabled = _saPage.CanGoForward;
    }
}
