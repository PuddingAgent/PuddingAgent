using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PuddingCode.Platform;

namespace PuddingRuntime.Services.Improvement.Rsi;

/// <summary>
/// 把 <see cref="RsiTurnSlice"/>（按 turn 切开的事件行）装配成带结局标注的 <see cref="RsiTrajectory"/>
/// 的<b>纯函数</b>（规格 S3 §2.3 / §2.4 冻结规则，实现不得另发明）。
/// <para>
/// 装配规则（冻结）：
/// <list type="bullet">
/// <item>输入 null / 空 ⇒ 空列表，不抛异常；</item>
/// <item>只有 <c>tool.call.completed</c> 与 <c>tool.call.failed</c> 生成 step，其余事件一律忽略；</item>
/// <item>结局字段全部复用 <see cref="RsiToolOutcomeDeriver.Derive"/> 解析（不重写判定逻辑）；
///       但 <c>tool.call.failed</c> 语义上就是失败 ⇒ Outcome 强制 <see cref="RsiToolOutcome.Failed"/>；</item>
/// <item>ToolName 空/空白 ⇒ 常量 <see cref="UnknownToolName"/>（required string 不得留 null）；</item>
/// <item>steps 按 Sequence 升序排序（Sequence 是唯一合法排序键，不得依赖输入顺序）；</item>
/// <item>配对（B4 冻结，任务书 §4.4）：requested ↔ completed/failed 按<b>工具名</b> FIFO 消费（镜像既有管道，不新发明）；
///       消费前把该 turn 的事件按 Sequence 升序<b>稳定</b>排序（作用于局部副本，⛔ 不赌上游顺序）；
///       arguments 缺失/纯空白的 requested <b>照样占 FIFO 槽位</b>（跳过会让后继 completed 配到更早的调用 ⇒ 张冠李戴的哈希）；
///       无未配对 requested ⇒ ArgsHash = null（不得猜、不得用最近一条顶替）；</item>
/// <item>截断与指纹（B4 冻结，任务书 §4.1/§4.2）：Output/Error 只保留 ≤512 字符预览 + 截断前字符数/UTF-8 字节数
///       （设计 §4.6「工具输出全文 ⛔」）；ArgsHash = 配对 requested 的 arguments 原文 SHA-256（UTF-8）小写 hex 64 位，
///       ⛔ 不做任何归一化、⛔ 不得照抄 ?? "{}" 兜底（缺失 ⇒ null）；</item>
/// <item>HasOutcomeAnomaly = 任一步 Outcome != Completed；</item>
/// <item>⛔ 失败步不得被丢弃，⛔ 不得模仿 ADR-064「Any(Failed) ⇒ 整条作废」（反面教材）；</item>
/// <item>Steps 为空的 turn 不产出轨迹（零工具调用不携带信号）；</item>
/// <item>输出顺序与输入 slices 顺序一致（稳定）。</item>
/// </list>
/// </para>
/// </summary>
public static class RsiTrajectoryAssembler
{
    /// <summary>ToolName 缺失时的占位常量（规格冻结：不得留 null）。</summary>
    public const string UnknownToolName = "(unknown)";

    /// <summary>装配轨迹。任何输入都不抛异常；无有效工具步的 turn 被跳过。</summary>
    public static IReadOnlyList<RsiTrajectory> Assemble(IReadOnlyList<RsiTurnSlice>? slices)
    {
        if (slices is null || slices.Count == 0)
            return [];

        var trajectories = new List<RsiTrajectory>(slices.Count);
        foreach (var slice in slices)
        {
            if (slice is null)
                continue;   // 防御 required 契约的显式 null：不抛异常是硬约束

            var steps = BuildSteps(slice);
            if (steps.Count == 0)
                continue;   // 零工具调用的 turn 不携带信号（规格 §2.3 冻结）

            trajectories.Add(new RsiTrajectory
            {
                WorkspaceId = slice.WorkspaceId,
                AgentInstanceId = slice.AgentInstanceId,
                SessionId = slice.SessionId,
                TurnId = slice.TurnId,
                Steps = steps,
                HasOutcomeAnomaly = steps.Any(step => step.Outcome != RsiToolOutcome.Completed),
            });
        }

        return trajectories;
    }

