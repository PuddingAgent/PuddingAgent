using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingRuntime.Models;

namespace PuddingRuntime.Services.GoalMode;

/// <summary>
/// Goal 模式默认实现。
/// 队列持久化于 {AgentInstanceRoot(agentId)}/goal_queue.json（重启可恢复）。
/// 消费式语义：成功注入即游标前进；单目标注入次数达到 MaxInjectionsPerGoal 自动跳过（熔断）。
/// </summary>
public sealed class GoalModeService : IGoalModeService
{
    private const string QueueFileName = "goal_queue.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly GoalModeOptions _options;
    private readonly PuddingDataPaths _paths;
    private readonly IMessageSystem _messageSystem;
    private readonly ILogger<GoalModeService> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _agentGates = new();

    public GoalModeService(
        IOptions<GoalModeOptions> options,
        PuddingDataPaths paths,
        IMessageSystem messageSystem,
        ILogger<GoalModeService> logger)
    {
        _options = options.Value;
        _paths = paths;
        _messageSystem = messageSystem;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> TryInjectNextGoalAsync(string workspaceId, string agentId, CancellationToken ct)
    {
        if (!_options.Enabled)
            return false;

        var gate = _agentGates.GetOrAdd(agentId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var queuePath = QueueFilePath(agentId);
            var state = await LoadStateAsync(queuePath, agentId, ct);
            if (state is null || state.Goals.Count == 0)
                return false;

            // 熔断：跳过已达注入上限的目标，防止单个目标无限循环
            while (state.Cursor < state.Goals.Count
                && state.Goals[state.Cursor].InjectionCount >= _options.MaxInjectionsPerGoal)
            {
                var skipped = state.Goals[state.Cursor];
                skipped.Status = "skipped";
                state.Cursor++;
                _logger.LogWarning(
                    "[GoalMode] Goal skipped after {MaxInjections} injections agent={AgentId} goal={GoalTitle}",
                    _options.MaxInjectionsPerGoal,
                    agentId,
                    skipped.Title);
            }

            if (state.Cursor >= state.Goals.Count)
            {
                state.UpdatedAt = DateTime.UtcNow;
                await PersistStateAsync(queuePath, state, ct);
                _logger.LogInformation(
                    "[GoalMode] Queue drained agent={AgentId} goals={GoalCount}",
                    agentId,
                    state.Goals.Count);
                return false;
            }

            var goal = state.Goals[state.Cursor];
            var goalIndex = state.Cursor;

            var envelope = BuildEnvelope(workspaceId, agentId, goalIndex, state.Goals.Count, goal);
            var sendResult = await _messageSystem.SendAsync(envelope, ct);

            // 发送成功后才推进游标与计数（消费式语义）
            goal.InjectionCount++;
            goal.Status = "injected";
            state.Cursor++;
            state.UpdatedAt = DateTime.UtcNow;
            await PersistStateAsync(queuePath, state, ct);

            _logger.LogInformation(
                "[GoalMode] Injected goal {GoalIndex}/{GoalCount} agent={AgentId} goal={GoalTitle} injections={InjectionCount} messageId={MessageId}",
                goalIndex + 1,
                state.Goals.Count,
                agentId,
                goal.Title,
                goal.InjectionCount,
                sendResult.MessageId);
            return true;
        }
        catch (Exception ex)
        {
            // 注入失败不得影响主投递路径
            _logger.LogWarning(
                ex,
                "[GoalMode] Goal injection failed agent={AgentId}",
                agentId);
            return false;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>追加目标到队列（供工具/管理端调用）。队列满时返回 false。</summary>
    public async Task<bool> EnqueueGoalAsync(string agentId, string title, string? detail, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title))
            return false;

        var gate = _agentGates.GetOrAdd(agentId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var queuePath = QueueFilePath(agentId);
            var state = await LoadStateAsync(queuePath, agentId, ct) ?? new GoalQueueState { AgentId = agentId };
            if (state.Goals.Count >= _options.MaxQueueLength)
            {
                _logger.LogWarning(
                    "[GoalMode] Queue full agent={AgentId} max={MaxLength}",
                    agentId,
                    _options.MaxQueueLength);
                return false;
            }

            state.AgentId = agentId;
            state.Goals.Add(new GoalEntry
            {
                Title = title.Trim(),
                Detail = detail,
                CreatedAt = DateTime.UtcNow,
            });
            state.UpdatedAt = DateTime.UtcNow;
            await PersistStateAsync(queuePath, state, ct);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    private string QueueFilePath(string agentId) =>
        Path.Combine(_paths.AgentInstanceRoot(agentId), QueueFileName);

    private async Task<GoalQueueState?> LoadStateAsync(string queuePath, string agentId, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(queuePath))
                return null;

            var json = await File.ReadAllTextAsync(queuePath, ct);
            if (string.IsNullOrWhiteSpace(json))
                return null;

            return JsonSerializer.Deserialize<GoalQueueState>(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "[GoalMode] Failed to load goal queue agent={AgentId} path={Path}",
                agentId,
                queuePath);
            return null;
        }
    }

    private async Task PersistStateAsync(string queuePath, GoalQueueState state, CancellationToken ct)
    {
        try
        {
            var dir = Path.GetDirectoryName(queuePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(state, JsonOptions);
            await File.WriteAllTextAsync(queuePath, json, ct);
        }
        catch (Exception ex)
        {
            // 持久化失败仅告警，不阻断注入（与 PersistHeartbeatAsync 同策略）
            _logger.LogWarning(
                ex,
                "[GoalMode] Failed to persist goal queue path={Path}",
                queuePath);
        }
    }

    private static MessageEnvelope BuildEnvelope(
        string workspaceId,
        string agentId,
        int goalIndex,
        int goalCount,
        GoalEntry goal)
    {
        var content = string.IsNullOrWhiteSpace(goal.Detail)
            ? $"── Goal 模式 · 下一目标 ({goalIndex + 1}/{goalCount}) ──\n\n{goal.Title}"
            : $"── Goal 模式 · 下一目标 ({goalIndex + 1}/{goalCount}) ──\n\n{goal.Title}\n\n{goal.Detail}";

        return new MessageEnvelope
        {
            From = new MessageAddress
            {
                Kind = MessageEndpointKinds.System,
                Id = "goal",
                WorkspaceId = workspaceId,
            },
            To = new[]
            {
                new MessageAddress
                {
                    Kind = MessageEndpointKinds.Agent,
                    Id = agentId,
                    WorkspaceId = workspaceId,
                },
            },
            Audience = MessageAudiences.Direct,
            Visibility = MessageVisibilities.Public,
            ContentType = MessageContentTypes.Text,
            Content = content,
            RoomId = workspaceId,
            Priority = 5,
            Metadata = new Dictionary<string, string>
            {
                ["source"] = "goal-mode",
                ["agent_id"] = agentId,
                ["goal_index"] = goalIndex.ToString(),
                ["goal_title"] = goal.Title,
            },
        };
    }
}