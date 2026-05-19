using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Serialization;
using PuddingCode.SubAgents;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services;

/// <summary>
/// ISubAgentRunStore 的文件系统实现。
/// 运行归档以文件为主存储（run.json / events.jsonl / tools.jsonl / output.md），数据库仅做索引。
/// 关联 ADR：Docs/07架构/21子代理工作空间与运行归档ADR.md
/// </summary>
public class FileSubAgentRunStore : ISubAgentRunStore
{
    private readonly PuddingDataPaths _paths;
    private readonly ILogger<FileSubAgentRunStore> _logger;
    private readonly IDbContextFactory<PlatformDbContext> _dbFactory;

    public FileSubAgentRunStore(
        PuddingDataPaths paths,
        ILogger<FileSubAgentRunStore> logger,
        IDbContextFactory<PlatformDbContext> dbFactory)
    {
        _paths = paths;
        _logger = logger;
        _dbFactory = dbFactory;
    }

    /// <inheritdoc />
    public async Task<SubAgentRunHandle> CreateRunAsync(SubAgentRunCreateRequest request, CancellationToken ct = default)
    {
        var runId = $"run_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}"[..32];
        var archivePath = _paths.SubAgentRunRoot(request.WorkspaceId, request.AgentInstanceId, runId);
        Directory.CreateDirectory(archivePath);

        var now = DateTimeOffset.UtcNow;
        var manifest = new SubAgentRunManifest
        {
            RunId = runId,
            ParentSessionId = request.ParentSessionId,
            SubSessionId = request.SubSessionId,
            WorkspaceId = request.WorkspaceId,
            AgentInstanceId = request.AgentInstanceId,
            TemplateId = request.TemplateId,
            Task = request.Task,
            Status = "running",
            StartedAt = now,
        };

        // 写 run.json
        var runJson = JsonSerializer.Serialize(manifest, PuddingJsonContracts.PrettyJson);
        await File.WriteAllTextAsync(Path.Combine(archivePath, "run.json"), runJson, ct);

        // 写 input.json
        var input = new
        {
            task = request.Task,
            parentSessionId = request.ParentSessionId,
            workspaceId = request.WorkspaceId,
        };
        var inputJson = JsonSerializer.Serialize(input, PuddingJsonContracts.PrettyJson);
        await File.WriteAllTextAsync(Path.Combine(archivePath, "input.json"), inputJson, ct);

        _logger.LogInformation(
            "[FileSubAgentRunStore] Created run runId={RunId} ws={WorkspaceId} agent={AgentInstanceId} path={ArchivePath}",
            runId, request.WorkspaceId, request.AgentInstanceId, archivePath);

        // 同步写 DB 索引
        await WriteDbIndexAsync(runId, request.ParentSessionId, request.SubSessionId,
            request.WorkspaceId, request.AgentInstanceId, request.TemplateId,
            "running", now.ToString("O"), null, archivePath, ct);

        return new SubAgentRunHandle { RunId = runId, ArchivePath = archivePath };
    }

    /// <inheritdoc />
    public async Task AppendEventAsync(string runId, string eventType, object payload, CancellationToken ct = default)
    {
        var runDir = ResolveRunDir(runId);
        if (runDir is null) return;

        var line = JsonSerializer.Serialize(new
        {
            eventId = Guid.NewGuid().ToString("N"),
            eventType,
            timestamp = DateTimeOffset.UtcNow.ToString("O"),
            payload,
        }, PuddingJsonContracts.JsonLines);

        await File.AppendAllTextAsync(Path.Combine(runDir, "events.jsonl"), line + Environment.NewLine, ct);
    }

    /// <inheritdoc />
    public async Task AppendToolAuditAsync(string runId, SubAgentToolAuditEntry entry, CancellationToken ct = default)
    {
        var runDir = ResolveRunDir(runId);
        if (runDir is null) return;

        var line = JsonSerializer.Serialize(entry, PuddingJsonContracts.JsonLines);
        await File.AppendAllTextAsync(Path.Combine(runDir, "tools.jsonl"), line + Environment.NewLine, ct);
    }

