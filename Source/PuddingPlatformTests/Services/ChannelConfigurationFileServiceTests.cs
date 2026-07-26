using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class ChannelConfigurationFileServiceTests
{
    [TestMethod]
    public async Task CreateAndUpdate_ChannelSecretStaysFileBackedAndIsNeverReturned()
    {
        var root = CreateRoot();
        try
        {
            var paths = PuddingDataPaths.FromRoot(root);
            var binder = new RecordingChannelBinder();
            var service = CreateService(paths, binder);

            var created = await service.CreateWorkspaceChannelAsync(
                "default",
                new UpsertWorkspaceChannelRequest(
                    "默认助手 · 飞书",
                    "内部机器人",
                    ChannelProviderKinds.Feishu,
                    "agent-01",
                    "cli_channel_test",
                    "top-secret",
                    true,
                    [" ou_admin ", "ou_admin"],
                    true));

            Assert.IsTrue(created.HasAppSecret);
            Assert.AreEqual("cli_channel_test", created.AppId);
            Assert.AreEqual("agent-01", created.BoundAgentId);
            CollectionAssert.AreEqual(
                new[] { "ou_admin" },
                created.PrivilegedUserOpenIds.ToArray());
            Assert.HasCount(1, binder.Updates);

            var manifestText = await File.ReadAllTextAsync(
                paths.ChannelManifestFile(created.ChannelId));
            StringAssert.Contains(manifestText, "top-secret");
            Assert.IsFalse(JsonSerializer.Serialize(created).Contains(
                "top-secret",
                StringComparison.Ordinal));

            var updated = await service.UpdateWorkspaceChannelAsync(
                "default",
                created.ChannelId,
                new UpsertWorkspaceChannelRequest(
                    "默认助手 · 飞书（更新）",
                    null,
                    ChannelProviderKinds.Feishu,
                    "agent-01",
                    "cli_channel_test",
                    AppSecret: " ",
                    StreamingRepliesEnabled: false,
                    PrivilegedUserOpenIds: [],
                    IsEnabled: true));

            Assert.IsTrue(updated.HasAppSecret);
            Assert.IsFalse(updated.StreamingRepliesEnabled);
            var stored = await service.GetChannelAsync(created.ChannelId);
            Assert.IsNotNull(stored);
            Assert.AreEqual("top-secret", stored!.Feishu!.AppSecret);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task MigrateLegacyAgentFeishu_CreatesChannelManifestAndRequestsAgentReference()
    {
        var root = CreateRoot();
        try
        {
            var paths = PuddingDataPaths.FromRoot(root);
            var agentRoot = paths.AgentInstanceRoot("agent-legacy");
            Directory.CreateDirectory(agentRoot);
#pragma warning disable CS0618
            var legacy = new AgentInstanceManifest
            {
                AgentInstanceId = "agent-legacy",
                TemplateId = "general-assistant",
                WorkspaceId = "default",
                DisplayName = "Legacy Agent",
                Feishu = new AgentFeishuBotConfig
                {
                    Enabled = true,
                    AppId = "cli_legacy",
                    AppSecret = "legacy-secret",
                    StreamingRepliesEnabled = true,
                    PrivilegedUserOpenIds = ["ou_owner"],
                },
            };
#pragma warning restore CS0618
            await File.WriteAllTextAsync(
                Path.Combine(agentRoot, "manifest.json"),
                JsonSerializer.Serialize(
                    legacy,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var binder = new RecordingChannelBinder();
            var service = CreateService(paths, binder);

            Assert.AreEqual(1, await service.MigrateLegacyAgentFeishuBindingsAsync());

            var channelId = "feishu-agent-legacy";
            var channel = await service.GetChannelAsync(channelId);
            Assert.IsNotNull(channel);
            Assert.AreEqual("cli_legacy", channel!.Feishu!.AppId);
            Assert.AreEqual("legacy-secret", channel.Feishu.AppSecret);
            Assert.IsTrue(binder.Updates.Any(update =>
                update == ("default", channelId, "agent-legacy")));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task Create_DuplicateFeishuAppId_IsRejected()
    {
        var root = CreateRoot();
        try
        {
            var service = CreateService(
                PuddingDataPaths.FromRoot(root),
                new RecordingChannelBinder());
            var request = new UpsertWorkspaceChannelRequest(
                "飞书一号",
                null,
                ChannelProviderKinds.Feishu,
                "agent-01",
                "cli_duplicate",
                "secret-one",
                true,
                [],
                true);
            await service.CreateWorkspaceChannelAsync("default", request);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.CreateWorkspaceChannelAsync(
                    "default",
                    request with
                    {
                        Name = "飞书二号",
                        AppSecret = "secret-two",
                    }));
            StringAssert.Contains(error.Message, "已绑定");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static ChannelConfigurationFileService CreateService(
        PuddingDataPaths paths,
        RecordingChannelBinder binder) => new(
        paths,
        new FakeAgentCatalog(),
        binder,
        NullLogger<ChannelConfigurationFileService>.Instance);

    private static string CreateRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "pudding-channel-config-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private sealed class FakeAgentCatalog : IWorkspaceAgentCatalog
    {
        public Task<IReadOnlyList<WorkspaceAgentDto>> ListAgentsAsync(
            string workspaceId,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WorkspaceAgentDto>>(
            [
                new WorkspaceAgentDto(
                    "agent-01",
                    "agent-01",
                    null,
                    "Agent 01",
                    null,
                    null,
                    "general-assistant",
                    null,
                    null,
                    null,
                    null,
                    true,
                    false,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow),
            ]);
    }

    private sealed class RecordingChannelBinder : IAgentChannelBinder
    {
        public List<(string WorkspaceId, string ChannelId, string? AgentId)> Updates { get; } = [];

        public Task SetChannelBindingAsync(
            string workspaceId,
            string channelId,
            string? agentId,
            CancellationToken ct = default)
        {
            Updates.Add((workspaceId, channelId, agentId));
            return Task.CompletedTask;
        }
    }
}
