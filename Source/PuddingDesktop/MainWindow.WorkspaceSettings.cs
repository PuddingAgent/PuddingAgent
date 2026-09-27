using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PuddingDesktop.Foundation;

namespace PuddingDesktop;

/// <summary>
/// DS-05 workspace tab: workspaces (create/edit/delete/freeze) and their members with access levels.
/// The built-in default workspace is shown as non-deletable and Core enforces the same rule.
/// </summary>
public sealed partial class MainWindow
{
    private IReadOnlyList<WorkspaceSummary> _wsWorkspaces = [];
    private IReadOnlyList<WorkspaceMember> _wsMembers = [];
    private WorkspaceSummary? _wsWorkspace;
    private bool _wsBuilt;
    private bool _wsSwitching;

    private ComboBox _wsPicker = null!, _wsTeam = null!, _wsTeamPolicy = null!, _wsCompanyPolicy = null!;
    private TextBox _wsName = null!, _wsDescription = null!, _wsUserProfile = null!;
    private TextBox _wsNewId = null!, _wsNewName = null!, _wsNewDescription = null!;
    private ToggleSwitch _wsEnabled = null!;
    private TextBlock _wsDetail = null!, _wsMembersSummary = null!;
    private ComboBox _wsMemberPicker = null!, _wsUserPicker = null!, _wsAccessLevel = null!;
    private InfoBar _wsNotice = null!;

    private void BuildWorkspacePanel()
    {
        if (_wsBuilt) return;
        _wsBuilt = true;

        _wsPicker = new ComboBox { Header = "工作区", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_wsPicker, "选择工作区");
        _wsPicker.SelectionChanged += async (_, _) => { if (!_wsSwitching) await LoadWorkspaceDetailAsync(); };
        _wsDetail = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };

        _wsName = Field("名称");
        _wsDescription = Field("描述");
        _wsUserProfile = Field("用户档案（JSON）", "留空表示不设置；必须是合法 JSON");
        _wsTeamPolicy = PolicyPicker("团队访问策略");
        _wsCompanyPolicy = PolicyPicker("公司访问策略");
        _wsEnabled = new ToggleSwitch { Header = "启用该工作区", OnContent = "已启用", OffContent = "已停用", IsOn = true };
        var save = new Button { Content = "保存工作区" };
        save.Click += async (_, _) => await SaveWorkspaceAsync();
        var freeze = new Button { Content = "冻结 / 解冻" };
        freeze.Click += async (_, _) => await ToggleWorkspaceFrozenAsync();
        var delete = new Button { Content = "删除工作区" };
        delete.Click += async (_, _) => await DeleteWorkspaceAsync();

        _wsNewId = Field("工作区 ID", "字母、数字、'-' 与 '_'，例如 team-a-space");
        _wsNewName = Field("名称");
        _wsNewDescription = Field("描述");
        _wsTeam = new ComboBox { Header = "所属团队", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_wsTeam, "新建工作区所属团队");
        var create = new Button { Content = "新建工作区" };
        create.Click += async (_, _) => await CreateWorkspaceAsync();

