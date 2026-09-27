using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PuddingDesktop.Foundation;

namespace PuddingDesktop;

/// <summary>
/// DS-14 diagnostics slice: the runtime timeline with filters and paging, and the overview card built from
/// Core's component health counts plus the most recent failed events. Text is redacted by Core, and the page
/// states what that redaction does and does not cover.
/// </summary>
public sealed partial class MainWindow
{
    private RuntimeTimelinePage _tlPage = RuntimeTimelinePage.Empty;
    private DiagnosticsOverview _dgOverview = DiagnosticsOverview.Empty;
    private RuntimeTimelineFilter _tlFilter = RuntimeTimelineFilter.Default;
    private bool _tlBuilt, _dgBuilt;

    private TextBox _tlSession = null!, _tlRun = null!, _tlTrace = null!, _tlAgent = null!;
    private TextBox _tlComponent = null!, _tlPageSize = null!;
    private ComboBox _tlStatus = null!, _tlSort = null!, _tlDisplayMode = null!;
    private TextBlock _tlPageText = null!, _tlFilterText = null!;
    private StackPanel _tlList = null!;
    private Button _tlPrevious = null!, _tlNext = null!;
    private InfoBar _tlNotice = null!;

    private TextBlock _dgHeadline = null!, _dgExtra = null!;
    private Button _dgRefresh = null!;
    private StackPanel _dgComponents = null!, _dgFailures = null!;
    private InfoBar _dgNotice = null!;

    private void BuildTimelinePanel()
    {
        if (_tlBuilt) return;
        _tlBuilt = true;

        _tlSession = Field("会话 ID", "按 SessionId 精确筛选");
        _tlRun = Field("Run ID", "子代理运行或其他 Run");
        _tlTrace = Field("Trace ID");
        _tlAgent = Field("Agent 实例 ID");
        _tlComponent = Field("组件", "例如 session_state / subagent");
        _tlStatus = new ComboBox { Header = "状态", HorizontalAlignment = HorizontalAlignment.Stretch };
        _tlStatus.Items.Add(new ComboBoxItem { Content = "全部状态", Tag = "" });
        foreach (var status in DiagnosticsText.Statuses)
            _tlStatus.Items.Add(new ComboBoxItem { Content = DiagnosticsText.DescribeStatus(status), Tag = status });
        _tlStatus.SelectedIndex = 0;
        _tlSort = new ComboBox { Header = "排序", HorizontalAlignment = HorizontalAlignment.Stretch };
        _tlSort.Items.Add(new ComboBoxItem { Content = "时间倒序（最新在前）", Tag = "desc" });
        _tlSort.Items.Add(new ComboBoxItem { Content = "时间正序", Tag = "asc" });
        _tlSort.SelectedIndex = 0;
        _tlDisplayMode = new ComboBox { Header = "展示模式", HorizontalAlignment = HorizontalAlignment.Stretch };
        _tlDisplayMode.Items.Add(new ComboBoxItem { Content = "raw（逐条事件）", Tag = "raw" });
        _tlDisplayMode.Items.Add(new ComboBoxItem { Content = "user（按用户视角归并）", Tag = "user" });
        _tlDisplayMode.SelectedIndex = 0;
        _tlPageSize = Field("每页条数", "1–500（与 Core 一致）");
        _tlPageSize.Text = "100";
        var query = new Button { Content = "查询" };
        query.Click += async (_, _) => await QueryTimelineAsync(resetPage: true);
        var clear = new Button { Content = "清空筛选" };
        clear.Click += async (_, _) =>
        {
            _tlFilter = RuntimeTimelineFilter.Default;
            ApplyTimelineFilterToForm();
            await QueryTimelineAsync(resetPage: true);
        };
        _tlPrevious = new Button { Content = "上一页" };
        _tlPrevious.Click += async (_, _) => await QueryTimelineAsync(resetPage: false, page: _tlPage.Page - 1);
        _tlNext = new Button { Content = "下一页" };
        _tlNext.Click += async (_, _) => await QueryTimelineAsync(resetPage: false, page: _tlPage.Page + 1);
        _tlPageText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        _tlFilterText = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = .8, FontSize = 12 };
        _tlList = new StackPanel { Spacing = 6 };
        _tlNotice = new InfoBar { IsOpen = false, IsClosable = true };

