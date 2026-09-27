using System.Text.Json;

namespace PuddingCode.Platform;

/// <summary>
/// NC-01：人工审批暂停的持久恢复点。
/// <para>
/// 语义（见 <c>Docs/Features/Desktop-Native-Approval-Pause-Resume-Design-2026-09-27.md</c>）：
/// 一个 Turn 在工具批次中途遇到 <c>NeedHuman</c> 时，把足以「从同一 invocation 继续」的状态冻结下来，
/// 释放执行租约（不再占用 worker 名额），并且<b>不</b>写任何终态事实。
/// </para>
/// <para>
/// 本记录只承载 Core 必须能独立校验的部分：身份绑定、原操作快照、冻结预算与截止时间。
/// Runtime 的循环携带状态（会话历史、批次已完成结果、工具曝光、计数器等）放在
/// <see cref="RuntimeStateJson"/>，所有权属于 Runtime；Core 只强制它有版本、有上限、是 JSON 对象。
/// </para>
/// </summary>
public sealed record ApprovalResumePoint
{
    /// <summary>恢复点格式版本。反序列化精确匹配；未知版本一律拒绝恢复，不做向后兼容猜测。</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Runtime 私有状态的字符上限（4 MiB）。</summary>
    public const int MaxRuntimeStateCharacters = 4 * 1024 * 1024;

    /// <summary>Run / Turn / Command 在审批等待期的非终态状态值。</summary>
    public const string WaitingApprovalStatus = "waiting_approval";

    /// <summary>被唤醒后旧 run 行的状态值；它只表示「该次租约已被一次成功恢复取代」。</summary>
    public const string ResumedRunStatus = "resumed";

    /// <summary>
    /// 恢复点在 <c>chat_execution_commands</c> 上的列名。刻意独立成列而不是并入
    /// <c>metadata_json</c>：后者上限 4096，而恢复点携带 Runtime 会话历史与批次状态。
    /// </summary>
    public const string CommandColumnName = "approval_resume_json";

    // ── 身份绑定：恢复前必须与行内事实逐一复核 ──
    public required string WorkspaceId { get; init; }
    public required string AgentInstanceId { get; init; }
    public required string SessionId { get; init; }
    public required string RunId { get; init; }
    public required string TurnId { get; init; }
    public required string CommandId { get; init; }
    /// <summary>待决定工具调用的 canonical <c>tool_call_id</c>；决定与消费都按它精确绑定。</summary>
    public required string InvocationId { get; init; }
    /// <summary>权威审批记录 ID（事实本体在 PuddingApproval.Sqlite，本字段只用于再次校验）。</summary>
    public required string ApprovalId { get; init; }

    // ── 原操作快照：人看到什么就批准什么，不做同义 JSON 重排 ──
    public required string ToolId { get; init; }
    public required string ArgumentsJson { get; init; }
    public required string ToolDefinitionJson { get; init; }
    public required string ExecutionRoot { get; init; }
    public required string PolicyRevision { get; init; }

    // ── 冻结预算：恢复只能缩短，不得放宽 ──
    public required DateTimeOffset DeadlineUtc { get; init; }
    public required int MaxRounds { get; init; }
    public required int MaxToolCallsTotal { get; init; }
    public required int UsedToolCalls { get; init; }
    public required int Round { get; init; }
    /// <summary>当前工具批次内待决定工具的下标；续行从它开始，之前的工具不得重跑。</summary>
    public required int PendingToolIndex { get; init; }

    // ── Runtime 私有状态（历史/批次结果/曝光/计数） ──
    public required string RuntimeStateJson { get; init; }

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>冻结的 Agent 执行快照 ID；续行必须复用同一 profile，不得重新解析。</summary>
    public string? SnapshotId { get; init; }

