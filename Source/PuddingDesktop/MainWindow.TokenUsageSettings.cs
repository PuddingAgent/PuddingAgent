using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PuddingDesktop.Foundation;

namespace PuddingDesktop;

/// <summary>
/// DS-15 usage slice: the token summary (windowed aggregate over both ledgers) and the usage event list.
/// The event list has no time window because Core's event query cannot translate that predicate on SQLite.
/// </summary>
public sealed partial class MainWindow
{
    private TokenUsageSummary _tuSummary = TokenUsageSummary.Empty;
    private TokenUsageLedgerPage _tuEvents = TokenUsageLedgerPage.Empty;
    private TokenUsageEventFilter _tuFilter = TokenUsageEventFilter.Default;
    private TokenUsageWindowKind _tuKind = TokenUsageWindowKind.Last7Days;
    private bool _tuBuilt;

    private ComboBox _tuWindow = null!;
    private TextBlock _tuHeadline = null!, _tuDetail = null!, _tuNoticeText = null!, _tuPageText = null!;
    private StackPanel _tuPerDay = null!, _tuRows = null!;
    private TextBox _tuWorkspace = null!, _tuSession = null!, _tuProvider = null!, _tuModel = null!, _tuPageSize = null!;
    private StackPanel _tuEventList = null!;
    private Button _tuPrevious = null!, _tuNext = null!;
    private InfoBar _tuNotice = null!;

    private void BuildTokenUsagePanel()
    {
        if (_tuBuilt) return;
        _tuBuilt = true;

        _tuWindow = new ComboBox { Header = "时间窗（UTC 日）", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var kind in TokenUsageWindow.Kinds)
            _tuWindow.Items.Add(new ComboBoxItem { Content = TokenUsageText.DescribeWindow(kind), Tag = kind.ToString() });
        _tuWindow.SelectedIndex = 1;
        var load = new Button { Content = "加载统计" };
        load.Click += async (_, _) => await LoadTokenUsageSummaryAsync();
        _tuHeadline = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        _tuDetail = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = .8, FontSize = 12 };
        _tuNoticeText = new TextBlock
        {
            Text = TokenUsageText.LedgerNotice + " " + TokenUsageText.LiveTodayNotice + " " + TokenUsageText.CostNotice,
            TextWrapping = TextWrapping.Wrap, Opacity = .8, FontSize = 12
        };
        _tuPerDay = new StackPanel { Spacing = 2 };
        var details = new Button { Content = "明细展开/收起" };
        details.Click += (_, _) => _tuRows.Visibility = _tuRows.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;
        _tuRows = new StackPanel { Spacing = 2, Visibility = Visibility.Collapsed };

        _tuWorkspace = Field("工作区", "Core 侧条件");
        _tuSession = Field("会话 ID", "Core 侧条件");
        _tuProvider = Field("服务商", "Core 侧条件");
        _tuModel = Field("模型", "Core 侧条件");
        _tuPageSize = Field("每页条数", "1–500");
        _tuPageSize.Text = "50";
        var query = new Button { Content = "查询用量事件" };
        query.Click += async (_, _) => await QueryTokenUsageEventsAsync(resetPage: true);
        var clear = new Button { Content = "清空筛选" };
        clear.Click += async (_, _) =>
        {
            _tuFilter = TokenUsageEventFilter.Default;
            ApplyTokenUsageFilterToForm();
            await QueryTokenUsageEventsAsync(resetPage: true);
        };
        _tuPrevious = new Button { Content = "上一页" };
        _tuPrevious.Click += async (_, _) => await QueryTokenUsageEventsAsync(resetPage: false, page: _tuEvents.Page - 1);
        _tuNext = new Button { Content = "下一页" };
        _tuNext.Click += async (_, _) => await QueryTokenUsageEventsAsync(resetPage: false, page: _tuEvents.Page + 1);
        _tuPageText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        _tuEventList = new StackPanel { Spacing = 2 };
        _tuNotice = new InfoBar { IsOpen = false, IsClosable = true };