        RuntimeTimelineSettings.Content = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(0, 16, 8, 16),
            Children =
            {
                Card("运行时间线", DiagnosticsText.RedactionNotice + " " + DiagnosticsText.FreeTextGapNotice,
                    _tlSession, _tlRun, _tlTrace, _tlAgent, _tlComponent, _tlStatus, _tlSort, _tlDisplayMode,
                    _tlPageSize, Row(query, clear), _tlFilterText, _tlPageText, Row(_tlPrevious, _tlNext),
                    _tlList, _tlNotice)
            }
        };
        SetTimelineEnabled(false);
    }

    private void BuildDiagnosticsOverviewPanel()
    {
        if (_dgBuilt) return;
        _dgBuilt = true;

        _dgRefresh = new Button { Content = "刷新概览" };
        _dgRefresh.Click += async (_, _) => await LoadDiagnosticsOverviewAsync();
        _dgHeadline = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        _dgExtra = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = .8, FontSize = 12 };
        _dgComponents = new StackPanel { Spacing = 2 };
        _dgFailures = new StackPanel { Spacing = 6 };
        _dgNotice = new InfoBar { IsOpen = false, IsClosable = true };

        DiagnosticsOverviewSettings.Content = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(0, 16, 8, 16),
            Children =
            {
                Card("运行诊断概览", DiagnosticsText.HealthNotice, Row(_dgRefresh), _dgHeadline, _dgExtra, _dgComponents),
                Card("近期失败事件", DiagnosticsText.FreeTextGapNotice, _dgFailures),
                _dgNotice
            }
        };
        SetDiagnosticsOverviewEnabled(false);
    }

    internal async Task QueryTimelineAsync(bool resetPage, int page = 1)
    {
        if (!_tlBuilt) return;
        var filter = ReadTimelineFilter() with { Page = resetPage ? 1 : Math.Max(1, page) };
        var errors = DiagnosticsText.Validate(filter);
        if (errors.Count > 0)
        {
            ShowNotice(_tlNotice, InfoBarSeverity.Warning, "请先修正筛选条件", string.Join(" ", errors));
            return;
        }
        _tlFilter = DiagnosticsText.Normalize(filter);
        SetTimelineEnabled(false);
        try
        {
            _tlPage = await _diagnostics.QueryTimelineAsync(_tlFilter);
            FillTimeline();
            SetTimelineEnabled(true);
            _tlNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportTimelineFailure(exception); }
    }

    private RuntimeTimelineFilter ReadTimelineFilter() => new(
        _tlSession.Text, _tlRun.Text, _tlTrace.Text, _tlAgent.Text, _tlComponent.Text,
        AgentSelected(_tlStatus) ?? "", _tlFilter.Page,
        int.TryParse(_tlPageSize.Text.Trim(), out var size) ? size : _tlFilter.PageSize,
        AgentSelected(_tlSort) ?? "desc", AgentSelected(_tlDisplayMode) ?? "raw");

    private void ApplyTimelineFilterToForm()
    {
        _tlSession.Text = _tlFilter.SessionId;
        _tlRun.Text = _tlFilter.RunId;
        _tlTrace.Text = _tlFilter.TraceId;
        _tlAgent.Text = _tlFilter.AgentInstanceId;
        _tlComponent.Text = _tlFilter.Component;
        _tlStatus.SelectedIndex = Math.Max(0, (_tlFilter.Status.Length == 0 ? -1 : DiagnosticsText.Statuses.ToList()
            .FindIndex(status => string.Equals(status, _tlFilter.Status, StringComparison.OrdinalIgnoreCase))) + 1);
        _tlSort.SelectedIndex = Math.Max(0, DiagnosticsText.SortOrders.ToList()
            .FindIndex(order => string.Equals(order, _tlFilter.SortOrder, StringComparison.OrdinalIgnoreCase)));
        _tlDisplayMode.SelectedIndex = Math.Max(0, DiagnosticsText.DisplayModes.ToList()
            .FindIndex(mode => string.Equals(mode, _tlFilter.DisplayMode, StringComparison.OrdinalIgnoreCase)));
        _tlPageSize.Text = _tlFilter.PageSize.ToString();
    }

    private void FillTimeline()
    {
        _tlFilterText.Text = "筛选：" + _tlFilter.DescribeText +
            $" · 排序 {(_tlFilter.SortOrder == "asc" ? "时间正序" : "时间倒序")} · 模式 {_tlFilter.DisplayMode}";
        _tlPageText.Text = _tlPage.PageText;
        _tlList.Children.Clear();
        if (_tlPage.Items.Count == 0)
            _tlList.Children.Add(Muted(DiagnosticsText.EmptyTimelineNotice));
        foreach (var entry in _tlPage.Items)
            _tlList.Children.Add(TimelineEntryBlock(entry));
        _tlPrevious.IsEnabled = _tlPage.CanGoBack;
        _tlNext.IsEnabled = _tlPage.CanGoForward;
    }

    private UIElement TimelineEntryBlock(RuntimeTimelineEntry entry)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock
        {
            Text = $"{entry.StartedAtUtc.ToLocalTime():MM-dd HH:mm:ss.fff} · {entry.Kind} · {entry.IdentityText}",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(Muted(entry.CorrelationText));
        if (entry.Summary.Length > 0) panel.Children.Add(Muted("摘要 " + entry.Summary));
        // 失败条目的错误文本按原样显示（Core 已截断/脱敏；自由文本里的密钥形态串不会被清洗）。
        if (entry.HasError) panel.Children.Add(Muted("错误 " + entry.Error));
        if (entry.Metadata.Count > 0)
            panel.Children.Add(Muted("元数据 " + string.Join(" · ",
                entry.Metadata.Select(pair => $"{pair.Key}={pair.Value}"))));
        return panel;
    }

    internal async Task LoadDiagnosticsOverviewAsync()
    {
        if (!_dgBuilt) return;
        SetDiagnosticsOverviewEnabled(false);
        try
        {
            _dgOverview = await _diagnostics.LoadOverviewAsync();
            _dgHeadline.Text = _dgOverview.HeadlineText;
            _dgExtra.Text = _dgOverview.ExtraText;
            _dgComponents.Children.Clear();
            if (_dgOverview.Components.Count == 0)
                _dgComponents.Children.Add(Muted("Core 还没有统计到任何组件（时间线里没有事件时是正常的）。"));
            foreach (var component in _dgOverview.Components)
                _dgComponents.Children.Add(Muted(
                    $"{component.Component} · {component.StatusText} · {component.CountsText} · {component.LastSeenText}" +
                    (component.HasFailures ? " · 该组件有失败" : "")));

            _dgFailures.Children.Clear();
            if (_dgOverview.RecentFailures.Items.Count == 0)
                _dgFailures.Children.Add(Muted("最近没有失败事件。"));
            foreach (var entry in _dgOverview.RecentFailures.Items)
                _dgFailures.Children.Add(TimelineEntryBlock(entry));

            SetDiagnosticsOverviewEnabled(true);
            _dgNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportDiagnosticsOverviewFailure(exception); }
    }

    private void ReportTimelineFailure(Exception exception)
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
        SetTimelineEnabled(!unavailable);
        ShowNotice(_tlNotice, severity, title, message);
    }

    private void ReportDiagnosticsOverviewFailure(Exception exception)
    {
        App.WriteDiagnostic(exception);
        var unavailable = exception is SettingsUnavailableException;
        ShowNotice(_dgNotice,
            unavailable ? InfoBarSeverity.Informational : InfoBarSeverity.Error,
            unavailable ? "Core 未就绪" : "查询失败",
            unavailable ? "该分类仍可浏览；查询已禁用，未读取任何内容。" : "请查看诊断日志后重试。");
        SetDiagnosticsOverviewEnabled(!unavailable);
    }

    private void SetTimelineEnabled(bool enabled)
    {
        foreach (var control in new Control[]
        {
            _tlSession, _tlRun, _tlTrace, _tlAgent, _tlComponent, _tlStatus, _tlSort, _tlDisplayMode, _tlPageSize
        }) control.IsEnabled = enabled;
        if (!enabled)
        {
            _tlPrevious.IsEnabled = false;
            _tlNext.IsEnabled = false;
            return;
        }
        _tlPrevious.IsEnabled = _tlPage.CanGoBack;
        _tlNext.IsEnabled = _tlPage.CanGoForward;
    }

    private void SetDiagnosticsOverviewEnabled(bool enabled)
    {
        // 概览只有一个刷新按钮，其余是只读文本。
        _dgRefresh.IsEnabled = enabled;
    }
}
