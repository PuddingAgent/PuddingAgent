using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PuddingDesktop.Foundation;

namespace PuddingDesktop;

/// <summary>
/// DS-04 model &amp; memory slice: chat / memory / embedding provider-model pairs, memory search mode and
/// reasoning effort, for the global template and for a workspace role instance. Stored values missing
/// from the live catalogue are preserved and shown instead of being silently rewritten.
/// </summary>
public sealed partial class MainWindow
{
    private sealed record ModelPairControls(ComboBox Provider, ComboBox Model, bool Embedding);

    private IReadOnlyList<AgentModelCatalogEntry> _amCatalog = [];
    private bool _amBuilt;
    private bool _amSwitching;

    private ComboBox _amTemplate = null!, _amMode = null!, _amWorkspace = null!, _amInstance = null!;
    private ModelPairControls _amTemplateChat = null!, _amTemplateMemory = null!, _amTemplateEmbedding = null!;
    private ModelPairControls _amInstanceChat = null!, _amInstanceMemory = null!, _amInstanceEmbedding = null!;
    private ComboBox _amInstanceMode = null!;
    private TextBox _amEffort = null!, _amInstanceEffort = null!;
    private TextBlock _amTemplateInfo = null!, _amInstanceInfo = null!;
    private InfoBar _amNotice = null!;

    private void BuildAgentModelPanel()
    {
        if (_amBuilt) return;
        _amBuilt = true;

        _amTemplate = new ComboBox { Header = "全局模板", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_amTemplate, "模型策略所属模板");
        _amTemplate.SelectionChanged += async (_, _) => { if (!_amSwitching && AgentSelected(_amTemplate) is { } id) await LoadTemplateModelPolicyAsync(id); };
        _amTemplateChat = BuildPair("默认对话模型", embedding: false);
        _amTemplateMemory = BuildPair("记忆模型", embedding: false);
        _amTemplateEmbedding = BuildPair("Embedding 模型", embedding: true);
        _amMode = BuildModePicker("记忆检索模式");
        _amEffort = Field("推理强度（可选）", "留空表示不设置；取值由服务商约定，例如 low / medium / high / max");
        _amTemplateInfo = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = .7, FontSize = 12 };
        var saveTemplate = new Button { Content = "保存模板模型策略" };
        saveTemplate.Click += async (_, _) => await SaveTemplateModelPolicyAsync();
        var reloadTemplate = new Button { Content = "重新读取" };
        reloadTemplate.Click += async (_, _) => { if (AgentSelected(_amTemplate) is { } id) await LoadTemplateModelPolicyAsync(id); };

