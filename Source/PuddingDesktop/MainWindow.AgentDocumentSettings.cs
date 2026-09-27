using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PuddingDesktop.Foundation;

namespace PuddingDesktop;

/// <summary>
/// DS-04 (document slice): template prompt/Markdown fields and per-instance Markdown documents.
/// Drafts survive document switches; saves carry a conflict token so a stale editor cannot overwrite
/// someone else's edit.
/// </summary>
public sealed partial class MainWindow
{
    private readonly Dictionary<string, string> _adTemplateDrafts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _adInstanceDrafts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _adInstanceTokens = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _adInstanceDefaults = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _adInstanceIssues = new(StringComparer.Ordinal);
    private string _adTemplateFingerprint = "";
    private string _adTemplateId = "";
    private string _adInstanceId = "";
    private bool _adSwitching;
    private bool _adBuilt;

    private ComboBox _adTemplate = null!, _adTemplateDoc = null!, _adWorkspace = null!, _adInstance = null!, _adInstanceDoc = null!;
    private TextBox _adTemplateText = null!, _adInstanceText = null!;
    private TextBlock _adTemplateInfo = null!, _adInstanceInfo = null!;
    private InfoBar _adNotice = null!;

    private void BuildAgentDocumentPanel()
    {
        if (_adBuilt) return;
        _adBuilt = true;

        _adTemplate = new ComboBox { Header = "全局模板", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_adTemplate, "文档所属模板");
        _adTemplate.SelectionChanged += async (_, _) => { if (!_adSwitching && AgentSelected(_adTemplate) is { } id) await LoadTemplateDocumentsAsync(id); };
        _adTemplateDoc = new ComboBox { Header = "文档", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_adTemplateDoc, "选择模板文档");
        _adTemplateDoc.SelectionChanged += (_, _) => SwitchTemplateDocument();
        _adTemplateText = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 240,
            HorizontalAlignment = HorizontalAlignment.Stretch, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas")
        };
        AutomationProperties.SetName(_adTemplateText, "模板文档内容");
        _adTemplateText.TextChanged += (_, _) => { if (!_adSwitching) StashTemplateDraft(); };
        _adTemplateInfo = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = .7, FontSize = 12 };
        var templateSave = new Button { Content = "保存模板文档" };
        templateSave.Click += async (_, _) => await SaveTemplateDocumentsAsync();
        var templateReload = new Button { Content = "放弃修改并重新读取" };
        templateReload.Click += async (_, _) => { if (AgentSelected(_adTemplate) is { } id) await LoadTemplateDocumentsAsync(id); };

        _adWorkspace = new ComboBox { Header = "工作区", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_adWorkspace, "文档所属工作区");
        _adWorkspace.SelectionChanged += async (_, _) => { if (!_adSwitching && AgentSelected(_adWorkspace) is { } id) await LoadAgentDocumentInstancesAsync(id); };
        _adInstance = new ComboBox { Header = "角色实例", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_adInstance, "文档所属角色实例");
        _adInstance.SelectionChanged += async (_, _) => { if (!_adSwitching && AgentSelected(_adWorkspace) is { } w && AgentSelected(_adInstance) is { } a) await LoadInstanceDocumentsAsync(w, a); };
        _adInstanceDoc = new ComboBox { Header = "文档", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_adInstanceDoc, "选择实例文档");
        _adInstanceDoc.SelectionChanged += (_, _) => SwitchInstanceDocument();
        _adInstanceText = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 240,
            HorizontalAlignment = HorizontalAlignment.Stretch, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas")
        };
        AutomationProperties.SetName(_adInstanceText, "实例文档内容");
        _adInstanceText.TextChanged += (_, _) => { if (!_adSwitching) StashInstanceDraft(); };
        _adInstanceInfo = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = .7, FontSize = 12 };
        var instanceSave = new Button { Content = "保存所选文档" };
        instanceSave.Click += async (_, _) => await SaveInstanceDocumentAsync();
        var showDefault = new Button { Content = "查看模板默认值" };
        showDefault.Click += async (_, _) => await ShowTemplateDefaultAsync();
        var instanceReload = new Button { Content = "放弃修改并重新读取" };
        instanceReload.Click += async (_, _) =>
        {
            if (AgentSelected(_adWorkspace) is { } w && AgentSelected(_adInstance) is { } a) await LoadInstanceDocumentsAsync(w, a);
        };

        _adNotice = new InfoBar { IsOpen = false, IsClosable = true };

        AgentDocumentsSettings.Content = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(0, 16, 8, 16),
            Children =
            {
                Card("模板文档（默认值）",
                    "模板是全局默认值；这里保存的是模板本身，不会写入任何实例。切换文档不会丢失未保存的编辑，" +
                    "保存时会带上读取时的指纹，模板被他人改动后会要求重新读取。",
                    _adTemplate, Row(templateSave, templateReload), _adTemplateDoc, _adTemplateText, _adTemplateInfo),
                Card("实例文档（覆盖值）",
                    "实例文档保存在角色实例目录；保存按文档提交并携带 Core 的 SHA-256 版本，冲突时不会覆盖他人改动。" +
                    "「与模板默认值一致」表示该实例没有覆盖模板。",
                    _adWorkspace, _adInstance, Row(instanceSave, showDefault, instanceReload), _adInstanceDoc,
                    _adInstanceText, _adInstanceInfo),
                _adNotice
            }
        };
        SetAgentDocumentEnabled(false);
    }

    internal async Task LoadAgentDocumentsAsync()
    {
        if (!_adBuilt) return;
        try
        {
            var templates = await _agentDirectory.ListTemplatesAsync();
            var workspaces = await _agentDirectory.ListWorkspacesAsync();
            _adSwitching = true;
            try
            {
                var previousTemplate = AgentSelected(_adTemplate);
                _adTemplate.Items.Clear();
                foreach (var template in templates)
                    _adTemplate.Items.Add(new ComboBoxItem { Content = $"{template.Name}（{template.TemplateId}）", Tag = template.TemplateId });
                var templateIndex = templates.ToList().FindIndex(template => template.TemplateId == previousTemplate);
                _adTemplate.SelectedIndex = templates.Count > 0 ? Math.Max(0, templateIndex) : -1;

                var previousWorkspace = AgentSelected(_adWorkspace);
                _adWorkspace.Items.Clear();
                foreach (var workspace in workspaces)
                    _adWorkspace.Items.Add(new ComboBoxItem { Content = $"{workspace.Name}（{workspace.WorkspaceId}）", Tag = workspace.WorkspaceId });
                var workspaceIndex = workspaces.ToList().FindIndex(workspace => workspace.WorkspaceId == previousWorkspace);
                _adWorkspace.SelectedIndex = workspaces.Count > 0 ? Math.Max(0, workspaceIndex) : -1;
            }
            finally { _adSwitching = false; }

            if (AgentSelected(_adTemplate) is { } templateId) await LoadTemplateDocumentsAsync(templateId);
            if (AgentSelected(_adWorkspace) is { } workspaceId) await LoadAgentDocumentInstancesAsync(workspaceId);
            SetAgentDocumentEnabled(true);
            _adNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportAgentDocumentFailure(exception); }
    }

    private async Task LoadAgentDocumentInstancesAsync(string workspaceId)
    {
        var instances = await _agentDirectory.ListInstancesAsync(workspaceId);
        _adSwitching = true;
        try
        {
            _adInstance.Items.Clear();
            foreach (var instance in instances)
                _adInstance.Items.Add(new ComboBoxItem { Content = $"{instance.DisplayName}（{instance.AgentId}）", Tag = instance.AgentId });
            _adInstance.SelectedIndex = instances.Count > 0 ? 0 : -1;
        }
        finally { _adSwitching = false; }
        if (AgentSelected(_adInstance) is { } agentId) await LoadInstanceDocumentsAsync(workspaceId, agentId);
        else { _adInstanceDrafts.Clear(); _adInstanceTokens.Clear(); _adInstanceText.Text = ""; _adInstanceInfo.Text = "该工作区还没有角色实例。"; }
    }

    private async Task LoadTemplateDocumentsAsync(string templateId)
    {
        try
        {
            var documents = await _agentDirectory.ReadTemplateDocumentsAsync(templateId);
            _adTemplateId = documents.TemplateId;
            _adTemplateFingerprint = documents.Fingerprint;
            _adTemplateDrafts.Clear();
            foreach (var pair in documents.Documents) _adTemplateDrafts[pair.Key] = pair.Value;
            _adTemplateDoc.Items.Clear();
            foreach (var slot in AgentDocuments.TemplateSlots)
                _adTemplateDoc.Items.Add(new ComboBoxItem { Content = slot.Title, Tag = slot.Key });
            _adSwitching = true;
            try { _adTemplateDoc.SelectedIndex = 0; _adTemplateText.Text = _adTemplateDrafts.GetValueOrDefault("systemPrompt", ""); }
            finally { _adSwitching = false; }
            _adTemplateInfo.Text = $"模板 {templateId} · 已加载 {_adTemplateDrafts.Count} 个文档（指纹 {Short(_adTemplateFingerprint)}）";
            _adNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportAgentDocumentFailure(exception); }
    }

    private async Task LoadInstanceDocumentsAsync(string workspaceId, string agentId)
    {
        try
        {
            var documents = await _agentDirectory.ReadInstanceDocumentsAsync(workspaceId, agentId);
            _adInstanceId = agentId;
            _adInstanceDrafts.Clear(); _adInstanceTokens.Clear(); _adInstanceDefaults.Clear(); _adInstanceIssues.Clear();
            foreach (var document in documents)
            {
                _adInstanceDrafts[document.Key] = document.Content;
                _adInstanceTokens[document.Key] = document.Sha256;
                _adInstanceDefaults[document.Key] = document.TemplateDefault;
                _adInstanceIssues[document.Key] = document.Issue;
            }
            _adInstanceDoc.Items.Clear();
            foreach (var slot in AgentDocuments.InstanceSlots)
                _adInstanceDoc.Items.Add(new ComboBoxItem { Content = slot.Title, Tag = slot.Key });
            _adSwitching = true;
            try
            {
                _adInstanceDoc.SelectedIndex = 0;
                _adInstanceText.Text = _adInstanceDrafts.GetValueOrDefault(AgentDocuments.InstanceSlots[0].Key, "");
            }
            finally { _adSwitching = false; }
            ApplyInstanceDocumentInfo();
            _adNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportAgentDocumentFailure(exception); }
    }

    private void SwitchTemplateDocument()
    {
        if (_adSwitching || AgentSelected(_adTemplateDoc) is not { } key) return;
        _adSwitching = true;
        try { _adTemplateText.Text = _adTemplateDrafts.GetValueOrDefault(key, ""); }
        finally { _adSwitching = false; }
    }

    private void StashTemplateDraft()
    {
        if (AgentSelected(_adTemplateDoc) is { } key) _adTemplateDrafts[key] = _adTemplateText.Text;
    }

    private void SwitchInstanceDocument()
    {
        if (_adSwitching || AgentSelected(_adInstanceDoc) is not { } key) return;
        _adSwitching = true;
        try { _adInstanceText.Text = _adInstanceDrafts.GetValueOrDefault(key, ""); }
        finally { _adSwitching = false; }
        ApplyInstanceDocumentInfo();
    }

    private void StashInstanceDraft()
    {
        if (AgentSelected(_adInstanceDoc) is { } key) _adInstanceDrafts[key] = _adInstanceText.Text;
    }

    private void ApplyInstanceDocumentInfo()
    {
        if (AgentSelected(_adInstanceDoc) is not { } key) { _adInstanceInfo.Text = ""; return; }
        var slot = AgentDocuments.Find(AgentDocuments.InstanceSlots, key);
        var isOverride = AgentDocuments.IsOverride(_adInstanceDrafts.GetValueOrDefault(key, ""), _adInstanceDefaults.GetValueOrDefault(key, ""));
        var token = _adInstanceTokens.GetValueOrDefault(key, "");
        var issue = _adInstanceIssues.GetValueOrDefault(key, "");
        _adInstanceInfo.Text = $"{slot?.FileName ?? key} · {(isOverride ? "实例已覆盖模板默认值" : "与模板默认值一致（未覆盖）")}" +
                               $" · 版本 {(token.Length == 0 ? "（尚未创建，首次保存会新建）" : Short(token))}" +
                               (issue.Length == 0 ? "" : $"\n{issue}");
    }

    private async Task SaveTemplateDocumentsAsync()
    {
        StashTemplateDraft();
        try
        {
            await _agentDirectory.SaveTemplateDocumentsAsync(
                new AgentTemplateDocuments(_adTemplateId, _adTemplateFingerprint, new Dictionary<string, string>(_adTemplateDrafts, StringComparer.Ordinal)));
            await LoadTemplateDocumentsAsync(_adTemplateId);
            ShowNotice(_adNotice, InfoBarSeverity.Success, "模板文档已保存",
                "只写入了模板；实例的覆盖值与未展示字段没有被改动。");
        }
        catch (Exception exception) { ReportAgentDocumentFailure(exception); }
    }

    private async Task SaveInstanceDocumentAsync()
    {
        if (AgentSelected(_adInstanceDoc) is not { } key) return;
        if (AgentSelected(_adWorkspace) is not { } workspaceId || string.IsNullOrEmpty(_adInstanceId))
        {
            ShowNotice(_adNotice, InfoBarSeverity.Warning, "请先选择实例", "实例文档保存在角色实例目录，必须先选择工作区与角色。");
            return;
        }
        StashInstanceDraft();
        try
        {
            await _agentDirectory.SaveInstanceDocumentAsync(workspaceId, _adInstanceId, key,
                _adInstanceDrafts.GetValueOrDefault(key, ""), _adInstanceTokens.GetValueOrDefault(key, ""));
            await LoadInstanceDocumentsAsync(workspaceId, _adInstanceId);
            ShowNotice(_adNotice, InfoBarSeverity.Success, "实例文档已保存",
                "已按文档写入实例目录并刷新版本；模板默认值未被修改。");
        }
        catch (Exception exception) { ReportAgentDocumentFailure(exception); }
    }

    private async Task ShowTemplateDefaultAsync()
    {
        if (AgentSelected(_adInstanceDoc) is not { } key) return;
        StashInstanceDraft();
        var slot = AgentDocuments.Find(AgentDocuments.InstanceSlots, key);
        var templateDefault = _adInstanceDefaults.GetValueOrDefault(key, "");
        var origin = AgentDocuments.TemplateSlotFor(key);
        var content = string.IsNullOrWhiteSpace(templateDefault)
            ? "该实例的来源模板没有对应的默认文档内容。"
            : templateDefault;
        await new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = $"模板默认值 · {slot?.Title ?? key}",
            Content = new ScrollViewer
            {
                MaxHeight = 420,
                Content = new TextBlock
                {
                    Text = origin.Length == 0 ? content : $"（来源模板字段：{origin}）\n\n{content}",
                    TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true
                }
            },
            CloseButtonText = "关闭"
        }.ShowAsync();
    }

    private static string Short(string value) => value.Length <= 8 ? value : value[..8];

    private void ReportAgentDocumentFailure(Exception exception)
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
        SetAgentDocumentEnabled(!unavailable);
        ShowNotice(_adNotice, severity, title, message);
    }

    private void SetAgentDocumentEnabled(bool enabled)
    {
        foreach (var control in new Control[]
        {
            _adTemplate, _adTemplateDoc, _adTemplateText, _adWorkspace, _adInstance, _adInstanceDoc, _adInstanceText
        }) control.IsEnabled = enabled;
    }
}
