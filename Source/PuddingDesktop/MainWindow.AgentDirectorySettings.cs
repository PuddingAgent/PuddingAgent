using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PuddingDesktop.Foundation;

namespace PuddingDesktop;

/// <summary>
/// DS-04 (directory slice): global templates, shipped presets and workspace role instances.
/// Every call goes through IAgentDirectorySettings into the in-process Core.
/// </summary>
public sealed partial class MainWindow
{
    private IReadOnlyList<AgentWorkspaceOption> _agentWorkspaces = [];
    private IReadOnlyList<AgentTemplateSummary> _agentTemplates = [];
    private IReadOnlyList<AgentTemplatePreset> _agentPresets = [];
    private IReadOnlyList<AgentAvatarOption> _agentAvatars = [];
    private IReadOnlyList<AgentInstanceSummary> _agentInstances = [];
    private AgentTemplateSummary? _agentTemplate;
    private AgentInstanceSummary? _agentInstance;
    private bool _agentBuilt;
    private bool _agentLoading;

    private ComboBox _agWorkspace = null!;
    private ComboBox _agTemplate = null!, _agPreset = null!, _agInstance = null!;
    private TextBox _agTemplateId = null!, _agTemplateName = null!, _agTemplateRole = null!, _agTemplateDescription = null!;
    private TextBox _agTemplateSort = null!;
    private ComboBox _agTemplateAvatar = null!;
    private ToggleSwitch _agTemplateEnabled = null!;
    private TextBlock _agTemplateInfo = null!, _agInstanceInfo = null!;
    private TextBox _agInstanceName = null!, _agInstanceDescription = null!, _agInstanceRole = null!;
    private ComboBox _agInstanceAvatar = null!;
    private ToggleSwitch _agInstanceEnabled = null!;
    private Button _agTemplateSave = null!, _agTemplateDelete = null!, _agImport = null!;
    private Button _agInstanceCreate = null!, _agInstanceSave = null!, _agInstanceDelete = null!, _agFreeze = null!;
    private InfoBar _agNotice = null!;

    private void BuildAgentDirectoryPanel()
    {
        if (_agentBuilt) return;
        _agentBuilt = true;

        _agWorkspace = new ComboBox { Header = "工作区", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_agWorkspace, "选择工作区");
        _agWorkspace.SelectionChanged += (_, _) => { if (AgentSelected(_agWorkspace) is { } id) _ = LoadAgentInstancesAsync(id); };
        var refresh = new Button { Content = "刷新" };
        refresh.Click += async (_, _) => await LoadAgentDirectoryAsync();

        _agTemplate = new ComboBox { Header = "全局模板", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_agTemplate, "选择全局模板");
        _agTemplate.SelectionChanged += (_, _) => ApplyAgentTemplateSelection();
        _agTemplateId = Field("模板 ID", "例如 general-assistant");
        _agTemplateName = Field("模板名称");
        _agTemplateRole = Field("角色标识", "Service / Coding / Review …");
        _agTemplateDescription = Field("描述");
        _agTemplateAvatar = new ComboBox { Header = "头像", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_agTemplateAvatar, "选择模板头像");
        _agTemplateSort = Field("排序", "0");
        _agTemplateEnabled = new ToggleSwitch { Header = "启用该模板", OnContent = "已启用", OffContent = "已停用", IsOn = true };
        _agTemplateInfo = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = .7, FontSize = 12 };
        _agTemplateDelete = new Button { Content = "删除模板" };
        _agTemplateDelete.Click += async (_, _) => await OnAgentDeleteTemplateAsync();
        _agTemplateSave = new Button { Content = "保存模板基础信息" };
        _agTemplateSave.Click += async (_, _) => await SaveAgentTemplateAsync();
        var newTemplate = new Button { Content = "新建模板" };
        newTemplate.Click += (_, _) => OnAgentNewTemplate();

        _agPreset = new ComboBox { Header = "随产品提供的预设", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_agPreset, "选择预设模板");
        _agImport = new Button { Content = "导入所选预设" };
        _agImport.Click += async (_, _) => await ImportAgentPresetAsync();

