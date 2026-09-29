using Microsoft.UI.Xaml;
using PuddingDesktop.Configuration;
using PuddingDesktop.Hosting;
using PuddingDesktop.Runtime;

namespace PuddingDesktop;

public partial class App : Application
{
    private DesktopApplicationCoordinator? _coordinator;
    private DesktopSingleInstanceService? _single;
    internal static string StateRoot => DesktopBootstrapPathProvider.GetDirectoryPath();
    public App() { InitializeComponent(); UnhandledException += (_, e) => WriteDiagnostic(e.Exception); }
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        UiThread.Queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        try
        {
            // The settings-directory identity also permits isolated lifecycle harnesses.
            _single = new DesktopSingleInstanceService("PuddingDesktop:" + Path.GetFullPath(StateRoot).ToUpperInvariant());
            if (!_single.TryAcquirePrimary())
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await _single.SignalPrimaryAsync(cts.Token); Exit(); return;
            }
            _single.ActivationRequested += (_, _) => UiThread.Post(() => _coordinator?.ActivateMainWindow());
            _coordinator = new DesktopApplicationCoordinator();
            await _coordinator.StartAsync(Environment.GetCommandLineArgs().Skip(1).ToArray(), CancellationToken.None);
        }
        catch (Exception ex) { WriteDiagnostic(ex); _coordinator?.ActivateMainWindow(); }
    }
    internal async Task FinishAsync()
    {
        if (_single is not null) await _single.DisposeAsync();
        Exit();
    }
    internal static void WriteDiagnostic(Exception exception) =>
        Diagnostics.DesktopDiagnosticLog.Write("WinUI", exception);
}

internal static class UiThread
{
    internal static Microsoft.UI.Dispatching.DispatcherQueue Queue { get; set; } = null!;
    internal static void Post(Action action)
    {
        if (Queue.HasThreadAccess) action(); else Queue.TryEnqueue(() => action());
    }
    internal static Task InvokeAsync(Func<Task> action) => new PuddingBrowser.WebView2.WinUiDispatcher(Queue).InvokeAsync(action, CancellationToken.None);
}
