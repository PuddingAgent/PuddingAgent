using System.ComponentModel;
using System.Text.Json;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PuddingChat;
using PuddingChat.WinUI;
using PuddingDesktop.Foundation;

namespace PuddingDesktop;

public sealed partial class MainWindow
{
    private readonly Kernel.WinUiDesktopServices _desktopServices;
    private bool _allowClose;
    private bool _exiting;
    private ChatWorkspace? _nativeChat;
    private readonly Func<IChatClient> _createChatClient;
    private readonly PuddingDesktop.Foundation.ILlmResourceSettings _llmSettings;
    private readonly PuddingDesktop.Foundation.IVoiceResourceSettings _voiceSettings;
    private readonly PuddingDesktop.Foundation.IAgentDirectorySettings _agentDirectory;
    private readonly PuddingDesktop.Foundation.IToolPluginSettings _toolPlugins;
    private readonly PuddingDesktop.Foundation.ISkillHubSettings _skillHub;
    private readonly PuddingDesktop.Foundation.ISkillPackageSettings _skillPackages;
    private readonly PuddingDesktop.Foundation.IWorkspaceSettings _workspaces;
    private readonly PuddingDesktop.Foundation.IChannelSettings _channels;
    private readonly PuddingDesktop.Foundation.IWorkspaceResourceSettings _workspaceResources;
    private readonly PuddingDesktop.Foundation.IMemoryLibrarySettings _memoryLibrary;
    private readonly PuddingDesktop.Foundation.IStorageSettings _storage;
    private string? _chatDataRoot;
    private string KernelSettingsPath => Path.Combine(App.StateRoot, "desktop.kernel.json");
    private sealed record KernelSettings(string DataRoot);
    private const string DefaultDataRoot = @"D:\data";

    private async Task InitializeKernelAsync(string[] arguments)
    {
        try
        {
            var root = DefaultDataRoot;
            if (File.Exists(KernelSettingsPath))
            {
                await using var settingsFile = File.OpenRead(KernelSettingsPath);
                root = (await JsonSerializer.DeserializeAsync<KernelSettings>(settingsFile))?.DataRoot
                    ?? throw new InvalidDataException("内核配置为空。");
            }
            var rootIndex = Array.IndexOf(arguments, "--data-root");
            if (rootIndex >= 0 && rootIndex + 1 < arguments.Length) root = Path.GetFullPath(arguments[rootIndex + 1]);
            DataRootEditor.Text = root;
            if (arguments.Contains("--demo")) { RefreshKernel(); return; }
            var reportIndex = Array.IndexOf(arguments, "--kernel-smoke-report");
            if (reportIndex >= 0 && reportIndex + 1 < arguments.Length)
            {
                if (rootIndex < 0) throw new InvalidOperationException("Kernel smoke requires an explicit isolated --data-root.");
                await RunKernelSmokeAsync(Path.GetFullPath(arguments[reportIndex + 1]));
                return;
            }
            await StartKernelAsync();
        }
        catch (Exception exception) { ReportKernelError(exception); }
    }

