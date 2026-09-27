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
            var originalWorkspaces = await client.GetWorkspacesAsync(timeout.Token);
            Assert.NotNull(originalWorkspaces);
            var localSetup = Assert.IsAssignableFrom<IWorkspaceSetupClient>(client);
            using (var localNetwork = new LoopbackObserver(address))
            {
                Assert.NotNull(await localSetup.GetSetupModelsAsync(timeout.Token));
                var input = new WorkspaceSetupRequest("default", "Native test", "Native builder", null);
                var created = await Task.WhenAll(localSetup.SetupWorkspaceAsync(input, timeout.Token), localSetup.SetupWorkspaceAsync(input, timeout.Token));
                Assert.Equal(created[0], created[1]);
                var reused = await localSetup.SetupWorkspaceAsync(input with { WorkspaceName = "Do not rename", RoleName = "Do not replace" }, timeout.Token);
                Assert.Equal(created[0], reused);
                Assert.Equal("Native builder", Assert.Single(await client.GetAgentsAsync("default", timeout.Token)).Label);
                Assert.Equal(Assert.Single(originalWorkspaces).Name, Assert.Single(await client.GetWorkspacesAsync(timeout.Token)).Name);
                await Assert.ThrowsAsync<ArgumentException>(() => localSetup.SetupWorkspaceAsync(input with { WorkspaceId = "../outside" }, timeout.Token));
                await Assert.ThrowsAsync<InvalidOperationException>(() => localSetup.SetupWorkspaceAsync(input with {
                    WorkspaceId = "invalid-model", Model = new("missing", "missing", "missing") }, timeout.Token));
                Assert.Single(await client.GetWorkspacesAsync(timeout.Token));
                var newWorkspace = await localSetup.SetupWorkspaceAsync(input with { WorkspaceId = "native-new" }, timeout.Token);
                Assert.Equal("native-new", newWorkspace.WorkspaceId);
                Assert.Single(await client.GetAgentsAsync("native-new", timeout.Token));
                Assert.Equal("Native test", (await client.GetWorkspacesAsync(timeout.Token)).Single(w => w.WorkspaceId == "native-new").Name);
                Assert.Empty(localNetwork.Requests);
            }
            using var status = await http.GetAsync(new Uri(address, "/api/bootstrap/status"), timeout.Token);
            using var statusBody = JsonDocument.Parse(await status.Content.ReadAsStringAsync(timeout.Token));
            Assert.False(statusBody.RootElement.GetProperty("hasAdmin").GetBoolean());
            Assert.Equal(0, statusBody.RootElement.GetProperty("userCount").GetInt32());
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
            var workspace = Assert.Single(workspaces, w => w.WorkspaceId == "default");
            var agents = await client.GetAgentsAsync(workspace.WorkspaceId, timeout.Token);
            var agent = Assert.Single(agents, a => a.Name == "Native builder" || a.DisplayName == "Native builder");
            var role = new RoleKey(workspace.WorkspaceId, agent.AgentId);
            var config = Assert.IsAssignableFrom<IConfigurationClient>(client);
            var modelEdit = new ProviderModelEdit("native-fixture", "Native fixture", "https://example.invalid/v1", false,
                new("fixture", "Fixture model", "openai", 8192, 1024), SecretChange.Replace, "fixture-only");
            await config.SaveProviderModelAsync(modelEdit, timeout.Token);
            var provider = Assert.Single(await config.GetProvidersAsync(timeout.Token), p => p.Id == "native-fixture");
            Assert.True(provider.HasKey);
            Assert.DoesNotContain("fixture-only", JsonSerializer.Serialize(provider));
            await config.SaveProviderModelAsync(modelEdit with { KeyChange = SecretChange.Keep, NewKey = null }, timeout.Token);
            Assert.True((await config.GetProvidersAsync(timeout.Token)).Single(p => p.Id == "native-fixture").HasKey);
            await config.SaveProviderModelAsync(modelEdit with { KeyChange = SecretChange.Clear, NewKey = null }, timeout.Token);
            Assert.False((await config.GetProvidersAsync(timeout.Token)).Single(p => p.Id == "native-fixture").HasKey);
            var profile = await config.GetRoleSettingsAsync(role, timeout.Token);
            await config.SaveRoleSettingsAsync(profile with { Description = "Edited natively", SystemPrompt = "Verify code before completion." }, timeout.Token);
            Assert.Equal("Edited natively", (await config.GetRoleSettingsAsync(role, timeout.Token)).Description);
            await config.SaveProviderModelAsync(modelEdit with { Enabled = true, KeyChange = SecretChange.Keep, NewKey = null }, timeout.Token);
            await config.SaveRoleSettingsAsync(profile with { ProviderId = "native-fixture", ModelId = "fixture" }, timeout.Token);
            Assert.Equal("fixture", (await config.GetRoleSettingsAsync(role, timeout.Token)).ModelId);
            await config.SaveRoleSettingsAsync(profile with { Description = "Edited natively", ProviderId = "", ModelId = "" }, timeout.Token);
            Assert.Equal("", (await config.GetRoleSettingsAsync(role, timeout.Token)).ProviderId);
            await config.SaveProviderModelAsync(modelEdit with { KeyChange = SecretChange.Keep, NewKey = null }, timeout.Token);
            var session = await client.EnsureSessionAsync(role, agent, timeout.Token);
            Assert.False(string.IsNullOrWhiteSpace(session));
            var images = Assert.IsAssignableFrom<IImageAttachmentClient>(client);
            var imagePath = Path.Combine(root, "fixture.png");
            using (var bitmap = new SkiaSharp.SKBitmap(4, 3))
            using (var image = SkiaSharp.SKImage.FromBitmap(bitmap))
            using (var encoded = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100))
                await File.WriteAllBytesAsync(imagePath, encoded.ToArray(), timeout.Token);
            var attachment = await images.ImportImageAsync(role, imagePath, timeout.Token);
            Assert.Equal("image/png", attachment.MimeType); Assert.Equal(4, attachment.Width); Assert.Equal(3, attachment.Height);
            var preview = await images.GetImagePreviewAsync(role.WorkspaceId, attachment.ArtifactId, timeout.Token);
            Assert.True(File.Exists(preview.LocalPath)); Assert.NotEqual(imagePath, preview.LocalPath);
            await Assert.ThrowsAsync<FileNotFoundException>(() => images.GetImagePreviewAsync("native-new", attachment.ArtifactId, timeout.Token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => images.ImportImageAsync(role with { AgentId = "missing" }, imagePath, timeout.Token));
            var invalidPath = Path.Combine(root, "invalid.png"); await File.WriteAllTextAsync(invalidPath, "not an image", timeout.Token);
            var invalidImage = await Assert.ThrowsAsync<PuddingCode.Core.VisionPipelineException>(() => images.ImportImageAsync(role, invalidPath, timeout.Token));
            Assert.Equal(PuddingCode.Core.VisionErrorCodes.MediaInvalid, invalidImage.Code);
            var pending = PendingSend.Create(role, session, "Native component admission test. Reply briefly.", [attachment]);
            var changes = Assert.IsAssignableFrom<IConversationChanges>(client);
            var beforeSend = await client.GetConversationAsync(role, null, timeout.Token);
            var change = changes.WaitForChangeAsync(role, session, beforeSend!.EventCursor, timeout.Token);
            var secondChange = changes.WaitForChangeAsync(role, session, beforeSend.EventCursor, timeout.Token);
            var receipt = await client.SendAsync(pending, timeout.Token);
            await Task.WhenAll(change, secondChange).WaitAsync(TimeSpan.FromSeconds(5));
            await changes.WaitForChangeAsync(role, session, beforeSend.EventCursor, timeout.Token).WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<InvalidOperationException>(() => changes.WaitForChangeAsync(role with { AgentId = "wrong" }, session, 0, timeout.Token));
            var retry = await client.SendAsync(pending, timeout.Token);
            Assert.Equal(receipt.MessageId, retry.MessageId);
            Assert.Equal(receipt.TurnIds, retry.TurnIds);
            Assert.NotEmpty(receipt.TurnIds);
            var conversation = await client.GetConversationAsync(role, null, timeout.Token);
            Assert.NotNull(conversation);
            Assert.Equal(receipt.ConversationId, conversation.MainSessionId);
            Assert.Contains(conversation.Messages, message => message.MessageId == receipt.MessageId);
            var persisted = Assert.Single(conversation.Messages, m => m.MessageId == receipt.MessageId);
            Assert.Contains(persisted.ContentParts!, part => part.Type == "image" && part.ArtifactId == attachment.ArtifactId && part.Detail == "original");
            Assert.DoesNotContain(imagePath, JsonSerializer.Serialize(persisted));
            var imageOnly = await client.SendAsync(PendingSend.Create(role, session, "", [attachment]), timeout.Token);
            var imageOnlyView = await client.GetConversationAsync(role, null, timeout.Token);
            var imageOnlyMessage = Assert.Single(imageOnlyView!.Messages, m => m.MessageId == imageOnly.MessageId);
            Assert.Equal("", imageOnlyMessage.Content);
            Assert.Contains(imageOnlyMessage.ContentParts!, p => p.Type == "image" && p.ArtifactId == attachment.ArtifactId);
            Assert.Contains(await client.GetStatusesAsync(workspace.WorkspaceId, timeout.Token), status => status.AgentId == agent.AgentId);
            var process = await client.GetProcessAsync(role, receipt.MessageId, timeout.Token);
            Assert.Equal(receipt.MessageId, process.MessageId);
            Assert.Empty(network.Requests);
            using var probe = await http.GetAsync(new Uri(address, "/health/ready"), timeout.Token);
            Assert.Contains("/health/ready", network.Requests); // Prove the zero-HTTP observation is not a disabled listener.
            var pendingSubscription = changes.WaitForChangeAsync(role, session, long.MaxValue, timeout.Token);
            await kernel.StopAsync(timeout.Token);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pendingSubscription);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => client.GetWorkspacesAsync(timeout.Token));
            await kernel.StartAsync(root, timeout.Token);
            using var restarted = factory.CreateChatClient();
            Assert.NotEmpty(await restarted.GetWorkspacesAsync(timeout.Token));
            var restoredPreview = await ((IImageAttachmentClient)restarted).GetImagePreviewAsync(role.WorkspaceId, attachment.ArtifactId, timeout.Token);
            Assert.True(File.Exists(restoredPreview.LocalPath));
            Assert.NotEmpty(await restarted.GetAgentsAsync(workspace.WorkspaceId, timeout.Token));
            var restartedConfig = Assert.IsAssignableFrom<IConfigurationClient>(restarted);
            Assert.Equal("Edited natively", (await restartedConfig.GetRoleSettingsAsync(role, timeout.Token)).Description);
            Assert.False((await restartedConfig.GetProvidersAsync(timeout.Token)).Single(p => p.Id == "native-fixture").HasKey);
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
