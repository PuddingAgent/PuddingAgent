using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PuddingDesktop.Foundation;

namespace PuddingDesktop;

/// <summary>
/// DS-04 capability/skill grants — the tab that DS-06 (tool catalogue) and DS-07 (Skill Hub) unblocked.
///
/// Templates store the grants new instances inherit at creation. An instance then holds an independent
/// snapshot, so this page shows the template baseline and the instance's deviation instead of implying a
/// live inheritance link, and it keeps "unspecified" (keep the stored value) distinct from "no grants".
/// </summary>
public sealed partial class MainWindow
{
    private IReadOnlyList<AgentGrantOption> _grOptions = [];
    private List<string> _grTemplateCapabilities = [];
    private List<string> _grTemplateSkillPackages = [];
    private AgentInstanceGrantState? _grInstanceState;
    private bool _grBuilt;
    private bool _grSwitching;

    private ComboBox _grTemplatePicker = null!, _grOptionPicker = null!, _grGrantPicker = null!;
    private TextBox _grSearch = null!;
    private TextBlock _grTemplateSummary = null!, _grInstanceSummary = null!;
    private ComboBox _grWorkspacePicker = null!, _grInstancePicker = null!;
    private InfoBar _grNotice = null!;

    private void BuildAgentGrantPanel()
    {
        if (_grBuilt) return;
        _grBuilt = true;

        _grTemplatePicker = new ComboBox { Header = "模板", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_grTemplatePicker, "选择模板");
        _grTemplatePicker.SelectionChanged += async (_, _) => { if (!_grSwitching) await LoadTemplateGrantsAsync(); };
        _grSearch = Field("搜索授权项", "按 ID、名称或分类过滤");
        _grSearch.TextChanged += (_, _) => { if (!_grSwitching) FillGrantPickers(); };
        _grOptionPicker = new ComboBox { Header = "可授权项", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_grOptionPicker, "选择要添加的授权项");
        var add = new Button { Content = "添加授权" };
        add.Click += (_, _) => AddGrant();
        _grGrantPicker = new ComboBox { Header = "已授权项", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_grGrantPicker, "选择要移除的授权项");
        var remove = new Button { Content = "移除授权" };
        remove.Click += (_, _) => RemoveGrant();
        _grTemplateSummary = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = .8, FontSize = 12 };
        var save = new Button { Content = "保存模板授权" };
        save.Click += async (_, _) => await SaveTemplateGrantsAsync();
        _grNotice = new InfoBar { IsOpen = false, IsClosable = true };

        _grWorkspacePicker = new ComboBox { Header = "工作区", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_grWorkspacePicker, "授权所属工作区");
        _grWorkspacePicker.SelectionChanged += async (_, _) => { if (!_grSwitching) await LoadGrantInstancesAsync(); };
        _grInstancePicker = new ComboBox { Header = "角色实例", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_grInstancePicker, "选择角色实例");
        _grInstancePicker.SelectionChanged += async (_, _) => { if (!_grSwitching) await LoadInstanceGrantsAsync(); };
        _grInstanceSummary = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        var adopt = new Button { Content = "采用模板授权" };
        adopt.Click += async (_, _) => await WriteInstanceGrantsAsync(adoptTemplate: true);
        var clear = new Button { Content = "明确不授权" };
        clear.Click += async (_, _) => await WriteInstanceGrantsAsync(adoptTemplate: false);
        var keep = new Button { Content = "保持实例当前值" };
        keep.Click += async (_, _) => await WriteInstanceGrantsAsync(adoptTemplate: null);

