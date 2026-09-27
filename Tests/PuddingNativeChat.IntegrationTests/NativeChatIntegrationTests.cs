using System.Net.Http.Json;
using System.Text.Json;
using System.Diagnostics;
using System.Collections.Concurrent;
using PuddingChat;
using PuddingDesktop.Composition;
using PuddingDesktop.Foundation;

namespace PuddingNativeChat.IntegrationTests;

public class NativeChatIntegrationTests
{
    [Fact]
    public async Task RealCore_LocalClientNeedsNoAccount_WebStillRequiresAuthentication()
    {
        var root = Path.Combine(Path.GetTempPath(), "Pudding-native-chat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        await File.WriteAllTextAsync(Path.Combine(root, "config", "system.json"), JsonSerializer.Serialize(new
        { Jwt = new { Key = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"), Issuer = "chat-test", Audience = "chat-test" } }));
        var factory = new DesktopKernelFactory(new Desktop());
        await using var kernel = new InProcessKernel(factory);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await kernel.StartAsync(root, timeout.Token);
            var address = kernel.Snapshot.WorkbenchAddress!;
            using var http = new HttpClient();
            using var client = factory.CreateChatClient();
            // A fresh Core has no account: native reads must already work.
            Assert.NotNull(await client.GetWorkspacesAsync(timeout.Token));
            var password = "Test-" + Guid.NewGuid().ToString("N") + "aA1";
            using var setup = await http.PostAsJsonAsync(new Uri(address, "/api/bootstrap/complete"), new
            { admin = new { userId = "native-test", email = "native@example.invalid", password },
                defaults = new { workspaceName = "Native test", agentName = "Native builder" } }, timeout.Token);
            Assert.True(setup.IsSuccessStatusCode, $"Bootstrap: {setup.StatusCode}");
            using var anonymous = await http.GetAsync(new Uri(address, "/api/workspaces"), timeout.Token);
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, anonymous.StatusCode);
            using var invalid = await http.PostAsJsonAsync(new Uri(address, "/api/login/account"),
                new { username = "native-test", password = "wrong" }, timeout.Token);
            using var invalidBody = JsonDocument.Parse(await invalid.Content.ReadAsStringAsync(timeout.Token));
            Assert.Equal("error", invalidBody.RootElement.GetProperty("status").GetString());
            using var remote = new HttpClient();
            using var login = await remote.PostAsJsonAsync(new Uri(address, "/api/login/account"),
                new { username = "native-test", password }, timeout.Token);
            using var loginBody = JsonDocument.Parse(await login.Content.ReadAsStringAsync(timeout.Token));
            Assert.Equal("ok", loginBody.RootElement.GetProperty("status").GetString());
            remote.DefaultRequestHeaders.Authorization = new("Bearer", loginBody.RootElement.GetProperty("token").GetString());
            using var authorized = await remote.GetAsync(new Uri(address, "/api/workspaces"), timeout.Token);
            Assert.Equal(System.Net.HttpStatusCode.OK, authorized.StatusCode);
            using var network = new LoopbackObserver(address);

            var workspaces = await client.GetWorkspacesAsync(timeout.Token);
            var workspace = Assert.Single(workspaces);
            var agents = await client.GetAgentsAsync(workspace.WorkspaceId, timeout.Token);
            var agent = Assert.Single(agents, a => a.Name == "Native builder" || a.DisplayName == "Native builder");
            var role = new RoleKey(workspace.WorkspaceId, agent.AgentId);
            var session = await client.EnsureSessionAsync(role, agent, timeout.Token);
            Assert.False(string.IsNullOrWhiteSpace(session));
            var pending = PendingSend.Create(role, session, "Native component admission test. Reply briefly.");
            var receipt = await client.SendAsync(pending, timeout.Token);
            var retry = await client.SendAsync(pending, timeout.Token);
            Assert.Equal(receipt.MessageId, retry.MessageId);
            Assert.Equal(receipt.TurnIds, retry.TurnIds);
            Assert.NotEmpty(receipt.TurnIds);
            var conversation = await client.GetConversationAsync(role, null, timeout.Token);
            Assert.NotNull(conversation);
            Assert.Equal(receipt.ConversationId, conversation.MainSessionId);
            Assert.Contains(conversation.Messages, message => message.MessageId == receipt.MessageId);
            Assert.Contains(await client.GetStatusesAsync(workspace.WorkspaceId, timeout.Token), status => status.AgentId == agent.AgentId);
            var process = await client.GetProcessAsync(role, receipt.MessageId, timeout.Token);
            Assert.Equal(receipt.MessageId, process.MessageId);
            Assert.Empty(network.Requests);
            using var probe = await http.GetAsync(new Uri(address, "/health/ready"), timeout.Token);
            Assert.Contains("/health/ready", network.Requests); // Prove the zero-HTTP observation is not a disabled listener.
            await kernel.StopAsync(timeout.Token);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => client.GetWorkspacesAsync(timeout.Token));
            await kernel.StartAsync(root, timeout.Token);
            using var restarted = factory.CreateChatClient();
            Assert.NotEmpty(await restarted.GetWorkspacesAsync(timeout.Token));
            Assert.NotEmpty(await restarted.GetAgentsAsync(workspace.WorkspaceId, timeout.Token));
        }
        finally
        {
            await kernel.DisposeAsync(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    private sealed class Desktop : IDesktopServices
    {
        public Task ShowAsync(ShellPage page, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task OpenDocumentAsync(WorkspaceDocument document, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class LoopbackObserver : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly Uri _address;
        private readonly IDisposable _all;
        private readonly List<IDisposable> _subscriptions = [];
        public ConcurrentQueue<string> Requests { get; } = new();
        public LoopbackObserver(Uri address) { _address = address; _all = DiagnosticListener.AllListeners.Subscribe(this); }
        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name == "HttpHandlerDiagnosticListener")
                lock (_subscriptions) _subscriptions.Add(listener.Subscribe(this));
        }
        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (!value.Key.EndsWith(".Start", StringComparison.Ordinal)) return;
            if (value.Value?.GetType().GetProperty("Request")?.GetValue(value.Value) is HttpRequestMessage { RequestUri: { } uri }
                && uri.GetLeftPart(UriPartial.Authority) == _address.GetLeftPart(UriPartial.Authority)) Requests.Enqueue(uri.AbsolutePath);
        }
        public void OnError(Exception error) { }
        public void OnCompleted() { }
        public void Dispose() { _all.Dispose(); lock (_subscriptions) foreach (var subscription in _subscriptions) subscription.Dispose(); }
    }
}
