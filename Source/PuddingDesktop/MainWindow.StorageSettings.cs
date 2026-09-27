using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PuddingDesktop.Foundation;

namespace PuddingDesktop;

/// <summary>
/// DS-09 inventory and policy slice: Core's cached storage snapshot (reading never scans), the sampled
/// trend, the data-class catalogue with its protections, and the retention policy edited with CAS.
/// </summary>
public sealed partial class MainWindow
{
    private StorageSnapshot? _stSnapshot;
    private StorageRetentionPolicy? _stPolicy;
    private StorageRetentionTarget? _stTarget;
    private IReadOnlyList<StorageDataClass> _stDataClasses = [];
    private IReadOnlyList<string> _stProtected = [];
    private bool _stBuilt;
    private bool _stSwitching;

    private TextBlock _stSnapshotSummary = null!, _stRefreshStatus = null!, _stPolicySummary = null!, _stTargetDetail = null!;
    private StackPanel _stDatabases = null!, _stClasses = null!, _stCatalogue = null!, _stProtectedList = null!, _stTrend = null!;
    private ComboBox _stTrendRange = null!, _stTargetPicker = null!;
    private ToggleSwitch _stAutomatic = null!, _stTargetEnabled = null!;
    private TextBox _stRetentionDays = null!;
    private Button _stSavePolicy = null!;
    private InfoBar _stNotice = null!;
    private InfoBar _stPolicyNotice = null!;

    private void BuildStoragePanel()
    {
        if (_stBuilt) return;
        _stBuilt = true;

        var refresh = new Button { Content = "请求刷新" };
        refresh.Click += async (_, _) => await RequestStorageRefreshAsync();
        _stSnapshotSummary = new TextBlock { TextWrapping = TextWrapping.Wrap };
        _stRefreshStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = .8, FontSize = 12 };
        _stDatabases = new StackPanel { Spacing = 2 };
        _stClasses = new StackPanel { Spacing = 2 };
        _stCatalogue = new StackPanel { Spacing = 2 };
        _stProtectedList = new StackPanel { Spacing = 2 };
        _stTrendRange = new ComboBox { Header = "趋势窗口", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var days in StorageText.TrendRanges)
            _stTrendRange.Items.Add(new ComboBoxItem { Content = $"{days} 天", Tag = days });
        _stTrendRange.SelectedIndex = 1;
        var trend = new Button { Content = "读取趋势" };
        trend.Click += async (_, _) => await LoadStorageTrendAsync();
        _stTrend = new StackPanel { Spacing = 2 };
        _stNotice = new InfoBar { IsOpen = false, IsClosable = true };
        _stPolicyNotice = new InfoBar { IsOpen = false, IsClosable = true };


