using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PuddingDesktop.Foundation;

namespace PuddingDesktop;

/// <summary>
/// DS-10 approval slice: the exact allowlist rules Core uses for automatic approval, and the append-only
/// audit trail with its decision statistics.
/// </summary>
public sealed partial class MainWindow
{
    private IReadOnlyList<ApprovalRule> _alRules = [];
    private ApprovalRule? _alRule;
    private ApprovalStats? _alStats;
    private bool _alBuilt;
    private bool _alSwitching;

    private ComboBox _alRulePicker = null!, _alSource = null!, _alStatus = null!, _alEffect = null!;
    private TextBox _alToolId = null!, _alWorkspace = null!, _alCommand = null!, _alArguments = null!;
    private TextBox _alReason = null!, _alApprover = null!;
    private TextBlock _alRuleDetail = null!;
    private Button _alDisable = null!;
    private InfoBar _alNotice = null!;

    private TextBox _alAuditWorkspace = null!, _alAuditTool = null!, _alAuditEventType = null!;
    private ComboBox _alAuditLimit = null!;
    private TextBlock _alStatsText = null!, _alAuditSummary = null!;
    private StackPanel _alAuditLines = null!;

    private void BuildApprovalPanel()
    {
        if (_alBuilt) return;
        _alBuilt = true;

        _alRulePicker = new ComboBox { Header = "授权规则", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_alRulePicker, "选择授权规则");
        _alRulePicker.SelectionChanged += (_, _) => { if (!_alSwitching) ApplyApprovalRuleSelection(); };
        var refresh = new Button { Content = "刷新规则" };
        refresh.Click += async (_, _) => await LoadApprovalRulesAsync();
        var create = new Button { Content = "新建规则" };
        create.Click += (_, _) => StartNewApprovalRule();
        _alToolId = Field("toolId", "Core 会规范化工具 ID");
        _alWorkspace = Field("workspaceId", "留空表示全局规则");
        _alCommand = Field("命令（精确匹配）", "与参数 JSON 至少填一个");
        _alArguments = Field("参数 JSON（精确匹配）", "必须是合法 JSON");
        _alSource = Vocabulary("来源", SecurityText.RuleSources, SecurityText.DescribeRuleSource);
        _alStatus = Vocabulary("状态", SecurityText.RuleStatuses, value => value);
        _alEffect = Vocabulary("效果", SecurityText.RuleEffects, SecurityText.DescribeRuleEffect);
        _alApprover = Field("批准者（用户 / Agent / 工单）", "只用于溯源，可留空");
        _alReason = Field("理由");
        var save = new Button { Content = "保存规则" };
        save.Click += async (_, _) => await SaveApprovalRuleAsync();
        _alDisable = new Button { Content = "停用规则" };
        _alDisable.Click += async (_, _) => await DisableApprovalRuleAsync();
        _alRuleDetail = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 12 };
        _alNotice = new InfoBar { IsOpen = false, IsClosable = true };