    /// <inheritdoc />
    public async Task<SubAgentRunTerminalWriteResult> CompleteRunAsync(string runId, SubAgentRunCompletion completion, CancellationToken ct = default)
    {
        var runDir = ResolveRunDir(runId);
        if (runDir is null)
        {
            _logger.LogWarning("[FileSubAgentRunStore] CompleteRunAsync: run dir not found for runId={RunId}", runId);
            return SubAgentRunTerminalWriteResult.NotFound;
        }

        var runJsonPath = Path.Combine(runDir, "run.json");
        if (!File.Exists(runJsonPath))
        {
            _logger.LogWarning("[FileSubAgentRunStore] CompleteRunAsync: run.json not found for runId={RunId}", runId);
            return SubAgentRunTerminalWriteResult.NotFound;
        }

        // 读 run.json，检查当前 Status — 幂等性保护
        var json = await File.ReadAllTextAsync(runJsonPath, ct);
        var manifest = JsonSerializer.Deserialize<SubAgentRunManifest>(json, PuddingJsonContracts.PrettyJson);
        if (manifest is null) return SubAgentRunTerminalWriteResult.NotFound;

        // 如果已经是 terminal 状态，返回 AlreadyTerminal
        if (manifest.Status is "completed" or "failed" or "cancelled")
        {
            _logger.LogWarning(
                "[FileSubAgentRunStore] CompleteRunAsync: run already terminal runId={RunId} currentStatus={CurrentStatus} requestedStatus={RequestedStatus}",
                runId, manifest.Status, completion.Status);
            return SubAgentRunTerminalWriteResult.AlreadyTerminal;
        }

        // 只有 running 状态才更新为 terminal
        var completedAt = DateTimeOffset.UtcNow;
        var updated = manifest with
        {
            Status = completion.Status,
            CompletedAt = completedAt,
        };

        var updatedJson = JsonSerializer.Serialize(updated, PuddingJsonContracts.PrettyJson);
        await File.WriteAllTextAsync(runJsonPath, updatedJson, ct);

        // 写 output.md
        if (!string.IsNullOrWhiteSpace(completion.Output))
        {
            await File.WriteAllTextAsync(Path.Combine(runDir, "output.md"), completion.Output, ct);
        }

        // 写 errors.jsonl
        if (!string.IsNullOrWhiteSpace(completion.ErrorMessage))
        {
            var errorLine = JsonSerializer.Serialize(new
            {
                timestamp = completedAt.ToString("O"),
                error = completion.ErrorMessage,
            }, PuddingJsonContracts.JsonLines);
            await File.AppendAllTextAsync(Path.Combine(runDir, "errors.jsonl"), errorLine + Environment.NewLine, ct);
        }

        _logger.LogInformation(
            "[FileSubAgentRunStore] Completed run runId={RunId} status={Status} rounds={Rounds} tools={Tools}",
            runId, completion.Status, completion.TotalRounds, completion.TotalToolCalls);

        // 同步更新 DB 索引
        await UpdateDbIndexAsync(runId, completion.Status, completedAt.ToString("O"),
            completion.ErrorMessage, completion.TotalRounds, completion.TotalToolCalls,
            completion.TotalDurationMs, ct);

        return SubAgentRunTerminalWriteResult.Applied;
    }

