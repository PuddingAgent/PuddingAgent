using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PuddingDesktop.Foundation;

namespace PuddingDesktop;

/// <summary>
/// DS-07 library slice: search the hub, inspect a skill and a version's full markdown, edit metadata,
/// retire, publish a new version and register an installation.
///
/// Retirement is soft (Core marks the skill retired and writes an audit event) and an install
/// registration is a ledger entry, not evidence that a package is installed or running.
/// </summary>
public sealed partial class MainWindow
{
    private IReadOnlyList<SkillHubSkillSummary> _slSkills = [];
    private SkillHubDetail? _slDetail;
    private SkillHubVersionContent? _slVersionContent;
    private bool _slBuilt;
    private bool _slSwitching;

    private TextBox _slQuery = null!, _slTag = null!;
    private ComboBox _slStatus = null!, _slSkillPicker = null!, _slVersionPicker = null!;
    private TextBlock _slDetailText = null!, _slVersionDetail = null!, _slInstallNotice = null!;
    private TextBox _slMarkdown = null!;
    private TextBox _slName = null!, _slSummary = null!, _slDescription = null!, _slTags = null!, _slKeywords = null!;
    private ComboBox _slMetaStatus = null!, _slVisibility = null!;
    private TextBox _slNewVersion = null!, _slParent = null!, _slNote = null!, _slNewTags = null!, _slNewMarkdown = null!;
    private ComboBox _slAction = null!;
    private TextBox _slAgent = null!, _slAgentWorkspace = null!, _slInstallVersion = null!, _slInstalledBy = null!;
    private InfoBar _slNotice = null!;

    private void BuildSkillLibraryPanel()
    {
        if (_slBuilt) return;
        _slBuilt = true;

        _slQuery = Field("搜索", "名称、摘要、描述、标签或关键词");
        _slTag = Field("标签筛选", "留空表示不过滤");
        _slStatus = new ComboBox { Header = "状态筛选", HorizontalAlignment = HorizontalAlignment.Stretch };
        _slStatus.Items.Add(new ComboBoxItem { Content = "全部状态", Tag = "" });
        foreach (var status in SkillHubText.Statuses)
            _slStatus.Items.Add(new ComboBoxItem { Content = SkillHubText.DescribeStatus(status), Tag = status });
        _slStatus.SelectedIndex = 0;
        var search = new Button { Content = "查询" };
        search.Click += async (_, _) => await LoadSkillLibraryAsync();
        _slSkillPicker = new ComboBox { Header = "技能", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_slSkillPicker, "选择技能");
        _slSkillPicker.SelectionChanged += async (_, _) => { if (!_slSwitching) await LoadSkillDetailAsync(); };
        _slDetailText = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        _slVersionPicker = new ComboBox { Header = "版本", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_slVersionPicker, "选择版本");
        _slVersionPicker.SelectionChanged += async (_, _) => { if (!_slSwitching) await LoadVersionAsync(); };
        _slVersionDetail = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 12 };
        _slMarkdown = new TextBox
        {
            Header = "版本全文（Skill Markdown，只读）", AcceptsReturn = true, IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap, Height = 180, HorizontalAlignment = HorizontalAlignment.Stretch,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas")
        };

        _slName = Field("名称");
        _slSummary = Field("摘要");
        _slDescription = Field("描述");
        _slTags = Field("标签", "逗号分隔");
        _slKeywords = Field("关键词", "逗号分隔");
        _slMetaStatus = new ComboBox { Header = "状态", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var status in SkillHubText.Statuses)
            _slMetaStatus.Items.Add(new ComboBoxItem { Content = $"{SkillHubText.DescribeStatus(status)}（{status}）", Tag = status });
        _slVisibility = new ComboBox { Header = "可见性", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var visibility in SkillHubText.Visibilities)
            _slVisibility.Items.Add(new ComboBoxItem { Content = visibility, Tag = visibility });
        var saveMeta = new Button { Content = "保存元数据" };
        saveMeta.Click += async (_, _) => await SaveSkillMetaAsync();
        var retire = new Button { Content = "退役技能" };
        retire.Click += async (_, _) => await RetireSkillAsync();

