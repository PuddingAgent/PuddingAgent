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
        var fixture = new Fixture(Path.ChangeExtension(Report, ".png")); var control = new ChatWorkspace(fixture);
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
                var providerForm = new ProviderConfigurationForm(fixture);
                await providerForm.LoadAsync(CancellationToken.None);
                Check(!await providerForm.SaveAsync(CancellationToken.None), "invalid provider form rejected without write");
                var providerPanel = (StackPanel)((ScrollViewer)providerForm.Content).Content;
                foreach (var input in providerPanel.Children.OfType<TextBox>())
                    input.Text = input.Header?.ToString() == "API 地址" ? "https://example.invalid/v1" : "fixture";
                var keyOperation = providerPanel.Children.OfType<ComboBox>().Single(c => c.Header?.ToString() == "密钥操作");
                keyOperation.SelectedIndex = 1;
                var keyInput = providerPanel.Children.OfType<PasswordBox>().Single(); keyInput.Password = "fixture-only";
                Check(await providerForm.SaveAsync(CancellationToken.None) && keyInput.Password == "" && fixture.SavedProvider?.KeyChange == SecretChange.Replace,
                    "native provider save clears key input");
                var roleForm = new RoleConfigurationForm(fixture, fixture, new("test", "builder"));
                await roleForm.LoadAsync(CancellationToken.None);
                Check(await roleForm.SaveAsync(CancellationToken.None) && fixture.SavedRole?.Name == "Builder", "native role profile save");
                Check(control.RoleCount == 2, "native role cards loaded");
                await control.SelectRoleAsync("test", fixture.Builder);
                control.Composer.Draft = "implement";
                await control.AddImagesAsync(["fixture.png"]); Check(control.Composer.ImageCount == 1, "native image draft added");
                await control.SelectRoleAsync("test", fixture.Reviewer);
                Check(control.Composer.ImageCount == 0, "image draft isolated by role");
                Check(control.Composer.Draft == "", "draft isolated");
                await control.SelectRoleAsync("test", fixture.Builder);
                Check(control.Composer.Draft == "implement", "draft restored");
                Check(control.Composer.ImageCount == 1, "image draft restored");
                await control.SendAsync();
                Check(control.Composer.Draft == "", "receipt clears draft");
                Check(fixture.Sent?.Role.AgentId == "builder", "send retains role");
                Check(fixture.Sent?.Images?.Count == 1 && control.Composer.ImageCount == 0, "typed image retained in send and receipt clears image draft");
                var imagePreview = new ImageAttachmentView(fixture, "test", "vision-fixture", "图片", CancellationToken.None);
                await imagePreview.LoadAsync(); Check(imagePreview.PreviewLoaded, "native bitmap decodes Core resolved preview");
                Check(control.CurrentConversation?.Messages.Length == 2, "canonical messages displayed");
                Check(fixture.ReplayRestarts == 1 && control.CurrentConversation?.ActiveRun?.OutputSnapshot.Markdown == "输出",
                    "snapshot race restarts bounded replay instead of retaining truncated activity");
                control.SetRoleFilter("does-not-exist");
                Check(control.VisibleRoleCount == 0 && control.SelectedRole?.AgentId == "builder", "search preserves active role");
                control.SetRoleFilter(""); Check(control.VisibleRoleCount == 2, "clear search restores roles");
                var readsBeforeStreaming = fixture.ConversationReads;
                fixture.Streaming = true; fixture.Changed.TrySetResult();
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                    while (control.CurrentConversation?.EventCursor != 3) await Task.Delay(10, timeout.Token);
                Check(control.CurrentConversation?.ActiveRun?.OutputSnapshot.Markdown == "输出流式正文", "commit notification streams without polling");
                Check(fixture.ConversationReads == readsBeforeStreaming && fixture.ActivityReads > 1,
                    "streaming activity uses deltas without full conversation refresh");
                var flow = new TurnContentView();
                flow.Update([new("thought", "thinking", "running", "思考", 1)], "正文");
                var reasoning = (Expander)flow.Children[0]; reasoning.IsExpanded = false;
                var answer = flow.Children[1];
                flow.Update([new("thought", "thinking", "running", "思考继续", 1)], "正文");
                Check(ReferenceEquals(reasoning, flow.Children[0]) && !reasoning.IsExpanded && ReferenceEquals(answer, flow.Children[1]), "stream retains blocks and disclosure state");
                await control.CancelAsync(); Check(fixture.Cancelled == "turn", "canonical cancellation");
                fixture.Terminal = true; fixture.TerminalChanged.TrySetResult();
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                    while (control.CurrentConversation?.EventCursor != 4) await Task.Delay(10, timeout.Token);
                Check(control.CurrentConversation?.ActiveRun is null && fixture.ConversationReads > readsBeforeStreaming,
                    "lifecycle changes return to authoritative Core snapshot");
                Check(MessageCard.RenderText("# Title\n```cs\nConsole.WriteLine(1);\n```\n正文") is StackPanel { Children.Count: 3 }, "native heading code text");
                var markdown = new MarkdownView("**粗体** *斜体* ~~删除~~ `code` [文档](https://example.com) [危险](javascript:alert)\n\n> 引用\n\n3. 第一\n4. 第二\n\n|名称|值|\n|---|---|\n|a|b|");
                var paragraph = (TextBlock)markdown.Children[0];
                Check(paragraph.Inlines.OfType<Microsoft.UI.Xaml.Documents.Bold>().Count() == 1 && paragraph.Inlines.OfType<Microsoft.UI.Xaml.Documents.Italic>().Count() == 1,
                    "native emphasis inlines");
                Check(paragraph.Inlines.OfType<Microsoft.UI.Xaml.Documents.Hyperlink>().Single().NavigateUri.Scheme == "https", "only web schemes become active links");
                Check(markdown.Children.Count == 4 && markdown.Children[3] is ScrollViewer { Content: Grid { Children.Count: 4 } }, "quote list and native table structure");
                var stable = new MarkdownView("# 稳定标题\n\n正文"); var title = stable.Children[0];
                stable.Update("# 稳定标题\n\n正文追加\n\n```cs\nvar x = 1;");
                Check(ReferenceEquals(title, stable.Children[0]) && stable.Children.Count == 3, "stream preserves stable blocks and renders unfinished code fence");
                var literal = new MarkdownView("<script>alert(1)</script>");
                Check(((TextBlock)literal.Children[0]).Inlines.OfType<Microsoft.UI.Xaml.Documents.Run>().Any(r => r.Text.Contains("<script>")), "html remains literal text");
                var reference = new MarkdownView("[链接][id]\n\n[id]: https://one.example");
                reference.Update("[链接][id]\n\n[id]: https://two.example");
                Check(((TextBlock)reference.Children[0]).Inlines.OfType<Microsoft.UI.Xaml.Documents.Hyperlink>().Single().NavigateUri.Host == "two.example", "reference definition changes update existing paragraph link");
                var tasks = new MarkdownView("- [x] 已完成\n- [ ] 待处理");
                var taskRows = (StackPanel)tasks.Children[0];
                var taskText = (TextBlock)((StackPanel)((Grid)taskRows.Children[0]).Children[1]).Children[0];
                Check(taskText.Inlines.OfType<Microsoft.UI.Xaml.Documents.Run>().Any(r => r.Text.Contains("☑")), "task list displays canonical checked state");
                var activity = new TurnContentView();
                activity.Update([new("root", "tool_call", "running", "", 1, "terminal", ToolCallId: "root"),
                    new("child", "tool_call", "running", "", 2, "read", ToolCallId: "child", ParentToolCallId: "root"),
                    new("spawn", "delegation", "running", "review code", 3, "reviewer", DelegationExecutionId: "run")], "");
                var childTool = (Expander)activity.Children[1]; childTool.IsExpanded = true;
                var delegation = (Expander)activity.Children[2]; delegation.IsExpanded = true;
                Check(childTool.Margin.Left == 16 && delegation.Header.ToString()!.Contains("子代理"), "native nested tool and delegation identity");
                activity.Update([new("root", "tool_call", "running", "", 1, "terminal", ToolCallId: "root"),
                    new("child", "tool_call", "running", "", 2, "read", ToolCallId: "child", ParentToolCallId: "root"),
                    new("spawn", "delegation", "running", "review code", 3, "reviewer", DelegationExecutionId: "run"),
                    new("done", "delegation", "success", "reviewed", 4, DelegationExecutionId: "run")], "");
                Check(ReferenceEquals(delegation, activity.Children[2]) && delegation.IsExpanded && delegation.Header.ToString()!.Contains("已完成"), "delegation terminal update preserves card and expansion");
                var acceptedInput = new ChatMessage("failed-input", null, "user", "用户", DateTimeOffset.UtcNow, "原始请求", "accepted", []);
                var interruptedCard = new MessageCard(acceptedInput, () => Task.FromResult(new ProcessDetails("failed-input",
                    [new("thought", "thinking", "done", "已输出的思考", 1), new("tool", "tool_result", "error", "失败记录", 2, "terminal", ExitCode: 1)])));
                var interruptedPanel = (StackPanel)((Border)interruptedCard.Content).Child;
                var interruptedDetails = interruptedPanel.Children.OfType<Expander>().Single();
                Check(interruptedDetails.Visibility == Visibility.Collapsed, "accepted input does not expose terminal detail prematurely");
                interruptedCard.Update(acceptedInput with { TurnOutcome = new("cancelled", null, null) });
                Check(interruptedPanel.Children.OfType<InfoBar>().Single() is { IsOpen: true, Severity: InfoBarSeverity.Informational }
                    && interruptedDetails.Visibility == Visibility.Visible, "cancellation without error text remains visible on existing input card");
                interruptedDetails.IsExpanded = true;
                Check(((StackPanel)interruptedDetails.Content).Children.OfType<TurnContentView>().Single().Children.Count == 2,
                    "terminal activity recovers into separate execution disclosure");
                Check(((TurnContentView)interruptedPanel.Children[1]).Children.Count == 1,
                    "recovered execution does not replace or mix with authored input");
                var slow = control.SelectRoleAsync("test", new Agent("slow", "slow"));
                await control.SelectRoleAsync("test", fixture.Reviewer);
                fixture.Late.TrySetResult(fixture.Conversation("slow")); await slow;
                Check(control.CurrentConversation?.AgentId == "reviewer", "late reply rejected");
                control.Dispose(); Check(fixture.Disposed, "transport disposed");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(Report))!);
                await File.WriteAllTextAsync(Report, JsonSerializer.Serialize(new { success = true, checks = 41, native = true }));
            }
            catch (Exception e) { await File.WriteAllTextAsync(Report, JsonSerializer.Serialize(new { success = false, error = e.ToString() })); Environment.ExitCode = 1; }
            finally { if (!Environment.GetCommandLineArgs().Contains("--preview")) { control.Dispose(); _window.Close(); } }
        };
        _window.Activate();
    }
    private static void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); }
    private sealed class Fixture(string imagePath) : IChatClient, IWorkspaceSetupClient, IConfigurationClient, IConversationChanges, IImageAttachmentClient, IConversationActivity
    {
        public int MaxImagesPerMessage => 600;
        public Task<AttachedImage> ImportImageAsync(RoleKey role, string path, CancellationToken ct) => Task.FromResult(new AttachedImage("vision-fixture", Path.GetFileName(path), "image/png", 1, 1));
        public async Task<ImagePreview> GetImagePreviewAsync(string workspace, string artifact, CancellationToken ct)
        {
            var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(imagePath)!);
            var file = await folder.CreateFileAsync(Path.GetFileName(imagePath), Windows.Storage.CreationCollisionOption.ReplaceExisting);
            using var stream = await file.OpenAsync(Windows.Storage.FileAccessMode.ReadWrite);
            var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied, 1, 1, 96, 96, [255, 100, 20, 255]);
            await encoder.FlushAsync();
            return new(imagePath, "image/png", 1, 1);
        }
        public RoleSettings? SavedRole;
        public ProviderModelEdit? SavedProvider;
        public Task<ProviderSettings[]> GetProvidersAsync(CancellationToken ct) => Task.FromResult<ProviderSettings[]>([]);
        public Task SaveProviderModelAsync(ProviderModelEdit edit, CancellationToken ct) { SavedProvider = edit; return Task.CompletedTask; }
        public Task<RoleSettings> GetRoleSettingsAsync(RoleKey role, CancellationToken ct) => Task.FromResult(new RoleSettings(role, "Builder", "Code", true, "Developer", "Build carefully", null, null));
        public Task SaveRoleSettingsAsync(RoleSettings edit, CancellationToken ct) { SavedRole = edit; return Task.CompletedTask; }
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
            Sent is null || Terminal ? null : new("r", "running", "执行中", "编译", new(Streaming ? "流式正文" : "输出", [new("e", "tool_call", "running", "dotnet build", 2, "terminal", ToolCallId: "call", TurnId: "turn")], new("turn", 2, 2, 2, false))),
            Sent is null ? 0 : Terminal ? 4 : Streaming ? 3 : 2);
        public readonly TaskCompletionSource Changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource TerminalChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Streaming, Terminal;
        public async Task WaitForChangeAsync(RoleKey role, string sessionId, long cursor, CancellationToken ct)
        {
            if (cursor < 3) await Changed.Task.WaitAsync(ct);
            else if (cursor < 4) await TerminalChanged.Task.WaitAsync(ct);
            else await Task.Delay(Timeout.Infinite, ct);
        }
        public int ConversationReads, ActivityReads, ReplayRestarts;
        public Task<Conversation?> GetConversationAsync(RoleKey role, long? cursor, CancellationToken ct)
        { ConversationReads++; return role.AgentId == "slow" ? Late.Task : Task.FromResult<Conversation?>(Conversation(role.AgentId)); }
        public Task<ActivityPage> ReadActivityAsync(RoleKey role, ActivityRead read, CancellationToken ct)
        {
            ActivityReads++;
            if (read.Replay && ReplayRestarts == 0)
            { ReplayRestarts++; return Task.FromResult(new ActivityPage(read, read.AfterSequence, false, true, [])); }
            var ceiling = read.ThroughSequence ?? (Terminal ? 4 : Streaming ? 3 : 2);
            ProcessItem[] events = [new("text-1", "text", "done", "输出", 1, TurnId: "turn"),
                new("e", "tool_call", "running", "dotnet build", 2, "terminal", ToolCallId: "call", TurnId: "turn"),
                new("text-3", "text", "done", "流式正文", 3, TurnId: "turn")];
            return Task.FromResult(new ActivityPage(read, ceiling, false, Terminal && ceiling >= 4,
                events.Where(e => e.Sequence > read.AfterSequence && e.Sequence <= ceiling).ToArray()));
        }
        public Task<string> EnsureSessionAsync(RoleKey role, Agent agent, CancellationToken ct) => Task.FromResult("session");
        public Task<Acceptance> SendAsync(PendingSend send, CancellationToken ct) { Sent = send; return Task.FromResult(new Acceptance("session", "m", ["turn"], 1)); }
        public Task CancelAsync(string workspace, string conversation, string turn, CancellationToken ct) { Cancelled = turn; return Task.CompletedTask; }
        public Task<ProcessDetails> GetProcessAsync(RoleKey role, string message, CancellationToken ct) => Task.FromResult(new ProcessDetails(message, []));
        public void Dispose() => Disposed = true;
    }
}
