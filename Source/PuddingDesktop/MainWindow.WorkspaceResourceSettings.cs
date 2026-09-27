using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PuddingDesktop.Foundation;

namespace PuddingDesktop;

/// <summary>
/// DS-05 resource cards: knowledge bases, workspace skills and workflows, all scoped to one workspace.
/// MCP config and workflow definitions are validated by Core; the page only pre-checks JSON shape.
/// </summary>
public sealed partial class MainWindow
{
    private IReadOnlyList<KnowledgeBaseSummary> _wrKnowledge = [];
    private IReadOnlyList<WorkspaceSkillSummary> _wrSkills = [];
    private IReadOnlyList<WorkspaceWorkflowSummary> _wrWorkflows = [];
    private KnowledgeBaseSummary? _wrKb;
    private WorkspaceSkillSummary? _wrSkill;
    private WorkspaceWorkflowSummary? _wrWorkflow;
    private bool _wrBuilt;
    private bool _wrSwitching;

    private ComboBox _wrWorkspacePicker = null!;
    private ComboBox _wrKbPicker = null!, _wrKbType = null!;
    private TextBox _wrKbName = null!, _wrKbDescription = null!;
    private ToggleSwitch _wrKbEnabled = null!;
    private TextBlock _wrKbDetail = null!;
    private ComboBox _wrSkillPicker = null!, _wrSkillType = null!;
    private TextBox _wrSkillName = null!, _wrSkillDescription = null!, _wrSkillConfig = null!;
    private ToggleSwitch _wrSkillEnabled = null!;
    private TextBlock _wrSkillDetail = null!;
    private ComboBox _wrWorkflowPicker = null!, _wrWorkflowStatus = null!;
    private TextBox _wrWorkflowName = null!, _wrWorkflowDescription = null!, _wrWorkflowDefinition = null!;
    private ToggleSwitch _wrWorkflowEnabled = null!;
    private TextBlock _wrWorkflowDetail = null!;
    private Button _wrKbDelete = null!, _wrSkillDelete = null!, _wrWorkflowDelete = null!;
    private InfoBar _wrNotice = null!;

    private void BuildWorkspaceResourcePanel()
    {
        if (_wrBuilt) return;
        _wrBuilt = true;

        _wrWorkspacePicker = new ComboBox { Header = "工作区", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_wrWorkspacePicker, "资源所属工作区");
        _wrWorkspacePicker.SelectionChanged += async (_, _) => { if (!_wrSwitching) await LoadWorkspaceResourcesAsync(); };
        _wrNotice = new InfoBar { IsOpen = false, IsClosable = true };

        _wrKbPicker = new ComboBox { Header = "知识库", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_wrKbPicker, "选择知识库");
        _wrKbPicker.SelectionChanged += (_, _) => { if (!_wrSwitching) ApplyKnowledgeBaseSelection(); };
        _wrKbName = Field("名称");
        _wrKbDescription = Field("描述");
        _wrKbType = VocabularyPicker("kbType", WorkspaceResourceText.KnowledgeBaseTypes);
        _wrKbEnabled = new ToggleSwitch { Header = "启用", OnContent = "已启用", OffContent = "已停用", IsOn = true };
        _wrKbDetail = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 12 };
        var kbSave = new Button { Content = "保存知识库" };
        kbSave.Click += async (_, _) => await SaveKnowledgeBaseAsync();
        _wrKbDelete = new Button { Content = "删除知识库" };
        _wrKbDelete.Click += async (_, _) => await DeleteKnowledgeBaseAsync();

