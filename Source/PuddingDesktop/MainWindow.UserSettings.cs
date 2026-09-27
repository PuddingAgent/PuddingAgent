using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PuddingDesktop.Foundation;

namespace PuddingDesktop;

/// <summary>
/// DS-12 user slice: accounts (create, edit, enable/disable, password, roles, delete). Passwords are
/// write-only here, and Core's "keep at least one Admin" rule is surfaced rather than worked around.
/// </summary>
public sealed partial class MainWindow
{
    private IReadOnlyList<AppUserAccount> _usUsers = [];
    private IReadOnlyList<PermissionRole> _usRoles = [];
    private AppUserAccount? _usUser;
    private readonly List<string> _usRoleIds = [];
    private bool _usBuilt;
    private bool _usSwitching;

    private ComboBox _usPicker = null!, _usType = null!, _usRolePicker = null!;
    private TextBox _usUserId = null!, _usUsername = null!, _usEmail = null!, _usDisplayName = null!;
    private ToggleSwitch _usEnabled = null!;
    private PasswordBox _usNewPassword = null!, _usConfirmPassword = null!, _usChangePassword = null!, _usChangeConfirm = null!;
    private TextBlock _usDetail = null!, _usRoleSummary = null!;
    private StackPanel _usRoleList = null!;
    private Button _usSave = null!, _usDelete = null!, _usAssignRoles = null!;
    private InfoBar _usNotice = null!;

    private void BuildUserPanel()
    {
        if (_usBuilt) return;
        _usBuilt = true;

        _usPicker = new ComboBox { Header = "用户", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_usPicker, "选择用户");
        _usPicker.SelectionChanged += (_, _) => { if (!_usSwitching) ApplyUserSelection(); };
        var refresh = new Button { Content = "刷新" };
        refresh.Click += async (_, _) => await LoadUsersAsync();
        var create = new Button { Content = "新建用户" };
        create.Click += (_, _) => StartNewUser();
        _usUserId = Field("UserId", "字母、数字、'-'、'_'、'.'；保存后不可更改");
        _usUsername = Field("用户名");
        _usEmail = Field("邮箱", "必须唯一");
        _usDisplayName = Field("显示名");
        _usType = new ComboBox { Header = "用户类型", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var type in UserContractsText.UserTypes)
            _usType.Items.Add(new ComboBoxItem { Content = UserContractsText.DescribeUserType(type), Tag = type });
        _usType.SelectedIndex = 1;
        _usEnabled = new ToggleSwitch { Header = "启用该账号", OnContent = "已启用", OffContent = "已停用", IsOn = true };
        _usNewPassword = Secret("初始密码", "新建必须填写，至少 6 位");
        _usConfirmPassword = Secret("确认密码", "再次输入");
        _usSave = new Button { Content = "保存用户" };
        _usSave.Click += async (_, _) => await SaveUserAsync();
        _usDelete = new Button { Content = "删除用户" };
        _usDelete.Click += async (_, _) => await DeleteUserAsync();
        _usDetail = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 12 };

        _usChangePassword = Secret("新密码", "至少 6 位");
        _usChangeConfirm = Secret("确认新密码", "再次输入");
        var changePassword = new Button { Content = "修改密码" };
        changePassword.Click += async (_, _) => await ChangeUserPasswordAsync();

        _usRolePicker = new ComboBox { Header = "可分配的角色", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_usRolePicker, "选择角色");
        var addRole = new Button { Content = "加入选择" };
        addRole.Click += (_, _) => AddUserRole();
        var clearRoles = new Button { Content = "清空选择" };
        clearRoles.Click += (_, _) => { _usRoleIds.Clear(); FillUserRoleSummary(); };
        _usRoleSummary = new TextBlock { TextWrapping = TextWrapping.Wrap };
        _usRoleList = new StackPanel { Spacing = 2 };
        _usAssignRoles = new Button { Content = "保存角色分配" };
        _usAssignRoles.Click += async (_, _) => await AssignUserRolesAsync();
        _usNotice = new InfoBar { IsOpen = false, IsClosable = true };

