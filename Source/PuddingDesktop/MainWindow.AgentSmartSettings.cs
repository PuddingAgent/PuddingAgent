using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PuddingDesktop.Foundation;

namespace PuddingDesktop;

/// <summary>
/// DS-04 Smart sub-agent slice: the seven Smart role routes on a workspace role instance. Routes are
/// stored as the literal "{providerId}/{modelId}" Core validates, so a half-filled selection is refused
/// rather than silently written as "not routed".
/// </summary>
public sealed partial class MainWindow
{
    private readonly Dictionary<string, ModelPairControls> _asPairs = new(StringComparer.Ordinal);
    private bool _asBuilt;
    private bool _asSwitching;

    private ComboBox _asWorkspace = null!, _asInstance = null!;
    private TextBlock _asInfo = null!;
    private InfoBar _asNotice = null!;

    private void BuildAgentSmartPanel()
    {
        if (_asBuilt) return;
        _asBuilt = true;

        _asWorkspace = new ComboBox { Header = "工作区", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_asWorkspace, "Smart 路由所属工作区");
        _asWorkspace.SelectionChanged += async (_, _) => { if (!_asSwitching && AgentSelected(_asWorkspace) is { } id) await LoadSmartInstancesAsync(id); };
        _asInstance = new ComboBox { Header = "角色实例", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_asInstance, "Smart 路由所属角色实例");
        _asInstance.SelectionChanged += async (_, _) =>
        {
            if (!_asSwitching && AgentSelected(_asWorkspace) is { } w && AgentSelected(_asInstance) is { } a) await LoadSmartRoutesAsync(w, a);
        };

        var children = new List<UIElement> { _asWorkspace, _asInstance };
        foreach (var slot in SmartRoleRoutes.Roles)
        {
            var pair = BuildPair($"{slot.Title} · {slot.Description}", embedding: false);
            _asPairs[slot.RoleId] = pair;
            children.Add(pair.Provider);
            children.Add(pair.Model);
        }
        _asInfo = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = .7, FontSize = 12, IsTextSelectionEnabled = true };
        children.Add(_asInfo);
        var save = new Button { Content = "保存 Smart 路由" };
        save.Click += async (_, _) => await SaveSmartRoutesAsync();
        var reload = new Button { Content = "重新读取" };
        reload.Click += async (_, _) =>
        {
            if (AgentSelected(_asWorkspace) is { } w && AgentSelected(_asInstance) is { } a) await LoadSmartRoutesAsync(w, a);
        };
        children.Add(Row(save, reload));
        _asNotice = new InfoBar { IsOpen = false, IsClosable = true };