        AgentCapabilitiesSettings.Content = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(0, 16, 8, 16),
            Children =
            {
                Card("模板授权", "模板记录能力（运行时工具）与技能包两类授权，新建实例时继承这份快照。搜索后可添加或移除。",
                    _grTemplatePicker, _grSearch, _grOptionPicker, Row(add), _grGrantPicker, Row(remove),
                    _grTemplateSummary, Row(save)),
                Card("实例授权", AgentGrantText.CreationInheritanceNotice,
                    _grWorkspacePicker, _grInstancePicker, _grInstanceSummary,
                    Row(adopt, clear, keep)),
                _grNotice
            }
        };
        SetAgentGrantEnabled(false);
    }

    internal async Task LoadAgentGrantsAsync()
    {
        if (!_grBuilt) return;
        try
        {
            var options = await _agentDirectory.ListGrantOptionsAsync();
            _grOptions = [.. options.Capabilities, .. options.SkillPackages];
            _grSwitching = true;
            try
            {
                var previous = AgentSelected(_grTemplatePicker);
                var templates = await _agentDirectory.ListTemplatesAsync();
                _grTemplatePicker.Items.Clear();
                foreach (var template in templates)
                    _grTemplatePicker.Items.Add(new ComboBoxItem { Content = $"{template.Name}（{template.TemplateId}）", Tag = template.TemplateId });
                var index = templates.ToList().FindIndex(template => template.TemplateId == previous);
                _grTemplatePicker.SelectedIndex = templates.Count > 0 ? Math.Max(0, index) : -1;

                var previousWorkspace = AgentSelected(_grWorkspacePicker);
                var workspaces = await _agentDirectory.ListWorkspacesAsync();
                _grWorkspacePicker.Items.Clear();
                foreach (var workspace in workspaces)
                    _grWorkspacePicker.Items.Add(new ComboBoxItem { Content = $"{workspace.Name}（{workspace.WorkspaceId}）", Tag = workspace.WorkspaceId });
                var wIndex = workspaces.ToList().FindIndex(workspace => workspace.WorkspaceId == previousWorkspace);
                _grWorkspacePicker.SelectedIndex = workspaces.Count > 0 ? Math.Max(0, wIndex) : -1;
            }
            finally { _grSwitching = false; }

            await LoadTemplateGrantsAsync();
            await LoadGrantInstancesAsync();
            SetAgentGrantEnabled(true);
            _grNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportAgentGrantFailure(exception); }
    }

    private async Task LoadTemplateGrantsAsync()
    {
        if (AgentSelected(_grTemplatePicker) is not { } templateId)
        {
            _grTemplateSummary.Text = "没有可用的模板。";
            return;
        }
        try
        {
            var grants = await _agentDirectory.ReadTemplateGrantsAsync(templateId);
            _grTemplateCapabilities = [.. grants.CapabilityIds];
            _grTemplateSkillPackages = [.. grants.SkillPackageIds];
            FillGrantPickers();
            _grNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportAgentGrantFailure(exception); }
    }

    private async Task LoadGrantInstancesAsync()
    {
        if (AgentSelected(_grWorkspacePicker) is not { } workspaceId)
        {
            _grInstancePicker.Items.Clear();
            _grInstanceState = null;
            _grInstanceSummary.Text = "没有可用的工作区。";
            return;
        }
        try
        {
            var instances = await _agentDirectory.ListInstancesAsync(workspaceId);
            _grSwitching = true;
            try
            {
                var previous = AgentSelected(_grInstancePicker);
                _grInstancePicker.Items.Clear();
                foreach (var instance in instances)
                    _grInstancePicker.Items.Add(new ComboBoxItem { Content = $"{instance.DisplayName}（{instance.AgentId}）", Tag = instance.AgentId });
                var index = instances.ToList().FindIndex(instance => instance.AgentId == previous);
                _grInstancePicker.SelectedIndex = instances.Count > 0 ? Math.Max(0, index) : -1;
            }
            finally { _grSwitching = false; }
            await LoadInstanceGrantsAsync();
        }
        catch (Exception exception) { ReportAgentGrantFailure(exception); }
    }

    private async Task LoadInstanceGrantsAsync()
    {
        var workspaceId = AgentSelected(_grWorkspacePicker);
        var agentId = AgentSelected(_grInstancePicker);
        if (string.IsNullOrEmpty(workspaceId) || string.IsNullOrEmpty(agentId))
        {
            _grInstanceState = null;
            _grInstanceSummary.Text = "请选择一个角色实例。";
            return;
        }
        try
        {
            _grInstanceState = await _agentDirectory.ReadInstanceGrantsAsync(workspaceId, agentId);
            var state = _grInstanceState;
            _grInstanceSummary.Text =
                $"{state.DisplayName}（{state.AgentId}）· 来源模板 {state.SourceTemplateId}\n" +
                $"实例授权：{AgentGrantText.DescribeSet(state.Grants, Options())}\n" +
                $"模板授权：{AgentGrantText.DescribeSet(state.TemplateGrants, Options())}\n" +
                $"{AgentGrantText.DescribeComparison(state.TemplateGrants, state.Grants)}\n\n" +
                AgentGrantText.CreationInheritanceNotice;
            _grNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportAgentGrantFailure(exception); }
    }

    private void FillGrantPickers()
    {
        var previous = AgentSelected(_grOptionPicker);
        var matching = _grOptions.Where(option => AgentGrantText.Matches(option, _grSearch.Text)).ToArray();
        _grSwitching = true;
        try
        {
            _grOptionPicker.Items.Clear();
            foreach (var option in matching)
                _grOptionPicker.Items.Add(new ComboBoxItem
                {
                    Content = $"{(option.IsCapability ? "能力" : "技能包")} · {option.Name}（{option.Id}）" +
                              (option.IsAvailable ? "" : " · 当前不可用"),
                    Tag = option.Id
                });
            var index = matching.ToList().FindIndex(option => option.Id == previous);
            _grOptionPicker.SelectedIndex = matching.Length > 0 ? Math.Max(0, index) : -1;

            var previousGrant = AgentSelected(_grGrantPicker);
            _grGrantPicker.Items.Clear();
            foreach (var id in _grTemplateCapabilities.Concat(_grTemplateSkillPackages))
            {
                var option = _grOptions.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
                var label = option is null
                    ? $"未知项（{id}）· 不在当前目录中"
                    : $"{(option.IsCapability ? "能力" : "技能包")} · {option.Name}（{id}）";
                _grGrantPicker.Items.Add(new ComboBoxItem { Content = label, Tag = id });
            }
            var grantIndex = _grGrantPicker.Items
                .Cast<ComboBoxItem>().ToList().FindIndex(item => (string?)item.Tag == previousGrant);
            _grGrantPicker.SelectedIndex = _grGrantPicker.Items.Count > 0 ? Math.Max(0, grantIndex) : -1;
        }
        finally { _grSwitching = false; }

        _grTemplateSummary.Text = _grTemplateCapabilities.Count + _grTemplateSkillPackages.Count == 0
            ? $"该模板没有任何授权（明确不授权）。可用项：{_grOptions.Count} 个。"
            : $"已授权 {_grTemplateCapabilities.Count} 项能力 · {_grTemplateSkillPackages.Count} 个技能包。可用项：{_grOptions.Count} 个。";
    }

    private void AddGrant()
    {
        if (AgentSelected(_grOptionPicker) is not { } id) { WarnAgentGrant("请先选择要添加的授权项"); return; }
        var option = _grOptions.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
        var target = option?.IsSkillPackage == true ? _grTemplateSkillPackages : _grTemplateCapabilities;
        if (target.Contains(id, StringComparer.OrdinalIgnoreCase)) { WarnAgentGrant($"{id} 已经在授权列表中"); return; }
        target.Add(id);
        FillGrantPickers();
        _grNotice.IsOpen = false;
    }

    private void RemoveGrant()
    {
        if (AgentSelected(_grGrantPicker) is not { } id) { WarnAgentGrant("请先选择要移除的授权项"); return; }
        // Unknown ids stay in whichever list holds them so removal never silently reassigns a grant.
        var removed = _grTemplateCapabilities.RemoveAll(value => string.Equals(value, id, StringComparison.OrdinalIgnoreCase))
                    + _grTemplateSkillPackages.RemoveAll(value => string.Equals(value, id, StringComparison.OrdinalIgnoreCase));
        if (removed == 0) { WarnAgentGrant($"{id} 不在授权列表中"); return; }
        FillGrantPickers();
        _grNotice.IsOpen = false;
    }

    private async Task SaveTemplateGrantsAsync()
    {
        if (AgentSelected(_grTemplatePicker) is not { } templateId) { WarnAgentGrant("请先选择模板"); return; }
        var grants = new AgentGrantSet(_grTemplateCapabilities, _grTemplateSkillPackages);
        var errors = AgentGrantText.Validate(grants, Options());
        if (errors.Count > 0)
        {
            ShowNotice(_grNotice, InfoBarSeverity.Warning, "授权项不在当前目录中", string.Join(" ", errors) + " 请先移除这些项。");
            return;
        }
        SetAgentGrantEnabled(false);
        try
        {
            await _agentDirectory.SaveTemplateGrantsAsync(templateId, grants);
            SetAgentGrantEnabled(true);
            ShowNotice(_grNotice, InfoBarSeverity.Success, "模板授权已保存",
                "新建实例时会继承这份授权；已存在的实例不受影响。");
        }
        catch (Exception exception) { ReportAgentGrantFailure(exception); }
    }

    private async Task WriteInstanceGrantsAsync(bool? adoptTemplate)
    {
        var workspaceId = AgentSelected(_grWorkspacePicker);
        var agentId = AgentSelected(_grInstancePicker);
        if (string.IsNullOrEmpty(workspaceId) || string.IsNullOrEmpty(agentId)) { WarnAgentGrant("请先选择角色实例"); return; }
        var template = _grInstanceState?.TemplateGrants ?? AgentGrantSet.Empty;
        var capabilities = adoptTemplate switch
        {
            true => AgentGrantSelection.Of(template.CapabilityIds),
            false => AgentGrantSelection.None,
            null => AgentGrantSelection.UnspecifiedSelection
        };
        var skillPackages = adoptTemplate switch
        {
            true => AgentGrantSelection.Of(template.SkillPackageIds),
            false => AgentGrantSelection.None,
            null => AgentGrantSelection.UnspecifiedSelection
        };
        SetAgentGrantEnabled(false);
        try
        {
            await _agentDirectory.SaveInstanceGrantsAsync(workspaceId, agentId, capabilities, skillPackages);
            await LoadInstanceGrantsAsync();
            SetAgentGrantEnabled(true);
            ShowNotice(_grNotice, InfoBarSeverity.Success,
                adoptTemplate switch { true => "已采用模板授权", false => "已明确不授权", null => "已保持实例当前值" },
                adoptTemplate switch
                {
                    true => "实例授权已写入模板的当前值。",
                    false => "实例授权已写入空列表（明确不授权），不会回退到模板。",
                    null => "Core 收到 null，实例授权保持原值。"
                });
        }
        catch (Exception exception) { ReportAgentGrantFailure(exception); }
    }

    private AgentGrantOptions Options() => new(
        [.. _grOptions.Where(option => option.IsCapability)],
        [.. _grOptions.Where(option => option.IsSkillPackage)]);

    private void WarnAgentGrant(string message) =>
        ShowNotice(_grNotice, InfoBarSeverity.Warning, "请先选择", message);

    private void ReportAgentGrantFailure(Exception exception)
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
        SetAgentGrantEnabled(!unavailable);
        ShowNotice(_grNotice, severity, title, message);
    }

    private void SetAgentGrantEnabled(bool enabled)
    {
        foreach (var control in new Control[]
        {
            _grTemplatePicker, _grSearch, _grOptionPicker, _grGrantPicker, _grWorkspacePicker, _grInstancePicker
        }) control.IsEnabled = enabled;
    }
}
