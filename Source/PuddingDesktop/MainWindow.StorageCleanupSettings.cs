using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PuddingDesktop.Foundation;

namespace PuddingDesktop;

/// <summary>
/// DS-09 cleanup slice: choose categories and a cutoff window, take Core's bounded preview, create a job
/// with an idempotency key, then confirm, cancel and watch progress and events.
/// </summary>
public sealed partial class MainWindow
{
    private StorageCleanupEstimate? _scPreview;
    private StorageCleanupRun? _scJob;
    private IReadOnlyList<StorageCleanupRun> _scJobs = [];
    private readonly List<string> _scTargets = [];
    private string _scRequestId = "";
    private bool _scBuilt;
    private bool _scSwitching;

    private ComboBox _scTargetPicker = null!, _scJobPicker = null!;
    private TextBox _scOlderThanDays = null!;
    private TextBlock _scTargetsSummary = null!, _scPreviewDetail = null!, _scJobDetail = null!;
    private StackPanel _scTargetPreviews = null!, _scEvents = null!;
    private Button _scCreateJob = null!, _scConfirm = null!, _scCancel = null!;
    private InfoBar _scNotice = null!;

    private void BuildStorageCleanupPanel()
    {
        if (_scBuilt) return;
        _scBuilt = true;

        _scTargetPicker = new ComboBox { Header = "可清理的数据类别", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_scTargetPicker, "选择要清理的数据类别");
        var add = new Button { Content = "加入选择" };
        add.Click += (_, _) => AddCleanupTarget();
        var clear = new Button { Content = "清空选择" };
        clear.Click += (_, _) => { _scTargets.Clear(); FillCleanupSummary(); };
        _scOlderThanDays = Field("早于天数", "留空表示不按时间限制（交给 Core 的截止时间）");
        var preview = new Button { Content = "生成预览" };
        preview.Click += async (_, _) => await CreateCleanupPreviewAsync();
        _scTargetsSummary = new TextBlock { TextWrapping = TextWrapping.Wrap };
        _scPreviewDetail = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 12 };
        _scTargetPreviews = new StackPanel { Spacing = 2 };
        _scCreateJob = new Button { Content = "创建清理作业" };
        _scCreateJob.Click += async (_, _) => await CreateCleanupJobAsync();
        _scNotice = new InfoBar { IsOpen = false, IsClosable = true };

        _scJobPicker = new ComboBox { Header = "清理作业", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_scJobPicker, "选择清理作业");
        _scJobPicker.SelectionChanged += async (_, _) => { if (!_scSwitching) await LoadCleanupJobAsync(); };
        var refresh = new Button { Content = "刷新作业" };
        refresh.Click += async (_, _) => await LoadCleanupJobsAsync();
        _scJobDetail = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        _scConfirm = new Button { Content = "确认执行" };
        _scConfirm.Click += async (_, _) => await ConfirmCleanupJobAsync();
        _scCancel = new Button { Content = "请求取消" };
        _scCancel.Click += async (_, _) => await CancelCleanupJobAsync();
        _scEvents = new StackPanel { Spacing = 2 };

