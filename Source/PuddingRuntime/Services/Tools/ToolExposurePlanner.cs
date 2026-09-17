using System.Text.Json;
using PuddingCode.Platform;
using PuddingCode.Runtime;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// Provider-independent tool exposure planning. It only changes which standard top-level
/// function definitions are sent on a round; provider-specific message-level tool declarations
/// deliberately do not belong here.
///
/// C01-B（行为层纵切）：
/// - 曝光集合排序策略为 <see cref="OrderingStrategyStableAppend"/>（稳定追加序）：以「既有已提交顺序」为基，
///   新增工具只追加到末尾，**不再对全量集合重新字母排序**（legacy 行为）；
/// - <see cref="ToolExposurePlan.ExposureRevision"/> 只在**曝光集合**变化时推进，与权限纪元
///   （<c>PermissionEpoch</c>，见 <see cref="SessionCompositionRecord.PermissionEpoch"/>）互相独立：
///   工具按需发现不得被记为权限变化；
/// - 排序策略相对 legacy（全量字母序）真正产生偏差时，作为**一次性显式 epoch** 上报
///   <see cref="CompositionChangeReasons.OrderingStrategyChanged"/>，禁止静默改序（R4）；
/// - 既有曝光引用了当前 catalog 已不存在的工具定义时，不谎称精确恢复
///   （<see cref="CompositionChangeReasons.ToolDefinitionMissing"/> + <c>ExactRestore = false</c>），
///   但不阻塞执行（R7）。
/// </summary>
internal static class ToolExposurePlanner
{
    internal const string SearchToolId = "search_tools";
    internal const int DeferredLoadingThreshold = 24;

    /// <summary>曝光排序策略标识：稳定追加序（既有项相对顺序不变，新增项只追加到末尾）。</summary>
    internal const string OrderingStrategyStableAppend = "stable_append";

    /// <summary>核心工具集（永不从可见集收缩，L1 TOOLS 索引与 CreatePlan 共用）。</summary>
    internal static readonly HashSet<string> CoreToolIds = new(StringComparer.OrdinalIgnoreCase)
    {
        SearchToolId,
        "goal_read",
        "goal_update",
        "send_message",
        "receive_messages",
        "agent_status",
        "agent_diagnostics",
        "spawn_sub_agent",
        "query_sub_agents",
        "list_llm_providers",
        "sleep",
    };

