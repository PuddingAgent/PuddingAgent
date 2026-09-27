using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PuddingDesktop.Foundation;

namespace PuddingDesktop;

/// <summary>
/// DS-12 role slice: the RBAC permission list. Core's built-in roles are read-only, and the page checks
/// permissions against the vocabulary Core's own roles use.
/// </summary>
public sealed partial class MainWindow
{
    private IReadOnlyList<PermissionRole> _rbRoles = [];
    private PermissionRole? _rbRole;
    private readonly List<string> _rbPermissions = [];
    private bool _rbBuilt;
    private bool _rbSwitching;

    private ComboBox _rbPicker = null!, _rbPermissionPicker = null!;
    private TextBox _rbRoleId = null!, _rbName = null!, _rbDescription = null!;
    private TextBlock _rbDetail = null!, _rbPermissionSummary = null!;
    private StackPanel _rbPermissionList = null!;
    private Button _rbSave = null!, _rbDelete = null!;
    private InfoBar _rbNotice = null!;

    private void BuildRolePanel()
    {
        if (_rbBuilt) return;
        _rbBuilt = true;

        _rbPicker = new ComboBox { Header = "角色", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_rbPicker, "选择角色");
        _rbPicker.SelectionChanged += (_, _) => { if (!_rbSwitching) ApplyRoleSelection(); };
        var refresh = new Button { Content = "刷新" };
        refresh.Click += async (_, _) => await LoadRolesAsync();
        var create = new Button { Content = "新建自定义角色" };
        create.Click += (_, _) => StartNewRole();
        _rbRoleId = Field("角色 ID", "字母、数字、'-'、'_'、'.'；保存后不可更改");
        _rbName = Field("角色名称");
        _rbDescription = Field("描述");
        _rbPermissionPicker = new ComboBox { Header = "可添加的权限", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_rbPermissionPicker, "选择权限");
        foreach (var permission in RoleText.KnownPermissions)
            _rbPermissionPicker.Items.Add(new ComboBoxItem
            {
                Content = RoleText.DescribePermission(permission), Tag = permission
            });
        _rbPermissionPicker.SelectedIndex = 0;
        var add = new Button { Content = "添加权限" };
        add.Click += (_, _) => AddRolePermission();
        var clear = new Button { Content = "清空权限" };
        clear.Click += (_, _) => { _rbPermissions.Clear(); FillRolePermissionSummary(); };
        _rbPermissionSummary = new TextBlock { TextWrapping = TextWrapping.Wrap };
        _rbPermissionList = new StackPanel { Spacing = 2 };
        _rbDetail = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 12 };
        _rbSave = new Button { Content = "保存角色" };
        _rbSave.Click += async (_, _) => await SaveRoleAsync();
        _rbDelete = new Button { Content = "删除角色" };
        _rbDelete.Click += async (_, _) => await DeleteRoleAsync();
        _rbNotice = new InfoBar { IsOpen = false, IsClosable = true };

