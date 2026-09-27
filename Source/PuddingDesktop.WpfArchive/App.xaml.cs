using System.Windows;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using PuddingDesktop.Diagnostics;
using PuddingDesktop.Hosting;
using PuddingDesktop.Runtime;

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
    private DesktopSingleInstanceService? _singleInstance;
    private bool _pendingActivation;

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

        try
        {
            _singleInstance = new DesktopSingleInstanceService();
            if (!_singleInstance.TryAcquirePrimary())
            {
                using var activationCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await _singleInstance.SignalPrimaryAsync(activationCts.Token);
                Shutdown(0);
                return;
            }

            _singleInstance.ActivationRequested += OnActivationRequested;
            _coordinator = new DesktopApplicationCoordinator();
            await _coordinator.StartAsync(e.Args, CancellationToken.None);
            if (_pendingActivation)
            {
                _pendingActivation = false;
                _coordinator.ActivateMainWindow();
            }
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

    private void OnActivationRequested(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_coordinator is null)
                _pendingActivation = true;
            else
                _coordinator.ActivateMainWindow();
        });
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

        if (_singleInstance is not null)
        {
            _singleInstance.ActivationRequested -= OnActivationRequested;
            try { await _singleInstance.DisposeAsync(); }
            catch { }
        }

        base.OnExit(e);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        _coordinator?.RequestExplicitExit();
        base.OnSessionEnding(e);
    }
}