    /// <summary>
    /// 计算一次曝光计划。纯函数：不持有会话状态，曝光纪元由调用方以
    /// <paramref name="epoch"/>（已提交基线）传入并在轮边界提交。
    /// </summary>
    /// <param name="availableTools">dispatch 冻结的可用工具定义全量（catalog）。</param>
    /// <param name="loadedToolIds">进程内渐进发现的已加载工具集合。</param>
    /// <param name="committedToolIds">dispatch 开始时的已提交（append-only）工具集合快照。</param>
    /// <param name="activationThreshold">延迟加载激活阈值。</param>
    /// <param name="previousVisibleToolIds">
    /// 上一次已提交的可见工具顺序（稳定追加序的基线）。为空表示「本次为会话首个计划」，
    /// 按 canonical（字母序）确定顺序，且不构成任何顺序变更事件。
    /// </param>
    /// <param name="epoch">调用方持有的已提交曝光纪元基线。</param>
    internal static ToolExposurePlan CreatePlan(
        IReadOnlyList<LlmToolDefinition> availableTools,
        IReadOnlySet<string>? loadedToolIds = null,
        IReadOnlySet<string>? committedToolIds = null,
        int activationThreshold = DeferredLoadingThreshold,
        IReadOnlyList<string>? previousVisibleToolIds = null,
        ToolExposureEpoch epoch = default)
    {
        ArgumentNullException.ThrowIfNull(availableTools);

        // canonical 顺序（legacy 全量字母序）：仅作为新会话的初始顺序与追加项的确定性次序，
        // 不再用于对既有集合重排（稳定追加序）。
        var canonicalTools = availableTools
            .OrderBy(tool => tool.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var availableIds = canonicalTools
            .Select(tool => tool.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        IReadOnlySet<string> loaded = loadedToolIds
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlySet<string> committed = committedToolIds
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // A discovered capability is exposed as one bounded bundle at the round boundary.
        // Only the already-authorized catalog may supply definitions. This does not grant
        // permission or execute a tool. In particular, read discovery never activates writes.
        var activated = new HashSet<string>(loaded, StringComparer.OrdinalIgnoreCase);
        activated.UnionWith(committed);
        if (previousVisibleToolIds is not null)
            activated.UnionWith(previousVisibleToolIds);
        activated.IntersectWith(availableIds);
        ExpandCapabilityBundles(activated, availableIds);

        List<LlmToolDefinition> visible;
        bool deferredLoadingEnabled;
        if (canonicalTools.Count <= Math.Max(1, activationThreshold)
            || canonicalTools.All(tool => !tool.Name.Equals(SearchToolId, StringComparison.OrdinalIgnoreCase)))
        {
            visible = canonicalTools;
            deferredLoadingEnabled = false;
        }
        else
        {
            // 不收缩：已授权集 = loaded ∪ committed（committedToolIds 是 session 已提交的不可变工具集合，
            // 跨会话清理/重启水合后保持），避免任一来源缩回时可见集收缩导致 provider prefix 漂移。
            var filtered = canonicalTools
                .Where(tool => CoreToolIds.Contains(tool.Name)
                    || activated.Contains(tool.Name))
                .ToList();

            // search_tools is the recovery path. If it disappears because of a registration or
            // capability mismatch, fail open to the already-authorized full set instead of making
            // deferred tools permanently unreachable.
            if (filtered.All(tool => !tool.Name.Equals(SearchToolId, StringComparison.OrdinalIgnoreCase)))
            {
                visible = canonicalTools;
                deferredLoadingEnabled = false;
            }
            else
            {
                visible = filtered;
                deferredLoadingEnabled = true;
            }
        }

        return BuildPlan(
            canonicalTools.Count,
            visible,
            deferredLoadingEnabled,
            previousVisibleToolIds,
            epoch,
            availableIds);
    }

    private static void ExpandCapabilityBundles(HashSet<string> activated, IReadOnlySet<string> available)
    {
        // Evaluate triggers before adding dependencies: adding read helpers must never
        // cascade into editing, code exploration, shell access, or Git mutations.
        var edit = activated.Overlaps(["file_write", "file_patch"]);
        var code = activated.Overlaps(["code_explore", "code_symbol_search"]);
        var read = edit || code || activated.Overlaps(["file_read", "file_search", "search_grep"]);
        var terminal = activated.Overlaps([
            "shell", "terminal_start", "terminal_read", "terminal_wait", "terminal_input", "terminal_cancel"]);
        var gitRead = activated.Overlaps(["git_status", "git_diff", "git_log"]);

        if (read)
            AddAvailable("file_read", "file_search", "search_grep");
        if (edit)
            AddAvailable("file_write", "file_patch");
        if (code)
            AddAvailable("code_explore", "code_symbol_search");
        if (terminal)
            AddAvailable("shell", "terminal_start", "terminal_read", "terminal_wait", "terminal_input", "terminal_cancel");
        if (gitRead)
            AddAvailable("git_status", "git_diff", "git_log");

        void AddAvailable(params string[] ids)
        {
            foreach (var id in ids)
                if (available.Contains(id))
                    activated.Add(id);
        }
    }

    /// <summary>
    /// 生成计划：稳定追加序 + 曝光纪元 / 原因计算（单一实现，避免多路径漂移）。
    /// </summary>
    private static ToolExposurePlan BuildPlan(
        int availableToolCount,
        IReadOnlyList<LlmToolDefinition> canonicalVisible,
        bool deferredLoadingEnabled,
        IReadOnlyList<string>? previousVisibleToolIds,
        ToolExposureEpoch epoch,
        IReadOnlySet<string> availableIds)
    {
        var ordered = OrderStableAppend(canonicalVisible, previousVisibleToolIds);
        var orderedNames = ordered.Select(tool => tool.Name).ToArray();
        var canonicalNames = canonicalVisible.Select(tool => tool.Name).ToArray();

        var hasPrevious = previousVisibleToolIds is { Count: > 0 };

        // 曝光集合是否变化（按名字集合比较，与顺序无关）。
        var setChanged = hasPrevious && !SetEqualsIgnoringCase(previousVisibleToolIds!, orderedNames);

        // 排序相对 legacy（全量字母序）是否已产生偏差：这是「排序策略切换」的可观测判据。
        var orderDeviatesFromLegacy = !orderedNames
            .SequenceEqual(canonicalNames, StringComparer.OrdinalIgnoreCase);

        // R4：策略切换只允许作为一次显式 epoch 上报（已声明过则不再重复上报）。
        var orderingEpoch = hasPrevious && orderDeviatesFromLegacy && !epoch.OrderingStrategyDeclared;

        // 既有曝光引用了当前 catalog 已不存在的定义：不谎称精确恢复，但不阻塞执行（R7）。
        var missingToolIds = hasPrevious
            ? previousVisibleToolIds!
                .Where(id => !string.IsNullOrWhiteSpace(id) && !availableIds.Contains(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : Array.Empty<string>();

        var changeReason = BuildChangeReason(orderingEpoch, setChanged, missingToolIds.Length > 0);

        return new ToolExposurePlan(
            ordered,
            availableToolCount,
            deferredLoadingEnabled,
            ExposureRevision: epoch.ExposureRevision + (setChanged ? 1L : 0L),
            OrderingStrategy: OrderingStrategyStableAppend,
            OrderingStrategyDeclared: epoch.OrderingStrategyDeclared || orderingEpoch,
            OrderingStrategyChanged: orderingEpoch,
            ExposureSetChanged: setChanged,
            ChangeReason: changeReason,
            ExactRestore: missingToolIds.Length == 0,
            MissingToolIds: missingToolIds);
    }

    /// <summary>
    /// 稳定追加序：以 <paramref name="previousVisibleToolIds"/> 的相对顺序为基（仍存在的项保持原相对顺序），
    /// 其余（新增项 / 首次出现的项）按 canonical 字母序追加到末尾。绝不重排既有项。
    /// </summary>
    private static List<LlmToolDefinition> OrderStableAppend(
        IReadOnlyList<LlmToolDefinition> canonicalVisible,
        IReadOnlyList<string>? previousVisibleToolIds)
    {
        if (previousVisibleToolIds is not { Count: > 0 })
            return canonicalVisible.ToList();

        var byName = new Dictionary<string, LlmToolDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in canonicalVisible)
            byName[tool.Name] = tool;

        var ordered = new List<LlmToolDefinition>(canonicalVisible.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in previousVisibleToolIds)
        {
            if (!string.IsNullOrWhiteSpace(id)
                && byName.TryGetValue(id, out var tool)
                && seen.Add(tool.Name))
            {
                ordered.Add(tool);
            }
        }

        // 追加项保持 canonical 次序（确定性）。
        foreach (var tool in canonicalVisible)
        {
            if (seen.Add(tool.Name))
                ordered.Add(tool);
        }

        return ordered;
    }

    private static bool SetEqualsIgnoringCase(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        var leftSet = left
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rightSet = right
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return leftSet.SetEquals(rightSet);
    }

    private static string BuildChangeReason(bool orderingEpoch, bool setChanged, bool definitionMissing)
    {
        var reasons = new List<string>(3);
        if (orderingEpoch)
            reasons.Add(CompositionChangeReasons.OrderingStrategyChanged);
        if (setChanged)
            reasons.Add(CompositionChangeReasons.ExposureChanged);
        if (definitionMissing)
            reasons.Add(CompositionChangeReasons.ToolDefinitionMissing);
        return reasons.Count == 0 ? CompositionChangeReasons.None : string.Join(',', reasons);
    }

    internal static int RegisterSearchResult(
        string toolName,
        bool success,
        string? output,
        ISet<string> loadedToolIds,
        IReadOnlyList<LlmToolDefinition> availableTools)
    {
        ArgumentNullException.ThrowIfNull(loadedToolIds);
        ArgumentNullException.ThrowIfNull(availableTools);

        if (!success
            || !toolName.Equals(SearchToolId, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(output))
        {
            return 0;
        }

        try
        {
            using var document = JsonDocument.Parse(output);
            if (!document.RootElement.TryGetProperty("loaded_tool_ids", out var ids)
                || ids.ValueKind != JsonValueKind.Array)
            {
                return 0;
            }

            var availableIds = availableTools
                .Select(tool => tool.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var added = 0;
            foreach (var item in ids.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                    continue;

                var toolId = item.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(toolId)
                    && availableIds.Contains(toolId)
                    && !toolId.Equals(SearchToolId, StringComparison.OrdinalIgnoreCase)
                    && loadedToolIds.Add(toolId))
                {
                    added++;
                }
            }

            return added;
        }
        catch (JsonException)
        {
            return 0;
        }
    }
}

/// <summary>
/// 调用方持有的已提交曝光纪元基线（C01-B AC4/AC6）：
/// <see cref="ExposureRevision"/> 为已提交的曝光 revision（只在曝光集合变化时 +1），
/// <see cref="OrderingStrategyDeclared"/> 表示稳定追加序 epoch 是否已显式声明过
/// （只允许声明一次，避免每轮重复上报 <see cref="CompositionChangeReasons.OrderingStrategyChanged"/>）。
/// </summary>
internal readonly record struct ToolExposureEpoch(
    long ExposureRevision = 0,
    bool OrderingStrategyDeclared = false);

/// <summary>
/// 一次曝光计划及其已提交事实（C01-B AC4/AC6）。
/// </summary>
/// <param name="VisibleTools">已提交的下一次 LLM invoke 可见定义（稳定追加序）。</param>
/// <param name="AvailableToolCount">dispatch 冻结的可用定义总数。</param>
/// <param name="DeferredLoadingEnabled">是否启用延迟加载（search_tools 恢复路径）。</param>
/// <param name="ExposureRevision">本次计划生效的曝光 revision（集合变化才推进）。</param>
/// <param name="OrderingStrategy">排序策略标识（当前恒为 <see cref="ToolExposurePlanner.OrderingStrategyStableAppend"/>）。</param>
/// <param name="OrderingStrategyDeclared">稳定追加序 epoch 是否已声明。</param>
/// <param name="OrderingStrategyChanged">本次是否为**一次性**排序策略切换 epoch。</param>
/// <param name="ExposureSetChanged">曝光集合是否变化。</param>
/// <param name="ChangeReason">本次曝光变化原因（none / tool_exposure_changed / ordering_strategy_changed / tool_definition_missing 的组合）。</param>
/// <param name="ExactRestore">既有曝光是否可被精确重建（false = 存在已缺失的工具定义）。</param>
/// <param name="MissingToolIds">已缺失的工具定义 ID（不谎称精确恢复，但不阻塞执行）。</param>
internal sealed record ToolExposurePlan(
    IReadOnlyList<LlmToolDefinition> VisibleTools,
    int AvailableToolCount,
    bool DeferredLoadingEnabled,
    long ExposureRevision = 0,
    string OrderingStrategy = ToolExposurePlanner.OrderingStrategyStableAppend,
    bool OrderingStrategyDeclared = false,
    bool OrderingStrategyChanged = false,
    bool ExposureSetChanged = false,
    string ChangeReason = CompositionChangeReasons.None,
    bool ExactRestore = true,
    IReadOnlyList<string>? MissingToolIds = null)
{
    internal int DeferredToolCount => Math.Max(0, AvailableToolCount - VisibleTools.Count);
}