        _amWorkspace = new ComboBox { Header = "工作区", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_amWorkspace, "模型策略所属工作区");
        _amWorkspace.SelectionChanged += async (_, _) => { if (!_amSwitching && AgentSelected(_amWorkspace) is { } id) await LoadModelPolicyInstancesAsync(id); };
        _amInstance = new ComboBox { Header = "角色实例", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_amInstance, "模型策略所属角色实例");
        _amInstance.SelectionChanged += async (_, _) =>
        {
            if (!_amSwitching && AgentSelected(_amWorkspace) is { } w && AgentSelected(_amInstance) is { } a) await LoadInstanceModelPolicyAsync(w, a);
        };
        _amInstanceChat = BuildPair("默认对话模型（覆盖）", embedding: false);
        _amInstanceMemory = BuildPair("记忆模型（覆盖）", embedding: false);
        _amInstanceEmbedding = BuildPair("Embedding 模型（覆盖）", embedding: true);
        _amInstanceMode = BuildModePicker("记忆检索模式（覆盖）");
        _amInstanceEffort = Field("推理强度（覆盖，可选）", "留空表示继承模板");
        _amInstanceInfo = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = .7, FontSize = 12 };
        var saveInstance = new Button { Content = "保存实例模型策略" };
        saveInstance.Click += async (_, _) => await SaveInstanceModelPolicyAsync();
        var reloadInstance = new Button { Content = "重新读取" };
        reloadInstance.Click += async (_, _) =>
        {
            if (AgentSelected(_amWorkspace) is { } w && AgentSelected(_amInstance) is { } a) await LoadInstanceModelPolicyAsync(w, a);
        };

        _amNotice = new InfoBar { IsOpen = false, IsClosable = true };

        AgentModelsSettings.Content = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(0, 16, 8, 16),
            Children =
            {
                Card("模板默认模型与记忆策略",
                    "模板是全局默认值；这里保存只写入模板本身。留空表示不指定而由运行时按服务商默认解析；" +
                    "服务商与模型必须同时指定或同时留空。",
                    _amTemplate, Row(saveTemplate, reloadTemplate), _amTemplateInfo,
                    _amTemplateChat.Provider, _amTemplateChat.Model,
                    _amTemplateMemory.Provider, _amTemplateMemory.Model,
                    _amTemplateEmbedding.Provider, _amTemplateEmbedding.Model,
                    _amMode, _amEffort),
                Card("实例模型与记忆策略",
                    "实例上的选择会覆盖模板默认值；留空表示继承模板。embedding 选择只接受 embedding 模型，" +
                    "对话与记忆选择只接受非 embedding 模型。",
                    _amWorkspace, _amInstance, Row(saveInstance, reloadInstance), _amInstanceInfo,
                    _amInstanceChat.Provider, _amInstanceChat.Model,
                    _amInstanceMemory.Provider, _amInstanceMemory.Model,
                    _amInstanceEmbedding.Provider, _amInstanceEmbedding.Model,
                    _amInstanceMode, _amInstanceEffort),
                _amNotice
            }
        };
        SetAgentModelEnabled(false);
    }

    private ComboBox BuildModePicker(string header)
    {
        var picker = new ComboBox { Header = header, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(picker, header);
        foreach (var mode in AgentModelPolicyText.MemorySearchModes) picker.Items.Add(new ComboBoxItem { Content = mode, Tag = mode });
        picker.SelectedIndex = AgentModelPolicyText.MemorySearchModes.ToList().IndexOf(AgentModelPolicyText.DefaultMemorySearchMode);
        return picker;
    }

    private ModelPairControls BuildPair(string header, bool embedding)
    {
        var provider = new ComboBox { Header = header + " · 服务商", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(provider, header + "服务商");
        var model = new ComboBox { Header = header + " · 模型", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(model, header + "模型");
        var pair = new ModelPairControls(provider, model, embedding);
        provider.SelectionChanged += (_, _) => { if (!_amSwitching) FillPairModels(pair); };
        return pair;
    }

    internal async Task LoadAgentModelsAsync()
    {
        if (!_amBuilt) return;
        try
        {
            _amCatalog = await _agentDirectory.ListModelCatalogAsync();
            var templates = await _agentDirectory.ListTemplatesAsync();
            var workspaces = await _agentDirectory.ListWorkspacesAsync();
            _amSwitching = true;
            try
            {
                var previousTemplate = AgentSelected(_amTemplate);
                _amTemplate.Items.Clear();
                foreach (var template in templates)
                    _amTemplate.Items.Add(new ComboBoxItem { Content = $"{template.Name}（{template.TemplateId}）", Tag = template.TemplateId });
                var templateIndex = templates.ToList().FindIndex(template => template.TemplateId == previousTemplate);
                _amTemplate.SelectedIndex = templates.Count > 0 ? Math.Max(0, templateIndex) : -1;

                var previousWorkspace = AgentSelected(_amWorkspace);
                _amWorkspace.Items.Clear();
                foreach (var workspace in workspaces)
                    _amWorkspace.Items.Add(new ComboBoxItem { Content = $"{workspace.Name}（{workspace.WorkspaceId}）", Tag = workspace.WorkspaceId });
                var workspaceIndex = workspaces.ToList().FindIndex(workspace => workspace.WorkspaceId == previousWorkspace);
                _amWorkspace.SelectedIndex = workspaces.Count > 0 ? Math.Max(0, workspaceIndex) : -1;
            }
            finally { _amSwitching = false; }

            if (AgentSelected(_amTemplate) is { } templateId) await LoadTemplateModelPolicyAsync(templateId);
            if (AgentSelected(_amWorkspace) is { } workspaceId) await LoadModelPolicyInstancesAsync(workspaceId);
            SetAgentModelEnabled(true);
            _amNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportAgentModelFailure(exception); }
    }

    private async Task LoadModelPolicyInstancesAsync(string workspaceId)
    {
        var instances = await _agentDirectory.ListInstancesAsync(workspaceId);
        _amSwitching = true;
        try
        {
            _amInstance.Items.Clear();
            foreach (var instance in instances)
                _amInstance.Items.Add(new ComboBoxItem { Content = $"{instance.DisplayName}（{instance.AgentId}）", Tag = instance.AgentId });
            _amInstance.SelectedIndex = instances.Count > 0 ? 0 : -1;
        }
        finally { _amSwitching = false; }
        if (AgentSelected(_amInstance) is { } agentId) await LoadInstanceModelPolicyAsync(workspaceId, agentId);
        else
        {
            _amInstanceInfo.Text = "该工作区还没有角色实例。";
            WritePolicy(_amTemplateChat, _amTemplateMemory, _amTemplateEmbedding, _amMode, _amEffort, AgentModelPolicy.Default, instance: true);
        }
    }

    private async Task LoadTemplateModelPolicyAsync(string templateId)
    {
        try
        {
            var policy = await _agentDirectory.ReadTemplateModelPolicyAsync(templateId);
            _amSwitching = true;
            try
            {
                WritePolicy(_amTemplateChat, _amTemplateMemory, _amTemplateEmbedding, _amMode, _amEffort, policy, instance: true);
                _amTemplateInfo.Text = DescribePolicy("模板", templateId, policy);
            }
            finally { _amSwitching = false; }
            _amNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportAgentModelFailure(exception); }
    }

    private async Task LoadInstanceModelPolicyAsync(string workspaceId, string agentId)
    {
        try
        {
            var policy = await _agentDirectory.ReadInstanceModelPolicyAsync(workspaceId, agentId);
            _amSwitching = true;
            try
            {
                WritePolicy(_amInstanceChat, _amInstanceMemory, _amInstanceEmbedding, _amInstanceMode, _amInstanceEffort, policy, instance: true);
                var inherited = new[] { policy.Chat, policy.Memory, policy.Embedding }.All(choice => !choice.IsSet)
                    && string.IsNullOrEmpty(policy.ReasoningEffort)
                    && string.Equals(policy.MemorySearchMode, AgentModelPolicyText.DefaultMemorySearchMode, StringComparison.Ordinal);
                _amInstanceInfo.Text = DescribePolicy("实例", agentId, policy)
                    + (inherited ? "\n当前三项模型与推理强度均未覆盖，运行时按模板与服务商默认解析。" : "");
            }
            finally { _amSwitching = false; }
            _amNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportAgentModelFailure(exception); }
    }

    private string DescribePolicy(string scope, string target, AgentModelPolicy policy) =>
        $"{scope} {target}\n对话：{AgentModelPolicyText.Describe(policy.Chat, _amCatalog)}" +
        $"\n记忆：{AgentModelPolicyText.Describe(policy.Memory, _amCatalog)}" +
        $"\nEmbedding：{AgentModelPolicyText.Describe(policy.Embedding, _amCatalog)}";

    private void FillPairModels(ModelPairControls pair)
    {
        var providerId = Selected(pair.Provider) ?? "";
        var models = _amCatalog
            .Where(entry => string.Equals(entry.ProviderId, providerId, StringComparison.OrdinalIgnoreCase)
                            && entry.IsEmbedding == pair.Embedding)
            .ToArray();
        pair.Model.Items.Clear();
        foreach (var entry in models)
            pair.Model.Items.Add(new ComboBoxItem
            {
                Content = entry.IsDeprecated ? $"{entry.ModelName}（已废弃）" : entry.ModelName,
                Tag = entry.ModelId
            });
        pair.Model.SelectedIndex = models.Length > 0 ? 0 : -1;
    }

    private void WritePair(ModelPairControls pair, AgentModelChoice choice)
    {
        var wasSwitching = _amSwitching;
        _amSwitching = true;
        try
        {
            FillProviderItems(pair);
            SelectOrAdd(pair.Provider, choice.ProviderId, choice.ProviderId.Length == 0 ? "" : $"{choice.ProviderId}（目录中不存在）");
            FillPairModels(pair);
            SelectOrAdd(pair.Model, choice.ModelId, choice.ModelId.Length == 0 ? "" : $"{choice.ModelId}（目录中不存在）");
        }
        finally { _amSwitching = wasSwitching; }
    }

    private void FillProviderItems(ModelPairControls pair)
    {
        var providers = _amCatalog.Where(entry => entry.IsEmbedding == pair.Embedding)
            .GroupBy(entry => (entry.ProviderId, entry.ProviderName))
            .Select(group => (group.Key.ProviderId, group.Key.ProviderName))
            .ToArray();
        pair.Provider.Items.Clear();
        foreach (var (providerId, providerName) in providers)
            pair.Provider.Items.Add(new ComboBoxItem { Content = $"{providerName}（{providerId}）", Tag = providerId });
    }

    private static void SelectOrAdd(ComboBox picker, string value, string labelWhenMissing)
    {
        for (var index = 0; index < picker.Items.Count; index++)
            if (picker.Items[index] is ComboBoxItem { Tag: string tag } && string.Equals(tag, value, StringComparison.Ordinal))
            {
                picker.SelectedIndex = index;
                return;
            }
        if (value.Length == 0) { picker.SelectedIndex = -1; return; }
        // A stored value the catalogue no longer contains is preserved and visible, not silently rewritten.
        picker.Items.Add(new ComboBoxItem { Content = labelWhenMissing, Tag = value });
        picker.SelectedIndex = picker.Items.Count - 1;
    }

    private void WritePolicy(ModelPairControls chat, ModelPairControls memory, ModelPairControls embedding,
        ComboBox mode, TextBox effort, AgentModelPolicy policy, bool instance)
    {
        _ = instance;
        WritePair(chat, policy.Chat);
        WritePair(memory, policy.Memory);
        WritePair(embedding, policy.Embedding);
        SelectMode(mode, policy.MemorySearchMode);
        effort.Text = policy.ReasoningEffort;
    }

    private static void SelectMode(ComboBox picker, string mode)
    {
        for (var index = 0; index < picker.Items.Count; index++)
            if (picker.Items[index] is ComboBoxItem { Tag: string tag } && string.Equals(tag, mode, StringComparison.Ordinal))
            {
                picker.SelectedIndex = index;
                return;
            }
        // Preserve a stored mode this build does not list instead of rewriting it to the default.
        picker.Items.Add(new ComboBoxItem { Content = $"{mode}（现有值）", Tag = mode });
        picker.SelectedIndex = picker.Items.Count - 1;
    }

    private static AgentModelChoice ReadPair(ModelPairControls pair) => new(Selected(pair.Provider) ?? "", Selected(pair.Model) ?? "");

    private AgentModelPolicy ReadPolicy(ModelPairControls chat, ModelPairControls memory, ModelPairControls embedding,
        ComboBox mode, TextBox effort) => new(
        ReadPair(chat), ReadPair(memory), ReadPair(embedding),
        Selected(mode) ?? AgentModelPolicyText.DefaultMemorySearchMode,
        AgentModelPolicyText.NormalizeReasoningEffort(effort.Text));

    private async Task SaveTemplateModelPolicyAsync()
    {
        if (AgentSelected(_amTemplate) is not { } templateId) return;
        var policy = ReadPolicy(_amTemplateChat, _amTemplateMemory, _amTemplateEmbedding, _amMode, _amEffort);
        var errors = AgentModelPolicyText.Validate(policy, _amCatalog);
        if (errors.Count > 0) { ShowNotice(_amNotice, InfoBarSeverity.Warning, "请先修正表单", string.Join(" ", errors)); return; }
        await RunAgentModelAsync("模板模型策略已保存", "只写入了模板；实例的覆盖值与 Prompt/Markdown 未被改动。",
            async () => { await _agentDirectory.SaveTemplateModelPolicyAsync(templateId, policy); await LoadTemplateModelPolicyAsync(templateId); });
    }

    private async Task SaveInstanceModelPolicyAsync()
    {
        if (AgentSelected(_amWorkspace) is not { } workspaceId || AgentSelected(_amInstance) is not { } agentId)
        {
            ShowNotice(_amNotice, InfoBarSeverity.Warning, "请先选择实例", "实例模型策略需要先选择工作区与角色。");
            return;
        }
        var policy = ReadPolicy(_amInstanceChat, _amInstanceMemory, _amInstanceEmbedding, _amInstanceMode, _amInstanceEffort);
        var errors = AgentModelPolicyText.Validate(policy, _amCatalog);
        if (errors.Count > 0) { ShowNotice(_amNotice, InfoBarSeverity.Warning, "请先修正表单", string.Join(" ", errors)); return; }
        await RunAgentModelAsync("实例模型策略已保存", "模板默认值未被修改；留空的项继续继承模板。",
            async () => { await _agentDirectory.SaveInstanceModelPolicyAsync(workspaceId, agentId, policy); await LoadInstanceModelPolicyAsync(workspaceId, agentId); });
    }

    private async Task RunAgentModelAsync(string title, string message, Func<Task> action)
    {
        SetAgentModelEnabled(false);
        try
        {
            await action();
            SetAgentModelEnabled(true);
            ShowNotice(_amNotice, InfoBarSeverity.Success, title, message);
        }
        catch (Exception exception) { ReportAgentModelFailure(exception); }
    }

    private void ReportAgentModelFailure(Exception exception)
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
        SetAgentModelEnabled(!unavailable);
        ShowNotice(_amNotice, severity, title, message);
    }

    private void SetAgentModelEnabled(bool enabled)
    {
        foreach (var control in new Control[]
        {
            _amTemplate, _amTemplateChat.Provider, _amTemplateChat.Model, _amTemplateMemory.Provider, _amTemplateMemory.Model,
            _amTemplateEmbedding.Provider, _amTemplateEmbedding.Model, _amMode, _amEffort,
            _amWorkspace, _amInstance, _amInstanceChat.Provider, _amInstanceChat.Model,
            _amInstanceMemory.Provider, _amInstanceMemory.Model, _amInstanceEmbedding.Provider,
            _amInstanceEmbedding.Model, _amInstanceMode, _amInstanceEffort
        }) control.IsEnabled = enabled;
    }
}