        _wrSkillPicker = new ComboBox { Header = "工作区技能", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_wrSkillPicker, "选择工作区技能");
        _wrSkillPicker.SelectionChanged += (_, _) => { if (!_wrSwitching) ApplySkillSelection(); };
        _wrSkillName = Field("名称");
        _wrSkillDescription = Field("描述");
        _wrSkillType = VocabularyPicker("skillType", WorkspaceResourceText.SkillTypes);
        _wrSkillConfig = new TextBox
        {
            Header = "configJson（MCP 必填且必须合法）", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            Height = 120, HorizontalAlignment = HorizontalAlignment.Stretch,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas")
        };
        _wrSkillEnabled = new ToggleSwitch { Header = "启用", OnContent = "已启用", OffContent = "已停用", IsOn = true };
        _wrSkillDetail = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 12 };
        var skillSave = new Button { Content = "保存技能" };
        skillSave.Click += async (_, _) => await SaveSkillAsync();
        _wrSkillDelete = new Button { Content = "删除技能" };
        _wrSkillDelete.Click += async (_, _) => await DeleteSkillAsync();

        _wrWorkflowPicker = new ComboBox { Header = "工作流", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_wrWorkflowPicker, "选择工作流");
        _wrWorkflowPicker.SelectionChanged += (_, _) => { if (!_wrSwitching) ApplyWorkflowSelection(); };
        _wrWorkflowName = Field("名称");
        _wrWorkflowDescription = Field("描述");
        _wrWorkflowStatus = VocabularyPicker("状态", WorkspaceResourceText.WorkflowStatuses);
        _wrWorkflowDefinition = new TextBox
        {
            Header = "definitionJson（可留空）", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            Height = 120, HorizontalAlignment = HorizontalAlignment.Stretch,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas")
        };
        _wrWorkflowEnabled = new ToggleSwitch { Header = "启用", OnContent = "已启用", OffContent = "已停用", IsOn = true };
        _wrWorkflowDetail = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 12 };
        var workflowSave = new Button { Content = "保存工作流" };
        workflowSave.Click += async (_, _) => await SaveWorkflowAsync();
        _wrWorkflowDelete = new Button { Content = "删除工作流" };
        _wrWorkflowDelete.Click += async (_, _) => await DeleteWorkflowAsync();

        WorkspaceResourcesSettings.Content = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(0, 16, 8, 16),
            Children =
            {
                Card("作用范围", "下面三张卡都属于所选工作区；跨工作区的资源 ID 会被 Core 拒绝。", _wrWorkspacePicker),
                Card("知识库", WorkspaceResourceText.KnowledgeBaseNotice + " 资源 ID 留空表示新建。",
                    _wrKbPicker, _wrKbName, _wrKbDescription, _wrKbType, _wrKbEnabled, _wrKbDetail,
                    Row(kbSave, _wrKbDelete)),
                Card("工作区技能", WorkspaceResourceText.McpConfigNotice + " 资源 ID 留空表示新建。",
                    _wrSkillPicker, _wrSkillName, _wrSkillDescription, _wrSkillType, _wrSkillConfig, _wrSkillEnabled,
                    _wrSkillDetail, Row(skillSave, _wrSkillDelete)),
                Card("工作流", WorkspaceResourceText.WorkflowNotice + " 资源 ID 留空表示新建。",
                    _wrWorkflowPicker, _wrWorkflowName, _wrWorkflowDescription, _wrWorkflowStatus,
                    _wrWorkflowDefinition, _wrWorkflowEnabled, _wrWorkflowDetail, Row(workflowSave, _wrWorkflowDelete)),
                _wrNotice
            }
        };
        SetWorkspaceResourceEnabled(false);
    }

    private static ComboBox VocabularyPicker(string header, IReadOnlyList<string> values)
    {
        var picker = new ComboBox { Header = header, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var value in values) picker.Items.Add(new ComboBoxItem { Content = value, Tag = value });
        picker.SelectedIndex = 0;
        return picker;
    }

    internal async Task LoadWorkspaceResourcesAsync()
    {
        if (!_wrBuilt) return;
        try
        {
            if (AgentSelected(_wrWorkspacePicker) is not { } workspaceId)
            {
                var workspaces = await _agentDirectory.ListWorkspacesAsync();
                _wrSwitching = true;
                try
                {
                    _wrWorkspacePicker.Items.Clear();
                    foreach (var workspace in workspaces)
                        _wrWorkspacePicker.Items.Add(new ComboBoxItem
                        {
                            Content = $"{workspace.Name}（{workspace.WorkspaceId}）", Tag = workspace.WorkspaceId
                        });
                    _wrWorkspacePicker.SelectedIndex = workspaces.Count > 0 ? 0 : -1;
                }
                finally { _wrSwitching = false; }
                if (AgentSelected(_wrWorkspacePicker) is not { } selected)
                {
                    _wrNotice.IsOpen = false;
                    return;
                }
                workspaceId = selected;
            }

            _wrKnowledge = await _workspaceResources.ListKnowledgeBasesAsync(workspaceId);
            _wrSkills = await _workspaceResources.ListSkillsAsync(workspaceId);
            _wrWorkflows = await _workspaceResources.ListWorkflowsAsync(workspaceId);
            FillResourcePickers();
            SetWorkspaceResourceEnabled(true);
            _wrNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportWorkspaceResourceFailure(exception); }
    }

    private void FillResourcePickers()
    {
        _wrSwitching = true;
        try
        {
            Fill(_wrKbPicker, _wrKnowledge.Select(kb => (kb.KbId, $"{kb.Name} · {kb.TypeText} · {kb.StateText}")), _wrKb?.KbId);
            Fill(_wrSkillPicker, _wrSkills.Select(skill => (skill.SkillId, $"{skill.Name} · {skill.TypeText} · {skill.StateText}")), _wrSkill?.SkillId);
            Fill(_wrWorkflowPicker, _wrWorkflows.Select(flow => (flow.WorkflowId, $"{flow.Name} · {flow.StatusText} · {flow.StateText}")), _wrWorkflow?.WorkflowId);

            static void Fill(ComboBox picker, IEnumerable<(string Id, string Label)> items, string? previous)
            {
                picker.Items.Clear();
                picker.Items.Add(new ComboBoxItem { Content = "＋ 新建（ID 留空）", Tag = "" });
                var list = items.ToArray();
                foreach (var (id, label) in list) picker.Items.Add(new ComboBoxItem { Content = label, Tag = id });
                var index = Array.FindIndex(list, item => item.Id == previous);
                picker.SelectedIndex = index >= 0 ? index + 1 : 0;
            }
        }
        finally { _wrSwitching = false; }

        ApplyKnowledgeBaseSelection();
        ApplySkillSelection();
        ApplyWorkflowSelection();
    }

    private void ApplyKnowledgeBaseSelection()
    {
        _wrKb = _wrKnowledge.FirstOrDefault(kb => kb.KbId == AgentSelected(_wrKbPicker));
        _wrKbName.Text = _wrKb?.Name ?? "";
        _wrKbDescription.Text = _wrKb?.Description ?? "";
        _wrKbEnabled.IsOn = _wrKb?.IsEnabled ?? true;
        _wrKbType.SelectedIndex = Math.Max(0, Index(WorkspaceResourceText.KnowledgeBaseTypes, _wrKb?.KbType));
        _wrKbDetail.Text = _wrKb is null
            ? $"新建知识库。当前工作区已有 {_wrKnowledge.Count} 个。"
            : $"ID {_wrKb.KbId} · 文档 {_wrKb.DocumentCount} 份（Core 统计）· " +
              $"创建 {_wrKb.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm} · 更新 {_wrKb.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm}";
        _wrKbDelete.IsEnabled = _wrKb is not null;
    }

    private void ApplySkillSelection()
    {
        _wrSkill = _wrSkills.FirstOrDefault(skill => skill.SkillId == AgentSelected(_wrSkillPicker));
        _wrSkillName.Text = _wrSkill?.Name ?? "";
        _wrSkillDescription.Text = _wrSkill?.Description ?? "";
        _wrSkillConfig.Text = _wrSkill?.ConfigJson ?? "";
        _wrSkillEnabled.IsOn = _wrSkill?.IsEnabled ?? true;
        _wrSkillType.SelectedIndex = Math.Max(0, Index(WorkspaceResourceText.SkillTypes, _wrSkill?.SkillType));
        _wrSkillDetail.Text = _wrSkill is null
            ? $"新建技能。当前工作区已有 {_wrSkills.Count} 个。"
            : $"ID {_wrSkill.SkillId} · 类型 {_wrSkill.TypeText}" + (_wrSkill.IsMcp ? "（configJson 由 Core 解析）" : "") +
              $" · 更新 {_wrSkill.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm}";
        _wrSkillDelete.IsEnabled = _wrSkill is not null;
    }

    private void ApplyWorkflowSelection()
    {
        _wrWorkflow = _wrWorkflows.FirstOrDefault(flow => flow.WorkflowId == AgentSelected(_wrWorkflowPicker));
        _wrWorkflowName.Text = _wrWorkflow?.Name ?? "";
        _wrWorkflowDescription.Text = _wrWorkflow?.Description ?? "";
        _wrWorkflowDefinition.Text = _wrWorkflow?.DefinitionJson ?? "";
        _wrWorkflowEnabled.IsOn = _wrWorkflow?.IsEnabled ?? true;
        _wrWorkflowStatus.SelectedIndex = Math.Max(0, Index(WorkspaceResourceText.WorkflowStatuses, _wrWorkflow?.Status));
        _wrWorkflowDetail.Text = _wrWorkflow is null
            ? $"新建工作流。当前工作区已有 {_wrWorkflows.Count} 个。"
            : $"ID {_wrWorkflow.WorkflowId} · 状态 {_wrWorkflow.StatusText} · " +
              (_wrWorkflow.DefinitionJson.Length == 0 ? "没有定义 JSON" : $"定义 {_wrWorkflow.DefinitionJson.Length} 字符") +
              $" · 更新 {_wrWorkflow.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm}";
        _wrWorkflowDelete.IsEnabled = _wrWorkflow is not null;
    }

    private static int Index(IReadOnlyList<string> values, string? value) =>
        value is null ? -1 : values.ToList().FindIndex(item => string.Equals(item, value, StringComparison.OrdinalIgnoreCase));

    private async Task SaveKnowledgeBaseAsync()
    {
        if (AgentSelected(_wrWorkspacePicker) is not { } workspaceId) { WarnWorkspaceResource("请先选择工作区"); return; }
        var edit = new KnowledgeBaseEdit(workspaceId, _wrKb?.KbId ?? "", _wrKbName.Text.Trim(),
            _wrKbDescription.Text.Trim(), AgentSelected(_wrKbType) ?? "VectorStore", _wrKbEnabled.IsOn);
        var errors = WorkspaceResourceText.Validate(edit);
        if (errors.Count > 0) { ShowNotice(_wrNotice, InfoBarSeverity.Warning, "请先修正表单", string.Join(" ", errors)); return; }
        await RunWorkspaceResourceAsync(_wrKb is null ? "知识库已创建" : "知识库已保存", "文档数量由 Core 统计，界面不写入。",
            async () => { await _workspaceResources.SaveKnowledgeBaseAsync(edit); await LoadWorkspaceResourcesAsync(); });
    }

    private async Task DeleteKnowledgeBaseAsync()
    {
        if (_wrKb is not { } kb || AgentSelected(_wrWorkspacePicker) is not { } workspaceId) { WarnWorkspaceResource("请先选择知识库"); return; }
        if (!await ConfirmResourceDeleteAsync("删除知识库", kb.Name, "该知识库的索引与文档关联将一并失效。")) return;
        await RunWorkspaceResourceAsync("知识库已删除", "资源已从该工作区移除。",
            async () => { await _workspaceResources.DeleteKnowledgeBaseAsync(workspaceId, kb.KbId); await LoadWorkspaceResourcesAsync(); });
    }

    private async Task SaveSkillAsync()
    {
        if (AgentSelected(_wrWorkspacePicker) is not { } workspaceId) { WarnWorkspaceResource("请先选择工作区"); return; }
        var edit = new WorkspaceSkillEdit(workspaceId, _wrSkill?.SkillId ?? "", _wrSkillName.Text.Trim(),
            _wrSkillDescription.Text.Trim(), AgentSelected(_wrSkillType) ?? "BuiltIn", _wrSkillConfig.Text.Trim(),
            _wrSkillEnabled.IsOn);
        var errors = WorkspaceResourceText.Validate(edit);
        if (errors.Count > 0) { ShowNotice(_wrNotice, InfoBarSeverity.Warning, "请先修正表单", string.Join(" ", errors)); return; }
        await RunWorkspaceResourceAsync(_wrSkill is null ? "技能已创建" : "技能已保存",
            string.Equals(edit.SkillType, "MCP", StringComparison.OrdinalIgnoreCase)
                ? "MCP 配置已交给 Core 解析；若被拒绝会显示真实原因。"
                : "非 MCP 技能的类型与配置按原样保存。",
            async () => { await _workspaceResources.SaveSkillAsync(edit); await LoadWorkspaceResourcesAsync(); });
    }

    private async Task DeleteSkillAsync()
    {
        if (_wrSkill is not { } skill || AgentSelected(_wrWorkspacePicker) is not { } workspaceId) { WarnWorkspaceResource("请先选择技能"); return; }
        if (!await ConfirmResourceDeleteAsync("删除技能", skill.Name, "删除后该技能不再对该工作区可用。")) return;
        await RunWorkspaceResourceAsync("技能已删除", "资源已从该工作区移除。",
            async () => { await _workspaceResources.DeleteSkillAsync(workspaceId, skill.SkillId); await LoadWorkspaceResourcesAsync(); });
    }

    private async Task SaveWorkflowAsync()
    {
        if (AgentSelected(_wrWorkspacePicker) is not { } workspaceId) { WarnWorkspaceResource("请先选择工作区"); return; }
        var edit = new WorkspaceWorkflowEdit(workspaceId, _wrWorkflow?.WorkflowId ?? "", _wrWorkflowName.Text.Trim(),
            _wrWorkflowDescription.Text.Trim(), _wrWorkflowDefinition.Text.Trim(),
            AgentSelected(_wrWorkflowStatus) ?? "Draft", _wrWorkflowEnabled.IsOn);
        var errors = WorkspaceResourceText.Validate(edit);
        if (errors.Count > 0) { ShowNotice(_wrNotice, InfoBarSeverity.Warning, "请先修正表单", string.Join(" ", errors)); return; }
        await RunWorkspaceResourceAsync(_wrWorkflow is null ? "工作流已创建" : "工作流已保存",
            edit.DefinitionJson.Length == 0 ? "定义留空，Core 会原样保存（没有定义）。" : "定义 JSON 已保存。",
            async () => { await _workspaceResources.SaveWorkflowAsync(edit); await LoadWorkspaceResourcesAsync(); });
    }

    private async Task DeleteWorkflowAsync()
    {
        if (_wrWorkflow is not { } workflow || AgentSelected(_wrWorkspacePicker) is not { } workspaceId) { WarnWorkspaceResource("请先选择工作流"); return; }
        if (!await ConfirmResourceDeleteAsync("删除工作流", workflow.Name, "删除后该工作流定义不再保留。")) return;
        await RunWorkspaceResourceAsync("工作流已删除", "资源已从该工作区移除。",
            async () => { await _workspaceResources.DeleteWorkflowAsync(workspaceId, workflow.WorkflowId); await LoadWorkspaceResourcesAsync(); });
    }

    private async Task<bool> ConfirmResourceDeleteAsync(string title, string name, string consequence)
    {
        var confirm = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = title,
            Content = $"将删除 {name}。{consequence}此操作不可撤销。",
            PrimaryButtonText = "删除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
        };
        return await confirm.ShowAsync() == ContentDialogResult.Primary;
    }

    private void WarnWorkspaceResource(string message) =>
        ShowNotice(_wrNotice, InfoBarSeverity.Warning, "请先选择", message);

    private async Task RunWorkspaceResourceAsync(string title, string message, Func<Task> action)
    {
        SetWorkspaceResourceEnabled(false);
        try
        {
            await action();
            SetWorkspaceResourceEnabled(true);
            ShowNotice(_wrNotice, InfoBarSeverity.Success, title, message);
        }
        catch (Exception exception) { ReportWorkspaceResourceFailure(exception); }
    }

    private void ReportWorkspaceResourceFailure(Exception exception)
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
        SetWorkspaceResourceEnabled(!unavailable);
        ShowNotice(_wrNotice, severity, title, message);
    }

    private void SetWorkspaceResourceEnabled(bool enabled)
    {
        foreach (var control in new Control[]
        {
            _wrWorkspacePicker, _wrKbPicker, _wrKbName, _wrKbDescription, _wrKbType, _wrKbEnabled,
            _wrSkillPicker, _wrSkillName, _wrSkillDescription, _wrSkillType, _wrSkillConfig, _wrSkillEnabled,
            _wrWorkflowPicker, _wrWorkflowName, _wrWorkflowDescription, _wrWorkflowStatus, _wrWorkflowDefinition,
            _wrWorkflowEnabled
        }) control.IsEnabled = enabled;
        if (!enabled)
        {
            _wrKbDelete.IsEnabled = false;
            _wrSkillDelete.IsEnabled = false;
            _wrWorkflowDelete.IsEnabled = false;
            return;
        }
        _wrKbDelete.IsEnabled = _wrKb is not null;
        _wrSkillDelete.IsEnabled = _wrSkill is not null;
        _wrWorkflowDelete.IsEnabled = _wrWorkflow is not null;
    }
}
