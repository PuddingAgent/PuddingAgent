using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PuddingDesktop.Foundation;

namespace PuddingDesktop;

/// <summary>
/// DS-07 overview + events slice: SKILL Hub statistics and the audit-event log. Read-only; publishing,
/// evolving, retiring and installing arrive in later slices.
/// </summary>
public sealed partial class MainWindow
{
    private IReadOnlyList<SkillHubSkillSummary> _skSkills = [];
    private IReadOnlyList<SkillHubEvent> _skEvents = [];
    private bool _skBuilt;
    private bool _skSwitching;

    private TextBlock _skOverviewSummary = null!, _skOverviewUpdated = null!, _skEventDetail = null!;
    private StackPanel _skActionCounts = null!, _skTopInstalled = null!;
    private ComboBox _skSkillFilter = null!, _skPageSize = null!, _skEventPicker = null!;
    private InfoBar _skNotice = null!;
    private InfoBar _skEventsNotice = null!;

    private void BuildSkillHubPanels()
    {
        if (_skBuilt) return;
        _skBuilt = true;

        _skOverviewSummary = new TextBlock { TextWrapping = TextWrapping.Wrap };
        _skOverviewUpdated = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = .7, FontSize = 12 };
        _skActionCounts = new StackPanel { Spacing = 4 };
        _skTopInstalled = new StackPanel { Spacing = 4 };
        _skNotice = new InfoBar { IsOpen = false, IsClosable = true };
        var refreshOverview = new Button { Content = "刷新概览" };
        refreshOverview.Click += async (_, _) => await LoadSkillOverviewAsync();

