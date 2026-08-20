using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// D1 修复（选型 A「只增不减」）：心跳工具暴露集只增不减。
///
/// 覆盖任务书 §4 三条测试要求：
/// 1) 单测：committed 暴露 ⊇ 白名单的 session 心跳 turn 不裁剪；全新 session 回退白名单；
/// 2) 回归：心跳 turn（工具集与普通 turn 一致）→ CompositionSnapshots 零新增行 + epoch 零递增；
/// 3) 既有全绿（build 0 错误 + Composition 相关测试）由测试套件整体承担。
///
/// 核心被测逻辑：<see cref="ToolProfileConfig.ShouldApplyHeartbeatToolFilter"/>
/// 与 <see cref="AgentExecutionService.ApplyToolProfile"/> 的心跳分支（本测试用等价模拟
/// 调用路径，链路依赖 AgentSessionManager loaded 集 + CompositionRecoveryService 水合）。
/// </summary>
[TestClass]
public sealed class ToolProfileHeartbeatExposureTests
{
    // D1 根因证据：30 全量工具 ∩ 心跳白名单 = 24，缺这 6 个白名单外工具。
    private static readonly string[] NonWhitelistedToolIds =
    [
        "code_index_list_projects",
        "git_branch_list",
        "image_reader",
        "list_tool_approvals",
        "task_list",
        "terminal_execute",
    ];

    // 24 个白名单内工具（代表普通 turn 全量集 ∩ 白名单）。
    private static readonly string[] WhitelistedToolIds =
    [
        "search_tools", "goal_read", "goal_update", "sleep", "receive_messages",
        "send_message", "agent_diagnostics", "agent_status", "search_memory", "save_memory",
        "query_session_logs", "list_agents", "query_sub_agents", "spawn_sub_agent",
        "file_read", "list_dir", "file_search", "search_grep", "code_outline", "code_summary",
        "project_map", "file_write", "file_patch", "shell",
    ];

    // 30 工具全量（普通 turn 暴露集）= 24 白名单内 + 6 白名单外。
    private static readonly string[] AllToolIds =
        [.. WhitelistedToolIds, .. NonWhitelistedToolIds];

    // ── §4.1 单测：已暴露集存在 → 不裁剪 ──────────────────

    [TestMethod]
    public void ShouldApplyHeartbeatToolFilter_ExistingExposure_ReturnsFalse_NoShrink()
    {
        // committed 暴露集 ⊇ 白名单（含白名单外工具）→ 不裁剪。
        var exposedFull = new HashSet<string>(AllToolIds, StringComparer.OrdinalIgnoreCase);
        Assert.IsFalse(
            ToolProfileConfig.ShouldApplyHeartbeatToolFilter(exposedFull),
            "已有已暴露集（含白名单外工具）→ heartbeat 不得应用白名单裁剪");

        // 已暴露集仅含白名单内工具 → 同样不裁剪（只增不减：不删任何已暴露工具）。
        var exposedWhitelistOnly = new HashSet<string>(WhitelistedToolIds, StringComparer.OrdinalIgnoreCase);
        Assert.IsFalse(
            ToolProfileConfig.ShouldApplyHeartbeatToolFilter(exposedWhitelistOnly),
            "已暴露集非空 → heartbeat 不裁剪（只增不减）");
    }

    // ── §4.1 单测：全新 session → 回退白名单 ─────────────────

