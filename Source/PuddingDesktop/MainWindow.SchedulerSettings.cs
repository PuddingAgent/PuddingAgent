using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PuddingDesktop.Foundation;

namespace PuddingDesktop;

/// <summary>
/// DS-16 scheduler slice: policy editing with CAS on the read revision, pause/resume, manual scan and repair,
/// plus the status card that shows what a scan actually did (not just what it decided).
/// </summary>
public sealed partial class MainWindow
{
    private SchedulerStatus? _schStatus;
    private IReadOnlyList<WorkspaceSummary> _schWorkspaces = [];
    private bool _schBuilt;
    private bool _schSwitching;

    private ComboBox _schWorkspace = null!, _schMode = null!;
    private ToggleSwitch _schEnabled = null!, _schPaused = null!, _schEventDriven = null!;
    private TextBox _schInterval = null!, _schCandidates = null!, _schStarts = null!;
    private TextBlock _schPolicyText = null!, _schPrereqText = null!, _schStateText = null!, _schScanText = null!;
    private TextBlock _schTrackerText = null!, _schCodesText = null!, _schNextText = null!, _schErrorText = null!;
    private Button _schSave = null!, _schPause = null!, _schResume = null!, _schScan = null!, _schRepair = null!;
    private InfoBar _schNotice = null!;

