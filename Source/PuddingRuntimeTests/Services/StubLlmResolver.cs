using PuddingCode.Abstractions;
using PuddingCode.Platform;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// 压缩摘要测试用的最小路由桩：不读配置、不发网络请求。
/// Profile 由被测代码按调用语义自行构造，这里只提供身份三元组。
/// </summary>
internal sealed class StubLlmResolver : ILlmResolver
{
    public Task<ResolvedLlmRoute> ResolveRouteAsync(
        string? modelRoute = null,
        IReadOnlyCollection<string>? requiredCapabilityTags = null,
        CancellationToken ct = default)
        => Task.FromResult(new ResolvedLlmRoute
        {
            ProviderId = "stub-provider",
            ModelId = modelRoute ?? "stub-model",
            Config = new LlmConfig { ModelId = modelRoute ?? "stub-model" },
        });
}
