using Microsoft.UI.Xaml;
using PuddingDesktop.Configuration;
using PuddingDesktop.Hosting;
using PuddingDesktop.Runtime;
using PuddingDesktop.Diagnostics;
using System.Diagnostics;
using System.Reflection;

namespace PuddingDesktop;

public partial class App : Application
{
    private DesktopApplicationCoordinator? _coordinator;
    private DesktopSingleInstanceService? _single;
    private Timer? _diagnosticHeartbeat;
    internal static string StateRoot => DesktopBootstrapPathProvider.GetDirectoryPath();
    public App()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            RecordException("AppDomain", e.ExceptionObject as Exception ?? new Exception("Non-Exception failure"), e.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, e) => RecordException("UnobservedTask", e.Exception, false);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            _diagnosticHeartbeat?.Dispose();
            DesktopDiagnosticLog.Journal?.ProcessExit();
        };
        UnhandledException += (_, e) => RecordException("WinUI.UnhandledException", e.Exception, true);
        InitializeComponent();
    }
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
            StartDiagnostics();
            _single.ActivationRequested += (_, _) => UiThread.Post(() => _coordinator?.ActivateMainWindow());
            _coordinator = new DesktopApplicationCoordinator();
            _coordinator.RuntimeChanged += (_, e) =>
            {
                CaptureDiagnostics();
                DesktopDiagnosticLog.Journal?.Record("core_state_changed",
                    $"from={e.Previous.State};to={e.Current.State};pid={e.Current.Session?.ProcessId};lastExitCode={e.Current.LastExitCode}");
            };
            _coordinator.StateChanged += (_, _) =>
            {
                CaptureDiagnostics();
                DesktopDiagnosticLog.Journal?.Record("shell_state_changed", _coordinator.State.ToString());
            };
            await _coordinator.StartAsync(Environment.GetCommandLineArgs().Skip(1).ToArray(), CancellationToken.None);
        }
        catch (Exception ex) { WriteDiagnostic(ex); _coordinator?.ActivateMainWindow(); }
    }
    internal async Task FinishAsync()
    {
        if (_single is not null) await _single.DisposeAsync();
        _diagnosticHeartbeat?.Dispose();
        DesktopDiagnosticLog.Journal?.CompleteExit();
        Exit();
    }
    private void StartDiagnostics()
    {
        try
        {
            var root = Path.Combine(StateRoot, "logs", "desktop-runs");
            DesktopRunJournal.RecoverAndPrune(root);
            using var process = Process.GetCurrentProcess();
            var build = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            var journal = new DesktopRunJournal(root, new(process.Id, process.StartTime.ToUniversalTime()), build);
            DesktopDiagnosticLog.Journal = journal;
            _diagnosticHeartbeat = new Timer(_ => CaptureDiagnostics(), null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
            var executable = Path.Combine(AppContext.BaseDirectory, "observer", "PuddingDesktop.ExitObserver.exe");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add(journal.DirectoryPath);
            using var observer = Process.Start(start);
            journal.Record("observer_started", $"pid={observer?.Id}");
        }
        catch (Exception ex) { DesktopDiagnosticLog.Write("DiagnosticsStart", ex); }
    }
    private void CaptureDiagnostics()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var runtime = _coordinator?.RuntimeSnapshot;
            DesktopDiagnosticLog.Journal?.Capture(new DesktopScene(
                _coordinator?.State.ToString() ?? "Starting", runtime?.State.ToString() ?? "Unknown",
                runtime?.Session?.ProcessId, runtime?.LastExitCode, runtime?.LastError,
                _coordinator?.CoreLogBuffer.GetTail(100), process.PrivateMemorySize64, process.WorkingSet64));
        }
        catch (Exception ex) { DesktopDiagnosticLog.Write("DiagnosticsCapture", ex); }
    }
    private static void RecordException(string source, Exception exception, bool fatal)
    {
        DesktopDiagnosticLog.Write(source, exception, fatal);
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
