using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class FeishuRecipientDirectoryTests
{
    [TestMethod]
    public async Task Reads_Config_Without_History_And_Refreshes_Whitelist()
    {
        using var fixture = new Fixture();
        await fixture.WriteChannelAsync();
        var recipients = await fixture.Directory.ListAsync("default", "agent-a");
        Assert.AreEqual(1, recipients.Count);
        Assert.AreEqual("ou_first", recipients[0].OpenId);
        Assert.AreEqual("feishu:channel-a", recipients[0].ConnectorId);
        Assert.IsFalse(JsonSerializer.Serialize(recipients).Contains("test-secret", StringComparison.Ordinal));

        await fixture.WriteChannelAsync(openIds: ["ou_second", " ou_second ", " "]);
        recipients = await fixture.Directory.ListAsync("default", "agent-a");
        CollectionAssert.AreEqual(new[] { "ou_second" }, recipients.Select(item => item.OpenId).ToArray());
        await fixture.WriteChannelAsync(openIds: []);
        Assert.AreEqual(0, (await fixture.Directory.ListAsync("default", "agent-a")).Count);
    }

    [TestMethod]
    [DataRow("disabled-channel")]
    [DataRow("disabled-agent")]
    [DataRow("frozen-agent")]
    [DataRow("unbound-agent")]
    [DataRow("duplicate-binding")]
    [DataRow("other-workspace")]
    [DataRow("other-agent")]
    [DataRow("no-secret")]
    [DataRow("disabled-provider")]
    [DataRow("duplicate-app")]
    public async Task Excludes_Unavailable_And_Foreign_Bindings(string scenario)
    {
        using var fixture = new Fixture();
        await fixture.WriteChannelAsync(
            enabled: scenario != "disabled-channel",
            secret: scenario == "no-secret" ? "" : "test-secret",
            workspace: scenario == "other-workspace" ? "other" : "default");
        fixture.Agents.Items = scenario switch
        {
            "disabled-agent" => [Fixture.Agent() with { IsEnabled = false }],
            "frozen-agent" => [Fixture.Agent() with { IsFrozen = true }],
            "unbound-agent" => [Fixture.Agent() with { ChannelIds = [] }],
            "duplicate-binding" => [Fixture.Agent(), Fixture.Agent() with { AgentId = "agent-b" }],
            _ => [Fixture.Agent()],
        };
        if (scenario == "disabled-provider")
            await fixture.Channels.UpdateProviderAsync("feishu", new("飞书", null, false));
        if (scenario == "duplicate-app")
            await fixture.WriteChannelAsync(channelId: "channel-b");

        var recipients = await fixture.Directory.ListAsync("default",
            scenario == "other-agent" ? "agent-b" : "agent-a");
        Assert.AreEqual(0, recipients.Count);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "pudding-feishu-recipient-tests", Guid.NewGuid().ToString("N"));
        public PuddingDataPaths Paths { get; }
        public Catalog Agents { get; } = new();
        public ChannelConfigurationFileService Channels { get; }
        public FeishuRecipientDirectory Directory { get; }

        public Fixture()
        {
            Paths = PuddingDataPaths.FromRoot(_root);
            Channels = new(Paths, Agents, new NoWrites(), NullLogger<ChannelConfigurationFileService>.Instance);
            Directory = new(Agents, Channels);
        }

        public async Task WriteChannelAsync(bool enabled = true, string secret = "test-secret",
            string workspace = "default", string channelId = "channel-a", string[]? openIds = null)
        {
            var path = Paths.ChannelManifestFile(channelId);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new ChannelInstanceManifest
            {
                ChannelId = channelId, Name = "Test channel", WorkspaceId = workspace,
                ProviderId = "feishu", IsEnabled = enabled,
                Feishu = new() { AppId = "cli_test", AppSecret = secret, PrivilegedUserOpenIds = openIds ?? ["ou_first"] },
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }

        public static WorkspaceAgentDto Agent() => new(
            "agent-a", "agent-a", null, "A", null, null, "general-assistant", null,
            null, null, null, true, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            ChannelIds: ["channel-a"]);

        public void Dispose()
        {
            if (System.IO.Directory.Exists(_root))
                System.IO.Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class Catalog : IWorkspaceAgentCatalog
    {
        public IReadOnlyList<WorkspaceAgentDto> Items { get; set; } = [Fixture.Agent()];
        public Task<IReadOnlyList<WorkspaceAgentDto>> ListAgentsAsync(string workspaceId, CancellationToken ct = default)
            => Task.FromResult(Items);
    }

    private sealed class NoWrites : IAgentChannelBinder
    {
        public Task SetChannelBindingAsync(string workspaceId, string channelId, string? agentId, CancellationToken ct = default)
            => throw new InvalidOperationException("Recipient discovery must not mutate bindings.");
    }
}