        RolesSettings.Content = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(0, 16, 8, 16),
            Children =
            {
                Card("权限角色（RBAC）", RoleText.SystemRoleNotice + " " + RoleText.PermissionNotice,
                    _rbPicker, Row(refresh, create), _rbRoleId, _rbName, _rbDescription,
                    _rbPermissionPicker, Row(add, clear), _rbPermissionSummary, _rbPermissionList,
                    _rbDetail, Row(_rbSave, _rbDelete)),
                Card("登记差异", RoleText.TeamUserPermissionGap, new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap, Opacity = .8, FontSize = 12,
                    Text = "Core 现存的 9 项权限：" + string.Join(" · ", RoleText.KnownPermissions)
                }),
                _rbNotice
            }
        };
        SetRoleEnabled(false);
    }

    internal async Task LoadRolesAsync()
    {
        if (!_rbBuilt) return;
        try
        {
            _rbRoles = await _roles.ListAsync();
            _rbSwitching = true;
            try
            {
                var previous = AgentSelected(_rbPicker);
                _rbPicker.Items.Clear();
                foreach (var role in _rbRoles)
                    _rbPicker.Items.Add(new ComboBoxItem
                    {
                        Content = $"{role.Name}（{role.RoleId}）· {role.KindText} · {role.Permissions.Count} 项权限",
                        Tag = role.RoleId
                    });
                var index = _rbRoles.ToList().FindIndex(role => role.RoleId == previous);
                _rbPicker.SelectedIndex = _rbRoles.Count > 0 ? Math.Max(0, index) : -1;
            }
            finally { _rbSwitching = false; }
            ApplyRoleSelection();
            SetRoleEnabled(true);
            _rbNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportRoleFailure(exception); }
    }

    private void ApplyRoleSelection()
    {
        _rbRole = AgentSelected(_rbPicker) is { } roleId
            ? _rbRoles.FirstOrDefault(role => role.RoleId == roleId)
            : null;
        var role = _rbRole;
        _rbRoleId.Text = role?.RoleId ?? "";
        _rbName.Text = role?.Name ?? "";
        _rbDescription.Text = role?.Description ?? "";
        _rbPermissions.Clear();
        if (role is not null) _rbPermissions.AddRange(role.Permissions);
        FillRolePermissionSummary();

        _rbDetail.Text = role is null
            ? (_rbRoles.Count == 0 ? "还没有角色。" : "请选择一个角色。")
            : $"{role.Name}（{role.RoleId}）· {role.KindText} · 创建 {role.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}\n" +
              (role.Description.Length == 0 ? "（没有描述）" : role.Description) + "\n" +
              (role.HasUnknownPermission
                  ? "注意：该角色包含 Core 未定义的权限取值，界面会原样显示但不提供新增同类取值。"
                  : "") +
              (role.IsEditable ? "" : RoleText.SystemRoleNotice);

        // 系统内置角色：Core 会拒绝修改与删除，所以界面直接把这两条路径关掉（并在详情里说明原因）。
        _rbSave.IsEnabled = role?.IsEditable ?? true;
        _rbDelete.IsEnabled = role?.IsEditable ?? false;
    }

    private void StartNewRole()
    {
        _rbSwitching = true;
        try { _rbPicker.SelectedIndex = -1; }
        finally { _rbSwitching = false; }
        _rbRole = null;
        ApplyRoleSelection();
        ShowNotice(_rbNotice, InfoBarSeverity.Informational, "新建自定义角色",
            "选择 Core 定义的权限取值；未知取值会被拦下。");
    }

    private void AddRolePermission()
    {
        if (AgentSelected(_rbPermissionPicker) is not { } permission) { WarnRole("请先选择权限"); return; }
        if (_rbPermissions.Contains(permission, StringComparer.Ordinal)) { WarnRole($"{permission} 已经添加"); return; }
        _rbPermissions.Add(permission);
        FillRolePermissionSummary();
        _rbNotice.IsOpen = false;
    }

    private void FillRolePermissionSummary()
    {
        var normalized = RoleText.Normalize(_rbPermissions);
        _rbPermissionSummary.Text = normalized.Count == 0
            ? "尚未选择权限（Core 允许空权限列表，但这样的角色不会授予任何能力）。"
            : $"已选择 {normalized.Count} 项权限：" + string.Join(" · ", normalized);
        _rbPermissionList.Children.Clear();
        foreach (var permission in normalized)
            _rbPermissionList.Children.Add(Muted(
                RoleText.DescribePermission(permission) +
                (RoleText.IsKnownPermission(permission) ? "" : " · 不在 Core 的 9 项之内")));
    }

    private async Task SaveRoleAsync()
    {
        var edit = new RoleEdit(_rbRole?.RoleId ?? _rbRoleId.Text.Trim(), _rbName.Text.Trim(),
            _rbDescription.Text.Trim(), RoleText.Normalize(_rbPermissions));
        if (_rbRole is not null && !_rbRole.IsEditable)
        {
            ShowNotice(_rbNotice, InfoBarSeverity.Warning, "系统内置角色不可修改", RoleText.SystemRoleNotice);
            return;
        }
        var isCreate = _rbRole is null;
        var errors = RoleText.Validate(edit, isCreate);
        if (errors.Count > 0) { ShowNotice(_rbNotice, InfoBarSeverity.Warning, "请先修正表单", string.Join(" ", errors)); return; }
        await RunRoleAsync(isCreate ? "角色已创建" : "角色已保存", "权限列表按 Core 的既有语义写入。",
            async () =>
            {
                if (isCreate) await _roles.CreateAsync(edit);
                else await _roles.UpdateAsync(edit);
                await LoadRolesAsync();
            });
    }

    private async Task DeleteRoleAsync()
    {
        if (_rbRole is not { } role) { WarnRole("请先选择角色"); return; }
        if (!role.IsEditable)
        {
            ShowNotice(_rbNotice, InfoBarSeverity.Warning, "系统内置角色不可删除", RoleText.SystemRoleNotice);
            return;
        }
        var confirm = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = "删除角色",
            Content = $"将删除 {role.Name}（{role.RoleId}）。已分配该角色的用户会失去它带来的权限。此操作不可撤销。",
            PrimaryButtonText = "删除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        await RunRoleAsync("角色已删除", "删除后该角色的权限不再生效。",
            async () => { await _roles.DeleteAsync(role.RoleId); await LoadRolesAsync(); });
    }

    private void WarnRole(string message) => ShowNotice(_rbNotice, InfoBarSeverity.Warning, "请先选择", message);

    private async Task RunRoleAsync(string title, string message, Func<Task> action)
    {
        SetRoleEnabled(false);
        try
        {
            await action();
            SetRoleEnabled(true);
            ShowNotice(_rbNotice, InfoBarSeverity.Success, title, message);
        }
        catch (Exception exception) { ReportRoleFailure(exception); }
    }

    private void ReportRoleFailure(Exception exception)
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
        SetRoleEnabled(!unavailable);
        ShowNotice(_rbNotice, severity, title, message);
    }

    private void SetRoleEnabled(bool enabled)
    {
        foreach (var control in new Control[]
        {
            _rbPicker, _rbRoleId, _rbName, _rbDescription, _rbPermissionPicker
        }) control.IsEnabled = enabled;
        if (!enabled)
        {
            _rbSave.IsEnabled = false;
            _rbDelete.IsEnabled = false;
            return;
        }
        var editable = _rbRole?.IsEditable ?? true;
        _rbSave.IsEnabled = editable;
        _rbDelete.IsEnabled = editable && _rbRole is not null;
    }
}