        SkillOverviewSettings.Content = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(0, 16, 8, 16),
            Children =
            {
                Card("概览", "技能总量、版本与安装统计，以及进化动作分布和安装最多的技能。只读，数据来自 Hub 的聚合查询。",
                    Row(refreshOverview), _skOverviewSummary, _skOverviewUpdated,
                    new TextBlock { Text = "进化动作分布", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }, _skActionCounts,
                    new TextBlock { Text = "安装最多的技能", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }, _skTopInstalled),
                _skNotice
            }
        };

        _skSkillFilter = new ComboBox { Header = "技能筛选", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_skSkillFilter, "事件所属技能");
        _skSkillFilter.SelectionChanged += async (_, _) => { if (!_skSwitching) await LoadSkillEventsAsync(); };
        _skPageSize = new ComboBox { Header = "条数", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var size in SkillHubText.EventPageSizes)
            _skPageSize.Items.Add(new ComboBoxItem { Content = size.ToString(), Tag = size });
        _skPageSize.SelectedIndex = 1;
        _skPageSize.SelectionChanged += async (_, _) => { if (!_skSwitching) await LoadSkillEventsAsync(); };
        var refreshEvents = new Button { Content = "刷新事件" };
        refreshEvents.Click += async (_, _) => await LoadSkillEventsAsync();
        _skEventPicker = new ComboBox { Header = "事件", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_skEventPicker, "选择审计事件");
        _skEventPicker.SelectionChanged += (_, _) => ShowSkillEventDetail();
        _skEventDetail = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        _skEventsNotice = new InfoBar { IsOpen = false, IsClosable = true };

        SkillEventsSettings.Content = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(0, 16, 8, 16),
            Children =
            {
                Card("事件审计", "按技能与条数查询 Hub 审计事件；事件类型与来源按 Core 的原始值显示，未知类型不会被改写。",
                    _skSkillFilter, _skPageSize, Row(refreshEvents), _skEventPicker, _skEventDetail),
                _skEventsNotice
            }
        };
        SetSkillHubEnabled(false);
    }

    internal async Task LoadSkillOverviewAsync()
    {
        if (!_skBuilt) return;
        try
        {
            var overview = await _skillHub.ReadOverviewAsync();
            _skOverviewSummary.Text =
                $"技能 {overview.TotalSkills}（启用 {overview.ActiveSkills} · 退役 {overview.RetiredSkills}）· " +
                $"版本 {overview.TotalVersions} · 安装 {overview.TotalInstalls} · 覆盖 Agent {overview.DistinctAgents} · " +
                $"已进化技能 {overview.EvolvedSkills}";
            _skOverviewUpdated.Text = overview.GeneratedAt == DateTimeOffset.MinValue
                ? ""
                : $"统计生成于 {overview.GeneratedAt.ToLocalTime():yyyy-MM-dd HH:mm}";
            _skActionCounts.Children.Clear();
            if (overview.EvolutionActionCounts.Count == 0)
                _skActionCounts.Children.Add(Muted("还没有进化动作记录。"));
            foreach (var count in overview.EvolutionActionCounts)
                _skActionCounts.Children.Add(Muted($"{count.Action}：{count.Count}"));
            _skTopInstalled.Children.Clear();
            if (overview.TopInstalled.Count == 0)
                _skTopInstalled.Children.Add(Muted("还没有安装记录。"));
            foreach (var skill in overview.TopInstalled)
                _skTopInstalled.Children.Add(Muted(
                    $"{skill.Name}（{skill.SkillId}）· 最新 {skill.LatestVersion} · {SkillHubText.DescribeStatus(skill.Status)} · " +
                    $"安装 {skill.InstallCount} · 版本 {skill.VersionCount}"));
            SetSkillHubEnabled(true);
            _skNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportSkillHubFailure(exception); }
    }

    internal async Task LoadSkillEventsAsync()
    {
        if (!_skBuilt) return;
        try
        {
            if (_skSkills.Count == 0)
            {
                _skSkills = await _skillHub.ListSkillsAsync(null, null, null, 1, 200);
                _skSwitching = true;
                try
                {
                    var previous = AgentSelected(_skSkillFilter);
                    _skSkillFilter.Items.Clear();
                    _skSkillFilter.Items.Add(new ComboBoxItem { Content = "全部技能", Tag = "" });
                    foreach (var skill in _skSkills)
                        _skSkillFilter.Items.Add(new ComboBoxItem { Content = $"{skill.Name}（{skill.SkillId}）", Tag = skill.SkillId });
                    var index = _skSkills.ToList().FindIndex(skill => skill.SkillId == previous);
                    _skSkillFilter.SelectedIndex = index >= 0 ? index + 1 : 0;
                }
                finally { _skSwitching = false; }
            }

            var skillId = AgentSelected(_skSkillFilter) ?? "";
            var limit = _skPageSize.SelectedItem is ComboBoxItem { Tag: int size } ? size : 50;
            _skEvents = await _skillHub.ListEventsAsync(skillId.Length == 0 ? null : skillId, limit);
            _skSwitching = true;
            try
            {
                var previous = _skEventPicker.SelectedIndex;
                _skEventPicker.Items.Clear();
                foreach (var entry in _skEvents)
                    _skEventPicker.Items.Add(new ComboBoxItem
                    {
                        Content = $"{entry.CreatedAt.ToLocalTime():MM-dd HH:mm} · {SkillHubText.DescribeEventType(entry.EventType)} · " +
                                  SkillHubText.DescribeSkillReference(entry.SkillId, entry.Version),
                        Tag = entry.Id
                    });
                _skEventPicker.SelectedIndex = _skEvents.Count > 0 ? Math.Clamp(previous, 0, _skEvents.Count - 1) : -1;
            }
            finally { _skSwitching = false; }
            ShowSkillEventDetail();
            SetSkillHubEnabled(true);
            _skNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportSkillHubFailure(exception); }
    }

    private void ShowSkillEventDetail()
    {
        if (_skEventPicker.SelectedItem is not ComboBoxItem { Tag: long id })
        {
            _skEventDetail.Text = _skEvents.Count == 0 ? "没有匹配的审计事件。" : "请选择一个事件。";
            return;
        }
        var entry = _skEvents.FirstOrDefault(candidate => candidate.Id == id);
        if (entry is null) { _skEventDetail.Text = "事件已不在当前结果集中，请刷新。"; return; }
        _skEventDetail.Text =
            $"#{entry.Id} · {SkillHubText.DescribeEventType(entry.EventType)}（{entry.EventType}）\n" +
            $"技能：{SkillHubText.DescribeSkillReference(entry.SkillId, entry.Version)}\n" +
            $"来源：{SkillHubText.DescribeActor(entry.ActorKind, entry.ActorId)}" +
            (entry.WorkspaceId.Length == 0 ? "" : $" · 工作区 {entry.WorkspaceId}") + "\n" +
            $"时间：{entry.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n" +
            (entry.PayloadJson.Length == 0 ? "载荷：无" : $"载荷：{entry.PayloadJson}");
    }

    private static TextBlock Muted(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = .8 };

    private void ReportSkillHubFailure(Exception exception)
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
        SetSkillHubEnabled(!unavailable);
        ShowNotice(_skNotice, severity, title, message);
        ShowNotice(_skEventsNotice, severity, title, message);
    }

    private void SetSkillHubEnabled(bool enabled)
    {
        foreach (var control in new Control[] { _skSkillFilter, _skPageSize, _skEventPicker }) control.IsEnabled = enabled;
    }
}
