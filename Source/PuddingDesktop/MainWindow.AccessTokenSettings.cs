using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PuddingDesktop.Foundation;

namespace PuddingDesktop;

/// <summary>
/// DS-11 access tokens: the External API policy, the token list with filters, creation, rename, and the
/// irreversible revoke. The plaintext token is shown once, at creation, and never again.
/// </summary>
public sealed partial class MainWindow
{
    private ExternalApiStatus? _atStatus;
    private AccessTokenListPage? _atPage;
    private AccessTokenSummary? _atToken;
    private readonly List<string> _atScopes = [];
    private readonly List<string> _atWorkspaces = [];
    private bool _atBuilt;
    private bool _atSwitching;

    private TextBlock _atStatusText = null!, _atListSummary = null!, _atTokenDetail = null!, _atSecret = null!;
    private ComboBox _atStatusFilter = null!, _atPageSize = null!, _atTokenPicker = null!, _atScopePicker = null!, _atWorkspacePicker = null!;
    private TextBox _atOwnerFilter = null!, _atWorkspaceFilter = null!, _atScopeFilter = null!;
    private TextBox _atNewName = null!, _atNewLifetime = null!, _atRenameName = null!, _atRevokeReason = null!;
    private TextBlock _atSelectionSummary = null!;
    private Button _atRevoke = null!, _atRename = null!;
    private InfoBar _atNotice = null!;