        _wsMembersSummary = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = .8, FontSize = 12 };
        _wsMemberPicker = new ComboBox { Header = "成员", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_wsMemberPicker, "选择成员");
        var remove = new Button { Content = "移除成员" };
        remove.Click += async (_, _) => await RemoveMemberAsync();
        _wsUserPicker = new ComboBox { Header = "添加成员", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_wsUserPicker, "选择要添加的用户");
        _wsAccessLevel = PolicyPicker("成员权限");
        var add = new Button { Content = "添加成员" };
        add.Click += async (_, _) => await AddMemberAsync();
        _wsNotice = new InfoBar { IsOpen = false, IsClosable = true };

        WorkspaceBasicSettings.Content = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(0, 16, 8, 16),
            Children =
            {
                Card("工作区", WorkspaceText.DefaultWorkspaceNotice + " " + WorkspaceText.FrozenNotice,
                    _wsPicker, _wsDetail, _wsName, _wsDescription, _wsUserProfile,
                    _wsTeamPolicy, _wsCompanyPolicy, _wsEnabled, Row(save, freeze, delete)),
                Card("新建工作区", "团队必须已存在；访问策略沿用 Core 的枚举值，界面不提供 Core 之外的取值。",
                    _wsNewId, _wsNewName, _wsNewDescription, _wsTeam, Row(create)),
                Card("成员与权限", "成员权限沿用工作区访问策略枚举；跨工作区移除成员会被 Core 拒绝，界面也不跨工作区操作。",
                    _wsMembersSummary, _wsMemberPicker, Row(remove), _wsUserPicker, _wsAccessLevel, Row(add)),
                _wsNotice
            }
        };
        SetWorkspaceEnabled(false);
    }

    private static ComboBox PolicyPicker(string header)
    {
        var picker = new ComboBox { Header = header, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var level in WorkspaceText.AccessLevels)
            picker.Items.Add(new ComboBoxItem { Content = WorkspaceText.DescribeAccessLevel(level), Tag = level });
        picker.SelectedIndex = 0;
        return picker;
    }

    internal async Task LoadWorkspacesAsync()
    {
        if (!_wsBuilt) return;
        try
        {
            _wsWorkspaces = await _workspaces.ListAsync();
            var teams = await _workspaces.ListTeamsAsync();
            var users = await _workspaces.ListUsersAsync();
            _wsSwitching = true;
            try
            {
                var previous = AgentSelected(_wsPicker);
                _wsPicker.Items.Clear();
                foreach (var workspace in _wsWorkspaces)
                    _wsPicker.Items.Add(new ComboBoxItem
                    {
                        Content = $"{workspace.Name}（{workspace.WorkspaceId}）· {workspace.StateText}",
                        Tag = workspace.WorkspaceId
                    });
                var index = _wsWorkspaces.ToList().FindIndex(workspace => workspace.WorkspaceId == previous);
                _wsPicker.SelectedIndex = _wsWorkspaces.Count > 0 ? Math.Max(0, index) : -1;

                var previousTeam = AgentSelected(_wsTeam);
                _wsTeam.Items.Clear();
                foreach (var team in teams)
                    _wsTeam.Items.Add(new ComboBoxItem { Content = $"{team.Name}（{team.TeamId}）", Tag = team.TeamId });
                var teamIndex = teams.ToList().FindIndex(team => team.TeamId == previousTeam);
                _wsTeam.SelectedIndex = teams.Count > 0 ? Math.Max(0, teamIndex) : -1;

                var previousUser = AgentSelected(_wsUserPicker);
                _wsUserPicker.Items.Clear();
                foreach (var user in users)
                    _wsUserPicker.Items.Add(new ComboBoxItem
                    {
                        Content = $"{user.Username}（{user.UserId}）" + (user.DisplayName.Length == 0 ? "" : $" · {user.DisplayName}"),
                        Tag = user.UserId
                    });
                var userIndex = users.ToList().FindIndex(user => user.UserId == previousUser);
                _wsUserPicker.SelectedIndex = users.Count > 0 ? Math.Max(0, userIndex) : -1;
            }
            finally { _wsSwitching = false; }

            await LoadWorkspaceDetailAsync();
            SetWorkspaceEnabled(true);
            _wsNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportWorkspaceFailure(exception); }
    }

    private async Task LoadWorkspaceDetailAsync()
    {
        _wsWorkspace = AgentSelected(_wsPicker) is { } id
            ? _wsWorkspaces.FirstOrDefault(workspace => workspace.WorkspaceId == id)
            : null;
        var workspace = _wsWorkspace;
        if (workspace is null)
        {
            _wsDetail.Text = _wsWorkspaces.Count == 0 ? "还没有工作区。" : "请选择一个工作区。";
            _wsMembersSummary.Text = "";
            _wsMembers = [];
            FillMemberPicker();
            return;
        }

        _wsName.Text = workspace.Name;
        _wsDescription.Text = workspace.Description;
        _wsUserProfile.Text = workspace.UserProfile;
        _wsEnabled.IsOn = workspace.IsEnabled;
        _wsTeamPolicy.SelectedIndex = Math.Max(0, WorkspaceText.AccessLevels.ToList().IndexOf(workspace.TeamAccessPolicy));
        _wsCompanyPolicy.SelectedIndex = Math.Max(0, WorkspaceText.AccessLevels.ToList().IndexOf(workspace.CompanyAccessPolicy));
        _wsDetail.Text =
            $"{workspace.Name}（{workspace.WorkspaceId}）· {workspace.StateText}\n" +
            $"团队 {workspace.TeamName}（{workspace.TeamId}）· 成员 {workspace.MemberCount} 人\n" +
            $"团队访问策略 {WorkspaceText.DescribeAccessLevel(workspace.TeamAccessPolicy)} · " +
            $"公司访问策略 {WorkspaceText.DescribeAccessLevel(workspace.CompanyAccessPolicy)}\n" +
            $"创建于 {workspace.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}" +
            (workspace.IsBuiltInDefault ? "\n" + WorkspaceText.DefaultWorkspaceNotice : "");

        try
        {
            _wsMembers = await _workspaces.ListMembersAsync(workspace.WorkspaceId);
            FillMemberPicker();
            _wsNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportWorkspaceFailure(exception); }
    }

    private void FillMemberPicker()
    {
        var previous = _wsMemberPicker.SelectedItem is ComboBoxItem { Tag: int id } ? id : -1;
        _wsSwitching = true;
        try
        {
            _wsMemberPicker.Items.Clear();
            foreach (var member in _wsMembers)
                _wsMemberPicker.Items.Add(new ComboBoxItem
                {
                    Content = $"{member.Username}（{member.UserId}）· {member.AccessText}",
                    Tag = member.Id
                });
            var index = _wsMembers.ToList().FindIndex(member => member.Id == previous);
            _wsMemberPicker.SelectedIndex = _wsMembers.Count > 0 ? Math.Max(0, index) : -1;
        }
        finally { _wsSwitching = false; }
        _wsMembersSummary.Text = _wsWorkspace is null
            ? "请先选择工作区。"
            : $"共 {_wsMembers.Count} 名成员。" + (_wsMembers.Count == 0 ? "（没有成员）" : "");
    }

    private async Task CreateWorkspaceAsync()
    {
        var create = new WorkspaceCreateRequest(_wsNewId.Text.Trim(), AgentSelected(_wsTeam) ?? "", _wsNewName.Text.Trim(),
            _wsNewDescription.Text.Trim(), "", AgentSelected(_wsTeamPolicy) ?? "Manage", AgentSelected(_wsCompanyPolicy) ?? "Manage");
        var errors = WorkspaceText.Validate(create);
        if (errors.Count > 0) { ShowNotice(_wsNotice, InfoBarSeverity.Warning, "请先修正表单", string.Join(" ", errors)); return; }
        await RunWorkspaceAsync("工作区已创建", "新工作区默认启用且未冻结；成员需要单独添加。",
            async () =>
            {
                await _workspaces.CreateAsync(create);
                _wsNewId.Text = "";
                _wsNewName.Text = "";
                _wsNewDescription.Text = "";
                _wsWorkspaces = await _workspaces.ListAsync();
                await LoadWorkspacesAsync();
            });
    }

    private async Task SaveWorkspaceAsync()
    {
        if (_wsWorkspace is not { } workspace) { WarnWorkspace("请先选择工作区"); return; }
        var edit = new WorkspaceEdit(workspace.WorkspaceId, _wsName.Text.Trim(), _wsDescription.Text.Trim(),
            _wsUserProfile.Text.Trim(), AgentSelected(_wsTeamPolicy) ?? "Manage", AgentSelected(_wsCompanyPolicy) ?? "Manage",
            _wsEnabled.IsOn);
        var errors = WorkspaceText.Validate(edit);
        if (errors.Count > 0) { ShowNotice(_wsNotice, InfoBarSeverity.Warning, "请先修正表单", string.Join(" ", errors)); return; }
        await RunWorkspaceAsync("工作区已保存", "名称、描述、访问策略与启用状态已更新；团队归属不可在原位更改。",
            async () => { await _workspaces.SaveAsync(edit); await ReloadWorkspacesAsync(workspace.WorkspaceId); });
    }

    private async Task ToggleWorkspaceFrozenAsync()
    {
        if (_wsWorkspace is not { } workspace) { WarnWorkspace("请先选择工作区"); return; }
        var target = !workspace.IsFrozen;
        await RunWorkspaceAsync(target ? "工作区已冻结" : "工作区已解冻", WorkspaceText.FrozenNotice,
            async () => { await _workspaces.SetFrozenAsync(workspace.WorkspaceId, target); await ReloadWorkspacesAsync(workspace.WorkspaceId); });
    }

    private async Task DeleteWorkspaceAsync()
    {
        if (_wsWorkspace is not { } workspace) { WarnWorkspace("请先选择工作区"); return; }
        if (workspace.IsBuiltInDefault)
        {
            ShowNotice(_wsNotice, InfoBarSeverity.Warning, "不能删除内置默认工作空间", WorkspaceText.DefaultWorkspaceNotice);
            return;
        }
        var confirm = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = "删除工作区",
            Content = $"将删除 {workspace.Name}（{workspace.WorkspaceId}）及其成员关系。该工作区下的角色与数据将失去归属。此操作不可撤销。",
            PrimaryButtonText = "删除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        await RunWorkspaceAsync("工作区已删除", "工作区与其成员关系已移除。",
            async () => { await _workspaces.DeleteAsync(workspace.WorkspaceId); await LoadWorkspacesAsync(); });
    }

    private async Task AddMemberAsync()
    {
        if (_wsWorkspace is not { } workspace) { WarnWorkspace("请先选择工作区"); return; }
        var userId = AgentSelected(_wsUserPicker);
        if (string.IsNullOrEmpty(userId)) { WarnWorkspace("请选择要添加的用户"); return; }
        var level = AgentSelected(_wsAccessLevel) ?? "ReadOnly";
        await RunWorkspaceAsync("成员已添加", "该成员只能访问本工作区；跨工作区操作会由 Core 拒绝。",
            async () => { await _workspaces.AddMemberAsync(workspace.WorkspaceId, userId, level); await ReloadMembersAsync(workspace.WorkspaceId); });
    }

    private async Task RemoveMemberAsync()
    {
        if (_wsWorkspace is not { } workspace) { WarnWorkspace("请先选择工作区"); return; }
        if (_wsMemberPicker.SelectedItem is not ComboBoxItem { Tag: int memberId }) { WarnWorkspace("请选择要移除的成员"); return; }
        await RunWorkspaceAsync("成员已移除", "只移除了本工作区的成员关系。",
            async () => { await _workspaces.RemoveMemberAsync(workspace.WorkspaceId, memberId); await ReloadMembersAsync(workspace.WorkspaceId); });
    }

    private async Task ReloadWorkspacesAsync(string workspaceId)
    {
        _wsWorkspaces = await _workspaces.ListAsync();
        _wsSwitching = true;
        try
        {
            _wsPicker.Items.Clear();
            foreach (var workspace in _wsWorkspaces)
                _wsPicker.Items.Add(new ComboBoxItem
                {
                    Content = $"{workspace.Name}（{workspace.WorkspaceId}）· {workspace.StateText}",
                    Tag = workspace.WorkspaceId
                });
            var index = _wsWorkspaces.ToList().FindIndex(workspace => workspace.WorkspaceId == workspaceId);
            _wsPicker.SelectedIndex = _wsWorkspaces.Count > 0 ? Math.Max(0, index) : -1;
        }
        finally { _wsSwitching = false; }
        await LoadWorkspaceDetailAsync();
    }

    private async Task ReloadMembersAsync(string workspaceId)
    {
        _wsMembers = await _workspaces.ListMembersAsync(workspaceId);
        FillMemberPicker();
        await ReloadWorkspacesAsync(workspaceId);
    }

    private void WarnWorkspace(string message) => ShowNotice(_wsNotice, InfoBarSeverity.Warning, "请先选择", message);

    private async Task RunWorkspaceAsync(string title, string message, Func<Task> action)
    {
        SetWorkspaceEnabled(false);
        try
        {
            await action();
            SetWorkspaceEnabled(true);
            ShowNotice(_wsNotice, InfoBarSeverity.Success, title, message);
        }
        catch (Exception exception) { ReportWorkspaceFailure(exception); }
    }

    private void ReportWorkspaceFailure(Exception exception)
    {
        App.WriteDiagnostic(exception);
        var unavailable = exception is SettingsUnavailableException;
        var (severity, title, message) = exception switch
        {
            SettingsUnavailableException reason => (InfoBarSeverity.Informational, "Core 未就绪",
                reason.Message + " 该分类仍可浏览；表单已禁用，未写入任何内容。"),
            ArgumentException argument => (InfoBarSeverity.Warning, "请求被 Core 拒绝", argument.Message),
            InvalidOperationException invalid => (InfoBarSeverity.Warning, "Core 拒绝了该操作", invalid.Message),
            _ => (InfoBarSeverity.Error, "操作失败", "请查看诊断日志后重试。")
        };
        SetWorkspaceEnabled(!unavailable);
        ShowNotice(_wsNotice, severity, title, message);
    }

    private void SetWorkspaceEnabled(bool enabled)
    {
        foreach (var control in new Control[]
        {
            _wsPicker, _wsName, _wsDescription, _wsUserProfile, _wsTeamPolicy, _wsCompanyPolicy, _wsEnabled,
            _wsNewId, _wsNewName, _wsNewDescription, _wsTeam, _wsMemberPicker, _wsUserPicker, _wsAccessLevel
        }) control.IsEnabled = enabled;
    }
}