        StorageOverviewSettings.Content = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(0, 16, 8, 16),
            Children =
            {
                Card("存储占用", StorageText.SnapshotNotice, Row(refresh), _stSnapshotSummary, _stRefreshStatus,
                    new TextBlock { Text = "数据库", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }, _stDatabases,
                    new TextBlock { Text = "分类占用", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }, _stClasses),
                Card("分类目录与保护", StorageText.EstimateNotice + " " + StorageText.ProtectedNotice,
                    _stCatalogue,
                    new TextBlock { Text = "受保护对象", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }, _stProtectedList,
                    _stTrendRange, Row(trend),
                    new TextBlock { Text = "趋势采样点", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }, _stTrend),
                _stNotice
            }
        };

        _stPolicySummary = new TextBlock { TextWrapping = TextWrapping.Wrap };
        _stAutomatic = new ToggleSwitch
        {
            Header = "启用自动清理", OnContent = "已启用", OffContent = "已停用", IsOn = false
        };
        _stTargetPicker = new ComboBox { Header = "目标分类", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_stTargetPicker, "选择保留策略目标");
        _stTargetPicker.SelectionChanged += (_, _) => { if (!_stSwitching) ApplyStorageTargetSelection(); };
        _stTargetEnabled = new ToggleSwitch { Header = "该分类启用自动清理", OnContent = "启用", OffContent = "未启用", IsOn = false };
        _stRetentionDays = Field("保留天数", "必须大于 0；停用请用上面的开关而不是填 0");
        _stTargetDetail = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = .8, FontSize = 12 };
        _stSavePolicy = new Button { Content = "保存策略" };
        _stSavePolicy.Click += async (_, _) => await SaveStoragePolicyAsync();
        var reload = new Button { Content = "重新读取策略" };
        reload.Click += async (_, _) => await LoadStoragePolicyAsync();

        StoragePolicySettings.Content = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(0, 16, 8, 16),
            Children =
            {
                Card("保留策略", StorageText.PolicyRevisionNotice,
                    _stPolicySummary, _stAutomatic,
                    _stTargetPicker, _stTargetEnabled, _stRetentionDays, _stTargetDetail,
                    Row(_stSavePolicy, reload)),
                _stPolicyNotice
            }
        };
        SetStorageEnabled(false);
    }

    internal async Task LoadStorageOverviewAsync()
    {
        if (!_stBuilt) return;
        try
        {
            _stSnapshot = await _storage.ReadSnapshotAsync();
            _stDataClasses = await _storage.ListDataClassesAsync();
            _stProtected = await _storage.ListProtectedObjectsAsync();
            RenderStorageSnapshot();
            RenderStorageCatalogue();
            SetStorageEnabled(true);
            _stNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportStorageFailure(exception); }
    }

    private void RenderStorageSnapshot()
    {
        var snapshot = _stSnapshot;
        if (snapshot is null) { _stSnapshotSummary.Text = "没有快照数据。"; return; }
        _stSnapshotSummary.Text =
            $"总占用 {StorageText.FormatBytes(snapshot.TotalBytes)}（数据库 {StorageText.FormatBytes(snapshot.DatabaseBytes)} · " +
            $"分类 {StorageText.FormatBytes(snapshot.ClassBytes)}）\n" +
            $"快照 revision {snapshot.Revision} · schema {snapshot.SchemaVersion} · " +
            $"采集 {snapshot.CapturedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm} · 更新 {snapshot.UpdatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}\n" +
            (snapshot.IsRefreshing ? "当前正在后台刷新。\n" : "") +
            (snapshot.Warnings.Count == 0 ? "" : $"警告 {snapshot.Warnings.Count} 条：" + string.Join("；", snapshot.Warnings));
        if (snapshot.IsEmpty) _stSnapshotSummary.Text += "\n（快照还是空的：Core 尚未完成首次采样。）";

        _stDatabases.Children.Clear();
        if (snapshot.Databases.Count == 0) _stDatabases.Children.Add(Muted("没有数据库条目。"));
        foreach (var database in snapshot.Databases)
            _stDatabases.Children.Add(Muted(
                $"{database.DisplayName}（{database.RelativePath}）· 合计 {StorageText.FormatBytes(database.TotalBytes)} · " +
                $"main {StorageText.FormatBytes(database.MainBytes)} · wal {StorageText.FormatBytes(database.WalBytes)} · " +
                $"可复用空闲 {StorageText.FormatBytes(database.ReusableFreeBytes)} · " +
                $"占比 {StorageText.DescribeShare(database.TotalBytes, snapshot.TotalBytes)}"));

        _stClasses.Children.Clear();
        var measured = snapshot.Classes.OrderByDescending(item => item.EstimatedBytes ?? -1).ToArray();
        if (measured.Length == 0) _stClasses.Children.Add(Muted("没有分类条目。"));
        foreach (var item in measured)
            _stClasses.Children.Add(Muted(
                $"{item.DisplayName}（{item.TargetId}）· {item.SizeText} · {StorageText.DescribeShare(item.EstimatedBytes, snapshot.TotalBytes)} · " +
                $"{item.EstimateText} · {item.RowsText}"));
    }

    private void RenderStorageCatalogue()
    {
        _stCatalogue.Children.Clear();
        if (_stDataClasses.Count == 0) _stCatalogue.Children.Add(Muted("Core 没有返回分类目录。"));
        foreach (var item in _stDataClasses)
            _stCatalogue.Children.Add(Muted(
                $"{item.DisplayName}（{item.TargetId}）· {item.SafetyText} · {item.CleanupText} · " +
                $"目录版本 {item.CatalogVersion}" +
                (item.Protected ? $" · 受保护：{item.ProtectionReason}" : "") +
                (item.DefaultRetentionDays is null ? "" : $" · 默认保留 {item.DefaultRetentionDays} 天")));

        _stProtectedList.Children.Clear();
        if (_stProtected.Count == 0) _stProtectedList.Children.Add(Muted("没有受保护对象。"));
        foreach (var target in _stProtected) _stProtectedList.Children.Add(Muted(target));
    }

    private async Task RequestStorageRefreshAsync()
    {
        try
        {
            var status = await _storage.RequestRefreshAsync();
            _stRefreshStatus.Text = $"刷新请求 {status.RefreshId}：{status.StateText} · " +
                                    $"请求于 {status.RequestedAtUtc.ToLocalTime():HH:mm:ss}" +
                                    (status.CompletedAtUtc is null ? "" : $" · 完成于 {status.CompletedAtUtc.Value.ToLocalTime():HH:mm:ss}") +
                                    $" · 快照 revision {status.SnapshotRevision}";
            // The snapshot is cached; re-reading shows whatever the sampler has produced so far.
            await LoadStorageOverviewAsync();
            ShowNotice(_stNotice, InfoBarSeverity.Success, "已请求刷新",
                "刷新在 Core 后台进行；快照读到的仍是最近一次完成的结果。");
        }
        catch (Exception exception) { ReportStorageFailure(exception); }
    }

    private async Task LoadStorageTrendAsync()
    {
        try
        {
            var days = _stTrendRange.SelectedItem is ComboBoxItem { Tag: int value } ? value : 30;
            var points = await _storage.ReadTrendAsync(days);
            _stTrend.Children.Clear();
            if (points.Count == 0)
            {
                _stTrend.Children.Add(Muted($"最近 {days} 天还没有采样点。"));
                return;
            }
            foreach (var point in points.OrderByDescending(point => point.CapturedAtUtc).Take(30))
                _stTrend.Children.Add(Muted(
                    $"{point.CapturedAtUtc.ToLocalTime():MM-dd HH:mm} · 数据库 {StorageText.FormatBytes(point.DatabaseTotalBytes)} · " +
                    $"分类 {point.ClassBytes.Count} 项"));
            _stTrend.Children.Add(Muted($"共 {points.Count} 个采样点（按时间倒序显示最近 30 个）。"));
            _stNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportStorageFailure(exception); }
    }

    internal async Task LoadStoragePolicyAsync()
    {
        if (!_stBuilt) return;
        try
        {
            _stPolicy = await _storage.ReadPolicyAsync();
            var policy = _stPolicy;
            _stPolicySummary.Text =
                $"策略版本 {policy.PolicyRevision} · 自动清理 {(policy.AutomaticCleanupEnabled ? "已启用" : "已停用")} · " +
                $"间隔 {policy.RunIntervalHours} 小时 · 启动延迟 {policy.StartupDelaySeconds} 秒\n" +
                $"上次完成 {(policy.LastCompletedAtUtc is null ? "从未" : policy.LastCompletedAtUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))} · " +
                $"下次预计 {(policy.NextRunEstimateUtc is null ? "未排期" : policy.NextRunEstimateUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))}" +
                (policy.Warnings.Count == 0 ? "" : "\n警告：" + string.Join("；", policy.Warnings));
            _stSwitching = true;
            try
            {
                var previous = AgentSelected(_stTargetPicker);
                _stAutomatic.IsOn = policy.AutomaticCleanupEnabled;
                _stTargetPicker.Items.Clear();
                foreach (var target in policy.Targets)
                    _stTargetPicker.Items.Add(new ComboBoxItem
                    {
                        Content = $"{target.DisplayName}（{target.TargetId}）· {target.EffectiveText}", Tag = target.TargetId
                    });
                var index = policy.Targets.ToList().FindIndex(target => target.TargetId == previous);
                _stTargetPicker.SelectedIndex = policy.Targets.Count > 0 ? Math.Max(0, index) : -1;
            }
            finally { _stSwitching = false; }
            ApplyStorageTargetSelection();
            SetStorageEnabled(true);
            _stNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportStorageFailure(exception); }
    }

    private void ApplyStorageTargetSelection()
    {
        _stTarget = AgentSelected(_stTargetPicker) is { } targetId
            ? _stPolicy?.Targets.FirstOrDefault(target => target.TargetId == targetId)
            : null;
        var target = _stTarget;
        _stSwitching = true;
        try
        {
            _stTargetEnabled.IsOn = target?.Enabled ?? false;
            _stRetentionDays.Text = target?.RetentionDays?.ToString() ?? "";
        }
        finally { _stSwitching = false; }
        _stTargetDetail.Text = target is null
            ? (_stPolicy is null ? "请先读取策略。" : "请选择一个目标分类。")
            : $"{target.DisplayName}（{target.TargetId}）· {target.RangeText} · " +
              (target.AutomaticCleanupAllowed ? "允许自动清理" : "Core 不允许该分类自动清理");
        _stTargetEnabled.IsEnabled = target?.AutomaticCleanupAllowed ?? false;
        _stRetentionDays.IsEnabled = target?.AutomaticCleanupAllowed ?? false;
        _stSavePolicy.IsEnabled = _stPolicy is not null;
    }

    private async Task SaveStoragePolicyAsync()
    {
        if (_stPolicy is not { } policy) { WarnStorage("请先读取策略"); return; }
        if (_stTarget is not { } selected) { WarnStorage("请先选择目标分类"); return; }

        var enabled = _stTargetEnabled.IsOn;
        var retentionDays = VoiceSettingsText.ParseOptionalInt(_stRetentionDays.Text);
        var targetErrors = StorageText.ValidateTarget(selected, enabled, retentionDays);
        if (targetErrors.Count > 0)
        {
            ShowNotice(_stNotice, InfoBarSeverity.Warning, "请先修正表单",
                $"{selected.DisplayName}：" + string.Join(" ", targetErrors));
            return;
        }

        // Core takes the full target list; the page submits every target it read, with one edited.
        var targets = policy.Targets.Select(target => target.TargetId == selected.TargetId
            ? new StorageRetentionTargetEdit(selected.TargetId, enabled, enabled ? retentionDays : null)
            : new StorageRetentionTargetEdit(target.TargetId, target.Enabled, target.RetentionDays)).ToArray();
        var update = new StorageRetentionPolicyUpdate(policy.PolicyRevision, _stAutomatic.IsOn, targets);
        var errors = StorageText.Validate(update);
        if (errors.Count > 0) { ShowNotice(_stNotice, InfoBarSeverity.Warning, "请先修正表单", string.Join(" ", errors)); return; }

        SetStorageEnabled(false);
        try
        {
            await _storage.SavePolicyAsync(update);
            SetStorageEnabled(true);
            await LoadStoragePolicyAsync();
            ShowNotice(_stNotice, InfoBarSeverity.Success, "策略已保存",
                $"提交时使用的策略版本是 {policy.PolicyRevision}，版本号会由 Core 递增。");
        }
        catch (Exception exception) { ReportStorageFailure(exception); }
    }

    private void WarnStorage(string message) => ShowNotice(_stNotice, InfoBarSeverity.Warning, "请先选择", message);

    private void ReportStorageFailure(Exception exception)
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
        SetStorageEnabled(!unavailable);
        ShowNotice(_stNotice, severity, title, message);
        ShowNotice(_stPolicyNotice, severity, title, message);
    }

    private void SetStorageEnabled(bool enabled)
    {
        foreach (var control in new Control[] { _stTrendRange, _stTargetPicker, _stAutomatic })
            control.IsEnabled = enabled;
        if (!enabled)
        {
            _stTargetEnabled.IsEnabled = false;
            _stRetentionDays.IsEnabled = false;
            _stSavePolicy.IsEnabled = false;
            return;
        }
        var allowed = _stTarget?.AutomaticCleanupAllowed ?? false;
        _stTargetEnabled.IsEnabled = allowed;
        _stRetentionDays.IsEnabled = allowed;
        _stSavePolicy.IsEnabled = _stPolicy is not null && _stTarget is not null;
    }
}
