using System.Windows;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using PuddingDesktop.Diagnostics;
using PuddingDesktop.Hosting;

namespace PuddingDesktop;

/// <summary>
/// WPF Application entry point for PuddingDesktop.
/// Windows are created by DesktopApplicationCoordinator.
/// OnExit is a last-resort fallback; normal shutdown is handled
/// by MainWindow.Window_Closing → Core stop → window close.
/// </summary>
public sealed partial class App : Application
{
    private DesktopApplicationCoordinator? _coordinator;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
            DesktopDiagnosticLog.Write("DispatcherUnhandledException", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
                DesktopDiagnosticLog.Write("AppDomainUnhandledException", exception);
        };

        _coordinator = new DesktopApplicationCoordinator();
        try
        {
            await _coordinator.StartAsync(e.Args, CancellationToken.None);
        }
        catch (Exception ex)
        {
            DesktopDiagnosticLog.Write("Startup", ex);
            MessageBox.Show(
                $"Failed to start Pudding Desktop: {ex.Message}",
                "Startup Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        // Best-effort last-resort cleanup — normal exit is through MainWindow
        if (_coordinator is not null)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await _coordinator.DisposeAsync();
            }
            catch
            {
                // Best-effort shutdown
            }
        }

        base.OnExit(e);
    }
}