    /// <summary>从一个 turn 的事件行生成按 Sequence 升序的 steps；只有 completed / failed 两类事件参与。</summary>
    private static List<RsiToolStep> BuildSteps(RsiTurnSlice slice)
    {
        var steps = new List<RsiToolStep>();
        if (slice.Events is null)
            return steps;

        // B4 冻结（任务书 §4.4）：配对前把事件按 Sequence 升序「稳定」排序（局部副本；LINQ OrderBy 是稳定排序），
        // ⛔ 不得依赖输入顺序 —— B3 的「组内已按 Sequence 升序」只是上游承诺，配对直接吃输入顺序会静默张冠李戴。
        var orderedEvents = slice.Events
            .Where(static row => row is not null)
            .OrderBy(static row => row.Sequence)
            .ToArray();

        // requested FIFO 桶：键 = 工具名（payload 的 name；缺失/空白归一为 UnknownToolName，与 step 的 ToolName 同一规则），
        // 每桶是未配对 requested 的队列，元素是入队时已算好的 ArgsHash —— null 也是合法槽位（缺失参数照样入队）。
        var pendingRequests = new Dictionary<string, Queue<string?>>(StringComparer.Ordinal);

        foreach (var row in orderedEvents)
        {
            switch (row.Type)
            {
                case ConversationEventTypes.ToolCallRequested:
                    EnqueueRequest(pendingRequests, row.Payload);
                    break;

                case ConversationEventTypes.ToolCallCompleted:
                    AddStep(steps, slice.TurnId, row, RsiToolOutcomeDeriver.Derive(row.Payload), forceFailed: false, pendingRequests);
                    break;

                case ConversationEventTypes.ToolCallFailed:
                    // failed 事件语义上就是失败：Derive 可能因 payload 无信号给出 Unknown，但结局强制 Failed。
                    AddStep(steps, slice.TurnId, row, RsiToolOutcomeDeriver.Derive(row.Payload), forceFailed: true, pendingRequests);
                    break;
            }
        }

        if (steps.Count > 1)
            steps.Sort(static (left, right) => left.Sequence.CompareTo(right.Sequence));

        return steps;
    }

    private static void AddStep(List<RsiToolStep> steps, string turnId, RsiEventRow row, RsiToolOutcomeResult derived, bool forceFailed, Dictionary<string, Queue<string?>> pendingRequests)
    {
        var toolName = derived.ToolName;
        var argsHash = TakeArgsHash(pendingRequests, toolName);
        steps.Add(new RsiToolStep
        {
            TurnId = turnId,
            ToolName = string.IsNullOrWhiteSpace(toolName) ? UnknownToolName : toolName,
            Sequence = row.Sequence,
            Outcome = forceFailed ? RsiToolOutcome.Failed : derived.Outcome,
            ExitCode = derived.ExitCode,
            ErrorPreview = PreviewText(derived.Error, out var errorChars, out _),
            ErrorChars = errorChars,
            OutputPreview = PreviewText(derived.Output, out var outputChars, out var outputBytes),
            OutputChars = outputChars,
            OutputBytes = outputBytes,
            ArgsHash = argsHash,
            OccurredAtUtc = row.OccurredAtUtc,
        });
    }

    // ── B4：截断预览 + 参数指纹（任务书 §4.1 / §4.2 / §4.4 冻结；纯静态、无副作用、无 IO，任何输入不抛异常）──

    /// <summary>预览最大总长（UTF-16 字符，含省略号）—— 规格 §2.13 B4 切片合同冻结 512。</summary>
    private const int MaxPreviewLength = 512;

    /// <summary>截断时保留的原文前缀字符数（512 − 省略号 1 字符 ⇒ 截断后总长恰为 512）。</summary>
    private const int PrefixCharsWhenTruncated = 511;

    /// <summary>截断标记 U+2026（1 个 UTF-16 字符）。</summary>
    private const string Ellipsis = "\u2026";

    /// <summary>
    /// 截断规则（任务书 §4.1 冻结）：原文缺失 ⇒ (null, 0, 0)；原文长度 ≤ 512 ⇒ 原样（不加省略号）；
    /// &gt; 512 ⇒ 前 511 字符 + <c>…</c>（总长 ≤ 512）。chars / bytes 一律按<b>截断前</b>原文计数
    /// （bytes 是 UTF-8 字节数，不是 UTF-16 长度）。⛔ 缺失不得压平成空串（「没有输出」与「输出为空」必须可区分）。
    /// </summary>
    private static string? PreviewText(string? raw, out int chars, out int bytes)
    {
        if (raw is null)
        {
            chars = 0;
            bytes = 0;
            return null;
        }

        chars = raw.Length;
        bytes = Encoding.UTF8.GetByteCount(raw);
        return raw.Length <= MaxPreviewLength ? raw : raw[..PrefixCharsWhenTruncated] + Ellipsis;
    }

