using System.Windows;
using System.Windows.Controls;
using PuddingDesktop.Storage;
using PuddingDesktop.ViewModels;
using UserControl = System.Windows.Controls.UserControl;

namespace PuddingDesktop.Views;

public partial class StorageView : UserControl
{
    private readonly StorageViewModel _viewModel;

    public StorageView()
    {
        var safetyValidator = new DataRootSafetyValidator();
        _viewModel = new StorageViewModel(
            new StorageAnalysisService(safetyValidator, new StorageCategoryCatalog()),
            new LogRetentionService(safetyValidator),
            new CoreStorageManagementClient());
        DataContext = _viewModel;
        InitializeComponent();
    }

    internal void Configure(
        string? dataRoot,
        Uri? coreAddress,
        Func<CancellationToken, Task<string>> controlTokenProvider)
    {
        _viewModel.SetDataRoot(dataRoot);
        _viewModel.ConfigureCore(
            coreAddress,
            coreAddress is null ? null : controlTokenProvider);
    }

    internal Task RefreshAsync(CancellationToken cancellationToken = default)
        => _viewModel.RefreshAsync(cancellationToken);

    internal void DisposeOperations()
        => _viewModel.Dispose();

    private async void Refresh_Click(object sender, RoutedEventArgs e)
        => await _viewModel.RefreshAsync(CancellationToken.None);

    private async void PreviewCleanup_Click(object sender, RoutedEventArgs e)
        => await _viewModel.PreviewLogCleanupAsync(CancellationToken.None);

    private async void ConfirmCleanup_Click(object sender, RoutedEventArgs e)
        => await _viewModel.ExecuteLogCleanupAsync(CancellationToken.None);

    private void CancelCleanup_Click(object sender, RoutedEventArgs e)
        => _viewModel.CancelCleanupPreview();

    private async void RefreshDatabaseDetails_Click(object sender, RoutedEventArgs e)
        => await _viewModel.RefreshDatabaseDetailsAsync(CancellationToken.None);

    private async void PreviewDatabaseCleanup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DatabaseStorageItemViewModel item })
            await _viewModel.PreviewDatabaseCleanupAsync(item.ItemId, CancellationToken.None);
    }

    private async void ConfirmDatabaseCleanup_Click(object sender, RoutedEventArgs e)
        => await _viewModel.ExecuteDatabaseCleanupAsync(CancellationToken.None);

    private void CancelDatabaseCleanup_Click(object sender, RoutedEventArgs e)
        => _viewModel.CancelDatabaseCleanupPreview();
}