        AllowlistSettings.Content = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(0, 16, 8, 16),
            Children =
            {
                Card("工具授权白名单", SecurityText.DisableIsNotDelete + " " + SecurityText.DenyBeatsAllow,
                    _alRulePicker, Row(refresh, create),
                    _alToolId, _alWorkspace, _alCommand, _alArguments, _alSource, _alStatus, _alEffect,
                    _alApprover, _alReason, _alRuleDetail, Row(save, _alDisable),
                    _alNotice)
            }
        };

        _alStatsText = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        _alAuditWorkspace = Field("工作区筛选", "留空表示全部");
        _alAuditTool = Field("工具筛选", "留空表示全部");
        _alAuditEventType = Field("事件类型筛选", "例如 allowlist_hit、ticket_denied");
        _alAuditLimit = new ComboBox { Header = "条数", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var limit in SecurityText.AuditLimits)
            _alAuditLimit.Items.Add(new ComboBoxItem { Content = limit.ToString(), Tag = limit });
        _alAuditLimit.SelectedIndex = 2;
        var auditRefresh = new Button { Content = "查询审计事件" };
        auditRefresh.Click += async (_, _) => await LoadApprovalAuditAsync();
        _alAuditSummary = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = .8, FontSize = 12 };
        _alAuditLines = new StackPanel { Spacing = 2 };

        // 两张卡都在 security/audit 页签：分类器健康 + 审批审计。
        var panel = (StackPanel)SecurityAuditSettings.Content;
        panel.Children.Insert(panel.Children.Count - 1, Card("审批审计与统计",
            SecurityText.AuditAppendOnly,
            _alStatsText, _alAuditWorkspace, _alAuditTool, _alAuditEventType, _alAuditLimit,
            Row(auditRefresh), _alAuditSummary, _alAuditLines));
    }

    private static ComboBox Vocabulary(string header, IReadOnlyList<string> values, Func<string, string> describe)
    {
        var picker = new ComboBox { Header = header, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var value in values) picker.Items.Add(new ComboBoxItem { Content = describe(value), Tag = value });
        picker.SelectedIndex = 0;
        return picker;
    }

    internal async Task LoadApprovalRulesAsync()
    {
        if (!_alBuilt) return;
        try
        {
            _alRules = await _security.ListApprovalRulesAsync(null, null, null);
            _alSwitching = true;
            try
            {
                var previous = AgentSelected(_alRulePicker);
                _alRulePicker.Items.Clear();
                foreach (var rule in _alRules)
                    _alRulePicker.Items.Add(new ComboBoxItem
                    {
                        Content = $"{rule.ToolId} · {rule.EffectText} · {rule.StatusText} · {rule.SourceText}",
                        Tag = rule.RuleId
                    });
                var index = _alRules.ToList().FindIndex(rule => rule.RuleId == previous);
                _alRulePicker.SelectedIndex = _alRules.Count > 0 ? Math.Max(0, index) : -1;
            }
            finally { _alSwitching = false; }
            ApplyApprovalRuleSelection();
            SetApprovalEnabled(true);
            _alNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportApprovalFailure(exception); }
    }

    private void ApplyApprovalRuleSelection()
    {
        _alRule = AgentSelected(_alRulePicker) is { } id
            ? _alRules.FirstOrDefault(rule => rule.RuleId == id)
            : null;
        var rule = _alRule;
        _alToolId.Text = rule?.ToolId ?? "";
        _alWorkspace.Text = rule?.WorkspaceId ?? "";
        _alCommand.Text = rule?.Command ?? "";
        _alArguments.Text = rule?.ArgumentsJson ?? "";
        _alReason.Text = rule?.Reason ?? "";
        _alApprover.Text = rule?.ApproverText is null or "无批准来源" ? "" : rule.ApproverText;
        _alSource.SelectedIndex = Math.Max(0, SecurityText.RuleSources.ToList().IndexOf(rule?.Source ?? "human"));
        _alStatus.SelectedIndex = Math.Max(0, SecurityText.RuleStatuses.ToList().IndexOf(rule?.Status ?? "enabled"));
        _alEffect.SelectedIndex = Math.Max(0, SecurityText.RuleEffects.ToList().IndexOf(rule?.Effect ?? "allow"));
        _alRuleDetail.Text = rule is null
            ? (_alRules.Count == 0 ? "还没有授权规则；可以新建一个。" : "请选择一条规则。")
            : $"ID {rule.RuleId} · {rule.MatchText}\n" +
              $"来源 {rule.SourceText} · {rule.EffectText} · {rule.StatusText}\n" +
              $"批准 {rule.ApproverText} · 命中 {rule.HitCount} 次" +
              (rule.LastHitAtUtc is null ? "" : $"（最近 {rule.LastHitAtUtc.Value.ToLocalTime():MM-dd HH:mm}）") + "\n" +
              $"定义版本 {rule.DefinitionVersion} · 创建 {rule.CreatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}" +
              (rule.UpdatedAtUtc is null ? "" : $" · 更新 {rule.UpdatedAtUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm}") +
              (rule.DisabledAtUtc is null ? "" : $" · 停用于 {rule.DisabledAtUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm}") +
              (rule.ExpiresAtUtc is null ? "" : $" · 有效期至 {rule.ExpiresAtUtc.Value.ToLocalTime():yyyy-MM-dd}") + "\n" +
              (rule.IsEnabled ? "" : SecurityText.DisableIsNotDelete);
        _alDisable.IsEnabled = rule is { IsEnabled: true };
    }

    private void StartNewApprovalRule()
    {
        _alSwitching = true;
        try { _alRulePicker.SelectedIndex = -1; }
        finally { _alSwitching = false; }
        _alRule = null;
        ApplyApprovalRuleSelection();
        ShowNotice(_alNotice, InfoBarSeverity.Informational, "新建规则",
            "命令与参数 JSON 是 Core 的精确匹配键，至少填一个。");
    }

    private async Task SaveApprovalRuleAsync()
    {
        var edit = new ApprovalRuleEdit(_alRule?.RuleId ?? "", _alWorkspace.Text.Trim(), _alToolId.Text.Trim(),
            _alCommand.Text.Trim(), _alArguments.Text.Trim(), AgentSelected(_alSource) ?? "human",
            AgentSelected(_alStatus) ?? "enabled", AgentSelected(_alEffect) ?? "allow",
            _alRule?.ApprovedByAgentInstanceId ?? "", _alRule?.ApprovedByUserId ?? "", _alRule?.ApprovalTicketId ?? "",
            _alReason.Text.Trim());
        var errors = SecurityText.Validate(edit);
        if (errors.Count > 0) { ShowNotice(_alNotice, InfoBarSeverity.Warning, "请先修正表单", string.Join(" ", errors)); return; }
        await RunApprovalAsync(edit.IsCreate ? "规则已创建" : "规则已保存", "规则改动会写入审计事件。",
            async () => { await _security.SaveApprovalRuleAsync(edit); await LoadApprovalRulesAsync(); });
    }

    private async Task DisableApprovalRuleAsync()
    {
        if (_alRule is not { } rule) { WarnApproval("请先选择规则"); return; }
        var confirm = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = "停用授权规则",
            Content = $"将把 {rule.ToolId} 的这条规则标记为已停用。{SecurityText.DisableIsNotDelete}",
            PrimaryButtonText = "停用", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        await RunApprovalAsync("规则已停用", SecurityText.DisableIsNotDelete,
            async () => { await _security.DisableApprovalRuleAsync(rule.RuleId); await LoadApprovalRulesAsync(); });
    }

    internal async Task LoadApprovalAuditAsync()
    {
        if (!_alBuilt) return;
        try
        {
            _alStats = await _security.ReadApprovalStatsAsync();
            _alStatsText.Text = _alStats.SummaryText;
            var limit = _alAuditLimit.SelectedItem is ComboBoxItem { Tag: int size } ? size : 200;
            var query = new ApprovalAuditQuery(_alAuditWorkspace.Text.Trim(), _alAuditTool.Text.Trim(),
                _alAuditEventType.Text.Trim(), limit);
            var events = await _security.ListApprovalAuditAsync(query);
            _alAuditLines.Children.Clear();
            _alAuditSummary.Text = events.Count == 0
                ? "没有匹配的审计事件。"
                : $"匹配 {events.Count} 条（上限 {limit}）；审计是追加写的，界面只读。";
            foreach (var entry in events.OrderByDescending(entry => entry.CreatedAtUtc).Take(60))
                _alAuditLines.Children.Add(Muted(
                    $"{entry.CreatedAtUtc.ToLocalTime():MM-dd HH:mm:ss} · {entry.EventTypeText}（{entry.EventType}）· " +
                    $"{entry.DecisionText} · {entry.TargetText}" +
                    (entry.WorkspaceId.Length == 0 ? "" : $" · 工作区 {entry.WorkspaceId}") +
                    (entry.Reason.Length == 0 ? "" : $" · {entry.Reason}")));
            SetApprovalEnabled(true);
            _alNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportApprovalFailure(exception); }
    }

    private void WarnApproval(string message) => ShowNotice(_alNotice, InfoBarSeverity.Warning, "请先选择", message);

    private async Task RunApprovalAsync(string title, string message, Func<Task> action)
    {
        SetApprovalEnabled(false);
        try
        {
            await action();
            SetApprovalEnabled(true);
            ShowNotice(_alNotice, InfoBarSeverity.Success, title, message);
        }
        catch (Exception exception) { ReportApprovalFailure(exception); }
    }

    private void ReportApprovalFailure(Exception exception)
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
        SetApprovalEnabled(!unavailable);
        ShowNotice(_alNotice, severity, title, message);
    }

    private void SetApprovalEnabled(bool enabled)
    {
        foreach (var control in new Control[]
        {
            _alRulePicker, _alToolId, _alWorkspace, _alCommand, _alArguments, _alSource, _alStatus, _alEffect,
            _alApprover, _alReason, _alAuditWorkspace, _alAuditTool, _alAuditEventType, _alAuditLimit
        }) control.IsEnabled = enabled;
        _alDisable.IsEnabled = enabled && (_alRule?.IsEnabled ?? false);
    }
}