        StorageCleanupSettings.Content = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(0, 16, 8, 16),
            Children =
            {
                Card("预览", StorageText.PreviewNotice + " " + StorageText.BudgetNotice,
                    _scTargetPicker, Row(add, clear), _scOlderThanDays, Row(preview),
                    _scTargetsSummary, _scPreviewDetail, _scTargetPreviews, Row(_scCreateJob)),
                Card("作业进度与事件", StorageText.JobNotice,
                    _scJobPicker, Row(refresh, _scConfirm, _scCancel), _scJobDetail, _scEvents),
                _scNotice
            }
        };
        SetStorageCleanupEnabled(false);
    }

    internal async Task LoadStorageCleanupAsync()
    {
        if (!_scBuilt) return;
        try
        {
            var classes = await _storage.ListDataClassesAsync();
            _scSwitching = true;
            try
            {
                var previous = AgentSelected(_scTargetPicker);
                _scTargetPicker.Items.Clear();
                // Only categories Core allows manual cleanup on can be previewed at all.
                foreach (var item in classes.Where(item => item.ManualCleanupAllowed))
                    _scTargetPicker.Items.Add(new ComboBoxItem
                    {
                        Content = $"{item.DisplayName}（{item.TargetId}）· {item.SafetyText}", Tag = item.TargetId
                    });
                var allowed = classes.Where(item => item.ManualCleanupAllowed).ToArray();
                var index = allowed.ToList().FindIndex(item => item.TargetId == previous);
                _scTargetPicker.SelectedIndex = allowed.Length > 0 ? Math.Max(0, index) : -1;
            }
            finally { _scSwitching = false; }
            FillCleanupSummary();
            await LoadCleanupJobsAsync();
            SetStorageCleanupEnabled(true);
            _scNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportStorageCleanupFailure(exception); }
    }

    private void AddCleanupTarget()
    {
        if (AgentSelected(_scTargetPicker) is not { } targetId) { WarnCleanup("请先选择一个数据类别"); return; }
        if (_scTargets.Contains(targetId, StringComparer.Ordinal)) { WarnCleanup($"{targetId} 已经在选择里"); return; }
        _scTargets.Add(targetId);
        FillCleanupSummary();
        _scNotice.IsOpen = false;
    }

    private void FillCleanupSummary()
    {
        _scTargetsSummary.Text = _scTargets.Count == 0
            ? "还没有选择类别：预览会因为没有目标而被 Core 拒绝，所以这里先拦住。"
            : $"已选择 {_scTargets.Count} 个类别：" + string.Join("、", _scTargets);
    }

    private async Task CreateCleanupPreviewAsync()
    {
        var errors = StorageText.ValidatePreview(_scTargets, _scOlderThanDays.Text);
        if (errors.Count > 0) { ShowNotice(_scNotice, InfoBarSeverity.Warning, "请先修正表单", string.Join(" ", errors)); return; }
        try
        {
            var olderThanDays = VoiceSettingsText.ParseOptionalInt(_scOlderThanDays.Text);
            _scPreview = await _storage.CreateCleanupPreviewAsync(_scTargets, olderThanDays);
            // A fresh preview gets a fresh idempotency key; pressing create twice reuses it.
            _scRequestId = $"desktop-{Guid.NewGuid():N}";

            var preview = _scPreview;
            _scPreviewDetail.Text =
                $"预览 {preview.PreviewId} · 目录版本 {preview.CatalogVersion} · 策略版本 {preview.PolicyRevision}\n" +
                $"截止时间 {preview.CutoffUtc.ToLocalTime():yyyy-MM-dd HH:mm} · " +
                $"生成 {preview.CreatedAtUtc.ToLocalTime():HH:mm:ss} · 过期 {preview.ExpiresAtUtc.ToLocalTime():HH:mm:ss}\n" +
                (preview.HasCandidates ? "Core 找到了候选数据。" : "Core 报告没有候选数据：创建作业也不会删除任何东西。") +
                (preview.Warnings.Count == 0 ? "" : "\n警告：" + string.Join("；", preview.Warnings));
            _scTargetPreviews.Children.Clear();
            foreach (var target in preview.Targets)
                _scTargetPreviews.Children.Add(Muted(
                    $"{target.DisplayName}（{target.TargetId}）· 候选 {target.CandidatesText} · " +
                    $"{StorageText.FormatBytes(target.EstimatedBytes)}" +
                    (target.OldestUtc is null ? "" : $" · 最早 {target.OldestUtc.Value.ToLocalTime():yyyy-MM-dd}") +
                    $" · {target.ActionSummary}"));

            _scCreateJob.IsEnabled = true;
            _scConfirm.IsEnabled = false;
            _scCancel.IsEnabled = false;
            ShowNotice(_scNotice, InfoBarSeverity.Success, "预览已生成",
                "预览会过期；过期后必须重新预览才能创建作业。");
            SetStorageCleanupEnabled(true);
        }
        catch (Exception exception) { ReportStorageCleanupFailure(exception); }
    }

    private async Task CreateCleanupJobAsync()
    {
        if (_scPreview is not { } preview) { WarnCleanup("请先生成预览"); return; }
        if (preview.IsExpired(DateTimeOffset.UtcNow))
        {
            ShowNotice(_scNotice, InfoBarSeverity.Warning, "预览已过期", "请重新生成预览后再创建作业。");
            return;
        }
        try
        {
            var jobId = await _storage.CreateCleanupJobAsync(preview.PreviewId, _scRequestId);
            await LoadCleanupJobsAsync();
            SelectCleanupJob(jobId);
            ShowNotice(_scNotice, InfoBarSeverity.Success, "作业已创建",
                "作业处于「需要确认」；确认后 Core 才会执行，取消可以随时请求。");
        }
        catch (Exception exception) { ReportStorageCleanupFailure(exception); }
    }

    private async Task LoadCleanupJobsAsync()
    {
        if (!_scBuilt) return;
        try
        {
            _scJobs = await _storage.ListCleanupJobsAsync();
            _scSwitching = true;
            try
            {
                var previous = _scJob?.JobId;
                _scJobPicker.Items.Clear();
                foreach (var job in _scJobs)
                    _scJobPicker.Items.Add(new ComboBoxItem
                    {
                        Content = $"{job.JobId.ToString()[..8]} · {job.StatusText} · {job.Trigger} · " +
                                  $"{job.CreatedAtUtc.ToLocalTime():MM-dd HH:mm}",
                        Tag = job.JobId
                    });
                var index = _scJobs.ToList().FindIndex(job => job.JobId == previous);
                _scJobPicker.SelectedIndex = _scJobs.Count > 0 ? Math.Max(0, index) : -1;
            }
            finally { _scSwitching = false; }
            await LoadCleanupJobAsync();
            SetStorageCleanupEnabled(true);
            _scNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportStorageCleanupFailure(exception); }
    }

    private void SelectCleanupJob(Guid jobId)
    {
        var item = _scJobPicker.Items.Cast<ComboBoxItem>().FirstOrDefault(candidate => (Guid?)candidate.Tag == jobId);
        if (item is not null) _scJobPicker.SelectedItem = item;
    }

    private async Task LoadCleanupJobAsync()
    {
        _scJob = _scJobPicker.SelectedItem is ComboBoxItem { Tag: Guid jobId } ? await _storage.ReadCleanupJobAsync(jobId) : null;
        var job = _scJob;
        if (job is null)
        {
            _scJobDetail.Text = _scJobs.Count == 0 ? "还没有清理作业。" : "请选择一个作业。";
            _scEvents.Children.Clear();
            _scConfirm.IsEnabled = false;
            _scCancel.IsEnabled = false;
            return;
        }

        _scJobDetail.Text =
            $"作业 {job.JobId} · 触发 {job.Trigger} · {job.StatusText}\n" +
            $"截止 {job.CutoffUtc.ToLocalTime():yyyy-MM-dd HH:mm} · 创建 {job.CreatedAtUtc.ToLocalTime():MM-dd HH:mm}" +
            (job.StartedAtUtc is null ? "" : $" · 开始 {job.StartedAtUtc.Value.ToLocalTime():MM-dd HH:mm}") +
            (job.FinishedAtUtc is null ? "" : $" · 结束 {job.FinishedAtUtc.Value.ToLocalTime():MM-dd HH:mm}") + "\n" +
            $"目标：{(job.TargetIds.Count == 0 ? "无" : string.Join("、", job.TargetIds))}\n" +
            $"{job.Progress.SummaryText}\n{job.Progress.RemainingText}" +
            (job.Warnings.Count == 0 ? "" : "\n警告：" + string.Join("；", job.Warnings)) +
            (job.ErrorMessage.Length == 0 ? "" : $"\n错误 {(job.ErrorCode.Length == 0 ? "" : job.ErrorCode + " ")}{job.ErrorMessage}");

        _scEvents.Children.Clear();
        var events = await _storage.ReadCleanupEventsAsync(job.JobId);
        if (events.Count == 0) _scEvents.Children.Add(Muted("该作业还没有事件。"));
        foreach (var item in events.OrderByDescending(item => item.TimestampUtc).Take(40))
            _scEvents.Children.Add(Muted(
                $"{item.TimestampUtc.ToLocalTime():MM-dd HH:mm:ss} · {item.KindText}" +
                (item.TargetId.Length == 0 ? "" : $" · {item.TargetId}") +
                (item.Message.Length == 0 ? "" : $" · {item.Message}") + item.CountersText));

        _scConfirm.IsEnabled = job.NeedsConfirmation;
        _scCancel.IsEnabled = job.CanCancel;
        _scCreateJob.IsEnabled = _scPreview is not null;
    }

    private async Task ConfirmCleanupJobAsync()
    {
        if (_scJob is not { } job) { WarnCleanup("请先选择作业"); return; }
        var confirm = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = "确认执行清理",
            Content = $"将确认作业 {job.JobId} 并按预览的范围删除/清空目标数据（截止 {job.CutoffUtc.ToLocalTime():yyyy-MM-dd HH:mm}）。" +
                      "删除的数据无法恢复。执行期间可以请求取消，但已处理的部分不会回滚。",
            PrimaryButtonText = "确认执行", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        await RunStorageCleanupAsync("已确认执行", "作业已进入队列，进度需要刷新查看。",
            async () =>
            {
                await _storage.ConfirmCleanupJobAsync(job.JobId);
                await LoadCleanupJobsAsync();
            });
    }

    private async Task CancelCleanupJobAsync()
    {
        if (_scJob is not { } job) { WarnCleanup("请先选择作业"); return; }
        await RunStorageCleanupAsync("已请求取消", "取消是请求：Core 会在安全检查点停止，已处理的部分不会回滚。",
            async () =>
            {
                await _storage.CancelCleanupJobAsync(job.JobId);
                await LoadCleanupJobsAsync();
            });
    }

    private void WarnCleanup(string message) => ShowNotice(_scNotice, InfoBarSeverity.Warning, "请先选择", message);

    private async Task RunStorageCleanupAsync(string title, string message, Func<Task> action)
    {
        SetStorageCleanupEnabled(false);
        try
        {
            await action();
            SetStorageCleanupEnabled(true);
            ShowNotice(_scNotice, InfoBarSeverity.Success, title, message);
        }
        catch (Exception exception) { ReportStorageCleanupFailure(exception); }
    }

    private void ReportStorageCleanupFailure(Exception exception)
    {
        App.WriteDiagnostic(exception);
        var unavailable = exception is SettingsUnavailableException;
        var (severity, title, message) = exception switch
        {
            SettingsUnavailableException reason => (InfoBarSeverity.Informational, "Core 未就绪",
                reason.Message + " 该分类仍可浏览；表单已禁用，未写入任何内容。"),
            SettingsConflictException conflict => (InfoBarSeverity.Warning, "预览或策略已变化，已阻止执行", conflict.Message),
            ArgumentException argument => (InfoBarSeverity.Warning, "请求被 Core 拒绝", argument.Message),
            InvalidOperationException invalid => (InfoBarSeverity.Warning, "Core 拒绝了该操作", invalid.Message),
            _ => (InfoBarSeverity.Error, "操作失败", "请查看诊断日志后重试。")
        };
        SetStorageCleanupEnabled(!unavailable);
        ShowNotice(_scNotice, severity, title, message);
    }

    private void SetStorageCleanupEnabled(bool enabled)
    {
        foreach (var control in new Control[] { _scTargetPicker, _scOlderThanDays, _scJobPicker })
            control.IsEnabled = enabled;
        if (!enabled)
        {
            _scCreateJob.IsEnabled = false;
            _scConfirm.IsEnabled = false;
            _scCancel.IsEnabled = false;
            return;
        }
        _scCreateJob.IsEnabled = _scPreview is not null && !_scPreview.IsExpired(DateTimeOffset.UtcNow);
        _scConfirm.IsEnabled = _scJob?.NeedsConfirmation ?? false;
        _scCancel.IsEnabled = _scJob?.CanCancel ?? false;
    }
}
