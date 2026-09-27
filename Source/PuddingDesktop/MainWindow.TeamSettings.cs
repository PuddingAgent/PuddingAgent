using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PuddingDesktop.Foundation;

namespace PuddingDesktop;

/// <summary>
/// DS-12 team slice: teams with their members, the workspaces a team owns, and each workspace's whitelist.
/// Core's guards (workspaces block team deletion, None is not a whitelist level, the default workspace is
/// protected) are surfaced rather than bypassed.
/// </summary>
public sealed partial class MainWindow
{
    private IReadOnlyList<TeamSummary> _tmTeams = [];
    private IReadOnlyList<TeamMember> _tmMembers = [];
    private IReadOnlyList<TeamWorkspace> _tmWorkspaces = [];
    private IReadOnlyList<TeamWhitelistEntry> _tmWhitelist = [];
    private TeamSummary? _tmTeam;
    private TeamWorkspace? _tmWorkspace;
    private bool _tmBuilt;
    private bool _tmSwitching;

    private ComboBox _tmPicker = null!, _tmRole = null!, _tmWorkspacePicker = null!, _tmTeamPolicy = null!, _tmCompanyPolicy = null!;
    private TextBox _tmTeamId = null!, _tmName = null!, _tmDescription = null!;
    private TextBox _tmNewWorkspaceId = null!, _tmNewWorkspaceName = null!, _tmWorkspaceName = null!, _tmWorkspaceDescription = null!;
    private ToggleSwitch _tmEnabled = null!, _tmWorkspaceEnabled = null!;
    private TextBlock _tmDetail = null!, _tmMemberSummary = null!, _tmWorkspaceDetail = null!, _tmWhitelistSummary = null!;
    private StackPanel _tmMemberList = null!, _tmWorkspaceList = null!, _tmWhitelistList = null!;
    private ComboBox _tmUserPicker = null!, _tmAccessLevel = null!, _tmWhitelistUser = null!;
    private Button _tmDelete = null!, _tmDeleteWorkspace = null!;
    private InfoBar _tmNotice = null!;

    private void BuildTeamPanel()
    {
        if (_tmBuilt) return;
        _tmBuilt = true;

        _tmPicker = new ComboBox { Header = "团队", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_tmPicker, "选择团队");
        _tmPicker.SelectionChanged += async (_, _) => { if (!_tmSwitching) await LoadTeamMembersAsync(); };
        var refresh = new Button { Content = "刷新" };
        refresh.Click += async (_, _) => await LoadTeamsAsync();
        var create = new Button { Content = "新建团队" };
        create.Click += (_, _) => StartNewTeam();
        _tmTeamId = Field("TeamId", "字母、数字、'-'、'_'、'.'；保存后不可更改");
        _tmName = Field("团队名称");
        _tmDescription = Field("描述");
        _tmEnabled = new ToggleSwitch { Header = "启用该团队", OnContent = "已启用", OffContent = "已停用", IsOn = true };
        var save = new Button { Content = "保存团队" };
        save.Click += async (_, _) => await SaveTeamAsync();
        _tmDelete = new Button { Content = "删除团队" };
        _tmDelete.Click += async (_, _) => await DeleteTeamAsync();
        _tmDetail = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 12 };

