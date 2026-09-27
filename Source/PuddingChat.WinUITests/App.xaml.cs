using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PuddingChat.WinUI;
using System.Text.Json;

namespace PuddingChat.WinUITests;

public partial class App : Application
{
    private Window? _window;
    private string Report => Environment.GetCommandLineArgs().Skip(1).First();
    public App() { InitializeComponent(); UnhandledException += (_, e) => File.WriteAllText(Report, e.Exception.ToString()); }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var fixture = new Fixture(); var control = new ChatWorkspace(fixture);
        _window = new Window { Content = control, SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop() };
        _window.Title = "Pudding · 原生聊天组件测试";
        control.RequestedTheme = ElementTheme.Light;
        _window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 800));
        control.Loaded += async (_, _) =>
        {
            try
            {
                await control.InitializeAsync();
                await control.InitializeAsync();
                Check(fixture.WorkspaceReads == 1, "automatic initialization shared across loads");
                if (Environment.GetCommandLineArgs().Contains("--preview"))
                {
                    await control.SelectRoleAsync("test", fixture.Builder); control.Composer.Draft = "implement";
                    await control.SendAsync(); return;
                }
                var setup = new WorkspaceSetupForm(fixture);
                await setup.LoadAsync(CancellationToken.None);
                var setupLoaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                setup.Loaded += (_, _) => setupLoaded.TrySetResult();
                var dialog = new ContentDialog { Content = setup, CloseButtonText = "关闭", XamlRoot = control.XamlRoot };
                var showing = dialog.ShowAsync();
                try { await setupLoaded.Task.WaitAsync(TimeSpan.FromSeconds(5)); Check(setup.IsLoaded, "native setup dialog loaded"); }
                finally { dialog.Hide(); await showing; }
                var created = await setup.SubmitAsync(CancellationToken.None);
                Check(created?.AgentId == "builder" && fixture.Setup?.RoleName == "编码助手", "native setup calls application port");
                Check(control.RoleCount == 2, "native role cards loaded");
                await control.SelectRoleAsync("test", fixture.Builder);
                control.Composer.Draft = "implement";
                await control.SelectRoleAsync("test", fixture.Reviewer);
                Check(control.Composer.Draft == "", "draft isolated");
                await control.SelectRoleAsync("test", fixture.Builder);
                Check(control.Composer.Draft == "implement", "draft restored");
                await control.SendAsync();
                Check(control.Composer.Draft == "", "receipt clears draft");
                Check(fixture.Sent?.Role.AgentId == "builder", "send retains role");
                Check(control.CurrentConversation?.Messages.Length == 2, "canonical messages displayed");
                await control.CancelAsync(); Check(fixture.Cancelled == "turn", "canonical cancellation");
                Check(MessageCard.RenderText("# Title\n```cs\nConsole.WriteLine(1);\n```\n正文") is StackPanel { Children.Count: 3 }, "native heading code text");
                var slow = control.SelectRoleAsync("test", new Agent("slow", "slow"));
                await control.SelectRoleAsync("test", fixture.Reviewer);
                fixture.Late.TrySetResult(fixture.Conversation("slow")); await slow;
                Check(control.CurrentConversation?.AgentId == "reviewer", "late reply rejected");
                control.Dispose(); Check(fixture.Disposed, "transport disposed");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(Report))!);
                await File.WriteAllTextAsync(Report, JsonSerializer.Serialize(new { success = true, checks = 13, native = true }));
            }
            catch (Exception e) { await File.WriteAllTextAsync(Report, JsonSerializer.Serialize(new { success = false, error = e.ToString() })); Environment.ExitCode = 1; }
            finally { if (!Environment.GetCommandLineArgs().Contains("--preview")) { control.Dispose(); _window.Close(); } }
        };
        _window.Activate();
    }
    private static void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); }
    private sealed class Fixture : IChatClient, IWorkspaceSetupClient
    {
        public WorkspaceSetupRequest? Setup;
        public Task<ModelChoice[]> GetSetupModelsAsync(CancellationToken ct) => Task.FromResult<ModelChoice[]>([]);
        public Task<WorkspaceSetupResult> SetupWorkspaceAsync(WorkspaceSetupRequest request, CancellationToken ct)
        { Setup = request; return Task.FromResult(new WorkspaceSetupResult(request.WorkspaceId, "builder")); }
        public Agent Builder = new("builder", "代码工程师", Description: "实现功能与修复");
        public Agent Reviewer = new("reviewer", "代码审阅者", Description: "检查边界与验证");
        public PendingSend? Sent; public string? Cancelled; public bool Disposed;
        public TaskCompletionSource<Conversation?> Late = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int WorkspaceReads;
        public async Task<Workspace[]> GetWorkspacesAsync(CancellationToken ct) { WorkspaceReads++; await Task.Delay(20, ct); return [new("test", "组件测试")]; }
        public Task<Agent[]> GetAgentsAsync(string workspace, CancellationToken ct) => Task.FromResult(new[] { Builder, Reviewer });
        public Task<AgentStatus[]> GetStatusesAsync(string workspace, CancellationToken ct) => Task.FromResult<AgentStatus[]>([new("builder", "idle", "待命", 0)]);
        public Conversation Conversation(string agent) => new("test", agent, "session",
            Sent is null ? [] : [new("m", null, "user", "用户", DateTimeOffset.UtcNow, "implement", "accepted", []),
                new("a", "r", "assistant", "代码工程师", DateTimeOffset.UtcNow, "# 进度\n```cs\nvar result = 1;\n```", "running", [])],
            Sent is null ? null : new("r", "running", "执行中", "编译", new("输出", [new("e", "tool_call", "running", "dotnet build", 2, "terminal", ToolCallId: "call", TurnId: "turn")], new("turn", 2, 2, 2, false))),
            Sent is null ? 0 : 2);
        public Task<Conversation?> GetConversationAsync(RoleKey role, long? cursor, CancellationToken ct) =>
            role.AgentId == "slow" ? Late.Task : Task.FromResult<Conversation?>(Conversation(role.AgentId));
        public Task<string> EnsureSessionAsync(RoleKey role, Agent agent, CancellationToken ct) => Task.FromResult("session");
        public Task<Acceptance> SendAsync(PendingSend send, CancellationToken ct) { Sent = send; return Task.FromResult(new Acceptance("session", "m", ["turn"], 1)); }
        public Task CancelAsync(string workspace, string conversation, string turn, CancellationToken ct) { Cancelled = turn; return Task.CompletedTask; }
        public Task<ProcessDetails> GetProcessAsync(RoleKey role, string message, CancellationToken ct) => Task.FromResult(new ProcessDetails(message, []));
        public void Dispose() => Disposed = true;
    }
}
