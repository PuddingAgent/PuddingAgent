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
            new LogRetentionService(safetyValidator));
        DataContext = _viewModel;
        InitializeComponent();
    }

    internal void SetDataRoot(string? dataRoot)
        => _viewModel.SetDataRoot(dataRoot);

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
}