    [TestMethod]
    public void ShouldApplyHeartbeatToolFilter_EmptyOrNullExposure_ReturnsTrue_FallbackToWhitelist()
    {
        // 全新 session 无已暴露集 → 回退白名单过滤（保留省 token 意图）。
        Assert.IsTrue(ToolProfileConfig.ShouldApplyHeartbeatToolFilter(null));
        Assert.IsTrue(
            ToolProfileConfig.ShouldApplyHeartbeatToolFilter(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void HeartbeatWhitelist_FiltersFreshSession_To24Tools_KeepingTokenSavingIntent()
    {
        // 全新 session 回退白名单：30 全量 ∩ 白名单 = 24（缺 6 个白名单外工具）。
        var freshVisible = AllToolIds
            .Where(t => ToolProfileConfig.ShouldInclude(ToolProfileConfig.HeartbeatProfileName, t))
            .ToList();

        Assert.AreEqual(24, freshVisible.Count, "全新 session 心跳白名单应裁剪为 24 个工具");
        foreach (var tool in NonWhitelistedToolIds)
        {
            Assert.IsFalse(
                ToolProfileConfig.ShouldInclude(ToolProfileConfig.HeartbeatProfileName, tool),
                $"{tool} 不应在心跳白名单内");
        }
    }

    // ── §4.2 回归：心跳 turn → Composition 零新增行 + epoch 零递增 ──

    [TestMethod]
    public async Task HeartbeatTurn_WithCommittedExposure_KeepsFullSet_CompositionReusesVersion_NoEpochIncrement()
    {
        var manager = new AgentSessionManager();
        manager.GetOrCreate("s-heartbeat", "global:general-assistant");
        var store = new InMemoryCompositionStore();
        var registry = new PersistentCompositionVersionRegistry(store);

        var normalTools = AllToolIds.Select((name, i) => Tool(name, $"desc-{i}")).ToList();
        var sysHash = CompositionSnapshot.Sha256Hex("normal-turn-system-prompt");
        var toolHashFull = CompositionSnapshot.ComputeToolSpecHash(normalTools);
        var fingerprintFull = CompositionSnapshot.ComputePermissionFingerprint(
            normalTools.Select(t => t.Name));

        // ── 普通 turn：暴露全量 30 工具 → 版本 v1 + 写穿 1 条 ──
        var normalObservation = registry.Observe(
            "s-heartbeat",
            sysHash,
            toolHashFull,
            toolIds: normalTools.Select(t => t.Name).ToList(),
            permissionEpoch: 5,
            permissionFingerprint: fingerprintFull);
        Assert.AreEqual(1, normalObservation.Version);
        await Task.Delay(200); // 等待异步写穿
        Assert.AreEqual(1, store.AppendCount, "普通 turn 应写穿 1 条 Composition 记录");

        // ── 心跳 turn 开跑：RecoverAsync 水合 committed toolset → loaded 非空 ──
        var recovery = new CompositionRecoveryService(manager, store, persistentRegistry: registry);
        await recovery.RecoverAsync("s-heartbeat");

        // ── 修复判定：已暴露集非空 → 心跳不裁剪 → 暴露集 = 普通 turn 全量 30 ──
        var exposed = manager.GetLoadedToolIds("s-heartbeat");
        Assert.IsFalse(
            ToolProfileConfig.ShouldApplyHeartbeatToolFilter(exposed),
            "已有已暴露集 → 心跳 turn 不应用白名单裁剪");
        var heartbeatTools = ApplyHeartbeatProfile(normalTools, exposed);
        Assert.AreEqual(normalTools.Count, heartbeatTools.Count, "修后心跳暴露集 ≡ 普通 turn 全量集");

        // ── 心跳 turn 暴露集与普通 turn 一致 → 复用版本：零新增行 + epoch 零递增 ──
        var heartbeatObservation = registry.Observe(
            "s-heartbeat",
            sysHash,
            toolHashFull,
            toolIds: heartbeatTools.Select(t => t.Name).ToList(),
            permissionEpoch: 5,
            permissionFingerprint: fingerprintFull);
        Assert.AreEqual(1, heartbeatObservation.Version, "心跳 turn 不得产生新 Composition 版本");
        Assert.AreEqual("none", heartbeatObservation.ChangeReason, "心跳 turn 不得上报 tool_spec_changed");
        Assert.AreEqual(5, heartbeatObservation.PermissionEpoch, "心跳 turn 不得递增 permission epoch");
        await Task.Delay(200);
        Assert.AreEqual(1, store.AppendCount, "心跳 turn 不得新增 CompositionSnapshots 行");
    }

    [TestMethod]
    public async Task HeartbeatTurn_ShrunkToolSet_CreatesNewVersion_AndEpochIncrement_ControlProbe()
    {
        // 对照探针：证明上一测试能检测抖动——修复前行为（裁剪为 24）必然产生
        // 新版本 + permission_changed + epoch 递增（D1 根因观测噪声）。
        var store = new InMemoryCompositionStore();
        var registry = new PersistentCompositionVersionRegistry(store);

        var normalTools = AllToolIds.Select((name, i) => Tool(name, $"desc-{i}")).ToList();
        var shrunkTools = normalTools
            .Where(t => ToolProfileConfig.ShouldInclude(ToolProfileConfig.HeartbeatProfileName, t.Name))
            .ToList();
        var sysHash = CompositionSnapshot.Sha256Hex("normal-turn-system-prompt");
        var toolHashFull = CompositionSnapshot.ComputeToolSpecHash(normalTools);
        var toolHashShrunk = CompositionSnapshot.ComputeToolSpecHash(shrunkTools);
        var fingerprintFull = CompositionSnapshot.ComputePermissionFingerprint(normalTools.Select(t => t.Name));
        var fingerprintShrunk = CompositionSnapshot.ComputePermissionFingerprint(shrunkTools.Select(t => t.Name));

        registry.Observe(
            "s-control",
            sysHash,
            toolHashFull,
            toolIds: normalTools.Select(t => t.Name).ToList(),
            permissionEpoch: 5,
            permissionFingerprint: fingerprintFull);

        var shrunk = registry.Observe(
            "s-control",
            sysHash,
            toolHashShrunk,
            toolIds: shrunkTools.Select(t => t.Name).ToList(),
            permissionEpoch: 5,
            permissionFingerprint: fingerprintShrunk);

        Assert.AreEqual(2, shrunk.Version, "裁剪（修复前行为）应产生新 Composition 版本");
        StringAssert.Contains(shrunk.ChangeReason, "tool_spec_changed");
        StringAssert.Contains(shrunk.ChangeReason, "permission_changed");
        Assert.AreEqual(6, shrunk.PermissionEpoch, "裁剪（修复前行为）epoch 应递增（虚增噪声）");
    }

    // ── 辅助 ───────────────────────────────────────────

    /// <summary>模拟 ApplyToolProfile 心跳分支的修复后语义（只增不减）。</summary>
    private static IReadOnlyList<LlmToolDefinition> ApplyHeartbeatProfile(
        IReadOnlyList<LlmToolDefinition> tools,
        IReadOnlySet<string> exposedToolIds)
    {
        // 与 AgentExecutionService.ApplyToolProfile 心跳分支完全同构：
        // 已暴露集非空 → 不裁剪；空 → 回退白名单。
        if (!ToolProfileConfig.ShouldApplyHeartbeatToolFilter(exposedToolIds))
            return tools.ToList();

        return tools
            .Where(t => ToolProfileConfig.ShouldInclude(ToolProfileConfig.HeartbeatProfileName, t.Name))
            .ToList();
    }

    private static LlmToolDefinition Tool(string name, string description)
        => new()
        {
            Name = name,
            Description = description,
            Parameters = new ToolParameterSchema(Array.Empty<ToolParameter>(), Array.Empty<string>()),
        };

    private sealed class InMemoryCompositionStore : ICompositionStore
    {
        private readonly List<SessionCompositionRecord> _records = new();

        public int AppendCount { get; private set; }

        public Task<SessionCompositionRecord?> GetLatestAsync(string sessionId, CancellationToken ct = default)
            => Task.FromResult(_records.Count == 0 ? null : _records[^1]);

        public Task<bool> AppendAsync(SessionCompositionRecord record, CancellationToken ct = default)
        {
            AppendCount++;
            _records.Add(record);
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<SessionCompositionRecord>> LoadAsync(string sessionId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SessionCompositionRecord>>(_records.ToArray());
    }
}