    public DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>
    /// 形状与边界校验。不校验与数据库行的一致性——那是 park/acquire 的 CAS 职责。
    /// </summary>
    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
            throw new ArgumentException(
                $"Unsupported approval resume point schema version {SchemaVersion}.");
        if (new[]
            {
                WorkspaceId, AgentInstanceId, SessionId, RunId, TurnId, CommandId, InvocationId,
                ApprovalId, ToolId, ArgumentsJson, ToolDefinitionJson, ExecutionRoot,
                PolicyRevision, RuntimeStateJson,
            }.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "An approval resume point must bind a complete execution identity, operation snapshot, policy revision and runtime state.");
        }
        if (!Path.IsPathFullyQualified(ExecutionRoot))
            throw new ArgumentException("Approval resume requires a resolved absolute execution root.");
        if (MaxRounds <= 0 || MaxToolCallsTotal <= 0)
            throw new ArgumentException("Frozen budgets must be positive.");
        if (UsedToolCalls < 0 || UsedToolCalls > MaxToolCallsTotal)
            throw new ArgumentException("Used tool calls must be within the frozen budget.");
        if (Round < 0 || Round >= MaxRounds)
            throw new ArgumentException("The frozen round must be within the frozen budget.");
        if (PendingToolIndex < 0)
            throw new ArgumentException("The pending tool index must be non-negative.");
        if (DeadlineUtc == default)
            throw new ArgumentException("The frozen execution deadline is required.");
        if (RuntimeStateJson.Length > MaxRuntimeStateCharacters)
            throw new ArgumentException("Runtime state exceeds the bounded resume point size.");
        using var state = JsonDocument.Parse(RuntimeStateJson, new JsonDocumentOptions { MaxDepth = 128 });
        if (state.RootElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Runtime state must be a JSON object.");
    }

    /// <summary>
    /// 与租约逐字段比对。park 与 acquire 都调用它，使「决定绑定的是同一执行身份」不依赖调用方自觉。
    /// </summary>
    public void ValidateAgainstLease(ExecutionLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (!string.Equals(WorkspaceId, lease.WorkspaceId, StringComparison.Ordinal)
            || !string.Equals(SessionId, lease.ConversationId, StringComparison.Ordinal)
            || !string.Equals(RunId, lease.RunId, StringComparison.Ordinal)
            || !string.Equals(TurnId, lease.TurnId, StringComparison.Ordinal)
            || !string.Equals(CommandId, lease.CommandId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The approval resume point does not match the lease it is being parked under.");
        }
    }

    /// <summary>冻结预算尚未到期才允许恢复；已过截止时间必须走超时收口，不得续行。</summary>
    public bool CanResumeAt(DateTimeOffset now) => DeadlineUtc > now;

    /// <summary>固定字段顺序的规范化 JSON；park 落盘与 acquire 读回使用同一份实现。</summary>
    public string ToJson()
    {
        Validate();
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteString("workspaceId", WorkspaceId);
            writer.WriteString("agentInstanceId", AgentInstanceId);
            writer.WriteString("sessionId", SessionId);
            writer.WriteString("runId", RunId);
            writer.WriteString("turnId", TurnId);
            writer.WriteString("commandId", CommandId);
            writer.WriteString("invocationId", InvocationId);
            writer.WriteString("approvalId", ApprovalId);
            writer.WriteString("toolId", ToolId);
            writer.WriteString("argumentsJson", ArgumentsJson);
            writer.WriteString("toolDefinitionJson", ToolDefinitionJson);
            writer.WriteString("executionRoot", ExecutionRoot);
            writer.WriteString("policyRevision", PolicyRevision);
            writer.WriteString("deadlineUtc", DeadlineUtc.ToUniversalTime().ToString("O"));
            writer.WriteNumber("maxRounds", MaxRounds);
            writer.WriteNumber("maxToolCallsTotal", MaxToolCallsTotal);
            writer.WriteNumber("usedToolCalls", UsedToolCalls);
            writer.WriteNumber("round", Round);
            writer.WriteNumber("pendingToolIndex", PendingToolIndex);
            if (SnapshotId is null) writer.WriteNull("snapshotId");
            else writer.WriteString("snapshotId", SnapshotId);
            writer.WriteString("createdAtUtc", CreatedAtUtc.ToUniversalTime().ToString("O"));
            writer.WritePropertyName("runtimeState");
            using (var state = JsonDocument.Parse(RuntimeStateJson, new JsonDocumentOptions { MaxDepth = 128 }))
                state.RootElement.WriteTo(writer);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// 严格解析。任何缺字段、类型不符、版本不符或边界越界都返回 false 并给出原因，绝不部分还原。
    /// </summary>
    public static bool TryParse(string? json, out ApprovalResumePoint? point, out string? error)
    {
        point = null;
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "empty";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 128 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "not_an_object";
                return false;
            }

            var schemaVersion = root.TryGetProperty("schemaVersion", out var versionElement)
                                && versionElement.TryGetInt32(out var parsedVersion)
                ? parsedVersion
                : -1;
            if (schemaVersion != CurrentSchemaVersion)
            {
                error = $"unsupported_schema_version:{schemaVersion}";
                return false;
            }

            var candidate = new ApprovalResumePoint
            {
                SchemaVersion = schemaVersion,
                WorkspaceId = RequireString(root, "workspaceId"),
                AgentInstanceId = RequireString(root, "agentInstanceId"),
                SessionId = RequireString(root, "sessionId"),
                RunId = RequireString(root, "runId"),
                TurnId = RequireString(root, "turnId"),
                CommandId = RequireString(root, "commandId"),
                InvocationId = RequireString(root, "invocationId"),
                ApprovalId = RequireString(root, "approvalId"),
                ToolId = RequireString(root, "toolId"),
                ArgumentsJson = RequireString(root, "argumentsJson"),
                ToolDefinitionJson = RequireString(root, "toolDefinitionJson"),
                ExecutionRoot = RequireString(root, "executionRoot"),
                PolicyRevision = RequireString(root, "policyRevision"),
                DeadlineUtc = DateTimeOffset.Parse(
                    RequireString(root, "deadlineUtc"),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind),
                MaxRounds = RequireInt32(root, "maxRounds"),
                MaxToolCallsTotal = RequireInt32(root, "maxToolCallsTotal"),
                UsedToolCalls = RequireInt32(root, "usedToolCalls"),
                Round = RequireInt32(root, "round"),
                PendingToolIndex = RequireInt32(root, "pendingToolIndex"),
                SnapshotId = root.TryGetProperty("snapshotId", out var snapshotElement)
                             && snapshotElement.ValueKind == JsonValueKind.String
                    ? snapshotElement.GetString()
                    : null,
                CreatedAtUtc = DateTimeOffset.Parse(
                    RequireString(root, "createdAtUtc"),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind),
                RuntimeStateJson = RequireProperty(root, "runtimeState").GetRawText(),
            };

            candidate.Validate();
            point = candidate;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException
                                      or InvalidOperationException or OverflowException)
        {
            error = ex.Message;
            point = null;
            return false;
        }
    }

    private static string RequireString(JsonElement root, string name)
    {
        var element = RequireProperty(root, name);
        if (element.ValueKind != JsonValueKind.String)
            throw new ArgumentException($"Resume point field '{name}' must be a string.");
        var value = element.GetString();
        return string.IsNullOrEmpty(value)
            ? throw new ArgumentException($"Resume point field '{name}' must not be empty.")
            : value;
    }

    private static int RequireInt32(JsonElement root, string name)
    {
        var element = RequireProperty(root, name);
        return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value)
            ? value
            : throw new ArgumentException($"Resume point field '{name}' must be an Int32.");
    }

    private static JsonElement RequireProperty(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element)
            ? element
            : throw new ArgumentException($"Resume point field '{name}' is missing.");
}

/// <summary>
/// NC-01：park 结果。LastSequence 为本轮 pending 输出的末序列（无输出时为 0）。
/// 该结果不表示任何终态事实成立。
/// </summary>
public sealed record ExecutionApprovalParkResult(long LastSequence, int EventCount);

/// <summary>
/// NC-01：一次成功的审批恢复领取。Lease 是新的租约（新 worker、新 fencing token）；
/// <see cref="ResumePoint"/> 是 park 时冻结的同一份事实。
/// <para>
/// 拿到租约<b>不等于</b>已获得执行许可：调用方仍须在派发前复核审批状态与操作快照，
/// 并按一次性消费语义消费许可（见审批接入设计 §3）。
/// </para>
/// </summary>
public sealed record ApprovalResumeAcquireResult(
    ExecutionLease Lease,
    ApprovalResumePoint ResumePoint);
