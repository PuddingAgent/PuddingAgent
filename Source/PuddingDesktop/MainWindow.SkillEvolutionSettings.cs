using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PuddingDesktop.Foundation;

namespace PuddingDesktop;

/// <summary>
/// DS-07 EVO MAP and install ledger slices. Both are read-only: the lineage is derived from published
/// versions, and the ledger records what an Agent reported rather than proving a package is installed.
/// </summary>
public sealed partial class MainWindow
{
    private IReadOnlyList<SkillHubSkillSummary> _seSkills = [];
    private SkillHubEvoMap? _seMap;
    private IReadOnlyList<SkillHubInstall> _siInstalls = [];
    private bool _seBuilt;
    private bool _seSwitching;

    private ComboBox _seSkill = null!, _seScope = null!;
    private TextBox _seLimit = null!;
    private TextBlock _seSummary = null!;
    private StackPanel _seTree = null!, _seEdges = null!;
    private InfoBar _seNotice = null!;

    private TextBox _siAgent = null!, _siSkill = null!, _siUpdateAgent = null!;
    private ComboBox _siPageSize = null!, _siPicker = null!;
    private TextBlock _siDetail = null!;
    private StackPanel _siUpdates = null!;
    private InfoBar _siNotice = null!;

    private void BuildSkillEvolutionPanels()
    {
        if (_seBuilt) return;
        _seBuilt = true;

        _seSkill = new ComboBox { Header = "技能", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_seSkill, "谱系所属技能");
        _seSkill.SelectionChanged += async (_, _) => { if (!_seSwitching) await LoadLineageAsync(); };
        _seScope = new ComboBox { Header = "范围", HorizontalAlignment = HorizontalAlignment.Stretch };
        _seScope.Items.Add(new ComboBoxItem { Content = "所选技能谱系", Tag = "single" });
        _seScope.Items.Add(new ComboBoxItem { Content = "全局谱系（多技能）", Tag = "global" });
        _seScope.SelectedIndex = 0;
        _seScope.SelectionChanged += async (_, _) => { if (!_seSwitching) await LoadLineageAsync(); };
        _seLimit = Field("全局谱系节点上限", "默认 500");
        var refresh = new Button { Content = "刷新谱系" };
        refresh.Click += async (_, _) => await LoadLineageAsync();
        _seSummary = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = .8, FontSize = 12 };
        _seTree = new StackPanel { Spacing = 2 };
        _seEdges = new StackPanel { Spacing = 2 };
        _seNotice = new InfoBar { IsOpen = false, IsClosable = true };