        AgentSmartSettings.Content = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(0, 16, 8, 16),
            Children =
            {
                Card("子代理模型路由",
                    $"只对角色实例生效（全局模板没有 Smart 入口）。留空表示该子代理按角色的默认模型运行；" +
                    $"填写时必须是 {SmartRoleRoutes.Format}，Core 会按同样的规则校验。",
                    children.ToArray()),
                _asNotice
            }
        };
        SetAgentSmartEnabled(false);
    }

    internal async Task LoadAgentSmartAsync()
    {
        if (!_asBuilt) return;
        try
        {
            if (_amCatalog.Count == 0) _amCatalog = await _agentDirectory.ListModelCatalogAsync();
            var workspaces = await _agentDirectory.ListWorkspacesAsync();
            _asSwitching = true;
            try
            {
                var previous = AgentSelected(_asWorkspace);
                _asWorkspace.Items.Clear();
                foreach (var workspace in workspaces)
                    _asWorkspace.Items.Add(new ComboBoxItem { Content = $"{workspace.Name}（{workspace.WorkspaceId}）", Tag = workspace.WorkspaceId });
                var index = workspaces.ToList().FindIndex(workspace => workspace.WorkspaceId == previous);
                _asWorkspace.SelectedIndex = workspaces.Count > 0 ? Math.Max(0, index) : -1;
            }
            finally { _asSwitching = false; }

            if (AgentSelected(_asWorkspace) is { } workspaceId) await LoadSmartInstancesAsync(workspaceId);
            SetAgentSmartEnabled(true);
            _asNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportAgentSmartFailure(exception); }
    }

    private async Task LoadSmartInstancesAsync(string workspaceId)
    {
        var instances = await _agentDirectory.ListInstancesAsync(workspaceId);
        _asSwitching = true;
        try
        {
            _asInstance.Items.Clear();
            foreach (var instance in instances)
                _asInstance.Items.Add(new ComboBoxItem { Content = $"{instance.DisplayName}（{instance.AgentId}）", Tag = instance.AgentId });
            _asInstance.SelectedIndex = instances.Count > 0 ? 0 : -1;
        }
        finally { _asSwitching = false; }
        if (AgentSelected(_asInstance) is { } agentId) await LoadSmartRoutesAsync(workspaceId, agentId);
        else
        {
            _asInfo.Text = "该工作区还没有角色实例；Smart 路由只存在于角色实例上。";
            foreach (var pair in _asPairs.Values) WritePair(pair, AgentModelChoice.None);
        }
    }

    private async Task LoadSmartRoutesAsync(string workspaceId, string agentId)
    {
        try
        {
            var routes = await _agentDirectory.ReadSmartRoutesAsync(workspaceId, agentId);
            _asSwitching = true;
            try
            {
                foreach (var slot in SmartRoleRoutes.Roles)
                    WritePair(_asPairs[slot.RoleId], SmartRoleRoutes.ToChoice(routes.GetValueOrDefault(slot.RoleId)));
                _asInfo.Text = string.Join("\n", SmartRoleRoutes.Roles.Select(slot =>
                    $"{slot.Title}：{SmartRoleRoutes.Describe(routes.GetValueOrDefault(slot.RoleId))}"));
            }
            finally { _asSwitching = false; }
            _asNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportAgentSmartFailure(exception); }
    }

    private async Task SaveSmartRoutesAsync()
    {
        if (AgentSelected(_asWorkspace) is not { } workspaceId || AgentSelected(_asInstance) is not { } agentId)
        {
            ShowNotice(_asNotice, InfoBarSeverity.Warning, "请先选择实例", "Smart 路由只存在于角色实例上。");
            return;
        }
        var routes = new Dictionary<string, string>(StringComparer.Ordinal);
        var errors = new List<string>();
        foreach (var slot in SmartRoleRoutes.Roles)
        {
            var choice = ReadPair(_asPairs[slot.RoleId]);
            if (choice.IsPartial)
            {
                errors.Add($"{slot.Title}：服务商与模型必须同时选择，或同时留空。");
                continue;
            }
            if (choice.IsSet && AgentModelPolicyText.Find(_amCatalog, choice.ProviderId, choice.ModelId) is null)
                errors.Add($"{slot.Title}：目录中不存在 {choice.ProviderId}/{choice.ModelId}。");
            routes[slot.RoleId] = SmartRoleRoutes.FromChoice(choice);
            errors.AddRange(SmartRoleRoutes.Validate(routes[slot.RoleId], slot.Title));
        }
        if (errors.Count > 0) { ShowNotice(_asNotice, InfoBarSeverity.Warning, "请先修正表单", string.Join(" ", errors)); return; }
        await RunAgentSmartAsync("Smart 路由已保存", "已写入该角色实例；全局模板与其他字段未被改动。",
            async () => { await _agentDirectory.SaveSmartRoutesAsync(workspaceId, agentId, routes); await LoadSmartRoutesAsync(workspaceId, agentId); });
    }

    private async Task RunAgentSmartAsync(string title, string message, Func<Task> action)
    {
        SetAgentSmartEnabled(false);
        try
        {
            await action();
            SetAgentSmartEnabled(true);
            ShowNotice(_asNotice, InfoBarSeverity.Success, title, message);
        }
        catch (Exception exception) { ReportAgentSmartFailure(exception); }
    }

    private void ReportAgentSmartFailure(Exception exception)
    {
        App.WriteDiagnostic(exception);
        var unavailable = exception is SettingsUnavailableException;
        var (severity, title, message) = exception switch
        {
            SettingsUnavailableException reason => (InfoBarSeverity.Informational, "Core 未就绪",
                reason.Message + " 该分类仍可浏览；表单已禁用，未写入任何内容。"),
            SettingsConflictException conflict => (InfoBarSeverity.Warning, "版本冲突，已阻止覆盖", conflict.Message),
            ArgumentException argument => (InfoBarSeverity.Warning, "表单或配置被 Core 拒绝", argument.Message),
            InvalidOperationException invalid => (InfoBarSeverity.Warning, "操作未完成", invalid.Message),
            _ => (InfoBarSeverity.Error, "操作失败", "请查看诊断日志后重试。")
        };
        SetAgentSmartEnabled(!unavailable);
        ShowNotice(_asNotice, severity, title, message);
    }

    private void SetAgentSmartEnabled(bool enabled)
    {
        var controls = new List<Control> { _asWorkspace, _asInstance };
        foreach (var pair in _asPairs.Values) { controls.Add(pair.Provider); controls.Add(pair.Model); }
        foreach (var control in controls) control.IsEnabled = enabled;
    }
}