    private void ShowFromCore(ShellPage page)
    {
        if (_exiting) throw new InvalidOperationException("窗口正在退出。");
        _state.Navigate(page); Activate();
    }
    private void OpenDocumentFromCore(WorkspaceDocument document)
    {
        if (_exiting) throw new InvalidOperationException("窗口正在退出。");
        _state.OpenDocument(document);
        _layout = _layout with { WorkspaceVisible = true }; ApplyLayout();
    }
    private void OnKernelStateChanged(object? sender, EventArgs args) => DispatcherQueue.TryEnqueue(RefreshKernel);
    private void RefreshKernel()
    {
        var snapshot = _kernel.Snapshot;
        KernelStatus.Title = snapshot.Description;
        KernelStatus.Severity = snapshot.State == DesktopKernelState.Failed ? InfoBarSeverity.Error : InfoBarSeverity.Informational;
        KernelBadge.Text = snapshot.State == DesktopKernelState.Ready ? "● Core DLL 已就绪" : "○ " + snapshot.Description;
        KernelDetails.Text = $"Desktop / Core PID：{Environment.ProcessId}\n{snapshot.WorkbenchAddress}";
        StartKernelButton.IsEnabled = snapshot.State is DesktopKernelState.Stopped or DesktopKernelState.Failed;
        StopKernelButton.IsEnabled = snapshot.State is DesktopKernelState.Starting or DesktopKernelState.Ready or DesktopKernelState.Failed;
        RestartKernelButton.IsEnabled = snapshot.State == DesktopKernelState.Ready;
        DataRootEditor.IsEnabled = !_exiting;
        if (snapshot.State != DesktopKernelState.Ready && _nativeChat is not null)
        {
            _nativeChat.Dispose(); _nativeChat = null;
            NativeChatPane.Content = null;
        }
        if (_loaded) OnStateChanged(this, new PropertyChangedEventArgs(nameof(ShellState.Page)));
        if (_loaded) RefreshAbout();
        // A settings tab opened before Core was ready reloads as soon as the kernel becomes ready.
        if (_loaded && (LlmProvidersSettings.Visibility == Visibility.Visible || LlmModelsSettings.Visibility == Visibility.Visible))
            LoadLlmIfNeeded();
    }
    private async Task<string> SaveDataRootAsync()
    {
        var input = DataRootEditor.Text.Trim();
        if (string.IsNullOrWhiteSpace(input) || !Path.IsPathFullyQualified(input))
            throw new ArgumentException("请输入完整的数据目录路径，例如 D:\\data。");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(input));
        if (File.Exists(root)) throw new ArgumentException("数据目录不能是文件。");
        Directory.CreateDirectory(App.StateRoot);
        var temporary = KernelSettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new KernelSettings(root)));
            File.Move(temporary, KernelSettingsPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        DataRootEditor.Text = root;
        return root;
    }
    private async void OnSaveDataRoot(object sender, RoutedEventArgs args)
    {
        try
        {
            await SaveDataRootAsync();
            KernelStatus.Title = "数据目录已保存";
            KernelStatus.Message = "重新打开 Desktop 后使用保存的目录；当前内核继续使用原目录。不会自动迁移数据。";
            KernelStatus.Severity = InfoBarSeverity.Success;
        }
        catch (Exception exception)
        {
            KernelStatus.Title = "目录未保存";
            KernelStatus.Message = exception is ArgumentException ? exception.Message : "无法保存目录，请检查路径与配置文件权限。";
            KernelStatus.Severity = InfoBarSeverity.Error;
        }
    }
    private void OnResetDataRoot(object sender, RoutedEventArgs args) => DataRootEditor.Text = DefaultDataRoot;
    private async Task StartKernelAsync()
    {
        var root = await SaveDataRootAsync();
        if (_chatDataRoot is not null && !string.Equals(root, _chatDataRoot, StringComparison.OrdinalIgnoreCase))
        {
            KernelStatus.Title = "新数据目录已保存";
            KernelStatus.Message = "请重新打开 Desktop 以切换数据目录与本机工作环境。";
            KernelStatus.Severity = InfoBarSeverity.Informational;
            return;
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await _kernel.StartAsync(root, timeout.Token);
        if (_kernel.Snapshot.State != DesktopKernelState.Ready) return;
        RefreshKernel();
        await OpenWorkbenchAsync(root);
    }
    private Task OpenWorkbenchAsync(string dataRoot)
    {
        var address = _kernel.Snapshot.WorkbenchAddress;
        if (address is null || _kernel.Snapshot.State != DesktopKernelState.Ready) return Task.CompletedTask;
        _nativeChat?.Dispose();
        _nativeChat = new ChatWorkspace(_createChatClient(), address);
        _nativeChat.SettingsRequested += (_, _) => _state.Navigate(ShellPage.Settings);
        _nativeChat.RuntimeRequested += (_, _) => _state.Navigate(ShellPage.RuntimeCenter);
        _nativeChat.AdministrationRequested += async (_, _) =>
        {
            try { await Windows.System.Launcher.LaunchUriAsync(address); }
            catch (Exception exception) { App.WriteDiagnostic(exception); }
        };
        NativeChatPane.Content = _nativeChat;
        _chatDataRoot = dataRoot;
        _demo = false;
        _state.ReplaceRoles([]);
        DemoButton.IsEnabled = false;
        ProjectLabel.Text = "已连接进程内 Core";
        EmptyRoles.Text = "返回工作台，在原生角色导航中选择角色。";
        ApplyLayout();
        return Task.CompletedTask;
    }
    private async void OnStartKernel(object sender, RoutedEventArgs args)
    { try { await StartKernelAsync(); } catch (Exception exception) { ReportKernelError(exception); } }
    private async void OnStopKernel(object sender, RoutedEventArgs args)
    { try { await _kernel.StopAsync(CancellationToken.None); } catch (Exception exception) { ReportKernelError(exception); } }
    private async void OnRestartKernel(object sender, RoutedEventArgs args)
    { try { await _kernel.StopAsync(CancellationToken.None); await StartKernelAsync(); } catch (Exception exception) { ReportKernelError(exception); } }
    private void ReportKernelError(Exception exception)
    {
        App.WriteDiagnostic(exception); RefreshKernel();
        KernelStatus.Title = "内核操作未完成";
        KernelStatus.Message = "请检查数据目录与诊断日志。可修正设置后重试。";
        KernelStatus.Severity = InfoBarSeverity.Error;
        _state.Navigate(ShellPage.RuntimeCenter);
    }
    private async void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;
        args.Cancel = true;
        await RequestExitAsync();
    }
    private async Task RequestExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        try
        {
            await _kernel.DisposeAsync();
            _kernel.StateChanged -= OnKernelStateChanged;
            _desktopServices.Dispose();
            _nativeChat?.Dispose(); _nativeChat = null; NativeChatPane.Content = null;
            _allowClose = true;
            Close();
        }
        catch (Exception exception) { _exiting = false; ReportKernelError(exception); }
    }
    private async Task RunKernelSmokeAsync(string reportPath)
    {
        try
        {
            await StartKernelAsync();
            var hostAssembly = AppDomain.CurrentDomain.GetAssemblies().Single(assembly => assembly.GetName().Name == "PuddingHost");
            using var http = new HttpClient();
            var address = _kernel.Snapshot.WorkbenchAddress!;
            (await http.GetAsync(new Uri(address, "/health/ready"))).EnsureSuccessStatusCode();
            var runningRoot = DataRootEditor.Text;
            // Exercise the real product mount with an isolated local role, without credentials or a model call.
            using (var chat = _createChatClient())
            {
                var setup = chat as IWorkspaceSetupClient ?? throw new InvalidOperationException("Native setup is not mounted.");
                var created = await setup.SetupWorkspaceAsync(new("default", "Native smoke workspace", "Native smoke role", null), CancellationToken.None);
                var agent = (await chat.GetAgentsAsync(created.WorkspaceId, CancellationToken.None)).Single(a => a.AgentId == created.AgentId);
                await OpenWorkbenchAsync(runningRoot);
                _state.Navigate(ShellPage.Workbench);
                var mountedChat = _nativeChat ?? throw new InvalidOperationException("Native chat is not mounted.");
                await mountedChat.InitializeAsync();
                await mountedChat.SelectRoleAsync(created.WorkspaceId, agent);
                mountedChat.Composer.Draft = "Inspect the attached source.";
                var fixturePath = Path.Combine(App.StateRoot, "native-chat-smoke.cs");
                await File.WriteAllTextAsync(fixturePath, "class NativeChatSmoke { }\n");
                await mountedChat.AddTextFilesAsync([fixturePath]);
                for (var attempt = 0; attempt < 100 && !mountedChat.IsLoaded; attempt++) await Task.Delay(50);
                if (!ReferenceEquals(NativeChatPane.Content, mountedChat) || mountedChat.RoleCount < 1
                    || !mountedChat.IsLoaded || NativeChatPane.Visibility != Visibility.Visible
                    || mountedChat.SelectedRole != new RoleKey(created.WorkspaceId, created.AgentId)
                    || mountedChat.Composer.FileCount != 1 || mountedChat.Composer.Draft != "Inspect the attached source.")
                    throw new InvalidOperationException("Product native chat role/draft/file composition failed.");
            }
            DataRootEditor.Text = Path.Combine(runningRoot, "saved-next-root");
            var savedWhileRunning = await SaveDataRootAsync();
            if (_kernel.Snapshot.State != DesktopKernelState.Ready
                || JsonSerializer.Deserialize<KernelSettings>(await File.ReadAllTextAsync(KernelSettingsPath))?.DataRoot != savedWhileRunning)
                throw new InvalidOperationException("Saving a directory must persist without stopping the active Core.");
            DataRootEditor.Text = runningRoot; await SaveDataRootAsync();
            await Task.Run(() => _desktopServices.ShowAsync(ShellPage.Settings));
            if (_state.Page != ShellPage.Settings) throw new InvalidOperationException("Desktop callback did not reach UI.");
            var previousChat = _nativeChat;
            await _kernel.StopAsync(CancellationToken.None);
            await StartKernelAsync();
            (await http.GetAsync(new Uri(_kernel.Snapshot.WorkbenchAddress!, "/health/ready"))).EnsureSuccessStatusCode();
            var restartedChat = _nativeChat ?? throw new InvalidOperationException("Native chat missing after Core restart.");
            await restartedChat.InitializeAsync();
            if (ReferenceEquals(previousChat, restartedChat) || restartedChat.RoleCount < 1)
                throw new InvalidOperationException("Core restart must mount a new chat client and reload the saved role.");
            await _kernel.StopAsync(CancellationToken.None);
            var nextRoot = Path.Combine(DataRootEditor.Text, "next-root");
            DataRootEditor.Text = nextRoot;
            await StartKernelAsync();
            var savedSettings = JsonSerializer.Deserialize<KernelSettings>(await File.ReadAllTextAsync(KernelSettingsPath));
            if (savedSettings?.DataRoot != nextRoot || _kernel.Snapshot.State != DesktopKernelState.Stopped)
                throw new InvalidOperationException("DataRoot change must be saved for next launch without reusing the old local context.");
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new { success = true, processId = Environment.ProcessId, coreAssembly = hostAssembly.Location, uiCallback = true, restart = true, dataRootChangeSaved = true, nativeChatMounted = true, nativeRoleAndFileDraft = true, nativeChatRecreatedAfterRestart = true }));
        }
        catch (Exception exception)
        {
            App.WriteDiagnostic(exception); Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new { success = false, error = exception.ToString() }));
            Environment.ExitCode = 1;
        }
        await RequestExitAsync();
    }
}
