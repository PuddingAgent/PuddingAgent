using PuddingCode.Configuration;
using PuddingCode.Tasks;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;
using PuddingPlatform.Services.Scheduling;

namespace PuddingPlatformTests.Services.Scheduling;

[TestClass]
public sealed class ProviderModelExecutionWindowResolverTests
{
    [TestMethod]
    public async Task Evaluate_Anytime_DoesNotRequirePriceProfile()
    {
        var resolver = CreateResolver("bigmodel", "glm", []);
        var now = DateTimeOffset.Parse("2026-08-29T02:00:00Z");

        var decision = await resolver.EvaluateAsync(
            "ws", "agent-1", TaskExecutionWindow.Anytime, now);

        Assert.AreEqual(PuddingCode.Scheduling.ExecutionWindowVerdict.Allow, decision.Verdict);
        Assert.AreEqual("allowed_anytime", decision.Code);
    }

    [TestMethod]
    public async Task Evaluate_BigModelNightWindow_AllowsInheritedAutomaticWork()
    {
        var resolver = CreateResolver(
            "bigmodel",
            "glm-5.3-flash",
            [
                Window("offpeak-00-14", "00:00", "14:00"),
                Window("offpeak-18-24", "18:00", "00:00"),
            ]);
        // 22:00 in Asia/Shanghai.
        var now = DateTimeOffset.Parse("2026-08-29T14:00:00Z");

        var decision = await resolver.EvaluateAsync(
            "ws", "agent-1", TaskExecutionWindow.Inherit, now);

        Assert.AreEqual(PuddingCode.Scheduling.ExecutionWindowVerdict.Allow, decision.Verdict);
        Assert.AreEqual("allowed_inherited_off_peak", decision.Code);
        Assert.AreEqual("bigmodel", decision.ProviderId);
        Assert.AreEqual("glm-5.3-flash", decision.ModelId);
        Assert.AreEqual("bigmodel-coding-plan-2026-08", decision.ProfileVersion);
        Assert.AreEqual("offpeak-18-24", decision.WindowKey);
    }

    [TestMethod]
    public async Task Evaluate_DeepSeekPeak_DefersToNextOffPeakBoundary()
    {
        var resolver = CreateResolver(
            "deepseek",
            "deepseek-v4-flash",
            [
                Window("offpeak-00-09", "00:00", "09:00"),
                Window("offpeak-12-14", "12:00", "14:00"),
                Window("offpeak-18-24", "18:00", "00:00"),
            ],
            profileVersion: "deepseek-v4-2026-08-16");
        // 10:00 in Asia/Shanghai, inside DeepSeek's 09:00-12:00 peak.
        var now = DateTimeOffset.Parse("2026-08-29T02:00:00Z");

        var decision = await resolver.EvaluateAsync(
            "ws", "agent-1", TaskExecutionWindow.OffPeakOnly, now);

        Assert.AreEqual(PuddingCode.Scheduling.ExecutionWindowVerdict.Defer, decision.Verdict);
        Assert.AreEqual("execution_window_peak_period", decision.Code);
        Assert.AreEqual(DateTimeOffset.Parse("2026-08-29T04:00:00Z"), decision.NextEligibleAtUtc);
        Assert.AreEqual("offpeak-12-14", decision.WindowKey);
    }

    [TestMethod]
    public async Task Evaluate_MissingProfile_FailsClosedWithStableCode()
    {
        var resolver = CreateResolver("bigmodel", "glm-5.3-flash", []);
        var now = DateTimeOffset.Parse("2026-08-29T14:00:00Z");

        var decision = await resolver.EvaluateAsync(
            "ws", "agent-1", TaskExecutionWindow.OffPeakOnly, now);

        Assert.AreEqual(PuddingCode.Scheduling.ExecutionWindowVerdict.Unknown, decision.Verdict);
        Assert.AreEqual("execution_window_route_profile_unknown", decision.Code);
    }

    private static ProviderModelExecutionWindowResolver CreateResolver(
        string providerId,
        string modelId,
        List<PuddingLlmPriceWindowConfig> windows,
        string profileVersion = "bigmodel-coding-plan-2026-08")
    {
        var config = new PuddingLlmProvidersConfig
        {
            Providers =
            [
                new PuddingLlmProviderConfig
                {
                    ProviderId = providerId,
                    Name = providerId,
                    BaseUrl = "https://example.invalid/v1",
                    ApiKey = "test-only",
                    Models =
                    [
                        new PuddingLlmModelConfig
                        {
                            ModelId = modelId,
                            Name = modelId,
                            Protocol = "responses",
                            PriceWindows = windows,
                            PriceWindowProfileVersion = windows.Count == 0
                                ? null
                                : profileVersion,
                        },
                    ],
                },
            ],
        };
        return new ProviderModelExecutionWindowResolver(
            new PuddingFileLlmConfigService(config),
            new Catalog([Agent(providerId, modelId)]));
    }

    private static PuddingLlmPriceWindowConfig Window(
        string key,
        string start,
        string end) => new()
    {
        WindowKey = key,
        TimeZoneId = "Asia/Shanghai",
        StartLocalTime = start,
        EndLocalTime = end,
        IsOffPeak = true,
    };

    private static WorkspaceAgentDto Agent(string providerId, string modelId) => new(
        AgentId: "agent-1",
        Name: "Agent 1",
        Description: null,
        DisplayName: "Agent 1",
        AvatarId: null,
        AvatarUrl: null,
        SourceTemplateId: "service",
        MainSessionId: "conversation-agent-1",
        SystemPromptOverride: null,
        PreferredProviderId: providerId,
        PreferredModelId: modelId,
        IsEnabled: true,
        IsFrozen: false,
        CreatedAt: DateTimeOffset.UtcNow,
        UpdatedAt: DateTimeOffset.UtcNow);

    private sealed class Catalog(IReadOnlyList<WorkspaceAgentDto> agents)
        : IWorkspaceAgentCatalog
    {
        public Task<IReadOnlyList<WorkspaceAgentDto>> ListAgentsAsync(
            string workspaceId,
            CancellationToken ct = default) => Task.FromResult(agents);
    }
}