        SkillEvolutionSettings.Content = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(0, 16, 8, 16),
            Children =
            {
                Card("EVO MAP", "谱系按发布的版本与父版本推导，只读。节点使用 Core 的 {技能}@{版本} 身份，" +
                    "父节点缺失或边悬空都会明确标注而不是悄悄丢掉。",
                    _seSkill, _seScope, _seLimit, Row(refresh), _seSummary,
                    new TextBlock { Text = "版本谱系", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }, _seTree,
                    new TextBlock { Text = "边（父 → 子）", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }, _seEdges),
                _seNotice
            }
        };

        _siAgent = Field("Agent 实例 ID 筛选", "留空表示全部");
        _siSkill = Field("技能 ID 筛选", "留空表示全部");
        _siPageSize = new ComboBox { Header = "条数", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var size in SkillHubText.InstallPageSizes)
            _siPageSize.Items.Add(new ComboBoxItem { Content = size.ToString(), Tag = size });
        _siPageSize.SelectedIndex = 1;
        var refreshInstalls = new Button { Content = "刷新台账" };
        refreshInstalls.Click += async (_, _) => await LoadInstallsAsync();
        _siPicker = new ComboBox { Header = "台账记录", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_siPicker, "选择台账记录");
        _siPicker.SelectionChanged += (_, _) => ShowInstallDetail();
        _siDetail = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        _siNotice = new InfoBar { IsOpen = false, IsClosable = true };

        _siUpdateAgent = Field("检查更新的 Agent 实例 ID", "必填：更新检查按 Agent 的已登记版本比较");
        var checkUpdates = new Button { Content = "检查更新" };
        checkUpdates.Click += async (_, _) => await CheckUpdatesAsync();
        _siUpdates = new StackPanel { Spacing = 2 };

        SkillInstallsSettings.Content = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(0, 16, 8, 16),
            Children =
            {
                Card("安装台账", SkillHubText.InstallLedgerNotice,
                    _siAgent, _siSkill, _siPageSize, Row(refreshInstalls), _siPicker, _siDetail),
                Card("更新检查", "按 Agent 的已登记版本与 Hub 最新版本比较；落后仅在版本字符串不同时报告，不猜测语义顺序。",
                    _siUpdateAgent, Row(checkUpdates), _siUpdates),
                _siNotice
            }
        };
        SetSkillEvolutionEnabled(false);
    }

    internal async Task LoadLineageAsync()
    {
        if (!_seBuilt) return;
        try
        {
            if (_seSkills.Count == 0)
            {
                _seSkills = await _skillHub.ListSkillsAsync(null, null, null, 1, 200);
                _seSwitching = true;
                try
                {
                    var previous = AgentSelected(_seSkill);
                    _seSkill.Items.Clear();
                    foreach (var skill in _seSkills)
                        _seSkill.Items.Add(new ComboBoxItem { Content = $"{skill.Name}（{skill.SkillId}）", Tag = skill.SkillId });
                    var index = _seSkills.ToList().FindIndex(skill => skill.SkillId == previous);
                    _seSkill.SelectedIndex = _seSkills.Count > 0 ? Math.Max(0, index) : -1;
                }
                finally { _seSwitching = false; }
            }

            var global = string.Equals(AgentSelected(_seScope), "global", StringComparison.Ordinal);
            var limit = VoiceSettingsText.ParseOptionalInt(_seLimit.Text) ?? 500;
            _seMap = global
                ? await _skillHub.ReadGlobalLineageAsync(null, limit)
                : AgentSelected(_seSkill) is { } skillId ? await _skillHub.ReadLineageAsync(skillId) : null;

            _seSwitching = true;
            try
            {
                _seTree.Children.Clear();
                _seEdges.Children.Clear();
                if (_seMap is null)
                {
                    _seSummary.Text = "请选择一个技能以查看它的谱系。";
                }
                else
                {
                    _seSummary.Text = SkillHubText.DescribeLineage(_seMap);
                    var lines = SkillHubText.RenderLineage(_seMap);
                    if (lines.Count == 0) _seTree.Children.Add(Muted("该范围还没有已发布的版本节点。"));
                    foreach (var line in lines) _seTree.Children.Add(Muted(line));
                    if (_seMap.Edges.Count == 0) _seEdges.Children.Add(Muted("没有版本关系边。"));
                    foreach (var edge in _seMap.Edges)
                        _seEdges.Children.Add(Muted($"{edge.FromNodeId} → {edge.ToNodeId} · {edge.Action}"));
                }
            }
            finally { _seSwitching = false; }
            SetSkillEvolutionEnabled(true);
            _seNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportSkillEvolutionFailure(exception); }
    }

    internal async Task LoadInstallsAsync()
    {
        if (!_seBuilt) return;
        try
        {
            var limit = _siPageSize.SelectedItem is ComboBoxItem { Tag: int size } ? size : 50;
            _siInstalls = await _skillHub.ListInstallsAsync(
                string.IsNullOrWhiteSpace(_siAgent.Text) ? null : _siAgent.Text.Trim(),
                string.IsNullOrWhiteSpace(_siSkill.Text) ? null : _siSkill.Text.Trim(), 1, limit);
            _seSwitching = true;
            try
            {
                _siPicker.Items.Clear();
                foreach (var install in _siInstalls)
                    _siPicker.Items.Add(new ComboBoxItem
                    {
                        Content = $"{install.SkillId}@{install.InstalledVersion} · {install.AgentInstanceId}",
                        Tag = $"{install.SkillId}|{install.AgentInstanceId}"
                    });
                _siPicker.SelectedIndex = _siInstalls.Count > 0 ? 0 : -1;
            }
            finally { _seSwitching = false; }
            ShowInstallDetail();
            SetSkillEvolutionEnabled(true);
            _siNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportSkillEvolutionFailure(exception); }
    }

    private void ShowInstallDetail()
    {
        if (_siPicker.SelectedItem is not ComboBoxItem { Tag: string key })
        {
            _siDetail.Text = _siInstalls.Count == 0 ? "没有匹配的台账记录。" : "请选择一条记录。";
            return;
        }
        var install = _siInstalls.FirstOrDefault(candidate => $"{candidate.SkillId}|{candidate.AgentInstanceId}" == key);
        if (install is null) { _siDetail.Text = "记录已不在当前结果集中，请刷新。"; return; }
        _siDetail.Text =
            $"{install.SkillId}@{install.InstalledVersion}\n" +
            $"Agent 实例：{install.AgentInstanceId}" +
            (install.WorkspaceId.Length == 0 ? "" : $" · 工作区 {install.WorkspaceId}") + "\n" +
            $"内容哈希：{(install.ContentHash.Length == 0 ? "未上报" : install.ContentHash)}\n" +
            $"上报者：{(install.InstalledBy.Length == 0 ? "未记录" : install.InstalledBy)}\n" +
            $"登记 {install.InstalledAt.ToLocalTime():yyyy-MM-dd HH:mm} · 更新 {install.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm}\n" +
            SkillHubText.InstallLedgerNotice;
    }

    private async Task CheckUpdatesAsync()
    {
        var agentInstanceId = _siUpdateAgent.Text.Trim();
        if (agentInstanceId.Length == 0)
        {
            ShowNotice(_siNotice, InfoBarSeverity.Warning, "请填写 Agent 实例 ID", "更新检查按 Agent 的已登记版本比较。");
            return;
        }
        try
        {
            var updates = await _skillHub.ListUpdatesAsync(agentInstanceId);
            _siUpdates.Children.Clear();
            if (updates.Count == 0)
                _siUpdates.Children.Add(Muted($"Agent {agentInstanceId} 没有落后的技能版本。"));
            foreach (var update in updates)
                _siUpdates.Children.Add(Muted(SkillHubText.DescribeUpdate(update)));
            SetSkillEvolutionEnabled(true);
            ShowNotice(_siNotice, InfoBarSeverity.Success, "更新检查完成",
                updates.Count == 0 ? "该 Agent 的已登记版本都不落后。" : $"发现 {updates.Count} 个可更新技能。");
        }
        catch (Exception exception) { ReportSkillEvolutionFailure(exception); }
    }

    private void ReportSkillEvolutionFailure(Exception exception)
    {
        App.WriteDiagnostic(exception);
        var unavailable = exception is SettingsUnavailableException;
        var (severity, title, message) = exception switch
        {
            SettingsUnavailableException reason => (InfoBarSeverity.Informational, "Core 未就绪",
                reason.Message + " 该分类仍可浏览；表单已禁用，未写入任何内容。"),
            ArgumentException argument => (InfoBarSeverity.Warning, "请求被 Core 拒绝", argument.Message),
            InvalidOperationException invalid => (InfoBarSeverity.Warning, "操作未完成", invalid.Message),
            _ => (InfoBarSeverity.Error, "操作失败", "请查看诊断日志后重试。")
        };
        SetSkillEvolutionEnabled(!unavailable);
        ShowNotice(_seNotice, severity, title, message);
        ShowNotice(_siNotice, severity, title, message);
    }

    private void SetSkillEvolutionEnabled(bool enabled)
    {
        foreach (var control in new Control[]
        {
            _seSkill, _seScope, _seLimit, _siAgent, _siSkill, _siPageSize, _siPicker, _siUpdateAgent
        }) control.IsEnabled = enabled;
    }
}
