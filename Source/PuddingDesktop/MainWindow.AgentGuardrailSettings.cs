using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PuddingDesktop.Foundation;

namespace PuddingDesktop;

/// <summary>
/// DS-04 guardrail slice: execution budgets and the container image override, for the global template
/// and for a workspace role instance. The form does not invent bounds Core does not apply; it only
/// refuses values no budget could honour.
/// </summary>
public sealed partial class MainWindow
{
    private bool _ag2Built;
    private bool _ag2Switching;

    private ComboBox _agTemplatePicker = null!, _agWorkspacePicker = null!, _agInstancePicker = null!;
    private TextBox _agTemplateRounds = null!, _agTemplateSeconds = null!, _agTemplateTools = null!, _agTemplateImage = null!;
    private TextBox _agInstanceRounds = null!, _agInstanceSeconds = null!, _agInstanceTools = null!, _agInstanceImage = null!;
    private TextBlock _agTemplateSummary = null!, _agInstanceSummary = null!;
    private InfoBar _ag2Notice = null!;

    private void BuildAgentGuardrailPanel()
    {
        if (_ag2Built) return;
        _ag2Built = true;

        _agTemplatePicker = new ComboBox { Header = "全局模板", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_agTemplatePicker, "护栏所属模板");
        _agTemplatePicker.SelectionChanged += async (_, _) => { if (!_ag2Switching && AgentSelected(_agTemplatePicker) is { } id) await LoadTemplateGuardrailsAsync(id); };
        _agTemplateRounds = Field("最大轮次", "Core 默认 200");
        _agTemplateSeconds = Field("最长运行时长（秒）", "Core 默认 86400");
        _agTemplateTools = Field("工具调用总上限", "Core 默认 100");
        _agTemplateImage = Field("容器镜像（可选）", "留空表示不覆盖运行环境");
        _agTemplateSummary = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = .7, FontSize = 12 };
        var saveTemplate = new Button { Content = "保存模板护栏" };
        saveTemplate.Click += async (_, _) => await SaveTemplateGuardrailsAsync();
        var reloadTemplate = new Button { Content = "重新读取" };
        reloadTemplate.Click += async (_, _) => { if (AgentSelected(_agTemplatePicker) is { } id) await LoadTemplateGuardrailsAsync(id); };