    private void BuildAccessTokenPanel()
    {
        if (_atBuilt) return;
        _atBuilt = true;

        _atStatusText = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        var refreshStatus = new Button { Content = "刷新策略" };
        refreshStatus.Click += async (_, _) => await LoadAccessTokenStatusAsync();
        _atNotice = new InfoBar { IsOpen = false, IsClosable = true };

        _atStatusFilter = new ComboBox { Header = "状态筛选", HorizontalAlignment = HorizontalAlignment.Stretch };
        _atStatusFilter.Items.Add(new ComboBoxItem { Content = "全部状态", Tag = "" });
        foreach (var status in AccessTokenText.Statuses)
            _atStatusFilter.Items.Add(new ComboBoxItem { Content = AccessTokenText.DescribeStatus(status), Tag = status });
        _atStatusFilter.SelectedIndex = 0;
        _atOwnerFilter = Field("Owner 筛选");
        _atWorkspaceFilter = Field("工作区筛选");
        _atScopeFilter = Field("Scope 筛选", "例如 tasks.read");
        _atPageSize = new ComboBox { Header = "每页", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var size in AccessTokenText.PageSizes)
            _atPageSize.Items.Add(new ComboBoxItem { Content = size.ToString(), Tag = size });
        _atPageSize.SelectedIndex = 0;
        var refreshList = new Button { Content = "刷新列表" };
        refreshList.Click += async (_, _) => await LoadAccessTokensAsync();
        _atListSummary = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = .8, FontSize = 12 };
        _atTokenPicker = new ComboBox { Header = "令牌", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_atTokenPicker, "选择令牌");
        _atTokenPicker.SelectionChanged += async (_, _) => { if (!_atSwitching) await LoadAccessTokenDetailAsync(); };
        _atTokenDetail = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 12 };

        _atNewName = Field("名称");
        _atNewLifetime = Field("有效期（天）", "留空使用默认值；上限见策略");
        _atScopePicker = new ComboBox { Header = "可添加的 scope", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_atScopePicker, "选择 scope");
        foreach (var scope in AccessTokenText.Scopes)
            _atScopePicker.Items.Add(new ComboBoxItem { Content = AccessTokenText.DescribeScope(scope), Tag = scope });
        _atScopePicker.SelectedIndex = 0;
        var addScope = new Button { Content = "添加 scope" };
        addScope.Click += (_, _) => AddScope();
        var clearScopes = new Button { Content = "清空 scope" };
        clearScopes.Click += (_, _) => { _atScopes.Clear(); FillScopeSummary(); };
        _atWorkspacePicker = new ComboBox { Header = "可添加的工作区", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_atWorkspacePicker, "选择工作区");
        var addWorkspace = new Button { Content = "添加工作区" };
        addWorkspace.Click += (_, _) => AddWorkspace();
        var clearWorkspaces = new Button { Content = "清空工作区" };
        clearWorkspaces.Click += (_, _) => { _atWorkspaces.Clear(); FillScopeSummary(); };
        _atSelectionSummary = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var create = new Button { Content = "创建令牌" };
        create.Click += async (_, _) => await CreateAccessTokenAsync();
        _atSecret = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas")
        };
        var copySecret = new Button { Content = "复制明文令牌" };
        copySecret.Click += (_, _) => CopyAccessTokenSecret();

        _atRenameName = Field("重命名");
        _atRename = new Button { Content = "保存名称" };
        _atRename.Click += async (_, _) => await RenameAccessTokenAsync();
        _atRevokeReason = Field("撤销原因");
        _atRevoke = new Button { Content = "撤销令牌" };
        _atRevoke.Click += async (_, _) => await RevokeAccessTokenAsync();

        AccessTokensSettings.Content = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(0, 16, 8, 16),
            Children =
            {
                Card("External API 策略", "策略由 Core 从配置读取（带短缓存）；页面只显示 Core 当前认可的取值。",
                    _atStatusText, Row(refreshStatus)),
                Card("访问令牌", AccessTokenText.VersionNotice + " " + AccessTokenText.RevokeNotice,
                    _atStatusFilter, _atOwnerFilter, _atWorkspaceFilter, _atScopeFilter, _atPageSize,
                    Row(refreshList), _atListSummary, _atTokenPicker, _atTokenDetail,
                    _atRenameName, Row(_atRename), _atRevokeReason, Row(_atRevoke)),
                Card("创建令牌", AccessTokenText.SecretOnceNotice + " " + AccessTokenText.ScopeNotice,
                    _atNewName, _atNewLifetime, _atScopePicker, Row(addScope, clearScopes),
                    _atWorkspacePicker, Row(addWorkspace, clearWorkspaces), _atSelectionSummary, Row(create),
                    _atSecret, Row(copySecret)),
                _atNotice
            }
        };
        SetAccessTokenEnabled(false);
    }

    internal async Task LoadAccessTokenStatusAsync()
    {
        if (!_atBuilt) return;
        try
        {
            _atStatus = await _accessTokens.ReadStatusAsync();
            var status = _atStatus;
            _atStatusText.Text =
                $"External API：{status.EnabledText} · {status.SchemeText}\n" +
                $"令牌有效期：{status.LifetimeText}\n" +
                $"每个 Owner 的有效令牌上限：{status.MaxActiveTokensPerOwner}\n" +
                $"公开基地址：{(status.PublicBaseUrl.Length == 0 ? "未配置（使用本地地址）" : status.PublicBaseUrl)}";
            SetAccessTokenEnabled(true);
            _atNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportAccessTokenFailure(exception); }
    }

    internal async Task LoadAccessTokensAsync()
    {
        if (!_atBuilt) return;
        try
        {
            if (_atStatus is null) await LoadAccessTokenStatusAsync();
            var size = _atPageSize.SelectedItem is ComboBoxItem { Tag: int value } ? value : 20;
            _atPage = await _accessTokens.ListAsync(new AccessTokenFilter(
                AgentSelected(_atStatusFilter) ?? "", _atOwnerFilter.Text.Trim(), _atWorkspaceFilter.Text.Trim(),
                _atScopeFilter.Text.Trim(), 1, size));
            var page = _atPage;
            _atSwitching = true;
            try
            {
                var previous = AgentSelected(_atTokenPicker);
                _atTokenPicker.Items.Clear();
                foreach (var token in page.Items)
                    _atTokenPicker.Items.Add(new ComboBoxItem
                    {
                        Content = $"{token.Name} · {token.StatusText} · v{token.Version} · {token.DisplayPrefix}",
                        Tag = token.TokenId
                    });
                var index = page.Items.ToList().FindIndex(token => token.TokenId == previous);
                _atTokenPicker.SelectedIndex = page.Items.Count > 0 ? Math.Max(0, index) : -1;
            }
            finally { _atSwitching = false; }
            _atListSummary.Text = $"共 {page.Total} 条 · 第 {page.Page}/{Math.Max(1, page.PageCount)} 页 · 本页 {page.Items.Count} 条";
            await FillWorkspacePickerAsync();
            await LoadAccessTokenDetailAsync();
            SetAccessTokenEnabled(true);
            _atNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportAccessTokenFailure(exception); }
    }

    private async Task FillWorkspacePickerAsync()
    {
        var workspaces = await _agentDirectory.ListWorkspacesAsync();
        _atSwitching = true;
        try
        {
            var previous = AgentSelected(_atWorkspacePicker);
            _atWorkspacePicker.Items.Clear();
            foreach (var workspace in workspaces)
                _atWorkspacePicker.Items.Add(new ComboBoxItem
                {
                    Content = $"{workspace.Name}（{workspace.WorkspaceId}）", Tag = workspace.WorkspaceId
                });
            var index = workspaces.ToList().FindIndex(workspace => workspace.WorkspaceId == previous);
            _atWorkspacePicker.SelectedIndex = workspaces.Count > 0 ? Math.Max(0, index) : -1;
        }
        finally { _atSwitching = false; }
    }

    private void AddScope()
    {
        if (AgentSelected(_atScopePicker) is not { } scope) { WarnAccessToken("请先选择 scope"); return; }
        if (_atScopes.Contains(scope, StringComparer.Ordinal)) { WarnAccessToken($"{scope} 已经添加"); return; }
        _atScopes.Add(scope);
        FillScopeSummary();
        _atNotice.IsOpen = false;
    }

    private void AddWorkspace()
    {
        if (AgentSelected(_atWorkspacePicker) is not { } workspaceId) { WarnAccessToken("请先选择工作区"); return; }
        if (_atWorkspaces.Contains(workspaceId, StringComparer.Ordinal)) { WarnAccessToken($"{workspaceId} 已经添加"); return; }
        _atWorkspaces.Add(workspaceId);
        FillScopeSummary();
        _atNotice.IsOpen = false;
    }

    private void FillScopeSummary()
    {
        _atSelectionSummary.Text =
            (_atScopes.Count == 0 ? "尚未选择 scope" : $"scope {_atScopes.Count} 项：" + string.Join(" · ", _atScopes)) + "\n" +
            (_atWorkspaces.Count == 0 ? "尚未选择工作区" : $"工作区 {_atWorkspaces.Count} 个：" + string.Join("、", _atWorkspaces));
    }

    private async Task CreateAccessTokenAsync()
    {
        if (_atStatus is not { } status) { WarnAccessToken("请先读取 External API 策略"); return; }
        var lifetime = VoiceSettingsText.ParseOptionalInt(_atNewLifetime.Text);
        if (!string.IsNullOrWhiteSpace(_atNewLifetime.Text) && lifetime is null)
        {
            ShowNotice(_atNotice, InfoBarSeverity.Warning, "有效期不是正整数", "请填 1 到上限之间的天数，或留空使用默认值。");
            return;
        }
        var request = new AccessTokenCreateRequest(_atNewName.Text.Trim(), [.. _atWorkspaces], [.. _atScopes], lifetime);
        var errors = AccessTokenText.Validate(request, status);
        if (errors.Count > 0) { ShowNotice(_atNotice, InfoBarSeverity.Warning, "请先修正表单", string.Join(" ", errors)); return; }

        SetAccessTokenEnabled(false);
        try
        {
            var created = await _accessTokens.CreateAsync(request);
            _atSecret.Text = created.TokenText;
            _atNewName.Text = "";
            _atNewLifetime.Text = "";
            _atScopes.Clear();
            _atWorkspaces.Clear();
            FillScopeSummary();
            await LoadAccessTokensAsync();
            SetAccessTokenEnabled(true);
            ShowNotice(_atNotice, InfoBarSeverity.Success, "令牌已创建",
                AccessTokenText.SecretOnceNotice + " 请立刻复制：" + created.Token.TokenId);
        }
        catch (Exception exception) { ReportAccessTokenFailure(exception); }
    }

    private void CopyAccessTokenSecret()
    {
        if (string.IsNullOrWhiteSpace(_atSecret.Text)) { WarnAccessToken("还没有可复制的明文令牌"); return; }
        try
        {
            var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
            data.SetText(_atSecret.Text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
            ShowNotice(_atNotice, InfoBarSeverity.Success, "明文令牌已复制", "关闭页面后无法再取回。");
        }
        catch (Exception exception)
        {
            App.WriteDiagnostic(exception);
            ShowNotice(_atNotice, InfoBarSeverity.Warning, "复制失败", "请手动选中文本框内容复制。");
        }
    }

    private async Task LoadAccessTokenDetailAsync()
    {
        _atToken = AgentSelected(_atTokenPicker) is { } tokenId
            ? _atPage?.Items.FirstOrDefault(token => token.TokenId == tokenId)
            : null;
        var token = _atToken;
        if (token is null)
        {
            _atTokenDetail.Text = _atPage?.Items.Count == 0 ? "没有匹配的令牌。" : "请选择一个令牌。";
            _atRenameName.Text = "";
            _atRevokeReason.Text = "";
            _atRename.IsEnabled = false;
            _atRevoke.IsEnabled = false;
            return;
        }
        _atRenameName.Text = token.Name;
        _atTokenDetail.Text =
            $"{token.Name}（{token.TokenId}）· {token.StatusText} · 版本 v{token.Version}\n" +
            $"前缀 {token.DisplayPrefix} · Owner {token.OwnerUserId}\n" +
            $"scope：{token.ScopesText}\n工作区：{token.WorkspacesText}\n" +
            $"创建 {token.CreatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm} · 过期 {token.ExpiresAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}\n" +
            $"{token.LastUsedText}" +
            (token.RevocationText.Length == 0 ? "" : "\n" + token.RevocationText) + "\n" +
            (token.IsActive ? AccessTokenText.RevokeNotice : "该令牌不是有效状态：不能再撤销，也不能恢复。");
        _atRename.IsEnabled = token.IsActive;
        _atRevoke.IsEnabled = token.IsActive;
    }

    private async Task RenameAccessTokenAsync()
    {
        if (_atToken is not { } token) { WarnAccessToken("请先选择令牌"); return; }
        var name = _atRenameName.Text.Trim();
        if (name.Length == 0) { ShowNotice(_atNotice, InfoBarSeverity.Warning, "名称不能为空", "请填写新的名称。"); return; }
        await RunAccessTokenAsync("名称已保存", $"本次提交使用的版本是 v{token.Version}。",
            async () => { await _accessTokens.RenameAsync(token.TokenId, token.Version, name); await LoadAccessTokensAsync(); });
    }

    private async Task RevokeAccessTokenAsync()
    {
        if (_atToken is not { } token) { WarnAccessToken("请先选择令牌"); return; }
        if (!token.IsActive) { ShowNotice(_atNotice, InfoBarSeverity.Warning, "该令牌已不是有效状态", AccessTokenText.RevokeNotice); return; }
        var reason = _atRevokeReason.Text.Trim();
        // Core allows an omitted reason (it only rejects >500 chars); the page requires one for traceability.
        if (reason.Length == 0) { ShowNotice(_atNotice, InfoBarSeverity.Warning, "请填写撤销原因", AccessTokenText.RevokeReasonNotice); return; }
        var confirm = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = "撤销访问令牌",
            Content = $"将立即撤销 {token.Name}（{token.DisplayPrefix}…）。{AccessTokenText.RevokeNotice}",
            PrimaryButtonText = "撤销", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        await RunAccessTokenAsync("令牌已撤销", AccessTokenText.RevokeNotice,
            async () => { await _accessTokens.RevokeAsync(token.TokenId, token.Version, reason); await LoadAccessTokensAsync(); });
    }

    private void WarnAccessToken(string message) => ShowNotice(_atNotice, InfoBarSeverity.Warning, "请先选择", message);

    private async Task RunAccessTokenAsync(string title, string message, Func<Task> action)
    {
        SetAccessTokenEnabled(false);
        try
        {
            await action();
            SetAccessTokenEnabled(true);
            ShowNotice(_atNotice, InfoBarSeverity.Success, title, message);
        }
        catch (Exception exception) { ReportAccessTokenFailure(exception); }
    }

    private void ReportAccessTokenFailure(Exception exception)
    {
        App.WriteDiagnostic(exception);
        var unavailable = exception is SettingsUnavailableException;
        var (severity, title, message) = exception switch
        {
            SettingsUnavailableException reason => (InfoBarSeverity.Informational, "Core 未就绪",
                reason.Message + " 该分类仍可浏览；表单已禁用，未写入任何内容。"),
            SettingsConflictException conflict => (InfoBarSeverity.Warning, "令牌版本冲突，已阻止覆盖", conflict.Message),
            ArgumentException argument => (InfoBarSeverity.Warning, "请求被 Core 拒绝", argument.Message),
            InvalidOperationException invalid => (InfoBarSeverity.Warning, "Core 拒绝了该操作", invalid.Message),
            _ => (InfoBarSeverity.Error, "操作失败", "请查看诊断日志后重试。")
        };
        SetAccessTokenEnabled(!unavailable);
        ShowNotice(_atNotice, severity, title, message);
    }

    private void SetAccessTokenEnabled(bool enabled)
    {
        foreach (var control in new Control[]
        {
            _atStatusFilter, _atOwnerFilter, _atWorkspaceFilter, _atScopeFilter, _atPageSize, _atTokenPicker,
            _atNewName, _atNewLifetime, _atScopePicker, _atWorkspacePicker, _atRenameName, _atRevokeReason
        }) control.IsEnabled = enabled;
        if (!enabled)
        {
            _atRename.IsEnabled = false;
            _atRevoke.IsEnabled = false;
            return;
        }
        var active = _atToken?.IsActive ?? false;
        _atRename.IsEnabled = active;
        _atRevoke.IsEnabled = active;
    }
}