    /// <inheritdoc />
    public async Task<SubAgentRunArchive?> GetRunArchiveAsync(string runId, CancellationToken ct = default)
    {
        var runDir = ResolveRunDir(runId);
        if (runDir is null) return null;

        var runJsonPath = Path.Combine(runDir, "run.json");
        if (!File.Exists(runJsonPath)) return null;

        // 读 run.json
        var json = await File.ReadAllTextAsync(runJsonPath, ct);
        var manifest = JsonSerializer.Deserialize<SubAgentRunManifest>(json, PuddingJsonContracts.PrettyJson);
        if (manifest is null) return null;

        // 读 events.jsonl（逐行反序列化，单行失败时记录 warning 并跳过）
        var events = new List<object>();
        var eventsPath = Path.Combine(runDir, "events.jsonl");
        if (File.Exists(eventsPath))
        {
            var lines = await File.ReadAllLinesAsync(eventsPath, ct);
            foreach (var line in lines)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    try
                    {
                        var obj = JsonSerializer.Deserialize<object>(line, PuddingJsonContracts.JsonLines);
                        if (obj is not null) events.Add(obj);
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogWarning(ex,
                            "[FileSubAgentRunStore] Skipping malformed JSONL line in {Path}", eventsPath);
                    }
                }
            }
        }

        // 读 tools.jsonl（逐行反序列化，单行失败时记录 warning 并跳过）
        var tools = new List<SubAgentToolAuditEntry>();
        var toolsPath = Path.Combine(runDir, "tools.jsonl");
        if (File.Exists(toolsPath))
        {
            var lines = await File.ReadAllLinesAsync(toolsPath, ct);
            foreach (var line in lines)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    try
                    {
                        var entry = JsonSerializer.Deserialize<SubAgentToolAuditEntry>(line, PuddingJsonContracts.JsonLines);
                        if (entry is not null) tools.Add(entry);
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogWarning(ex,
                            "[FileSubAgentRunStore] Skipping malformed JSONL line in {Path}", toolsPath);
                    }
                }
            }
        }

        // 读 output.md
        string? output = null;
        var outputPath = Path.Combine(runDir, "output.md");
        if (File.Exists(outputPath))
        {
            output = await File.ReadAllTextAsync(outputPath, ct);
        }

        // 读 errors.jsonl
        string? errorOutput = null;
        var errorsPath = Path.Combine(runDir, "errors.jsonl");
        if (File.Exists(errorsPath))
        {
            errorOutput = await File.ReadAllTextAsync(errorsPath, ct);
        }

        return new SubAgentRunArchive
        {
            Manifest = manifest,
            Events = events,
            Tools = tools,
            Output = output,
            ErrorOutput = errorOutput,
        };
    }

    /// <summary>
    /// 通过 runId 反向查找 run 目录。
    /// 遍历现有 workspace agent runs 目录匹配 runId 子目录名。
    /// </summary>
    private string? ResolveRunDir(string runId)
    {
        // 尝试从 runId 格式解析时间戳部分（避免全盘扫描）
        // runId 格式: run_20260519_HHmmss_xxxxxxxx（共32字符）
        // 直接遍历 workspaces 下的 agents/*/runs/ 目录
        var workspacesRoot = _paths.WorkspacesRoot;
        if (!Directory.Exists(workspacesRoot)) return null;

        foreach (var wsDir in Directory.GetDirectories(workspacesRoot))
        {
            var agentsRoot = Path.Combine(wsDir, "agents");
            if (!Directory.Exists(agentsRoot)) continue;

            foreach (var agentDir in Directory.GetDirectories(agentsRoot))
            {
                var runsRoot = Path.Combine(agentDir, "runs");
                if (!Directory.Exists(runsRoot)) continue;

                var candidate = Path.Combine(runsRoot, runId);
                if (Directory.Exists(candidate)) return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// 同步写 DB 索引 — 创建 run 时调用。
    /// </summary>
    private async Task WriteDbIndexAsync(
        string runId, string parentSessionId, string subSessionId,
        string workspaceId, string agentInstanceId, string templateId,
        string status, string startedAt, string? completedAt, string archivePath,
        CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            db.SubAgentRuns.Add(new SubAgentRunEntity
            {
                RunId = runId,
                ParentSessionId = parentSessionId,
                SubSessionId = subSessionId,
                WorkspaceId = workspaceId,
                AgentInstanceId = agentInstanceId,
                TemplateId = templateId,
                Status = status,
                StartedAt = startedAt,
                CompletedAt = completedAt,
                ArchivePath = archivePath,
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // DB 写入非致命 — 文件系统是主存储，DB 仅索引
            _logger.LogWarning(ex, "[FileSubAgentRunStore] DB index write failed for runId={RunId}", runId);
        }
    }

    /// <summary>
    /// 同步更新 DB 索引 — 完成 run 时调用。
    /// </summary>
    private async Task UpdateDbIndexAsync(
        string runId, string status, string completedAt,
        string? errorMessage, int totalRounds, int totalToolCalls,
        long totalDurationMs, CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var entity = await db.SubAgentRuns.FirstOrDefaultAsync(e => e.RunId == runId, ct);
            if (entity is not null)
            {
                entity.Status = status;
                entity.CompletedAt = completedAt;
                entity.ErrorMessage = errorMessage;
                entity.TotalRounds = totalRounds;
                entity.TotalToolCalls = totalToolCalls;
                entity.TotalDurationMs = totalDurationMs;
                await db.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[FileSubAgentRunStore] DB index update failed for runId={RunId}", runId);
        }
    }
}