        _slAction = new ComboBox { Header = "进化动作", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var action in SkillHubText.EvolutionActions)
            _slAction.Items.Add(new ComboBoxItem { Content = action, Tag = action });
        _slAction.SelectedIndex = SkillHubText.EvolutionActions.ToList().IndexOf("patch");
        _slNewVersion = Field("新版本号", "例如 1.1.0");
        _slParent = Field("父版本", "默认取当前选中版本");
        _slNote = Field("发布说明");
        _slNewTags = Field("新版本标签", "逗号分隔；留空沿用当前标签");
        _slNewMarkdown = new TextBox
        {
            Header = "新版本 Skill Markdown", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            Height = 140, HorizontalAlignment = HorizontalAlignment.Stretch,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas")
        };
        var publish = new Button { Content = "发布新版本" };
        publish.Click += async (_, _) => await PublishSkillVersionAsync();

        _slAgent = Field("Agent 实例 ID", "上报该技能版本的角色实例");
        _slAgentWorkspace = Field("工作区 ID（可选）");
        _slInstallVersion = Field("已安装版本");
        _slInstalledBy = Field("上报者（可选）");
        var register = new Button { Content = "登记安装" };
        register.Click += async (_, _) => await RegisterSkillInstallAsync();
        _slInstallNotice = new TextBlock
        {
            Text = SkillHubText.InstallLedgerNotice, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = .75
        };

        _slNotice = new InfoBar { IsOpen = false, IsClosable = true };

