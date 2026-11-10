using System.Text.Json;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingDingTalk;
using PuddingRuntime.Services;

namespace PuddingAgent.Services;

public sealed class DingTalkGroupChannel(PuddingDataPaths paths, AgentManifestCatalog agents, IRuntimeLlmClient llm,
    ILlmConfigService configs, IHttpClientFactory http, ILoggerFactory logs, IAgentExecutionStateRegistry state,
    ILogger<DingTalkGroupChannel> log) : BackgroundService
{
    private sealed record Settings(bool Enabled, string AgentId, string ClientId, string CredentialFile, string? ReviewedSourceRoot);
    private sealed record Credential(string ClientId, string ClientSecret);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var file = paths.SystemConfigFile("dingtalk-group.json");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (File.Exists(file))
                {
                    var binding = await ReadAsync<Settings>(file, stoppingToken);
                    if (binding.Enabled && await ActiveAsync(binding, stoppingToken))
                        await RunBindingAsync(file, binding, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                // Provider exceptions can contain capability URLs. Never log the exception message or request body.
                log.LogWarning("DingTalk group connection unavailable ({Failure})", ex.GetType().Name);
            }
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }

    private async Task RunBindingAsync(string file, Settings binding, CancellationToken ct)
    {
        var root = Path.Combine(paths.ChannelsRoot, "dingtalk-group");
        Directory.CreateDirectory(root);
        using var owner = new FileStream(Path.Combine(root, "owner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var journal = new DingTalkReplyJournal(root);
        using var agent = new DingTalkPublicAgent(agents, paths, binding.AgentId, llm, configs, binding.ReviewedSourceRoot,
            new GroupFeedbackInbox(Path.Combine(root, "feedback")), http, logs, state);
        var handler = new DingTalkGroupHandler(binding.ClientId, agent, journal, TimeProvider.System);
        using var stream = new DingTalkStream();
        async Task<bool> IsCurrent(CancellationToken token) =>
            File.Exists(file) && await ReadAsync<Settings>(file, token) == binding && await ActiveAsync(binding, token);
        while (await IsCurrent(ct))
        {
            var key = await ReadAsync<Credential>(binding.CredentialFile, ct);
            if (key.ClientId != binding.ClientId || string.IsNullOrWhiteSpace(key.ClientSecret))
                throw new InvalidDataException("Credential binding invalid.");
            await stream.ListenAsync(key.ClientId, key.ClientSecret, handler,
                result => log.LogInformation("DingTalk group {Status}", result), ct, IsCurrent);
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
    }

    private async Task<bool> ActiveAsync(Settings binding, CancellationToken ct)
    {
        var manifest = await agents.GetAsync(binding.AgentId, ct);
        return manifest is { IsEnabled: true, IsFrozen: false, AgentsMdFile: "AGENTS.md" }
            && manifest.AgentInstanceId == binding.AgentId;
    }

    private static async Task<T> ReadAsync<T>(string path, CancellationToken ct)
    {
        using var input = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(input, JsonSerializerOptions.Web, ct)
            ?? throw new InvalidDataException("Missing channel binding.");
    }
}