        TokenUsageSettings.Content = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(0, 16, 8, 16),
            Children =
            {
                Card("Token 用量汇总", "汇总来自 Core 的按日聚合（闭日走缓存、今日实时）。",
                    _tuWindow, Row(load), _tuHeadline, _tuDetail, _tuNoticeText, _tuPerDay, Row(details), _tuRows),
                Card("用量明细（事件）", TokenUsageText.DateRangeNotice,
                    _tuWorkspace, _tuSession, _tuProvider, _tuModel, _tuPageSize, Row(query, clear),
                    _tuPageText, Row(_tuPrevious, _tuNext), _tuEventList),
                _tuNotice
            }
        };
        SetTokenUsageEnabled(false);
    }

    internal async Task LoadTokenUsageSummaryAsync()
    {
        if (!_tuBuilt) return;
        var kind = Enum.TryParse<TokenUsageWindowKind>(AgentSelected(_tuWindow), out var parsed)
            ? parsed
            : TokenUsageWindowKind.Last7Days;
        SetTokenUsageEnabled(false);
        try
        {
            _tuKind = kind;
            _tuSummary = await _usage.LoadSummaryAsync(TokenUsageWindow.For(kind, DateTime.UtcNow));
            _tuHeadline.Text = _tuSummary.HeadlineText;
            _tuDetail.Text = _tuSummary.DetailText;
            _tuPerDay.Children.Clear();
            if (_tuSummary.PerDay.Count == 0)
                _tuPerDay.Children.Add(Muted("该时间窗内没有用量记录（两个账本都查过了）。"));
            foreach (var day in _tuSummary.PerDay)
                _tuPerDay.Children.Add(Muted(
                    $"{day.Day} · Token {day.Tokens} · 请求 {day.Requests} · 成本 {TokenUsageText.Money(day.Cost)}"));

            _tuRows.Children.Clear();
            foreach (var row in _tuSummary.Rows.OrderByDescending(row => row.DayText, StringComparer.Ordinal))
                _tuRows.Children.Add(Muted(row.LineText));

            SetTokenUsageEnabled(true);
            _tuNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportTokenUsageFailure(exception); }
    }

    internal async Task QueryTokenUsageEventsAsync(bool resetPage, int page = 1)
    {
        if (!_tuBuilt) return;
        var filter = ReadTokenUsageFilter() with { Page = resetPage ? 1 : Math.Max(1, page) };
        var errors = TokenUsageText.Validate(filter);
        if (errors.Count > 0)
        {
            ShowNotice(_tuNotice, InfoBarSeverity.Warning, "请先修正筛选条件", string.Join(" ", errors));
            return;
        }
        _tuFilter = TokenUsageText.Normalize(filter);
        SetTokenUsageEnabled(false);
        try
        {
            _tuEvents = await _usage.ListEventsAsync(_tuFilter);
            _tuPageText.Text = "筛选：" + _tuFilter.DescribeText + "（无时间窗，见卡片说明）\n" + _tuEvents.PageText;
            _tuEventList.Children.Clear();
            if (_tuEvents.Items.Count == 0)
                _tuEventList.Children.Add(Muted("没有匹配的用量事件。"));
            foreach (var item in _tuEvents.Items)
            {
                _tuEventList.Children.Add(Muted(item.LineText));
                _tuEventList.Children.Add(Muted(item.ScopeText));
            }
            SetTokenUsageEnabled(true);
            _tuNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportTokenUsageFailure(exception); }
    }

    private TokenUsageEventFilter ReadTokenUsageFilter() => new(
        _tuWorkspace.Text, _tuSession.Text, _tuProvider.Text, _tuModel.Text,
        _tuFilter.Page, int.TryParse(_tuPageSize.Text.Trim(), out var size) ? size : _tuFilter.PageSize);

    private void ApplyTokenUsageFilterToForm()
    {
        _tuWorkspace.Text = _tuFilter.WorkspaceId;
        _tuSession.Text = _tuFilter.SessionId;
        _tuProvider.Text = _tuFilter.ProviderId;
        _tuModel.Text = _tuFilter.ModelId;
        _tuPageSize.Text = _tuFilter.PageSize.ToString();
    }

    private void ReportTokenUsageFailure(Exception exception)
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
        SetTokenUsageEnabled(!unavailable);
        ShowNotice(_tuNotice, severity, title, message);
    }

    private void SetTokenUsageEnabled(bool enabled)
    {
        foreach (var control in new Control[]
        {
            _tuWindow, _tuWorkspace, _tuSession, _tuProvider, _tuModel, _tuPageSize
        }) control.IsEnabled = enabled;
        if (!enabled)
        {
            _tuPrevious.IsEnabled = false;
            _tuNext.IsEnabled = false;
            return;
        }
        _tuPrevious.IsEnabled = _tuEvents.CanGoBack;
        _tuNext.IsEnabled = _tuEvents.CanGoForward;
    }
}