        _agInstance = new ComboBox { Header = "角色实例", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_agInstance, "选择角色实例");
        _agInstance.SelectionChanged += (_, _) => ApplyAgentInstanceSelection();
        _agInstanceName = Field("角色名称");
        _agInstanceDescription = Field("描述");
        _agInstanceRole = Field("角色标识");
        _agInstanceAvatar = new ComboBox { Header = "头像", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_agInstanceAvatar, "选择角色头像");
        _agInstanceEnabled = new ToggleSwitch { Header = "启用该角色", OnContent = "已启用", OffContent = "已停用", IsOn = true };
        _agInstanceInfo = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = .7, FontSize = 12, IsTextSelectionEnabled = true };
        _agInstanceCreate = new Button { Content = "从所选模板新建角色" };
        _agInstanceCreate.Click += async (_, _) => await CreateAgentInstanceAsync();
        _agInstanceDelete = new Button { Content = "删除角色" };
        _agInstanceDelete.Click += async (_, _) => await OnAgentDeleteInstanceAsync();
        _agFreeze = new Button { Content = "冻结角色" };
        _agFreeze.Click += async (_, _) => await ToggleAgentFreezeAsync();
        _agInstanceSave = new Button { Content = "保存角色基础信息" };
        _agInstanceSave.Click += async (_, _) => await SaveAgentInstanceAsync();

        _agNotice = new InfoBar { IsOpen = false, IsClosable = true };

        AgentDirectorySettings.Content = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(0, 16, 8, 16),
            Children =
            {
                Card("全局模板与角色目录",
                    "模板是全局共享的默认值，角色实例属于某个工作区。编辑前先看清当前选中的是模板还是实例；" +
                    "这里只改基础信息，Prompt/文档在“角色与 Prompt”页签单独编辑，不会被本页清空。",
                    _agWorkspace, Row(refresh), _agTemplate,
                    Row(newTemplate, _agTemplateDelete), _agTemplateSave,
                    _agTemplateId, _agTemplateName, _agTemplateRole, _agTemplateDescription, _agTemplateAvatar, _agTemplateSort,
                    _agTemplateEnabled, _agTemplateInfo, _agPreset, Row(_agImport)),
                Card("角色实例",
                    "从模板创建实例后，模板 ID 按现有契约固定不变。冻结与停用是两种状态：冻结只影响该实例的执行准入。",
                    _agInstance, Row(_agInstanceCreate, _agInstanceDelete, _agFreeze),
                    _agInstanceName, _agInstanceDescription, _agInstanceRole, _agInstanceAvatar, _agInstanceEnabled, _agInstanceInfo,
                    Row(_agInstanceSave)),
                _agNotice
            }
        };
        SetAgentEnabled(false);
    }

    private static string? AgentSelected(ComboBox picker) => (picker.SelectedItem as ComboBoxItem)?.Tag as string;

    internal async Task LoadAgentDirectoryAsync()
    {
        if (!_agentBuilt || _agentLoading) return;
        _agentLoading = true;
        try
        {
            _agentWorkspaces = await _agentDirectory.ListWorkspacesAsync();
            _agentTemplates = await _agentDirectory.ListTemplatesAsync();
            _agentPresets = await _agentDirectory.ListPresetsAsync();
            _agentAvatars = await _agentDirectory.ListAvatarsAsync();
            FillAgentWorkspaces();
            FillAgentTemplates();
            FillAgentAvatars();
            FillAgentPresets();
            if (AgentSelected(_agWorkspace) is { } workspaceId) await LoadAgentInstancesAsync(workspaceId);
            SetAgentEnabled(true);
            _agNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportAgentFailure(exception); }
        finally { _agentLoading = false; }
    }

    private async Task LoadAgentInstancesAsync(string workspaceId)
    {
        try
        {
            _agentInstances = await _agentDirectory.ListInstancesAsync(workspaceId);
            _agInstance.Items.Clear();
            foreach (var instance in _agentInstances)
                _agInstance.Items.Add(new ComboBoxItem { Content = $"{instance.DisplayName}（{instance.AgentId}）", Tag = instance.AgentId });
            _agInstance.SelectedIndex = _agentInstances.Count > 0 ? 0 : -1;
            if (_agentInstances.Count == 0) { _agentInstance = null; ApplyAgentInstanceSelection(); }
        }
        catch (Exception exception) { ReportAgentFailure(exception); }
    }

    private void FillAgentWorkspaces()
    {
        var previous = AgentSelected(_agWorkspace);
        _agWorkspace.Items.Clear();
        foreach (var workspace in _agentWorkspaces)
            _agWorkspace.Items.Add(new ComboBoxItem { Content = $"{workspace.Name}（{workspace.WorkspaceId}）", Tag = workspace.WorkspaceId });
        var found = _agentWorkspaces.ToList().FindIndex(workspace => workspace.WorkspaceId == previous);
        _agWorkspace.SelectedIndex = _agentWorkspaces.Count > 0 ? Math.Max(0, found) : -1;
    }

    private void FillAgentAvatars()
    {
        _agTemplateAvatar.Items.Clear();
        _agInstanceAvatar.Items.Clear();
        foreach (var avatar in _agentAvatars)
        {
            var label = string.IsNullOrWhiteSpace(avatar.RecommendedUse) ? avatar.Name : $"{avatar.Name} · {avatar.RecommendedUse}";
            _agTemplateAvatar.Items.Add(new ComboBoxItem { Content = label, Tag = avatar.AvatarId });
            _agInstanceAvatar.Items.Add(new ComboBoxItem { Content = label, Tag = avatar.AvatarId });
        }
    }

    private void FillAgentTemplates()
    {
        var previous = _agentTemplate?.TemplateId;
        _agTemplate.Items.Clear();
        foreach (var template in _agentTemplates)
            _agTemplate.Items.Add(new ComboBoxItem { Content = $"{template.Name}（{template.TemplateId}）", Tag = template.TemplateId });
        var found = _agentTemplates.ToList().FindIndex(template => template.TemplateId == previous);
        _agTemplate.SelectedIndex = _agentTemplates.Count > 0 ? Math.Max(0, found) : -1;
        ApplyAgentTemplateSelection();
    }

    private void FillAgentPresets()
    {
        _agPreset.Items.Clear();
        foreach (var preset in _agentPresets)
            _agPreset.Items.Add(new ComboBoxItem { Content = $"{preset.Name}（{preset.TemplateId}）", Tag = preset.TemplateId });
        _agPreset.SelectedIndex = _agentPresets.Count > 0 ? 0 : -1;
    }

    private void ApplyAgentTemplateSelection()
    {
        _agentTemplate = AgentSelected(_agTemplate) is { } id
            ? _agentTemplates.FirstOrDefault(template => template.TemplateId == id)
            : null;
        var template = _agentTemplate;
        _agTemplateId.Text = template?.TemplateId ?? "";
        _agTemplateId.IsReadOnly = template is not null;
        _agTemplateName.Text = template?.Name ?? "";
        _agTemplateRole.Text = template?.Role ?? "";
        _agTemplateDescription.Text = template?.Description ?? "";
        _agTemplateAvatar.SelectedIndex = Math.Max(0, _agentAvatars.ToList().FindIndex(avatar => avatar.AvatarId == template?.AvatarId));
        _agTemplateSort.Text = (template?.SortOrder ?? 0).ToString();
        _agTemplateEnabled.IsOn = template?.IsEnabled ?? true;
        _agTemplateInfo.Text = template is null
            ? ""
            : $"内置：{(template.IsBuiltIn ? "是" : "否")} · 能力 {template.SelectedCapabilityCount} · 技能包 {template.SelectedSkillPackageCount}";
        _agTemplateDelete.IsEnabled = template is not null;
    }

    private void OnAgentNewTemplate()
    {
        _agTemplate.SelectedIndex = -1;
        _agentTemplate = null;
        ApplyAgentTemplateSelection();
        _agNotice.IsOpen = false;
    }

    private async Task SaveAgentTemplateAsync()
    {
        var edit = new AgentTemplateEdit(_agTemplateId.Text.Trim(), _agTemplateName.Text.Trim(), _agTemplateRole.Text.Trim(),
            _agTemplateDescription.Text.Trim(), _agTemplateEnabled.IsOn,
            VoiceSettingsText.ParseOptionalInt(_agTemplateSort.Text) ?? 0,
            AgentSelected(_agTemplateAvatar) ?? "");
        var errors = AgentDirectoryText.Validate(edit);
        if (errors.Count > 0) { ShowNotice(_agNotice, InfoBarSeverity.Warning, "请先修正表单", string.Join(" ", errors)); return; }
        await RunAgentAsync("模板已保存", "只替换了基础信息；Prompt、Markdown 文档与授权字段保持原值。",
            async () => { await _agentDirectory.SaveTemplateAsync(edit); await LoadAgentDirectoryAsync(); });
    }

    private async Task OnAgentDeleteTemplateAsync()
    {
        if (_agentTemplate is not { } template) return;
        var confirm = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = "删除全局模板",
            Content = $"将删除模板 {template.Name}（{template.TemplateId}）。已从它创建的实例不受影响，但不能再用它新建角色。此操作不可撤销。",
            PrimaryButtonText = "删除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        await RunAgentAsync("模板已删除", "模板目录已更新。",
            async () => { await _agentDirectory.DeleteTemplateAsync(template.TemplateId); await LoadAgentDirectoryAsync(); });
    }

    private async Task ImportAgentPresetAsync()
    {
        if (AgentSelected(_agPreset) is not { } templateId) return;
        await RunAgentAsync("预设已导入", "预设已写入全局模板目录，可继续编辑基础信息。",
            async () => { await _agentDirectory.ImportPresetAsync(templateId); await LoadAgentDirectoryAsync(); });
    }

    private void ApplyAgentInstanceSelection()
    {
        _agentInstance = AgentSelected(_agInstance) is { } id
            ? _agentInstances.FirstOrDefault(instance => instance.AgentId == id)
            : null;
        var instance = _agentInstance;
        _agInstanceName.Text = instance?.Name ?? "";
        _agInstanceDescription.Text = instance?.Description ?? "";
        _agInstanceRole.Text = instance?.Role ?? "";
        _agInstanceAvatar.SelectedIndex = Math.Max(0, _agentAvatars.ToList().FindIndex(avatar => avatar.AvatarId == instance?.AvatarId));
        _agInstanceEnabled.IsOn = instance?.IsEnabled ?? true;
        _agInstanceInfo.Text = instance is null
            ? ""
            : $"实例 ID：{instance.AgentId}\n来源模板：{(string.IsNullOrEmpty(instance.SourceTemplateId) ? "无" : instance.SourceTemplateId)}" +
              $"\n状态：{AgentDirectoryText.DescribeState(instance.IsEnabled, instance.IsFrozen)}" +
              $"\n主会话：{(instance.HasMainSession ? "已建立" : "未建立")} · 更新于 {instance.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm}";
        _agFreeze.Content = instance?.IsFrozen == true ? "解冻角色" : "冻结角色";
        _agInstanceDelete.IsEnabled = instance is not null;
        _agFreeze.IsEnabled = instance is not null;
    }

    private async Task CreateAgentInstanceAsync()
    {
        var create = new AgentInstanceCreate(AgentSelected(_agWorkspace) ?? "", _agInstanceName.Text.Trim(),
            _agInstanceDescription.Text.Trim(), _agentTemplate?.TemplateId ?? AgentSelected(_agTemplate) ?? "");
        var errors = AgentDirectoryText.Validate(create);
        if (errors.Count > 0) { ShowNotice(_agNotice, InfoBarSeverity.Warning, "请先修正表单", string.Join(" ", errors)); return; }
        await RunAgentAsync("角色已创建", "实例已按来源模板生成；角色标识与 Prompt 默认值来自模板。",
            async () => { await _agentDirectory.CreateInstanceAsync(create); await LoadAgentInstancesAsync(create.WorkspaceId); });
    }

    private async Task SaveAgentInstanceAsync()
    {
        var edit = new AgentInstanceEdit(AgentSelected(_agWorkspace) ?? "", _agentInstance?.AgentId ?? "",
            _agInstanceName.Text.Trim(), _agInstanceDescription.Text.Trim(), _agInstanceRole.Text.Trim(), _agInstanceEnabled.IsOn,
            AgentSelected(_agInstanceAvatar) ?? "");
        var errors = AgentDirectoryText.Validate(edit);
        if (errors.Count > 0) { ShowNotice(_agNotice, InfoBarSeverity.Warning, "请先修正表单", string.Join(" ", errors)); return; }
        await RunAgentAsync("角色基础信息已保存", "只替换了名称、描述、角色标识与启用状态；来源模板与 Prompt 覆盖保持原值。",
            async () => { await _agentDirectory.SaveInstanceAsync(edit); await LoadAgentInstancesAsync(edit.WorkspaceId); });
    }

    private async Task OnAgentDeleteInstanceAsync()
    {
        if (_agentInstance is not { } instance) return;
        var confirm = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = "删除角色实例",
            Content = $"将删除 {instance.DisplayName}（{instance.AgentId}）及其实例目录与会话数据。此操作不可撤销。",
            PrimaryButtonText = "删除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        var workspaceId = AgentSelected(_agWorkspace) ?? "";
        await RunAgentAsync("角色已删除", "实例目录已由 Core 清理。",
            async () => { await _agentDirectory.DeleteInstanceAsync(workspaceId, instance.AgentId); await LoadAgentInstancesAsync(workspaceId); });
    }

    private async Task ToggleAgentFreezeAsync()
    {
        if (_agentInstance is not { } instance) return;
        var frozen = !instance.IsFrozen;
        var workspaceId = AgentSelected(_agWorkspace) ?? "";
        if (frozen)
        {
            var confirm = new ContentDialog
            {
                XamlRoot = Root.XamlRoot, Title = "冻结角色",
                Content = $"冻结后 {instance.DisplayName} 不能接受新的执行；历史会话与数据保留。",
                PrimaryButtonText = "冻结", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        }
        await RunAgentAsync(frozen ? "角色已冻结" : "角色已解冻",
            frozen ? "该实例不再接受新的执行准入。" : "该实例恢复执行准入。",
            async () => { await _agentDirectory.SetInstanceFrozenAsync(workspaceId, instance.AgentId, frozen); await LoadAgentInstancesAsync(workspaceId); });
    }

    private async Task RunAgentAsync(string title, string message, Func<Task> action)
    {
        SetAgentEnabled(false);
        try
        {
            await action();
            SetAgentEnabled(true);
            ShowNotice(_agNotice, InfoBarSeverity.Success, title, message);
        }
        catch (Exception exception) { ReportAgentFailure(exception); }
    }

    private void ReportAgentFailure(Exception exception)
    {
        App.WriteDiagnostic(exception);
        var unavailable = exception is SettingsUnavailableException;
        var (severity, title, message) = exception switch
        {
            SettingsUnavailableException reason => (InfoBarSeverity.Informational, "Core 未就绪",
                reason.Message + " 该分类仍可浏览；表单已禁用，未写入任何内容。"),
            SettingsConflictException conflict => (InfoBarSeverity.Warning, "版本冲突", conflict.Message),
            ArgumentException argument => (InfoBarSeverity.Warning, "表单或配置被 Core 拒绝", argument.Message),
            InvalidOperationException invalid => (InfoBarSeverity.Warning, "操作未完成", invalid.Message),
            _ => (InfoBarSeverity.Error, "操作失败", "请查看诊断日志后重试。")
        };
        SetAgentEnabled(!unavailable);
        ShowNotice(_agNotice, severity, title, message);
    }

    private void SetAgentEnabled(bool enabled)
    {
        foreach (var control in new Control[]
        {
            _agWorkspace, _agTemplate, _agPreset, _agInstance, _agTemplateId, _agTemplateName, _agTemplateRole,
            _agTemplateDescription, _agTemplateAvatar, _agTemplateSort, _agTemplateEnabled, _agTemplateSave,
            _agImport, _agInstanceName, _agInstanceDescription, _agInstanceRole, _agInstanceAvatar, _agInstanceEnabled,
            _agInstanceCreate, _agInstanceSave
        }) control.IsEnabled = enabled;
        if (!enabled) { _agTemplateDelete.IsEnabled = false; _agInstanceDelete.IsEnabled = false; _agFreeze.IsEnabled = false; return; }
        _agTemplateDelete.IsEnabled = _agentTemplate is not null;
        _agInstanceDelete.IsEnabled = _agentInstance is not null;
        _agFreeze.IsEnabled = _agentInstance is not null;
    }
}
