using System.Security.Cryptography;
using System.Text.Json;
using PuddingCode.Abstractions;
using PuddingCode.Platform;

namespace PuddingPlatform.Services.Snapshot;

/// <summary>
/// ADR-059: Agent Execution Snapshot Factory — 组装 Agent/Template/LLM/Skill 配置为不可变快照。
/// 快照只消费统一解析后的 AgentRuntimeProfile，不自行读取 Agent、模板、Provider 或 Skill 存储。
/// 哈希输入显式排除 LLM 密钥与 Skill 下载地址。
/// ADR-077 §4.3：冻结主模型 Protocol/CapabilityTags/VisionPolicy 与可选 VisionHelperRoute；
/// Coordinator 与 Image Reader 消费同一判定，不再各自读取可热变的模型目录。
/// </summary>
public sealed class AgentExecutionSnapshotFactory(
    ILogger<AgentExecutionSnapshotFactory> logger,
    ILlmConfigService? llmConfigService = null,
    ILlmResolver? llmResolver = null) : IAgentExecutionSnapshotFactory
{
    public async Task<AgentExecutionSnapshot> CreateAsync(
        AgentRuntimeProfile profile,
        AgentExecutionSnapshot? previousSnapshot, CancellationToken ct)
    {
        if (previousSnapshot is not null)
        {
            logger.LogDebug("[SnapshotFactory] Reusing previous snapshot {SnapshotId}",
                previousSnapshot.SnapshotId);
            return previousSnapshot;
        }

        var toolReferences = profile.ToolDefinitions?
            .Select(tool => new SnapshotToolRef(tool.Name, Version: null, Source: profile.CapabilitySource))
            .ToArray();
        var skillReferences = profile.SkillPackages?
            .Select(skill => new SnapshotSkillRef(skill.SkillPackageId, Revision: 0))
            .ToArray();

        var modelInfo = llmConfigService?.GetAllModels().FirstOrDefault(model =>
            string.Equals(model.ProviderId, profile.PreferredProviderId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(model.ModelId, profile.PreferredModelId, StringComparison.OrdinalIgnoreCase));
        var capabilityTags = modelInfo?.CapabilityTags?.ToList();
        var protocol = modelInfo?.Protocol;

        VisionHelperRouteSnapshot? visionHelperRoute = null;
        if (llmResolver is not null && !string.IsNullOrWhiteSpace(profile.VisionHelperModel))
        {
            try
            {
                // requiredCapabilityTags 保证 helper 路由具备 vision；解析失败保持 null（delegate 时 fail closed）。
                var route = await llmResolver.ResolveRouteAsync(profile.VisionHelperModel, ["vision"], ct);
                visionHelperRoute = new VisionHelperRouteSnapshot(
                    route.ProviderId,
                    route.ModelId,
                    ["vision"]);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "[SnapshotFactory] visionHelperModel route failed to resolve; image_reader delegate mode will fail closed");
            }
        }

        // V5-T2（ADR-088 决策 2/5）：视觉预算策略来源 = 配置合同 ∩ 模型类别，不再硬编码 Default。
        // 语义显式（无静默假定支持）：模型不在配置 / IsEmbedding / 含 image-generation 标签 /
        // 无 vision 标签 → null（明确不支持图片输入，即便误配 vision 节也忽略）；具备 vision 标签 →
        // 配置合同投影，未配置合同 → VisionRequestPolicy.Default（明确的产品护栏策略，非协议限制）。
        var (visionPolicy, visionPolicySource) = ResolveVisionPolicy(modelInfo);

        var hashInput = JsonSerializer.SerializeToUtf8Bytes(new
        {
            profile.WorkspaceId,
            profile.AgentId,
            profile.DisplayName,
            profile.AvatarUrl,
            profile.SourceTemplateId,
            profile.ConsciousProfileId,
            profile.PreferredProviderId,
            profile.PreferredModelId,
            profile.SystemPrompt,
            profile.CapabilityPolicy,
            profile.MaxRounds,
            profile.MaxElapsedSeconds,
            profile.MaxToolCallsTotal,
            tools = profile.ToolDefinitions?.Select(tool => tool.Name).OrderBy(name => name),
            skills = profile.SkillPackages?
                .Select(skill => new { skill.SkillPackageId, skill.Version })
                .OrderBy(skill => skill.SkillPackageId),
            capabilityTags = capabilityTags is null ? null : capabilityTags.OrderBy(tag => tag),
            protocol,
            visionHelperRoute = visionHelperRoute is null
                ? null
                : new { visionHelperRoute.ProviderId, visionHelperRoute.ModelId },
            // V5-T2：策略（含版本）进快照哈希 —— 配置热更新后新 Run 冻结新快照，哈希可区分。
            visionPolicy = visionPolicy is null
                ? null
                : new
                {
                    visionPolicy.ImageTokenEstimatorVersion,
                    visionPolicy.MaxImagesPerRequest,
                    visionPolicy.InlineMaxBytesPerImage,
                    visionPolicy.InlineMaxTotalBytes,
                    visionPolicy.InlineMaxTotalWireBytes,
                    visionPolicy.FilesMaxBytesPerImage,
                    visionPolicy.FilesMaxTotalBytes,
                    visionPolicy.EstimatedTokensPerImageUpperBound,
                },
        });
        var snapshotHash = $"sha256:{Convert.ToHexString(SHA256.HashData(hashInput)).ToLowerInvariant()}";

        var snapshot = new AgentExecutionSnapshot(
            SnapshotId: Guid.NewGuid().ToString("N"),
            WorkspaceId: profile.WorkspaceId,
            AgentId: profile.AgentId,
            Revision: 0,
            SnapshotHash: snapshotHash,
            DisplayName: profile.DisplayName,
            AvatarUrl: profile.AvatarUrl,
            SystemPrompt: profile.SystemPrompt,
            PersonaJson: null,
            ProviderId: profile.PreferredProviderId,
            ProfileId: profile.ConsciousProfileId,
            ModelId: profile.PreferredModelId,
            CapabilityPolicy: profile.CapabilityPolicy,
            ToolDefinitions: toolReferences,
            SkillReferences: skillReferences,
            MemoryPolicyJson: null,
            BudgetTotalTokens: null,
            BudgetMaxRounds: profile.MaxRounds,
            BudgetMaxToolCalls: profile.MaxToolCallsTotal,
            Timeout: profile.MaxElapsedSeconds is > 0
                ? TimeSpan.FromSeconds(profile.MaxElapsedSeconds.Value)
                : null,
            CreatedAt: DateTimeOffset.UtcNow,
            CapabilityTags: capabilityTags,
            Protocol: protocol,
            VisionPolicy: visionPolicy,
            VisionHelperRoute: visionHelperRoute);

        // V5-T2：策略版本随快照创建日志可观测（版本 + 来源），便于区分「当前执行版本」。
        logger.LogInformation(
            "[SnapshotFactory] Created snapshot={SnapshotId} agent={AgentId} vision={Vision} protocol={Protocol} visionPolicy={VisionPolicyVersion} visionPolicySource={VisionPolicySource}",
            snapshot.SnapshotId,
            profile.AgentId,
            snapshot.SupportsVision ? 1 : 0,
            string.IsNullOrWhiteSpace(protocol) ? "unknown" : protocol,
            visionPolicy?.ImageTokenEstimatorVersion ?? "none",
            visionPolicySource);

        return snapshot;
    }

    /// <summary>
    /// V5-T2：解析快照视觉预算策略及其来源标签。模型类别安全（ADR-088 决策 2）：
    /// embedding / image-generation / 无 vision 标签一律 null（不投影策略，不误标）。
    /// 图像生成判定锚点与 PuddingFileConfigLoader 的 imageGeneration 节校验一致
    /// （capabilityTags 含 "image-generation"）；embedding 判定用既有 LlmModelInfo.IsEmbedding。
    /// </summary>
    private static (PuddingCode.Core.VisionRequestPolicy? Policy, string Source) ResolveVisionPolicy(
        PuddingCode.Abstractions.LlmModelInfo? modelInfo)
    {
        if (modelInfo is null)
            return (null, "model-not-found");

        var tags = modelInfo.CapabilityTags ?? [];
        var unsupported = modelInfo.IsEmbedding
            || tags.Contains("image-generation", StringComparer.OrdinalIgnoreCase)
            || !tags.Contains("vision", StringComparer.OrdinalIgnoreCase);
        if (unsupported)
            return (null, "model-unsupported");

        return modelInfo.VisionContract is { } contract
            ? (contract.ToPolicy(), "contract")
            : (PuddingCode.Core.VisionRequestPolicy.Default, "product-default");
    }

    public Task<AgentExecutionSnapshot?> FindByIdAsync(string snapshotId, CancellationToken ct)
    {
        logger.LogDebug("[SnapshotFactory] FindById {SnapshotId} — not yet persisted", snapshotId);
        return Task.FromResult<AgentExecutionSnapshot?>(null);
    }
}