    /// <summary>
    /// requested 事件入桶：解析 payload 的 name 与 arguments 原文，计算 ArgsHash 后进入该工具名的 FIFO 队列。
    /// arguments 缺失/纯空白 ⇒ null 槽位，<b>照样入队</b>（任务书 §4.4：跳过会让后继 completed 配到更早的调用）；
    /// payload 缺失/非法/非对象 ⇒ 视同无参数，按 UnknownToolName 占槽（与 completed 侧同一归一化，缺失不是跳过的理由）。
    /// </summary>
    private static void EnqueueRequest(Dictionary<string, Queue<string?>> pendingRequests, string? payloadJson)
    {
        string? toolName = null;
        string? argumentsRaw = null;

        if (!string.IsNullOrWhiteSpace(payloadJson))
        {
            try
            {
                using var document = JsonDocument.Parse(payloadJson);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    toolName = GetStringField(root, "name");
                    argumentsRaw = GetArgumentsRawText(root);
                }
            }
            catch (JsonException)
            {
                // payload 非法 ⇒ 视同无参数：仍按 UnknownToolName 占槽（任务书 §4.4 的占位纪律）。
            }
        }

        var bucket = string.IsNullOrWhiteSpace(toolName) ? UnknownToolName : toolName;
        if (!pendingRequests.TryGetValue(bucket, out var queue))
            pendingRequests[bucket] = queue = new Queue<string?>();
        queue.Enqueue(ComputeArgsHash(argumentsRaw));
    }

    /// <summary>取该工具名桶里<b>最早的</b>未配对 requested 的 ArgsHash（FIFO）；桶空/不存在 ⇒ null（不得猜、不得用最近一条顶替）。</summary>
    private static string? TakeArgsHash(Dictionary<string, Queue<string?>> pendingRequests, string? completedToolName)
    {
        var bucket = string.IsNullOrWhiteSpace(completedToolName) ? UnknownToolName : completedToolName;
        return pendingRequests.TryGetValue(bucket, out var queue) && queue.Count > 0
            ? queue.Dequeue()
            : null;
    }

    /// <summary>
    /// ArgsHash（任务书 §4.2 冻结）：SHA-256（arguments 原文的 UTF-8 字节）小写 hex，固定 64 位；
    /// ⛔ 不做任何归一化（不 trim、不解析 JSON、不排序键 —— 归一化属后续「无进展循环」切片，本片不夹带）；
    /// 缺失或纯空白 ⇒ null。⛔ 不得照抄 ConversationSkillEvolutionTrajectorySource.cs:115 的 ?? "{}" 兜底 ——
    /// 那会把「参数缺失」捏造成真实值，让两次缺失调用得到同一哈希 ⇒ 虚假「参数相同」信号且无断言会失败。
    /// </summary>
    private static string? ComputeArgsHash(string? argumentsRaw)
    {
        if (string.IsNullOrWhiteSpace(argumentsRaw))
            return null;

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(argumentsRaw));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    /// <summary>
    /// 取 requested payload 中 arguments 键的<b>原文</b>（规格 §2.13 实测形状：arguments 是 JSON 文本字符串）：
    /// 字符串值 ⇒ GetString（反序列化内容即参数原文文本，内部空格/键序原样保留）；JSON null ⇒ 缺失；
    /// 其他形态（对象/数组等，防御性）⇒ GetRawText 源文本切片。键缺失 ⇒ null（不得兜底 "{}"）。
    /// </summary>
    private static string? GetArgumentsRawText(JsonElement root)
    {
        if (!root.TryGetProperty("arguments", out var value) || value.ValueKind == JsonValueKind.Null)
            return null;

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
    }

    /// <summary>防御性字符串读取（与 RsiToolOutcomeDeriver.GetString 同一契约：非对象/键缺失/非字符串 ⇒ null，绝不抛异常）。</summary>
    private static string? GetStringField(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