    private void BuildSchedulerPanel()
    {
        if (_schBuilt) return;
        _schBuilt = true;

        _schWorkspace = new ComboBox { Header = "工作区（调度按工作区隔离）", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_schWorkspace, "选择工作区");
        _schWorkspace.SelectionChanged += async (_, _) => { if (!_schSwitching) await LoadSchedulerStatusAsync(); };
        var refresh = new Button { Content = "刷新状态" };
        refresh.Click += async (_, _) => await LoadSchedulerStatusAsync();
        _schEnabled = new ToggleSwitch { Header = "启用后台调度", OnContent = "已启用", OffContent = "已关闭" };
        _schPaused = new ToggleSwitch { Header = "暂停该工作区", OnContent = "已暂停", OffContent = "未暂停" };
        _schEventDriven = new ToggleSwitch { Header = "事件驱动扫描", OnContent = "已启用", OffContent = "已关闭" };
        _schMode = new ComboBox { Header = "模式", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var mode in SchedulerText.Modes)
            _schMode.Items.Add(new ComboBoxItem { Content = SchedulerText.DescribeMode(mode), Tag = mode });
        _schInterval = Field("扫描间隔（秒）", "1–3600（Core 允许 1 秒–1 小时）");
        _schCandidates = Field("候选上限", "1–500");
        _schStarts = Field("单轮启动上限", "1–32；authoritative-single 会强制为 1");
        _schPolicyText = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 12 };
        _schPrereqText = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = .85, FontSize = 12 };
        _schSave = new Button { Content = "保存策略（CAS）" };
        _schSave.Click += async (_, _) => await SaveSchedulerPolicyAsync();
        _schPause = new Button { Content = "暂停" };
        _schPause.Click += async (_, _) => await SetSchedulerPausedAsync(true);
        _schResume = new Button { Content = "恢复" };
        _schResume.Click += async (_, _) => await SetSchedulerPausedAsync(false);

        _schStateText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        _schScanText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        _schTrackerText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        _schCodesText = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = .85, FontSize = 12 };
        _schNextText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        _schErrorText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        _schScan = new Button { Content = "立即扫描" };
        _schScan.Click += async (_, _) => await RunSchedulerScanAsync(repair: false);
        _schRepair = new Button { Content = "立即修复" };
        _schRepair.Click += async (_, _) => await RunSchedulerScanAsync(repair: true);
        _schNotice = new InfoBar { IsOpen = false, IsClosable = true };

        SchedulerSettings.Content = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(0, 16, 8, 16),
            Children =
            {
                Card("调度策略", SchedulerText.RevisionNotice + " " + SchedulerText.ModeNotice + " " +
                    SchedulerText.AuthoritativeNotice + " " + SchedulerText.MaxStartsNotice,
                    _schWorkspace, Row(refresh), _schEnabled, _schPaused, _schEventDriven, _schMode,
                    _schInterval, _schCandidates, _schStarts, _schPolicyText, _schPrereqText,
                    Row(_schSave, _schPause, _schResume)),
                Card("本轮调度状态", SchedulerText.ExecuteNotice + " " + SchedulerText.ManualScanNotice,
                    _schStateText, _schScanText, _schTrackerText, _schCodesText, _schNextText, _schErrorText,
                    Row(_schScan, _schRepair)),
                _schNotice
            }
        };
        SetSchedulerEnabled(false);
    }

    internal async Task LoadSchedulerStatusAsync()
    {
        if (!_schBuilt) return;
        try
        {
            if (_schWorkspaces.Count == 0)
                _schWorkspaces = await _workspaces.ListAsync();

            _schSwitching = true;
            try
            {
                if (_schWorkspace.Items.Count != _schWorkspaces.Count)
                {
                    var previous = AgentSelected(_schWorkspace);
                    _schWorkspace.Items.Clear();
                    foreach (var workspace in _schWorkspaces)
                        _schWorkspace.Items.Add(new ComboBoxItem
                        {
                            Content = $"{workspace.WorkspaceId} · {workspace.StateText}", Tag = workspace.WorkspaceId
                        });
                    var index = _schWorkspaces.ToList().FindIndex(item => item.WorkspaceId == previous);
                    _schWorkspace.SelectedIndex = _schWorkspaces.Count > 0 ? Math.Max(0, index) : -1;
                }
            }
            finally { _schSwitching = false; }

            if (AgentSelected(_schWorkspace) is not { } workspaceId)
            {
                _schStatus = null;
                _schStateText.Text = "没有工作区可选：调度按工作区隔离。";
                SetSchedulerEnabled(true);
                return;
            }

            _schStatus = await _scheduler.GetStatusAsync(workspaceId);
            FillSchedulerForm(_schStatus);
            SetSchedulerEnabled(true);
            _schNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportSchedulerFailure(exception); }
    }

    private void FillSchedulerForm(SchedulerStatus status)
    {
        var policy = status.Policy;
        _schEnabled.IsOn = policy.Enabled;
        _schPaused.IsOn = policy.Paused;
        _schEventDriven.IsOn = policy.EventDrivenEnabled;
        _schMode.SelectedIndex = Math.Max(0, SchedulerText.Modes.ToList().FindIndex(mode => mode == policy.Mode));
        _schInterval.Text = policy.ScanIntervalSeconds.ToString();
        _schCandidates.Text = policy.CandidateLimit.ToString();
        _schStarts.Text = policy.MaxStartsPerScan.ToString();

        var effective = SchedulerText.EffectiveMaxStarts(policy);
        _schPolicyText.Text =
            $"revision {policy.Revision} · {policy.SwitchText} · 模式 {policy.ModeText}\n" +
            $"间隔 {policy.IntervalText} · 候选上限 {policy.CandidateLimit} · " +
            $"单轮启动上限 {policy.MaxStartsPerScan}" +
            (effective != policy.MaxStartsPerScan ? $"（当前模式实际用 {effective}）" : "") +
            $" · 事件驱动 {SchedulerText.OnOff(policy.EventDrivenEnabled)}";
        _schPrereqText.Text = "前置：" + status.Prerequisites.DescribeText +
            (status.Prerequisites.AuthoritativeReady ? "（authoritative 系可用）" : "（authoritative 系会被 Core 拒绝）");

        _schStateText.Text = $"工作区 {status.WorkspaceId} · 状态 {status.StateText}" +
            (status.IsFaulted ? " ⚠" : "");
        var scan = status.LastScan;
        _schScanText.Text = scan is null
            ? "还没有扫描记录（可点「立即扫描」触发一次）。"
            : $"{scan.TriggerText} · {scan.TimeText} · 模式 {SchedulerText.DescribeMode(scan.Mode)}\n" +
              $"{scan.AgentText} · 候选池 {scan.Backlog}（可精化 {scan.RefinementReady} / 需精化 {scan.NeedsRefinement} / 已提升 {scan.Promoted}）\n" +
              scan.CandidateText + (scan.StartedSomething ? " · 本轮确实启动了任务" : " · 本轮没有启动任务");
        _schTrackerText.Text = scan is null ? "跟踪器：无数据" : scan.TrackerText + " · " + scan.RepairText;
        _schCodesText.Text = scan is null ? "" : scan.CodesText;
        _schNextText.Text = status.NextScanText;
        _schErrorText.Text = "最近错误：" + status.LastErrorText;
    }

    private async Task SaveSchedulerPolicyAsync()
    {
        if (_schStatus is not { } status || AgentSelected(_schWorkspace) is not { } workspaceId) { WarnScheduler("请先选择工作区"); return; }
        var edit = SchedulerText.Normalize(new SchedulerPolicyEdit(
            status.Policy.Revision, _schEnabled.IsOn, _schPaused.IsOn, AgentSelected(_schMode) ?? status.Policy.Mode,
            ParseInt(_schInterval.Text, status.Policy.ScanIntervalSeconds),
            ParseInt(_schCandidates.Text, status.Policy.CandidateLimit),
            ParseInt(_schStarts.Text, status.Policy.MaxStartsPerScan), _schEventDriven.IsOn));
        var errors = SchedulerText.Validate(edit, status.Prerequisites);
        if (errors.Count > 0) { ShowNotice(_schNotice, InfoBarSeverity.Warning, "请先修正表单", string.Join(" ", errors)); return; }
        await RunSchedulerAsync("策略已保存", "revision 已前进；下一次读取会看到新值。",
            async () => { _schStatus = await _scheduler.SavePolicyAsync(workspaceId, edit); FillSchedulerForm(_schStatus); });
    }

    private async Task SetSchedulerPausedAsync(bool paused)
    {
        if (_schStatus is not { } status || AgentSelected(_schWorkspace) is not { } workspaceId) { WarnScheduler("请先选择工作区"); return; }
        await RunSchedulerAsync(paused ? "已暂停" : "已恢复",
            paused ? "暂停只影响自动派发；状态查询、评估与手动修复仍可用。" : "恢复后后台扫描会重新参与。",
            async () => { _schStatus = await _scheduler.SetPausedAsync(workspaceId, paused, status.Policy.Revision); FillSchedulerForm(_schStatus); });
    }

    private async Task RunSchedulerScanAsync(bool repair)
    {
        if (AgentSelected(_schWorkspace) is not { } workspaceId) { WarnScheduler("请先选择工作区"); return; }
        await RunSchedulerAsync(repair ? "修复已完成" : "扫描已完成", SchedulerText.ExecuteNotice,
            async () =>
            {
                var summary = repair
                    ? await _scheduler.RunRepairAsync(workspaceId)
                    : await _scheduler.RunScanAsync(workspaceId);
                _schStatus = await _scheduler.GetStatusAsync(workspaceId);
                FillSchedulerForm(_schStatus);
                ShowNotice(_schNotice, InfoBarSeverity.Success, repair ? "修复已完成" : "扫描已完成",
                    (repair ? summary.RepairText : summary.CandidateText + " · " + summary.TrackerText));
            });
    }

    private static int ParseInt(string text, int fallback) =>
        int.TryParse(text.Trim(), out var value) ? value : fallback;

    private void WarnScheduler(string message) => ShowNotice(_schNotice, InfoBarSeverity.Warning, "请先选择", message);

    private async Task RunSchedulerAsync(string title, string message, Func<Task> action)
    {
        SetSchedulerEnabled(false);
        try
        {
            await action();
            SetSchedulerEnabled(true);
            ShowNotice(_schNotice, InfoBarSeverity.Success, title, message);
        }
        catch (Exception exception) { ReportSchedulerFailure(exception); }
    }

    private void ReportSchedulerFailure(Exception exception)
    {
        App.WriteDiagnostic(exception);
        var unavailable = exception is SettingsUnavailableException;
        var (severity, title, message) = exception switch
        {
            SettingsUnavailableException reason => (InfoBarSeverity.Informational, "Core 未就绪",
                reason.Message + " 该分类仍可浏览；表单已禁用，未写入任何内容。"),
            SettingsConflictException conflict => (InfoBarSeverity.Warning, "策略版本冲突，已阻止覆盖", conflict.Message),
            ArgumentException argument => (InfoBarSeverity.Warning, "请求被 Core 拒绝", argument.Message),
            InvalidOperationException invalid => (InfoBarSeverity.Warning, "Core 拒绝了该操作", invalid.Message),
            _ => (InfoBarSeverity.Error, "操作失败", "请查看诊断日志后重试。")
        };
        SetSchedulerEnabled(!unavailable);
        ShowNotice(_schNotice, severity, title, message);
    }

    private void SetSchedulerEnabled(bool enabled)
    {
        foreach (var control in new Control[]
        {
            _schWorkspace, _schEnabled, _schPaused, _schEventDriven, _schMode, _schInterval, _schCandidates, _schStarts
        }) control.IsEnabled = enabled;
        foreach (var button in new[] { _schSave, _schPause, _schResume, _schScan, _schRepair }) button.IsEnabled = enabled;
    }
}
