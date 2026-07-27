using System.Text.Json;
using PuddingCodexService.Models;

namespace PuddingCodexService.Services;

public sealed class SupervisorRestartRequestWriter(
    CodexServiceOptions options)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<PuddingRestartAccepted> RequestAsync(
        CodexTaskRecord task,
        CancellationToken ct = default)
    {
        if (task.Status != CodexTaskStatus.Completed)
            throw new InvalidOperationException("Pudding restart requires a completed Codex task.");

        await _gate.WaitAsync(ct);
        try
        {
            var requestPath = Path.Combine(options.SupervisorRunDirectory, "backend.restart.request.json");
            if (File.Exists(requestPath))
            {
                var pending = JsonSerializer.Deserialize<SupervisorRestartRequest>(
                    await File.ReadAllTextAsync(requestPath, ct),
                    JsonOptions)
                    ?? throw new InvalidOperationException("The pending Pudding restart request is invalid.");
                if (string.Equals(pending.TaskId, task.TaskId, StringComparison.Ordinal))
                    return new PuddingRestartAccepted(pending.RequestId, pending.TaskId, pending.NotBeforeUtc);
                throw new InvalidOperationException("A Pudding backend restart request is already pending.");
            }

            var completed = await FindCompletedRequestAsync(task.TaskId, ct);
            if (completed is not null)
                return completed;

            var now = DateTimeOffset.UtcNow;
            var request = new SupervisorRestartRequest(
                Guid.NewGuid().ToString("N"),
                task.TaskId,
                now,
                now.AddSeconds(options.RestartDelaySeconds));
            var tempPath = $"{requestPath}.{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllTextAsync(
                    tempPath,
                    JsonSerializer.Serialize(request, JsonOptions),
                    ct);
                File.Move(tempPath, requestPath);
            }
            finally
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }

            return new PuddingRestartAccepted(request.RequestId, request.TaskId, request.NotBeforeUtc);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<PuddingRestartAccepted?> FindCompletedRequestAsync(
        string taskId,
        CancellationToken ct)
    {
        foreach (var path in Directory.EnumerateFiles(
                     options.SupervisorRunDirectory,
                     "backend.restart.result.*.json")
                 .OrderByDescending(File.GetLastWriteTimeUtc))
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, ct));
            var root = document.RootElement;
            if (!root.TryGetProperty("taskId", out var storedTaskId)
                || !string.Equals(storedTaskId.GetString(), taskId, StringComparison.Ordinal)
                || !root.TryGetProperty("requestId", out var requestId))
            {
                continue;
            }

            var completedAt = root.TryGetProperty("completedAtUtc", out var completedAtUtc)
                              && completedAtUtc.TryGetDateTimeOffset(out var parsed)
                ? parsed
                : new DateTimeOffset(File.GetLastWriteTimeUtc(path));
            return new PuddingRestartAccepted(requestId.GetString()!, taskId, completedAt);
        }

        return null;
    }

    public async Task<string> GetResultAsync(string requestId, CancellationToken ct = default)
    {
        if (!Guid.TryParseExact(requestId, "N", out _))
            throw new ArgumentException("requestId must be a 32-character GUID.", nameof(requestId));
        var path = Path.Combine(options.SupervisorRunDirectory, $"backend.restart.result.{requestId}.json");
        return File.Exists(path)
            ? await File.ReadAllTextAsync(path, ct)
            : JsonSerializer.Serialize(new { requestId, status = "pending" }, JsonOptions);
    }

    private sealed record SupervisorRestartRequest(
        string RequestId,
        string TaskId,
        DateTimeOffset RequestedAtUtc,
        DateTimeOffset NotBeforeUtc);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
}