        SkillLibrarySettings.Content = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(0, 16, 8, 16),
            Children =
            {
                Card("技能库", "按名称/标签/状态检索 Hub 技能；发布与进化沿用 Core 的动作白名单，退役是软删并写审计事件。",
                    _slQuery, _slTag, _slStatus, Row(search), _slSkillPicker, _slDetailText,
                    _slVersionPicker, _slVersionDetail, _slMarkdown),
                Card("编辑元数据", "只更新名称、摘要、描述、标签、关键词、状态与可见性；版本与 Markdown 不受影响。",
                    _slName, _slSummary, _slDescription, _slTags, _slKeywords, _slMetaStatus, _slVisibility,
                    Row(saveMeta, retire)),
                Card("发布新版本", "版本发布追加一条版本记录并写审计事件；非 create 的动作必须给出父版本。",
                    _slNewVersion, _slAction, _slParent, _slNote, _slNewTags, _slNewMarkdown, Row(publish)),
                Card("安装登记", "向台账登记某个 Agent 上报的版本。台账是上报记录，不是安装成功或正在运行的证据。",
                    _slAgent, _slAgentWorkspace, _slInstallVersion, _slInstalledBy, Row(register), _slInstallNotice),
                _slNotice
            }
        };
        SetSkillLibraryEnabled(false);
    }

    internal async Task LoadSkillLibraryAsync()
    {
        if (!_slBuilt) return;
        try
        {
            var status = AgentSelected(_slStatus) ?? "";
            _slSkills = await _skillHub.ListSkillsAsync(
                string.IsNullOrWhiteSpace(_slQuery.Text) ? null : _slQuery.Text.Trim(),
                string.IsNullOrWhiteSpace(_slTag.Text) ? null : _slTag.Text.Trim(),
                status.Length == 0 ? null : status, 1, 200);
            _slSwitching = true;
            try
            {
                var previous = AgentSelected(_slSkillPicker);
                _slSkillPicker.Items.Clear();
                foreach (var skill in _slSkills)
                    _slSkillPicker.Items.Add(new ComboBoxItem
                    {
                        Content = $"{skill.Name}（{skill.SkillId}）· {SkillHubText.DescribeStatus(skill.Status)}",
                        Tag = skill.SkillId
                    });
                var index = _slSkills.ToList().FindIndex(skill => skill.SkillId == previous);
                _slSkillPicker.SelectedIndex = _slSkills.Count > 0 ? Math.Max(0, index) : -1;
            }
            finally { _slSwitching = false; }
            await LoadSkillDetailAsync();
            SetSkillLibraryEnabled(true);
            _slNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportSkillLibraryFailure(exception); }
    }

    private async Task LoadSkillDetailAsync()
    {
        var skillId = AgentSelected(_slSkillPicker);
        if (string.IsNullOrEmpty(skillId))
        {
            _slDetailText.Text = _slSkills.Count == 0 ? "没有匹配的技能。" : "请选择一个技能。";
            _slVersionPicker.Items.Clear();
            _slMarkdown.Text = "";
            return;
        }
        try
        {
            _slDetailSkillId = skillId;
            _slDetail = await _skillHub.ReadSkillAsync(skillId);
            if (_slDetail is null)
            {
                _slDetailText.Text = "该技能已不存在，请重新查询。";
                _slVersionPicker.Items.Clear();
                _slMarkdown.Text = "";
                return;
            }
            _slDetailText.Text = BuildDetailText();
            FillMetaEditors();
            _slSwitching = true;
            try
            {
                _slVersionPicker.Items.Clear();
                foreach (var version in _slDetail!.Versions)
                    _slVersionPicker.Items.Add(new ComboBoxItem
                    {
                        Content = $"{version.Version} · {version.EvolutionAction}" +
                                  (string.IsNullOrEmpty(version.ParentVersion) ? "" : $"（父 {version.ParentVersion}）"),
                        Tag = version.Version
                    });
                _slVersionPicker.SelectedIndex = _slDetail.Versions.Count > 0 ? 0 : -1;
                _slInstallVersion.Text = _slDetail.Skill.LatestVersion;
            }
            finally { _slSwitching = false; }
            await LoadVersionAsync();
            _slNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportSkillLibraryFailure(exception); }
    }

    private async Task LoadVersionAsync()
    {
        var skillId = AgentSelected(_slSkillPicker);
        var version = AgentSelected(_slVersionPicker);
        if (string.IsNullOrEmpty(skillId) || string.IsNullOrEmpty(version))
        {
            _slVersionDetail.Text = "";
            _slMarkdown.Text = "";
            _slVersionContent = null;
            return;
        }
        try
        {
            _slVersionContent = await _skillHub.ReadVersionAsync(skillId, version);
            if (_slVersionContent is null)
            {
                _slVersionDetail.Text = "该版本已不存在，请重新查询。";
                _slMarkdown.Text = "";
                return;
            }
            _slVersionDetail.Text =
                $"{_slVersionContent.SkillId}@{_slVersionContent.Version} · 动作 {_slVersionContent.EvolutionAction}" +
                (string.IsNullOrEmpty(_slVersionContent.ParentVersion) ? "" : $" · 父版本 {_slVersionContent.ParentVersion}") + "\n" +
                $"内容哈希 {_slVersionContent.ContentHash} · {_slVersionContent.ContentBytes} 字节 · " +
                $"发布 {_slVersionContent.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}\n" +
                $"发布者 {SkillHubText.DescribeActor(_slVersionContent.PublishedByAgentId.Length == 0 ? null : "agent", _slVersionContent.PublishedByAgentId.Length == 0 ? null : _slVersionContent.PublishedByAgentId)}" +
                (_slVersionContent.PublishNote.Length == 0 ? "" : $"\n说明：{_slVersionContent.PublishNote}");
            _slMarkdown.Text = _slVersionContent.SkillMarkdown;
            _slParent.Text = _slVersionContent.Version;
            _slNewTags.Text = VoiceSettingsText.FormatList(_slDetail?.Skill.Tags);
            _slNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportSkillLibraryFailure(exception); }
    }

    private string _slDetailSkillId = "";

    private string BuildDetailText()
    {
        if (_slDetail is null) return $"{_slDetailSkillId}：没有可显示的详情。";
        var skill = _slDetail.Skill;
        return
            $"{skill.Name}（{skill.SkillId}）\n" +
            $"状态：{SkillHubText.DescribeStatus(skill.Status)} · 可见性：{skill.Visibility}\n" +
            $"最新版本：{skill.LatestVersion} · 版本数 {skill.VersionCount} · 安装 {skill.InstallCount} · 发布 {skill.PublishCount}\n" +
            $"标签：{VoiceSettingsText.FormatList(skill.Tags)}\n" +
            (_slDetail.RecentInstalls.Count == 0
                ? "最近安装：无台账记录"
                : "最近安装：" + string.Join("；", _slDetail.RecentInstalls.Select(install =>
                    $"{install.AgentInstanceId}@{install.InstalledVersion}")) + "\n" + SkillHubText.InstallLedgerNotice) + "\n\n" +
            (string.IsNullOrWhiteSpace(skill.Summary) ? "" : skill.Summary + "\n") +
            skill.Description;
    }


    private void FillMetaEditors()
    {
        if (_slDetail is null) return;
        var skill = _slDetail.Skill;
        _slName.Text = skill.Name;
        _slSummary.Text = skill.Summary;
        _slDescription.Text = skill.Description;
        _slTags.Text = VoiceSettingsText.FormatList(skill.Tags);
        _slKeywords.Text = VoiceSettingsText.FormatList(skill.Keywords);
        _slMetaStatus.SelectedIndex = Math.Max(0, SkillHubText.Statuses.ToList().IndexOf(skill.Status));
        _slVisibility.SelectedIndex = Math.Max(0, SkillHubText.Visibilities.ToList().IndexOf(skill.Visibility));
    }

    private SkillHubMetaEdit ReadMetaEdit() => new(
        _slName.Text.Trim(), _slSummary.Text.Trim(), _slDescription.Text.Trim(),
        VoiceSettingsText.ParseList(_slTags.Text), VoiceSettingsText.ParseList(_slKeywords.Text),
        AgentSelected(_slMetaStatus) ?? "active", AgentSelected(_slVisibility) ?? "global");

    private async Task SaveSkillMetaAsync()
    {
        if (string.IsNullOrEmpty(_slDetailSkillId)) { Warn("请先选择技能"); return; }
        var edit = ReadMetaEdit();
        var errors = SkillHubText.Validate(edit);
        if (errors.Count > 0) { ShowNotice(_slNotice, InfoBarSeverity.Warning, "请先修正表单", string.Join(" ", errors)); return; }
        await RunSkillLibraryAsync("元数据已保存", "名称、摘要、描述、标签、关键词、状态与可见性已更新；版本与 Markdown 未改动。",
            async () =>
            {
                await _skillHub.SaveSkillMetaAsync(_slDetailSkillId, edit);
                await ReloadDetailAsync();
            });
    }

    private async Task RetireSkillAsync()
    {
        if (_slDetail is null) { Warn("请先选择技能"); return; }
        var skill = _slDetail.Skill;
        var confirm = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = "退役技能",
            Content = $"将把 {skill.Name}（{skill.SkillId}）标记为已退役并写入审计事件。退役是软删：版本与安装台账保留，" +
                      "但该技能不应再被安装或使用。此操作不可撤销。",
            PrimaryButtonText = "退役", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        await RunSkillLibraryAsync("技能已退役", "该技能已标记为 retired，并已写入审计事件。",
            async () =>
            {
                await _skillHub.RetireSkillAsync(skill.SkillId);
                await ReloadDetailAsync();
            });
    }

    private async Task PublishSkillVersionAsync()
    {
        if (_slDetail is null) { Warn("请先选择技能"); return; }
        var skill = _slDetail.Skill;
        var tags = VoiceSettingsText.ParseList(_slNewTags.Text);
        var publish = new SkillHubVersionPublish(skill.SkillId, skill.Name, _slNewVersion.Text.Trim(),
            _slNewMarkdown.Text, AgentSelected(_slAction) ?? "patch", _slParent.Text.Trim(), _slNote.Text.Trim(),
            tags.Count == 0 ? skill.Tags : tags, skill.Visibility);
        var errors = SkillHubText.Validate(publish);
        if (errors.Count > 0) { ShowNotice(_slNotice, InfoBarSeverity.Warning, "请先修正表单", string.Join(" ", errors)); return; }
        await RunSkillLibraryAsync("新版本已发布", "版本记录与审计事件已写入；台账不受影响。",
            async () =>
            {
                await _skillHub.PublishVersionAsync(publish);
                _slNewVersion.Text = "";
                _slNewMarkdown.Text = "";
                _slNote.Text = "";
                await ReloadDetailAsync();
            });
    }

    private async Task RegisterSkillInstallAsync()
    {
        if (string.IsNullOrEmpty(_slDetailSkillId)) { Warn("请先选择技能"); return; }
        var registration = new SkillHubInstallRegistration(_slDetailSkillId, _slAgent.Text.Trim(),
            _slAgentWorkspace.Text.Trim(), _slInstallVersion.Text.Trim(), "", _slInstalledBy.Text.Trim());
        var errors = SkillHubText.Validate(registration);
        if (errors.Count > 0) { ShowNotice(_slNotice, InfoBarSeverity.Warning, "请先修正表单", string.Join(" ", errors)); return; }
        await RunSkillLibraryAsync("安装已登记", SkillHubText.InstallLedgerNotice,
            async () =>
            {
                await _skillHub.RegisterInstallAsync(registration);
                await ReloadDetailAsync();
            });
    }

    private async Task ReloadDetailAsync()
    {
        _slDetail = await _skillHub.ReadSkillAsync(_slDetailSkillId);
        if (_slDetail is null)
        {
            _slDetailText.Text = "该技能已被移除或无法读取，请重新查询。";
            return;
        }
        _slDetailText.Text = BuildDetailText();
        _slSwitching = true;
        try
        {
            var keep = AgentSelected(_slVersionPicker);
            _slVersionPicker.Items.Clear();
            foreach (var version in _slDetail.Versions)
                _slVersionPicker.Items.Add(new ComboBoxItem
                {
                    Content = $"{version.Version} · {version.EvolutionAction}" +
                              (string.IsNullOrEmpty(version.ParentVersion) ? "" : $"（父 {version.ParentVersion}）"),
                    Tag = version.Version
                });
            var index = _slDetail.Versions.ToList().FindIndex(version => version.Version == keep);
            _slVersionPicker.SelectedIndex = _slDetail.Versions.Count > 0
                ? (index >= 0 ? index : _slDetail.Versions.Count - 1)
                : -1;
        }
        finally { _slSwitching = false; }
        await LoadVersionAsync();
    }

    private void Warn(string message) => ShowNotice(_slNotice, InfoBarSeverity.Warning, "请先选择技能", message);

    private async Task RunSkillLibraryAsync(string title, string message, Func<Task> action)
    {
        SetSkillLibraryEnabled(false);
        try
        {
            await action();
            SetSkillLibraryEnabled(true);
            ShowNotice(_slNotice, InfoBarSeverity.Success, title, message);
        }
        catch (Exception exception) { ReportSkillLibraryFailure(exception); }
    }

    private void ReportSkillLibraryFailure(Exception exception)
    {
        App.WriteDiagnostic(exception);
        var unavailable = exception is SettingsUnavailableException;
        var (severity, title, message) = exception switch
        {
            SettingsUnavailableException reason => (InfoBarSeverity.Informational, "Core 未就绪",
                reason.Message + " 该分类仍可浏览；表单已禁用，未写入任何内容。"),
            SettingsConflictException conflict => (InfoBarSeverity.Warning, "版本冲突，已阻止覆盖", conflict.Message),
            ArgumentException argument => (InfoBarSeverity.Warning, "表单或配置被 Core 拒绝", argument.Message),
            InvalidOperationException invalid => (InfoBarSeverity.Warning, "Core 拒绝了该操作", invalid.Message),
            _ => (InfoBarSeverity.Error, "操作失败", "请查看诊断日志后重试。")
        };
        SetSkillLibraryEnabled(!unavailable);
        ShowNotice(_slNotice, severity, title, message);
    }

    private void SetSkillLibraryEnabled(bool enabled)
    {
        foreach (var control in new Control[]
        {
            _slQuery, _slTag, _slStatus, _slSkillPicker, _slVersionPicker, _slName, _slSummary, _slDescription,
            _slTags, _slKeywords, _slMetaStatus, _slVisibility, _slNewVersion, _slAction, _slParent, _slNote,
            _slNewTags, _slNewMarkdown, _slAgent, _slAgentWorkspace, _slInstallVersion, _slInstalledBy
        }) control.IsEnabled = enabled;
    }
}
