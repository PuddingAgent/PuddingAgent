using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PuddingDesktop.Foundation;

namespace PuddingDesktop;

/// <summary>
/// DS-14 session directory: Core's sessions with the page's own filters, paging and detail. The page says
/// which filters Core actually applies and which ones it applies itself.
/// </summary>
public sealed partial class MainWindow
{
    private SessionDirectoryPage _sdPage = SessionDirectoryPage.Empty;
    private SessionFilter _sdFilter = SessionFilter.Default;
    private SessionDirectoryEntry? _sdSession;
    private bool _sdBuilt;

    private TextBox _sdWorkspace = null!, _sdChannel = null!, _sdUser = null!, _sdTemplate = null!;
    private TextBox _sdText = null!, _sdPageSize = null!;
    private ComboBox _sdStatus = null!, _sdRole = null!;
    private TextBlock _sdPageText = null!, _sdFilterText = null!, _sdDetail = null!;
    private StackPanel _sdList = null!;
    private Button _sdPrevious = null!, _sdNext = null!;
    private InfoBar _sdNotice = null!;

    private void BuildSessionDirectoryPanel()
    {
        if (_sdBuilt) return;
        _sdBuilt = true;

        _sdWorkspace = Field("工作区", "Core 侧条件");
        _sdChannel = Field("渠道", "Core 侧条件");
        _sdUser = Field("用户（Owner）", "Core 侧条件");
        _sdTemplate = Field("Agent 模板 ID", "本页筛选");
        _sdText = Field("关键字", "标题 / 会话 ID / Owner 的子串匹配（本页筛选）");
        _sdStatus = new ComboBox { Header = "状态", HorizontalAlignment = HorizontalAlignment.Stretch };
        _sdStatus.Items.Add(new ComboBoxItem { Content = "全部状态", Tag = "" });
        foreach (var status in SessionDirectoryText.Statuses)
            _sdStatus.Items.Add(new ComboBoxItem { Content = SessionDirectoryText.DescribeStatus(status), Tag = status });
        _sdStatus.SelectedIndex = 0;
        _sdRole = new ComboBox { Header = "会话角色", HorizontalAlignment = HorizontalAlignment.Stretch };
        _sdRole.Items.Add(new ComboBoxItem { Content = "全部角色", Tag = "" });
        foreach (var role in SessionDirectoryText.Roles)
            _sdRole.Items.Add(new ComboBoxItem { Content = SessionDirectoryText.DescribeRole(role), Tag = role });
        _sdRole.SelectedIndex = 0;
        _sdPageSize = Field("每页条数", "1–500");
        _sdPageSize.Text = "50";
        var query = new Button { Content = "查询" };
        query.Click += async (_, _) => await QuerySessionsAsync(resetPage: true);
        var clear = new Button { Content = "清空筛选" };
        clear.Click += async (_, _) =>
        {
            _sdFilter = SessionFilter.Default;
            ApplySessionFilterToForm();
            await QuerySessionsAsync(resetPage: true);
        };
        _sdPrevious = new Button { Content = "上一页" };
        _sdPrevious.Click += async (_, _) => await QuerySessionsAsync(resetPage: false, page: _sdPage.Page - 1);
        _sdNext = new Button { Content = "下一页" };
        _sdNext.Click += async (_, _) => await QuerySessionsAsync(resetPage: false, page: _sdPage.Page + 1);
        _sdPageText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        _sdFilterText = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = .8, FontSize = 12 };
        _sdList = new StackPanel { Spacing = 2 };
        _sdDetail = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 12 };
        _sdNotice = new InfoBar { IsOpen = false, IsClosable = true };

        SessionDirectorySettings.Content = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(0, 16, 8, 16),
            Children =
            {
                Card("会话目录", SessionDirectoryText.SourceNotice + " " + SessionDirectoryText.ClientSideNotice +
                    " " + SessionDirectoryText.FrozenNotice,
                    _sdWorkspace, _sdChannel, _sdUser, _sdTemplate, _sdText, _sdStatus, _sdRole, _sdPageSize,
                    Row(query, clear), _sdFilterText, _sdPageText, Row(_sdPrevious, _sdNext),
                    _sdList, _sdDetail, _sdNotice)
            }
        };
        SetSessionDirectoryEnabled(false);
    }

    internal async Task QuerySessionsAsync(bool resetPage, int page = 1)
    {
        if (!_sdBuilt) return;
        var filter = ReadSessionFilter() with { Page = resetPage ? 1 : Math.Max(1, page) };
        var errors = SessionDirectoryText.Validate(filter);
        if (errors.Count > 0)
        {
            ShowNotice(_sdNotice, InfoBarSeverity.Warning, "请先修正筛选条件", string.Join(" ", errors));
            return;
        }
        _sdFilter = SessionDirectoryText.Normalize(filter);
        SetSessionDirectoryEnabled(false);
        try
        {
            _sdPage = await _sessions.ListAsync(_sdFilter);
            FillSessionDirectory();
            SetSessionDirectoryEnabled(true);
            _sdNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportSessionDirectoryFailure(exception); }
    }

    private SessionFilter ReadSessionFilter() => new(
        _sdWorkspace.Text, _sdChannel.Text, _sdUser.Text, _sdTemplate.Text,
        AgentSelected(_sdStatus) ?? "", AgentSelected(_sdRole) ?? "", _sdText.Text,
        _sdFilter.Page, int.TryParse(_sdPageSize.Text.Trim(), out var size) ? size : _sdFilter.PageSize);

    private void ApplySessionFilterToForm()
    {
        _sdWorkspace.Text = _sdFilter.WorkspaceId;
        _sdChannel.Text = _sdFilter.ChannelId;
        _sdUser.Text = _sdFilter.UserId;
        _sdTemplate.Text = _sdFilter.AgentTemplateId;
        _sdText.Text = _sdFilter.Text;
        _sdStatus.SelectedIndex = 0;
        _sdRole.SelectedIndex = 0;
        _sdPageSize.Text = _sdFilter.PageSize.ToString();
    }

    private void FillSessionDirectory()
    {
        _sdFilterText.Text = "筛选：" + _sdFilter.DescribeText +
            "（工作区/渠道/用户由 Core 过滤，其余在本页过滤）";
        _sdPageText.Text = _sdPage.PageText;
        _sdList.Children.Clear();
        if (_sdPage.Items.Count == 0)
        {
            _sdList.Children.Add(Muted("没有匹配的会话。"));
            _sdSession = null;
            _sdDetail.Text = "请调整筛选条件。";
        }
        else
        {
            foreach (var session in _sdPage.Items)
                _sdList.Children.Add(Muted(
                    $"{session.LastActiveAt.ToLocalTime():MM-dd HH:mm} · {session.DisplayTitle} · {session.StatusText} · " +
                    $"{session.RoleText} · 模板 {session.AgentTemplateId} · 渠道 {session.ChannelId} · Owner {session.OwnerUserId}"));
            _sdSession = _sdPage.Items[0];
            FillSessionDetail(_sdSession);
        }
        _sdPrevious.IsEnabled = _sdPage.CanGoBack;
        _sdNext.IsEnabled = _sdPage.CanGoForward;
    }

    private void FillSessionDetail(SessionDirectoryEntry session) => _sdDetail.Text =
        $"会话 {session.SessionId} · {session.DisplayTitle}\n" +
        $"工作区 {session.WorkspaceId} · 渠道 {session.ChannelId} · 模板 {session.AgentTemplateId}\n" +
        $"类型 {session.TypeText} · 角色 {session.RoleText} · 状态 {session.StatusText}\n" +
        $"主体 {session.PrincipalText}\n" +
        $"{session.LineageText}\n" +
        $"创建 {session.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} · 最近活跃 {session.ActiveText}";

    private void ReportSessionDirectoryFailure(Exception exception)
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
        SetSessionDirectoryEnabled(!unavailable);
        ShowNotice(_sdNotice, severity, title, message);
    }

    private void SetSessionDirectoryEnabled(bool enabled)
    {
        foreach (var control in new Control[]
        {
            _sdWorkspace, _sdChannel, _sdUser, _sdTemplate, _sdText, _sdStatus, _sdRole, _sdPageSize
        }) control.IsEnabled = enabled;
        if (!enabled)
        {
            _sdPrevious.IsEnabled = false;
            _sdNext.IsEnabled = false;
            return;
        }
        _sdPrevious.IsEnabled = _sdPage.CanGoBack;
        _sdNext.IsEnabled = _sdPage.CanGoForward;
    }
}
