using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using PuddingDesktop.Storage;

namespace PuddingDesktop.ViewModels;

public sealed class StorageViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IStorageAnalysisService _analysisService;
    private readonly ILogRetentionService _logRetentionService;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private CancellationTokenSource? _operationCts;

    private string? _dataRoot;
    private bool _isBusy;
    private string _statusText = "等待扫描";
    private string? _errorText;
    private string _totalSizeText = "—";
    private string _driveSummaryText = "尚未读取磁盘容量";
    private string _lastScanText = "尚未扫描";
    private string _warningText = "";
    private double _driveUsedPercent;
    private double _puddingDrivePercent;
    private LogCleanupPreview? _cleanupPreview;
    private string? _cleanupResultText;
    private int _disposeState;

    public StorageViewModel(
        IStorageAnalysisService analysisService,
        ILogRetentionService logRetentionService)
    {
        _analysisService = analysisService;
        _logRetentionService = logRetentionService;
    }

    public ObservableCollection<StorageCategoryItemViewModel> Categories { get; } = [];

    public string DataRoot => string.IsNullOrWhiteSpace(_dataRoot) ? "未配置" : _dataRoot;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            _isBusy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanRefresh));
            OnPropertyChanged(nameof(CanCleanLogs));
        }
    }

    public bool CanRefresh => !IsBusy && !string.IsNullOrWhiteSpace(_dataRoot);
    public bool CanCleanLogs => !IsBusy && !string.IsNullOrWhiteSpace(_dataRoot);

    public string StatusText
    {
        get => _statusText;
        private set { _statusText = value; OnPropertyChanged(); }
    }

    public string? ErrorText
    {
        get => _errorText;
        private set
        {
            _errorText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    public string TotalSizeText
    {
        get => _totalSizeText;
        private set { _totalSizeText = value; OnPropertyChanged(); }
    }

    public string DriveSummaryText
    {
        get => _driveSummaryText;
        private set { _driveSummaryText = value; OnPropertyChanged(); }
    }

    public string LastScanText
    {
        get => _lastScanText;
        private set { _lastScanText = value; OnPropertyChanged(); }
    }

    public string WarningText
    {
        get => _warningText;
        private set
        {
            _warningText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasWarnings));
        }
    }

    public bool HasWarnings => !string.IsNullOrWhiteSpace(WarningText);

    public double DriveUsedPercent
    {
        get => _driveUsedPercent;
        private set { _driveUsedPercent = value; OnPropertyChanged(); }
    }

    public double PuddingDrivePercent
    {
        get => _puddingDrivePercent;
        private set { _puddingDrivePercent = value; OnPropertyChanged(); }
    }

    public bool HasCleanupPreview => CleanupPreview is not null;

    public LogCleanupPreview? CleanupPreview
    {
        get => _cleanupPreview;
        private set
        {
            _cleanupPreview = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasCleanupPreview));
            OnPropertyChanged(nameof(CleanupPreviewTitle));
            OnPropertyChanged(nameof(CleanupPreviewDescription));
        }
    }

    public string CleanupPreviewTitle => CleanupPreview is null
        ? string.Empty
        : $"可清理 {StorageSizeFormatter.Format(CleanupPreview.CandidateBytes)}";

    public string CleanupPreviewDescription => CleanupPreview is null
        ? string.Empty
        : $"将永久删除一天前的 {CleanupPreview.Candidates.Count:N0} 个日志文件；删除前会再次检查文件是否变化。";

    public string? CleanupResultText
    {
        get => _cleanupResultText;
        private set
        {
            _cleanupResultText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasCleanupResult));
        }
    }

    public bool HasCleanupResult => !string.IsNullOrWhiteSpace(CleanupResultText);

    public void SetDataRoot(string? dataRoot)
    {
        var normalized = string.IsNullOrWhiteSpace(dataRoot) ? null : dataRoot.Trim();
        if (string.Equals(_dataRoot, normalized, StringComparison.OrdinalIgnoreCase))
            return;

        CancelCurrentOperation();
        _dataRoot = normalized;
        Categories.Clear();
        CleanupPreview = null;
        CleanupResultText = null;
        ErrorText = null;
        TotalSizeText = "—";
        DriveSummaryText = "尚未读取磁盘容量";
        LastScanText = "尚未扫描";
        DriveUsedPercent = 0;
        PuddingDrivePercent = 0;
        StatusText = normalized is null ? "请先在系统设置中配置数据目录" : "等待扫描";
        OnPropertyChanged(nameof(DataRoot));
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(CanCleanLogs));
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_dataRoot))
        {
            ErrorText = "请先在系统设置中配置数据目录。";
            return;
        }

        await RunExclusiveAsync(async ct =>
        {
            IsBusy = true;
            ErrorText = null;
            StatusText = "正在扫描数据目录…";
            var progress = new Progress<StorageScanProgress>(item =>
            {
                StatusText = item.ScannedFileCount == 0
                    ? "正在扫描数据目录…"
                    : $"已扫描 {item.ScannedFileCount:N0} 个文件 · {StorageSizeFormatter.Format(item.ScannedBytes)}";
            });

            try
            {
                var snapshot = await _analysisService.AnalyzeAsync(_dataRoot, progress, ct);
                ApplySnapshot(snapshot);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                StatusText = "扫描已取消";
            }
            catch (Exception ex)
            {
                ErrorText = ex.Message;
                StatusText = "扫描失败";
            }
            finally
            {
                IsBusy = false;
            }
        }, cancellationToken);
    }

    public async Task PreviewLogCleanupAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_dataRoot))
        {
            ErrorText = "请先在系统设置中配置数据目录。";
            return;
        }

        await RunExclusiveAsync(async ct =>
        {
            IsBusy = true;
            ErrorText = null;
            CleanupResultText = null;
            StatusText = "正在计算可清理的旧日志…";
            try
            {
                var preview = await _logRetentionService.PreviewAsync(
                    _dataRoot,
                    TimeSpan.FromDays(1),
                    ct);
                if (preview.Candidates.Count == 0)
                {
                    CleanupPreview = null;
                    CleanupResultText = "没有一天前的日志需要清理。";
                    StatusText = "没有一天前的日志需要清理";
                }
                else
                {
                    CleanupPreview = preview;
                    StatusText = "清理预览已生成，请确认后继续";
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                StatusText = "清理预览已取消";
            }
            catch (Exception ex)
            {
                ErrorText = ex.Message;
                StatusText = "无法生成清理预览";
            }
            finally
            {
                IsBusy = false;
            }
        }, cancellationToken);
    }

    public async Task ExecuteLogCleanupAsync(CancellationToken cancellationToken)
    {
        var preview = CleanupPreview;
        if (preview is null || preview.Candidates.Count == 0)
        {
            CleanupPreview = null;
            return;
        }

        await RunExclusiveAsync(async ct =>
        {
            IsBusy = true;
            ErrorText = null;
            StatusText = "正在清理旧日志…";
            var progress = new Progress<LogCleanupProgress>(item =>
            {
                StatusText = $"正在清理 {item.ProcessedFiles:N0}/{item.TotalFiles:N0} · 已释放 {StorageSizeFormatter.Format(item.DeletedBytes)}";
            });

            try
            {
                var result = await _logRetentionService.ExecuteAsync(preview, progress, ct);
                CleanupPreview = null;
                CleanupResultText = $"已删除 {result.DeletedFiles:N0} 个文件，释放 {StorageSizeFormatter.Format(result.DeletedBytes)}；跳过 {result.SkippedFiles:N0} 个，失败 {result.FailedFiles:N0} 个。";
                StatusText = result.FailedFiles == 0 ? "日志清理完成" : "日志清理完成，部分文件失败";

                var snapshot = await _analysisService.AnalyzeAsync(_dataRoot!, progress: null, ct);
                ApplySnapshot(snapshot);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                StatusText = "日志清理已取消";
            }
            catch (Exception ex)
            {
                ErrorText = ex.Message;
                StatusText = "日志清理失败";
            }
            finally
            {
                IsBusy = false;
            }
        }, cancellationToken);
    }

    public void CancelCleanupPreview()
    {
        CleanupPreview = null;
        StatusText = "已取消清理";
    }

    private void ApplySnapshot(StorageSnapshot snapshot)
    {
        Categories.Clear();
        foreach (var category in snapshot.Categories)
            Categories.Add(new StorageCategoryItemViewModel(category));

        TotalSizeText = StorageSizeFormatter.Format(snapshot.LogicalBytes);
        LastScanText = $"上次扫描 {snapshot.CapturedAt:HH:mm:ss}";
        WarningText = snapshot.Warnings.Count == 0
            ? string.Empty
            : $"扫描时跳过或无法读取 {snapshot.Warnings.Count:N0} 个项目。统计结果可能略小于实际值。";

        if (snapshot.DriveTotalBytes > 0)
        {
            var usedBytes = Math.Max(0, snapshot.DriveTotalBytes - snapshot.DriveFreeBytes);
            DriveUsedPercent = ClampPercent(usedBytes, snapshot.DriveTotalBytes);
            PuddingDrivePercent = ClampPercent(snapshot.LogicalBytes, snapshot.DriveTotalBytes);
            DriveSummaryText = $"磁盘已用 {StorageSizeFormatter.Format(usedBytes)} / {StorageSizeFormatter.Format(snapshot.DriveTotalBytes)} · 可用 {StorageSizeFormatter.Format(snapshot.DriveFreeBytes)}";
        }
        else
        {
            DriveUsedPercent = 0;
            PuddingDrivePercent = 0;
            DriveSummaryText = "无法读取磁盘总量；当前显示文件逻辑大小。";
        }

        StatusText = $"扫描完成 · {snapshot.Categories.Sum(item => item.FileCount):N0} 个文件";
    }

    private async Task RunExclusiveAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            CancelCurrentOperation();
            _operationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            await operation(_operationCts.Token);
        }
        finally
        {
            _operationCts?.Dispose();
            _operationCts = null;
            _operationLock.Release();
        }
    }

    private void CancelCurrentOperation()
        => _operationCts?.Cancel();

    private static double ClampPercent(long value, long total)
        => total <= 0 ? 0 : Math.Clamp(value * 100d / total, 0d, 100d);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            return;

        CancelCurrentOperation();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class StorageCategoryItemViewModel
{
    public StorageCategoryItemViewModel(StorageCategorySnapshot snapshot)
    {
        Kind = snapshot.Definition.Kind;
        DisplayName = snapshot.Definition.DisplayName;
        Description = snapshot.Definition.Description;
        IconGlyph = snapshot.Definition.IconGlyph;
        CanClean = snapshot.Definition.CanClean;
        SizeText = StorageSizeFormatter.Format(snapshot.LogicalBytes);
        FileCountText = $"{snapshot.FileCount:N0} 个文件";
    }

    public StorageCategoryKind Kind { get; }
    public string DisplayName { get; }
    public string Description { get; }
    public string IconGlyph { get; }
    public bool CanClean { get; }
    public string SizeText { get; }
    public string FileCountText { get; }
}