        UsersSettings.Content = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(0, 16, 8, 16),
            Children =
            {
                Card("账号", UserContractsText.PasswordNotice + " " + UserContractsText.EmailConflictNotice,
                    _usPicker, Row(refresh, create), _usUserId, _usUsername, _usEmail, _usDisplayName, _usType,
                    _usEnabled, _usNewPassword, _usConfirmPassword, _usDetail, Row(_usSave, _usDelete)),
                Card("修改密码", "改密只写不读；Core 的下限是 6 位。",
                    _usChangePassword, _usChangeConfirm, Row(changePassword)),
                Card("角色分配", UserContractsText.RoleReplacementNotice,
                    _usRolePicker, Row(addRole, clearRoles), _usRoleSummary, _usRoleList, Row(_usAssignRoles)),
                Card("删除保护", UserContractsText.LastAdminNotice),
                _usNotice
            }
        };
        SetUserEnabled(false);
    }

    private static PasswordBox Secret(string header, string placeholder) => new()
    {
        Header = header, PlaceholderText = placeholder, PasswordChar = "●",
        HorizontalAlignment = HorizontalAlignment.Stretch
    };

    internal async Task LoadUsersAsync()
    {
        if (!_usBuilt) return;
        try
        {
            _usUsers = await _users.ListAsync();
            _usRoles = await _roles.ListAsync();
            _usSwitching = true;
            try
            {
                var previous = AgentSelected(_usPicker);
                _usPicker.Items.Clear();
                foreach (var user in _usUsers)
                    _usPicker.Items.Add(new ComboBoxItem
                    {
                        Content = $"{user.Username}（{user.UserId}）· {user.UserTypeText} · {user.StateText}",
                        Tag = user.UserId
                    });
                var index = _usUsers.ToList().FindIndex(user => user.UserId == previous);
                _usPicker.SelectedIndex = _usUsers.Count > 0 ? Math.Max(0, index) : -1;

                var previousRole = AgentSelected(_usRolePicker);
                _usRolePicker.Items.Clear();
                foreach (var role in _usRoles)
                    _usRolePicker.Items.Add(new ComboBoxItem
                    {
                        Content = $"{role.Name}（{role.RoleId}）· {role.KindText}", Tag = role.RoleId
                    });
                var roleIndex = _usRoles.ToList().FindIndex(role => role.RoleId == previousRole);
                _usRolePicker.SelectedIndex = _usRoles.Count > 0 ? Math.Max(0, roleIndex) : -1;
            }
            finally { _usSwitching = false; }
            ApplyUserSelection();
            SetUserEnabled(true);
            _usNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportUserFailure(exception); }
    }

    private void ApplyUserSelection()
    {
        _usUser = AgentSelected(_usPicker) is { } userId
            ? _usUsers.FirstOrDefault(user => user.UserId == userId)
            : null;
        var user = _usUser;
        _usUserId.Text = user?.UserId ?? "";
        _usUsername.Text = user?.Username ?? "";
        _usEmail.Text = user?.Email ?? "";
        _usDisplayName.Text = user?.DisplayName ?? "";
        _usEnabled.IsOn = user?.IsEnabled ?? true;
        _usType.SelectedIndex = Math.Max(0, UserContractsText.UserTypes.ToList()
            .FindIndex(type => string.Equals(type, user?.UserType, StringComparison.OrdinalIgnoreCase)));
        _usNewPassword.Password = "";
        _usConfirmPassword.Password = "";
        _usRoleIds.Clear();
        if (user is not null) _usRoleIds.AddRange(user.RoleIds);
        FillUserRoleSummary();

        _usDetail.Text = user is null
            ? (_usUsers.Count == 0 ? "还没有用户。" : "请选择一个用户。")
            : $"{user.Username}（{user.UserId}）· {user.UserTypeText} · {user.StateText}\n" +
              $"邮箱 {user.Email} · 角色 {user.RolesText}\n" +
              $"创建 {user.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}" +
              (user.Avatar.Length == 0 ? "" : $" · 头像 {user.Avatar}") + "\n" +
              UserContractsText.PasswordNotice +
              (user.IsAdmin ? "\n" + UserContractsText.LastAdminNotice : "");
        // 新建时密码必填；编辑时不改密码就走「修改密码」卡。
        _usSave.IsEnabled = true;
        _usDelete.IsEnabled = user is not null;
        _usAssignRoles.IsEnabled = user is not null;
    }

    private void StartNewUser()
    {
        _usSwitching = true;
        try { _usPicker.SelectedIndex = -1; }
        finally { _usSwitching = false; }
        _usUser = null;
        ApplyUserSelection();
        ShowNotice(_usNotice, InfoBarSeverity.Informational, "新建用户",
            "填写 UserId、用户名、邮箱、类型与初始密码（需二次确认）。");
    }

    private void AddUserRole()
    {
        if (AgentSelected(_usRolePicker) is not { } roleId) { WarnUser("请先选择角色"); return; }
        if (_usRoleIds.Contains(roleId, StringComparer.Ordinal)) { WarnUser($"{roleId} 已经在选择里"); return; }
        _usRoleIds.Add(roleId);
        FillUserRoleSummary();
        _usNotice.IsOpen = false;
    }

    private void FillUserRoleSummary()
    {
        var normalized = _usRoleIds.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        _usRoleSummary.Text = normalized.Length == 0
            ? "当前选择为空：保存后该用户将没有任何角色（这是全量替换的结果）。"
            : $"已选择 {normalized.Length} 个角色：" + string.Join("、", normalized);
        _usRoleList.Children.Clear();
        foreach (var roleId in normalized)
        {
            var role = _usRoles.FirstOrDefault(candidate => candidate.RoleId == roleId);
            _usRoleList.Children.Add(Muted(role is null
                ? $"{roleId} · 不在当前角色目录中（Core 只会匹配不到）"
                : $"{role.Name}（{role.RoleId}）· {role.KindText} · {role.Permissions.Count} 项权限"));
        }
    }

    private async Task SaveUserAsync()
    {
        if (_usUser is null)
        {
            var create = new UserCreate(_usUserId.Text.Trim(), _usUsername.Text.Trim(), _usEmail.Text.Trim(),
                _usDisplayName.Text.Trim(), AgentSelected(_usType) ?? "SimpleUser",
                _usNewPassword.Password, _usConfirmPassword.Password);
            var errors = UserContractsText.Validate(create);
            if (errors.Count > 0) { ShowNotice(_usNotice, InfoBarSeverity.Warning, "请先修正表单", string.Join(" ", errors)); return; }
            await RunUserAsync("用户已创建", "新账号默认启用；密码只写不读。",
                async () =>
                {
                    await _users.CreateAsync(create);
                    _usNewPassword.Password = "";
                    _usConfirmPassword.Password = "";
                    await LoadUsersAsync();
                });
            return;
        }

        var edit = new UserMetaEdit(_usUser.UserId, _usUsername.Text.Trim(), _usEmail.Text.Trim(),
            _usDisplayName.Text.Trim(), AgentSelected(_usType) ?? "SimpleUser", _usEnabled.IsOn);
        var editErrors = UserContractsText.Validate(edit);
        if (editErrors.Count > 0) { ShowNotice(_usNotice, InfoBarSeverity.Warning, "请先修正表单", string.Join(" ", editErrors)); return; }
        await RunUserAsync("用户已保存", "UserId 不可更改；邮箱重复会被 Core 拒绝。",
            async () => { await _users.UpdateAsync(edit); await LoadUsersAsync(); });
    }

    private async Task ChangeUserPasswordAsync()
    {
        if (_usUser is not { } user) { WarnUser("请先选择用户"); return; }
        var change = new UserPasswordChange(user.UserId, _usChangePassword.Password, _usChangeConfirm.Password);
        var errors = UserContractsText.Validate(change);
        if (errors.Count > 0) { ShowNotice(_usNotice, InfoBarSeverity.Warning, "请先修正表单", string.Join(" ", errors)); return; }
        await RunUserAsync("密码已修改", UserContractsText.PasswordNotice,
            async () =>
            {
                await _users.ChangePasswordAsync(change);
                _usChangePassword.Password = "";
                _usChangeConfirm.Password = "";
            });
    }

    private async Task AssignUserRolesAsync()
    {
        if (_usUser is not { } user) { WarnUser("请先选择用户"); return; }
        await RunUserAsync("角色分配已保存", UserContractsText.RoleReplacementNotice,
            async () => { await _users.AssignRolesAsync(user.UserId, _usRoleIds); await LoadUsersAsync(); });
    }

    private async Task DeleteUserAsync()
    {
        if (_usUser is not { } user) { WarnUser("请先选择用户"); return; }
        var confirm = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = "删除用户",
            Content = $"将删除 {user.Username}（{user.UserId}）。该账号的登录与角色分配会一并失效。" +
                      (user.IsAdmin ? " " + UserContractsText.LastAdminNotice : "") + " 此操作不可撤销。",
            PrimaryButtonText = "删除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        await RunUserAsync("用户已删除", "该账号不再存在。",
            async () => { await _users.DeleteAsync(user.UserId); await LoadUsersAsync(); });
    }

    private void WarnUser(string message) => ShowNotice(_usNotice, InfoBarSeverity.Warning, "请先选择", message);

    private async Task RunUserAsync(string title, string message, Func<Task> action)
    {
        SetUserEnabled(false);
        try
        {
            await action();
            SetUserEnabled(true);
            ShowNotice(_usNotice, InfoBarSeverity.Success, title, message);
        }
        catch (Exception exception) { ReportUserFailure(exception); }
    }

    private void ReportUserFailure(Exception exception)
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
        SetUserEnabled(!unavailable);
        ShowNotice(_usNotice, severity, title, message);
    }

    private void SetUserEnabled(bool enabled)
    {
        foreach (var control in new Control[]
        {
            _usPicker, _usUserId, _usUsername, _usEmail, _usDisplayName, _usType, _usEnabled,
            _usNewPassword, _usConfirmPassword, _usChangePassword, _usChangeConfirm, _usRolePicker
        }) control.IsEnabled = enabled;
        _usSave.IsEnabled = enabled;
        _usDelete.IsEnabled = enabled && _usUser is not null;
        _usAssignRoles.IsEnabled = enabled && _usUser is not null;
    }
}
