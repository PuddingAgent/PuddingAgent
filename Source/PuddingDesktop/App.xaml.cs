using Microsoft.UI.Xaml;

namespace PuddingDesktop;

public partial class App : Application
{
    private MainWindow? _window;
    internal static string StateRoot { get; } = ResolveStateRoot();

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, args) => WriteDiagnostic(args.Exception);
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var current = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent();
        var instance = Microsoft.Windows.AppLifecycle.AppInstance.FindOrRegisterForKey("PuddingDesktop.WinUi.Skeleton." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(StateRoot).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant()))));
        if (!instance.IsCurrent)
        {
            await instance.RedirectActivationToAsync(current.GetActivatedEventArgs());
            Exit();
            return;
        }
        _window = new MainWindow(desktop =>
        {
            var factory = new PuddingDesktop.Composition.DesktopKernelFactory(desktop);
            var kernel = new PuddingDesktop.Foundation.InProcessKernel(factory);
            return (kernel, factory.CreateChatClient, factory.CreateLlmSettings(kernel));
        });
        instance.Activated += (_, _) => _window.DispatcherQueue.TryEnqueue(() => _window.Activate());
        _window.Activate();
    }

    internal static void WriteDiagnostic(Exception exception)
    {
        try
        {
            Directory.CreateDirectory(StateRoot);
            File.AppendAllText(Path.Combine(StateRoot, "desktop.log"), $"{DateTimeOffset.Now:O} {exception}\n");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string ResolveStateRoot()
    {
        var args = Environment.GetCommandLineArgs();
        var index = Array.IndexOf(args, "--state-root");
        return index >= 0 && index + 1 < args.Length
            ? Path.GetFullPath(args[index + 1])
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pudding", "WinUiSkeleton");
    }
}