        _tmMemberSummary = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = .8, FontSize = 12 };
        _tmMemberList = new StackPanel { Spacing = 2 };
        _tmUserPicker = new ComboBox { Header = "添加成员（用户）", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_tmUserPicker, "选择用户");
        _tmRole = new ComboBox { Header = "团队角色", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var role in TeamContractsText.MemberRoles)
            _tmRole.Items.Add(new ComboBoxItem { Content = TeamContractsText.DescribeMemberRole(role), Tag = role });
        _tmRole.SelectedIndex = 0;
        var addMember = new Button { Content = "添加成员" };
        addMember.Click += async (_, _) => await AddTeamMemberAsync();
        var removeMember = new Button { Content = "移除成员" };
        removeMember.Click += async (_, _) => await RemoveTeamMemberAsync();

        _tmWorkspaceList = new StackPanel { Spacing = 2 };
        _tmWorkspacePicker = new ComboBox { Header = "团队下的工作区", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_tmWorkspacePicker, "选择工作区");
        _tmWorkspacePicker.SelectionChanged += async (_, _) => { if (!_tmSwitching) await LoadWhitelistAsync(); };
        _tmNewWorkspaceId = Field("新工作区 ID", "留空表示编辑所选工作区");
        _tmNewWorkspaceName = Field("新工作区名称");
        _tmWorkspaceName = Field("名称");
        _tmWorkspaceDescription = Field("描述");
        _tmTeamPolicy = PolicyPicker("团队访问策略");
        _tmCompanyPolicy = PolicyPicker("公司访问策略");
        _tmWorkspaceEnabled = new ToggleSwitch { Header = "启用该工作区", OnContent = "已启用", OffContent = "已停用", IsOn = true };
        var saveWorkspace = new Button { Content = "保存工作区" };
        saveWorkspace.Click += async (_, _) => await SaveTeamWorkspaceAsync();
        var createWorkspace = new Button { Content = "在团队下新建工作区" };
        createWorkspace.Click += async (_, _) => await CreateTeamWorkspaceAsync();
        _tmDeleteWorkspace = new Button { Content = "删除工作区" };
        _tmDeleteWorkspace.Click += async (_, _) => await DeleteTeamWorkspaceAsync();
        _tmWorkspaceDetail = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 12 };

        _tmWhitelistSummary = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = .8, FontSize = 12 };
        _tmWhitelistList = new StackPanel { Spacing = 2 };
        _tmWhitelistUser = new ComboBox { Header = "白名单成员（用户）", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_tmWhitelistUser, "选择白名单用户");
        _tmAccessLevel = PolicyPicker("白名单访问级别");
        _tmAccessLevel.Items.Clear();
        foreach (var level in TeamContractsText.WhitelistLevels)
            _tmAccessLevel.Items.Add(new ComboBoxItem { Content = TeamContractsText.DescribeAccessLevel(level), Tag = level });
        _tmAccessLevel.SelectedIndex = 0;
        var addWhitelist = new Button { Content = "加入白名单" };
        addWhitelist.Click += async (_, _) => await AddWhitelistMemberAsync();
        var removeWhitelist = new Button { Content = "移出白名单" };
        removeWhitelist.Click += async (_, _) => await RemoveWhitelistMemberAsync();
        _tmNotice = new InfoBar { IsOpen = false, IsClosable = true };

        TeamsSettings.Content = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(0, 16, 8, 16),
            Children =
            {
                Card("团队", TeamContractsText.TeamDeleteNotice,
                    _tmPicker, Row(refresh, create), _tmTeamId, _tmName, _tmDescription, _tmEnabled,
                    _tmDetail, Row(save, _tmDelete)),
                Card("团队成员", "团队成员要求用户真实存在；角色属于 Member/Admin，同一用户不可重复加入。",
                    _tmMemberSummary, _tmMemberList, _tmUserPicker, _tmRole, Row(addMember, removeMember)),
                Card("团队下的工作区", TeamContractsText.DefaultWorkspaceNotice,
                    _tmWorkspaceList, _tmWorkspacePicker, _tmWorkspaceDetail,
                    _tmNewWorkspaceId, _tmNewWorkspaceName, Row(createWorkspace),
                    _tmWorkspaceName, _tmWorkspaceDescription, _tmTeamPolicy, _tmCompanyPolicy, _tmWorkspaceEnabled,
                    Row(saveWorkspace, _tmDeleteWorkspace)),
                Card("工作区白名单", TeamContractsText.WhitelistNotice,
                    _tmWhitelistSummary, _tmWhitelistList, _tmWhitelistUser, _tmAccessLevel,
                    Row(addWhitelist, removeWhitelist)),
                _tmNotice
            }
        };
        SetTeamEnabled(false);
    }

    internal async Task LoadTeamsAsync()
    {
        if (!_tmBuilt) return;
        try
        {
            _tmTeams = await _teams.ListAsync();
            var users = await _users.ListAsync();
            _tmSwitching = true;
            try
            {
                var previous = AgentSelected(_tmPicker);
                _tmPicker.Items.Clear();
                foreach (var team in _tmTeams)
                    _tmPicker.Items.Add(new ComboBoxItem
                    {
                        Content = $"{team.Name}（{team.TeamId}）· {team.StateText} · {team.CountsText}",
                        Tag = team.TeamId
                    });
                var index = _tmTeams.ToList().FindIndex(team => team.TeamId == previous);
                _tmPicker.SelectedIndex = _tmTeams.Count > 0 ? Math.Max(0, index) : -1;

                var previousUser = AgentSelected(_tmUserPicker);
                FillUserPicker(_tmUserPicker, users, previousUser);
                var previousWhitelistUser = AgentSelected(_tmWhitelistUser);
                FillUserPicker(_tmWhitelistUser, users, previousWhitelistUser);
            }
            finally { _tmSwitching = false; }
            await LoadTeamMembersAsync();
            SetTeamEnabled(true);
            _tmNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportTeamFailure(exception); }
    }

    private static void FillUserPicker(ComboBox picker, IReadOnlyList<AppUserAccount> users, string? previous)
    {
        picker.Items.Clear();
        foreach (var user in users)
            picker.Items.Add(new ComboBoxItem
            {
                Content = $"{user.Username}（{user.UserId}）· {user.StateText}", Tag = user.UserId
            });
        var index = users.ToList().FindIndex(user => user.UserId == previous);
        picker.SelectedIndex = users.Count > 0 ? Math.Max(0, index) : -1;
    }

    private async Task LoadTeamMembersAsync()
    {
        _tmTeam = AgentSelected(_tmPicker) is { } teamId
            ? _tmTeams.FirstOrDefault(team => team.TeamId == teamId)
            : null;
        var team = _tmTeam;
        _tmTeamId.Text = team?.TeamId ?? "";
        _tmName.Text = team?.Name ?? "";
        _tmDescription.Text = team?.Description ?? "";
        _tmEnabled.IsOn = team?.IsEnabled ?? true;
        _tmDetail.Text = team is null
            ? (_tmTeams.Count == 0 ? "还没有团队。" : "请选择一个团队。")
            : $"{team.Name}（{team.TeamId}）· {team.StateText} · {team.CountsText}\n" +
              (team.Description.Length == 0 ? "（没有描述）" : team.Description) + "\n" +
              $"创建 {team.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}" +
              (team.CanDelete ? "" : "\n" + TeamContractsText.TeamDeleteNotice);
        _tmDelete.IsEnabled = team is not null;

        if (team is null)
        {
            _tmMembers = [];
            _tmWorkspaces = [];
            FillTeamMemberList();
            FillTeamWorkspaceList();
            return;
        }

        try
        {
            _tmMembers = await _teams.ListMembersAsync(team.TeamId);
            FillTeamMemberList();
            _tmWorkspaces = await _teams.ListWorkspacesAsync(team.TeamId);
            FillTeamWorkspaceList();
            _tmNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportTeamFailure(exception); }
    }

    private void FillTeamMemberList()
    {
        _tmMemberList.Children.Clear();
        _tmMemberSummary.Text = _tmTeam is null
            ? "请先选择团队。"
            : $"成员 {_tmMembers.Count} 人" + (_tmMembers.Count == 0 ? "（还没有成员）" : "");
        foreach (var member in _tmMembers)
            _tmMemberList.Children.Add(Muted(
                $"{member.Username}（{member.UserId}）· {member.RoleText}" +
                (member.DisplayName.Length == 0 ? "" : $" · {member.DisplayName}")));
    }

    private void FillTeamWorkspaceList()
    {
        _tmSwitching = true;
        try
        {
            var previous = _tmWorkspace?.WorkspaceId;
            _tmWorkspacePicker.Items.Clear();
            foreach (var workspace in _tmWorkspaces)
                _tmWorkspacePicker.Items.Add(new ComboBoxItem
                {
                    Content = $"{workspace.Name}（{workspace.WorkspaceId}）· {workspace.StateText}", Tag = workspace.WorkspaceId
                });
            var index = _tmWorkspaces.ToList().FindIndex(workspace => workspace.WorkspaceId == previous);
            _tmWorkspacePicker.SelectedIndex = _tmWorkspaces.Count > 0 ? Math.Max(0, index) : -1;
        }
        finally { _tmSwitching = false; }
        ApplyTeamWorkspaceSelection();
    }

    private void ApplyTeamWorkspaceSelection()
    {
        _tmWorkspace = AgentSelected(_tmWorkspacePicker) is { } workspaceId
            ? _tmWorkspaces.FirstOrDefault(workspace => workspace.WorkspaceId == workspaceId)
            : null;
        var workspace = _tmWorkspace;
        _tmWorkspaceName.Text = workspace?.Name ?? "";
        _tmWorkspaceDescription.Text = workspace?.Description ?? "";
        _tmWorkspaceEnabled.IsOn = workspace?.IsEnabled ?? true;
        _tmTeamPolicy.SelectedIndex = Math.Max(0, TeamContractsText.AccessLevels.ToList()
            .FindIndex(level => string.Equals(level, workspace?.TeamAccessPolicy, StringComparison.OrdinalIgnoreCase)));
        _tmCompanyPolicy.SelectedIndex = Math.Max(0, TeamContractsText.AccessLevels.ToList()
            .FindIndex(level => string.Equals(level, workspace?.CompanyAccessPolicy, StringComparison.OrdinalIgnoreCase)));
        _tmWorkspaceDetail.Text = workspace is null
            ? (_tmWorkspaces.Count == 0 ? "该团队还没有工作区。" : "请选择一个工作区。")
            : $"{workspace.Name}（{workspace.WorkspaceId}）· {workspace.StateText} · 成员 {workspace.MemberCount}\n" +
              $"{workspace.PolicyText}\n" +
              (workspace.IsBuiltInDefault ? TeamContractsText.DefaultWorkspaceNotice : "");
        _tmDeleteWorkspace.IsEnabled = workspace is not null && !workspace.IsBuiltInDefault;
    }

    private async Task LoadWhitelistAsync()
    {
        if (_tmWorkspace is not { } workspace)
        {
            _tmWhitelist = [];
            FillWhitelistList();
            return;
        }
        try
        {
            _tmWhitelist = await _teams.ListWorkspaceMembersAsync(workspace.WorkspaceId);
            FillWhitelistList();
            _tmNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportTeamFailure(exception); }
    }

    private void FillWhitelistList()
    {
        _tmWhitelistList.Children.Clear();
        _tmWhitelistSummary.Text = _tmWorkspace is null
            ? "请先选择一个工作区。"
            : $"白名单 {_tmWhitelist.Count} 人" + (_tmWhitelist.Count == 0 ? "（为空表示只按工作区策略放行）" : "");
        foreach (var entry in _tmWhitelist)
            _tmWhitelistList.Children.Add(Muted(
                $"#{entry.Id} · {entry.Username}（{entry.UserId}）· {entry.AccessText}"));
    }

    private void StartNewTeam()
    {
        _tmSwitching = true;
        try { _tmPicker.SelectedIndex = -1; }
        finally { _tmSwitching = false; }
        _tmTeam = null;
        _ = LoadTeamMembersAsync();
        ShowNotice(_tmNotice, InfoBarSeverity.Informational, "新建团队", "填写 TeamId、名称与描述后保存。");
    }

    private async Task SaveTeamAsync()
    {
        if (_tmTeam is null)
        {
            var create = new TeamEdit(_tmTeamId.Text.Trim(), _tmName.Text.Trim(), _tmDescription.Text.Trim(), _tmEnabled.IsOn);
            var errors = TeamContractsText.Validate(create, isCreate: true);
            if (errors.Count > 0) { ShowNotice(_tmNotice, InfoBarSeverity.Warning, "请先修正表单", string.Join(" ", errors)); return; }
            await RunTeamAsync("团队已创建", "新团队可以接着添加成员与工作区。",
                async () => { await _teams.CreateAsync(create); await LoadTeamsAsync(); });
            return;
        }

        var edit = new TeamEdit(_tmTeam.TeamId, _tmName.Text.Trim(), _tmDescription.Text.Trim(), _tmEnabled.IsOn);
        var editErrors = TeamContractsText.Validate(edit, isCreate: false);
        if (editErrors.Count > 0) { ShowNotice(_tmNotice, InfoBarSeverity.Warning, "请先修正表单", string.Join(" ", editErrors)); return; }
        await RunTeamAsync("团队已保存", "TeamId 不可更改。",
            async () => { await _teams.UpdateAsync(edit); await LoadTeamsAsync(); });
    }

    private async Task DeleteTeamAsync()
    {
        if (_tmTeam is not { } team) { WarnTeam("请先选择团队"); return; }
        if (!team.CanDelete)
        {
            ShowNotice(_tmNotice, InfoBarSeverity.Warning, "该团队下还有工作区", TeamContractsText.TeamDeleteNotice);
            return;
        }
        var confirm = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = "删除团队",
            Content = $"将删除 {team.Name}（{team.TeamId}）及其成员关系。此操作不可撤销。",
            PrimaryButtonText = "删除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        await RunTeamAsync("团队已删除", "团队与其成员关系已移除。",
            async () => { await _teams.DeleteAsync(team.TeamId); await LoadTeamsAsync(); });
    }

    private async Task AddTeamMemberAsync()
    {
        if (_tmTeam is not { } team) { WarnTeam("请先选择团队"); return; }
        var add = new TeamMemberAdd(team.TeamId, AgentSelected(_tmUserPicker) ?? "", AgentSelected(_tmRole) ?? "Member");
        var errors = TeamContractsText.Validate(add);
        if (errors.Count > 0) { ShowNotice(_tmNotice, InfoBarSeverity.Warning, "请先修正表单", string.Join(" ", errors)); return; }
        await RunTeamAsync("成员已添加", "同一用户不能重复加入同一团队。",
            async () => { await _teams.AddMemberAsync(add); await LoadTeamMembersAsync(); });
    }

    private async Task RemoveTeamMemberAsync()
    {
        if (_tmTeam is not { } team) { WarnTeam("请先选择团队"); }
        var userId = AgentSelected(_tmUserPicker);
        if (_tmTeam is null || string.IsNullOrEmpty(userId)) { WarnTeam("请在成员选择里指定要移除的用户"); return; }
        await RunTeamAsync("成员已移除", "只移除了该团队下的成员关系。",
            async () => { await _teams.RemoveMemberAsync(_tmTeam.TeamId, userId); await LoadTeamMembersAsync(); });
    }

    private async Task CreateTeamWorkspaceAsync()
    {
        if (_tmTeam is not { } team) { WarnTeam("请先选择团队"); return; }
        var create = new TeamWorkspaceEdit(team.TeamId, _tmNewWorkspaceId.Text.Trim(), _tmNewWorkspaceName.Text.Trim(),
            "", "", AgentSelected(_tmTeamPolicy) ?? "Manage", AgentSelected(_tmCompanyPolicy) ?? "Manage", true);
        var errors = TeamContractsText.Validate(create, isCreate: true);
        if (errors.Count > 0) { ShowNotice(_tmNotice, InfoBarSeverity.Warning, "请先修正表单", string.Join(" ", errors)); return; }
        await RunTeamAsync("工作区已创建", "工作区归属该团队；访问策略沿用 Core 的枚举取值。",
            async () =>
            {
                await _teams.CreateWorkspaceAsync(create);
                _tmNewWorkspaceId.Text = "";
                _tmNewWorkspaceName.Text = "";
                await LoadTeamMembersAsync();
            });
    }

    private async Task SaveTeamWorkspaceAsync()
    {
        if (_tmTeam is not { } team || _tmWorkspace is not { } workspace) { WarnTeam("请先选择工作区"); return; }
        var edit = new TeamWorkspaceEdit(team.TeamId, workspace.WorkspaceId, _tmWorkspaceName.Text.Trim(),
            _tmWorkspaceDescription.Text.Trim(), workspace.UserProfile,
            AgentSelected(_tmTeamPolicy) ?? "Manage", AgentSelected(_tmCompanyPolicy) ?? "Manage", _tmWorkspaceEnabled.IsOn);
        var errors = TeamContractsText.Validate(edit, isCreate: false);
        if (errors.Count > 0) { ShowNotice(_tmNotice, InfoBarSeverity.Warning, "请先修正表单", string.Join(" ", errors)); return; }
        await RunTeamAsync("工作区已保存", "名称、描述、访问策略与启用状态已更新。",
            async () => { await _teams.UpdateWorkspaceAsync(edit); await LoadTeamMembersAsync(); });
    }

    private async Task DeleteTeamWorkspaceAsync()
    {
        if (_tmWorkspace is not { } workspace) { WarnTeam("请先选择工作区"); return; }
        if (workspace.IsBuiltInDefault)
        {
            ShowNotice(_tmNotice, InfoBarSeverity.Warning, "不能删除内置默认工作空间", TeamContractsText.DefaultWorkspaceNotice);
            return;
        }
        var confirm = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = "删除工作区",
            Content = $"将删除 {workspace.Name}（{workspace.WorkspaceId}）及其白名单。此操作不可撤销。",
            PrimaryButtonText = "删除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        await RunTeamAsync("工作区已删除", "工作区与其白名单已移除。",
            async () => { await _teams.DeleteWorkspaceAsync(workspace.WorkspaceId); await LoadTeamMembersAsync(); });
    }

    private async Task AddWhitelistMemberAsync()
    {
        if (_tmWorkspace is not { } workspace) { WarnTeam("请先选择工作区"); return; }
        var add = new TeamWorkspaceMemberAdd(workspace.WorkspaceId, AgentSelected(_tmWhitelistUser) ?? "",
            AgentSelected(_tmAccessLevel) ?? "ReadOnly");
        var errors = TeamContractsText.Validate(add);
        if (errors.Count > 0) { ShowNotice(_tmNotice, InfoBarSeverity.Warning, "请先修正表单", string.Join(" ", errors)); return; }
        await RunTeamAsync("已加入白名单", TeamContractsText.WhitelistNotice,
            async () => { await _teams.AddWorkspaceMemberAsync(add); await LoadWhitelistAsync(); });
    }

    private async Task RemoveWhitelistMemberAsync()
    {
        if (_tmWorkspace is not { } workspace) { WarnTeam("请先选择工作区"); return; }
        var entry = _tmWhitelist.FirstOrDefault();
        if (entry is null) { WarnTeam("该工作区白名单为空"); return; }
        await RunTeamAsync("已移出白名单", "只移除了该工作区白名单中的这条记录。",
            async () => { await _teams.RemoveWorkspaceMemberAsync(workspace.WorkspaceId, entry.Id); await LoadWhitelistAsync(); });
    }

    private void WarnTeam(string message) => ShowNotice(_tmNotice, InfoBarSeverity.Warning, "请先选择", message);

    private async Task RunTeamAsync(string title, string message, Func<Task> action)
    {
        SetTeamEnabled(false);
        try
        {
            await action();
            SetTeamEnabled(true);
            ShowNotice(_tmNotice, InfoBarSeverity.Success, title, message);
        }
        catch (Exception exception) { ReportTeamFailure(exception); }
    }

    private void ReportTeamFailure(Exception exception)
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
        SetTeamEnabled(!unavailable);
        ShowNotice(_tmNotice, severity, title, message);
    }

    private void SetTeamEnabled(bool enabled)
    {
        foreach (var control in new Control[]
        {
            _tmPicker, _tmTeamId, _tmName, _tmDescription, _tmEnabled, _tmUserPicker, _tmRole,
            _tmWorkspacePicker, _tmNewWorkspaceId, _tmNewWorkspaceName, _tmWorkspaceName, _tmWorkspaceDescription,
            _tmTeamPolicy, _tmCompanyPolicy, _tmWorkspaceEnabled, _tmWhitelistUser, _tmAccessLevel
        }) control.IsEnabled = enabled;
        _tmDelete.IsEnabled = enabled && _tmTeam is not null;
        _tmDeleteWorkspace.IsEnabled = enabled && _tmWorkspace is not null && !_tmWorkspace.IsBuiltInDefault;
    }
}