        _agWorkspacePicker = new ComboBox { Header = "工作区", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_agWorkspacePicker, "护栏所属工作区");
        _agWorkspacePicker.SelectionChanged += async (_, _) => { if (!_ag2Switching && AgentSelected(_agWorkspacePicker) is { } id) await LoadGuardrailInstancesAsync(id); };
        _agInstancePicker = new ComboBox { Header = "角色实例", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_agInstancePicker, "护栏所属角色实例");
        _agInstancePicker.SelectionChanged += async (_, _) =>
        {
            if (!_ag2Switching && AgentSelected(_agWorkspacePicker) is { } w && AgentSelected(_agInstancePicker) is { } a) await LoadInstanceGuardrailsAsync(w, a);
        };
        _agInstanceRounds = Field("最大轮次", "创建时继承模板");
        _agInstanceSeconds = Field("最长运行时长（秒）", "创建时继承模板");
        _agInstanceTools = Field("工具调用总上限", "创建时继承模板");
        _agInstanceImage = Field("容器镜像（可选）", "留空表示不覆盖运行环境");
        _agInstanceSummary = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = .7, FontSize = 12 };
        var saveInstance = new Button { Content = "保存实例护栏" };
        saveInstance.Click += async (_, _) => await SaveInstanceGuardrailsAsync();
        var reloadInstance = new Button { Content = "重新读取" };
        reloadInstance.Click += async (_, _) =>
        {
            if (AgentSelected(_agWorkspacePicker) is { } w && AgentSelected(_agInstancePicker) is { } a) await LoadInstanceGuardrailsAsync(w, a);
        };

        _ag2Notice = new InfoBar { IsOpen = false, IsClosable = true };

        AgentGuardrailsSettings.Content = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(0, 16, 8, 16),
            Children =
            {
                Card("预算与运行环境（模板）",
                    "轮次、时长与工具调用是 Core 的执行预算，保存后立即对新建实例生效；" +
                    "容器镜像留空表示不使用镜像覆盖。Core 只做 ?? 继承，不在这些值上再加钳制，因此这里也不发明上限。",
                    _agTemplatePicker, Row(saveTemplate, reloadTemplate), _agTemplateSummary,
                    _agTemplateRounds, _agTemplateSeconds, _agTemplateTools, _agTemplateImage),
                Card("预算与运行环境（实例）",
                    "实例上的值覆盖模板默认值（新建实例时继承模板）。修改只会写入该实例，不会回写模板。",
                    _agWorkspacePicker, _agInstancePicker, Row(saveInstance, reloadInstance), _agInstanceSummary,
                    _agInstanceRounds, _agInstanceSeconds, _agInstanceTools, _agInstanceImage),
                _ag2Notice
            }
        };
        SetAgentGuardrailEnabled(false);
    }

    internal async Task LoadAgentGuardrailsAsync()
    {
        if (!_ag2Built) return;
        try
        {
            var templates = await _agentDirectory.ListTemplatesAsync();
            var workspaces = await _agentDirectory.ListWorkspacesAsync();
            _ag2Switching = true;
            try
            {
                var previousTemplate = AgentSelected(_agTemplatePicker);
                _agTemplatePicker.Items.Clear();
                foreach (var template in templates)
                    _agTemplatePicker.Items.Add(new ComboBoxItem { Content = $"{template.Name}（{template.TemplateId}）", Tag = template.TemplateId });
                var templateIndex = templates.ToList().FindIndex(template => template.TemplateId == previousTemplate);
                _agTemplatePicker.SelectedIndex = templates.Count > 0 ? Math.Max(0, templateIndex) : -1;

                var previousWorkspace = AgentSelected(_agWorkspacePicker);
                _agWorkspacePicker.Items.Clear();
                foreach (var workspace in workspaces)
                    _agWorkspacePicker.Items.Add(new ComboBoxItem { Content = $"{workspace.Name}（{workspace.WorkspaceId}）", Tag = workspace.WorkspaceId });
                var workspaceIndex = workspaces.ToList().FindIndex(workspace => workspace.WorkspaceId == previousWorkspace);
                _agWorkspacePicker.SelectedIndex = workspaces.Count > 0 ? Math.Max(0, workspaceIndex) : -1;
            }
            finally { _ag2Switching = false; }

            if (AgentSelected(_agTemplatePicker) is { } templateId) await LoadTemplateGuardrailsAsync(templateId);
            if (AgentSelected(_agWorkspacePicker) is { } workspaceId) await LoadGuardrailInstancesAsync(workspaceId);
            SetAgentGuardrailEnabled(true);
            _ag2Notice.IsOpen = false;
        }
        catch (Exception exception) { ReportAgentGuardrailFailure(exception); }
    }

    private async Task LoadGuardrailInstancesAsync(string workspaceId)
    {
        var instances = await _agentDirectory.ListInstancesAsync(workspaceId);
        _ag2Switching = true;
        try
        {
            _agInstancePicker.Items.Clear();
            foreach (var instance in instances)
                _agInstancePicker.Items.Add(new ComboBoxItem { Content = $"{instance.DisplayName}（{instance.AgentId}）", Tag = instance.AgentId });
            _agInstancePicker.SelectedIndex = instances.Count > 0 ? 0 : -1;
        }
        finally { _ag2Switching = false; }
        if (AgentSelected(_agInstancePicker) is { } agentId) await LoadInstanceGuardrailsAsync(workspaceId, agentId);
        else
        {
            FillGuardrails(_agInstanceRounds, _agInstanceSeconds, _agInstanceTools, _agInstanceImage, AgentGuardrailPolicy.Default);
            _agInstanceSummary.Text = "该工作区还没有角色实例。";
        }
    }

    private async Task LoadTemplateGuardrailsAsync(string templateId)
    {
        try
        {
            var policy = await _agentDirectory.ReadTemplateGuardrailsAsync(templateId);
            _ag2Switching = true;
            try
            {
                FillGuardrails(_agTemplateRounds, _agTemplateSeconds, _agTemplateTools, _agTemplateImage, policy);
                _agTemplateSummary.Text = $"模板 {templateId}：{policy.Describe()}";
            }
            finally { _ag2Switching = false; }
            _ag2Notice.IsOpen = false;
        }
        catch (Exception exception) { ReportAgentGuardrailFailure(exception); }
    }

    private async Task LoadInstanceGuardrailsAsync(string workspaceId, string agentId)
    {
        try
        {
            var policy = await _agentDirectory.ReadInstanceGuardrailsAsync(workspaceId, agentId);
            _ag2Switching = true;
            try
            {
                FillGuardrails(_agInstanceRounds, _agInstanceSeconds, _agInstanceTools, _agInstanceImage, policy);
                _agInstanceSummary.Text = $"实例 {agentId}：{policy.Describe()}";
            }
            finally { _ag2Switching = false; }
            _ag2Notice.IsOpen = false;
        }
        catch (Exception exception) { ReportAgentGuardrailFailure(exception); }
    }

    private static void FillGuardrails(TextBox rounds, TextBox seconds, TextBox tools, TextBox image, AgentGuardrailPolicy policy)
    {
        rounds.Text = policy.MaxRounds.ToString();
        seconds.Text = policy.MaxElapsedSeconds.ToString();
        tools.Text = policy.MaxToolCallsTotal.ToString();
        image.Text = policy.ContainerImage;
    }

    /// <summary>Unparseable text becomes 0 so validation reports it instead of silently keeping the old value.</summary>
    private static int ParseRequiredInt(string? text) =>
        int.TryParse((text ?? "").Trim(), out var value) ? value : 0;

    private static AgentGuardrailPolicy ReadGuardrails(TextBox rounds, TextBox seconds, TextBox tools, TextBox image) =>
        new(ParseRequiredInt(rounds.Text), ParseRequiredInt(seconds.Text), ParseRequiredInt(tools.Text),
            AgentGuardrailText.NormalizeContainerImage(image.Text));

    private async Task SaveTemplateGuardrailsAsync()
    {
        if (AgentSelected(_agTemplatePicker) is not { } templateId) return;
        var policy = ReadGuardrails(_agTemplateRounds, _agTemplateSeconds, _agTemplateTools, _agTemplateImage);
        var errors = AgentGuardrailText.Validate(policy);
        if (errors.Count > 0) { ShowNotice(_ag2Notice, InfoBarSeverity.Warning, "请先修正表单", string.Join(" ", errors)); return; }
        await RunAgentGuardrailAsync("模板护栏已保存", "只写入了模板；实例的覆盖值与其它字段未被改动。",
            async () => { await _agentDirectory.SaveTemplateGuardrailsAsync(templateId, policy); await LoadTemplateGuardrailsAsync(templateId); });
    }

    private async Task SaveInstanceGuardrailsAsync()
    {
        if (AgentSelected(_agWorkspacePicker) is not { } workspaceId || AgentSelected(_agInstancePicker) is not { } agentId)
        {
            ShowNotice(_ag2Notice, InfoBarSeverity.Warning, "请先选择实例", "实例护栏需要先选择工作区与角色。");
            return;
        }
        var policy = ReadGuardrails(_agInstanceRounds, _agInstanceSeconds, _agInstanceTools, _agInstanceImage);
        var errors = AgentGuardrailText.Validate(policy);
        if (errors.Count > 0) { ShowNotice(_ag2Notice, InfoBarSeverity.Warning, "请先修正表单", string.Join(" ", errors)); return; }
        await RunAgentGuardrailAsync("实例护栏已保存", "模板默认值未被修改；Smart 路由与基础资料保持原值。",
            async () => { await _agentDirectory.SaveInstanceGuardrailsAsync(workspaceId, agentId, policy); await LoadInstanceGuardrailsAsync(workspaceId, agentId); });
    }

    private async Task RunAgentGuardrailAsync(string title, string message, Func<Task> action)
    {
        SetAgentGuardrailEnabled(false);
        try
        {
            await action();
            SetAgentGuardrailEnabled(true);
            ShowNotice(_ag2Notice, InfoBarSeverity.Success, title, message);
        }
        catch (Exception exception) { ReportAgentGuardrailFailure(exception); }
    }

    private void ReportAgentGuardrailFailure(Exception exception)
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
        SetAgentGuardrailEnabled(!unavailable);
        ShowNotice(_ag2Notice, severity, title, message);
    }

    private void SetAgentGuardrailEnabled(bool enabled)
    {
        foreach (var control in new Control[]
        {
            _agTemplatePicker, _agTemplateRounds, _agTemplateSeconds, _agTemplateTools, _agTemplateImage,
            _agWorkspacePicker, _agInstancePicker, _agInstanceRounds, _agInstanceSeconds, _agInstanceTools, _agInstanceImage
        }) control.IsEnabled = enabled;
    }
}
