using System.Text.Json;
using PuddingCodexService.Models;

namespace PuddingCodexService.Services;

public sealed class SupervisorRestartRequestWriter(
    CodexServiceOptions options,
    CodexTaskCoordinator coordinator)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<PuddingRestartAccepted> RequestAsync(string taskId, CancellationToken ct = default)
    {
        var task = await coordinator.GetRequiredAsync(taskId, ct);
        if (task.Status != CodexTaskStatus.Completed)
            throw new InvalidOperationException("Pudding restart requires a completed Codex task.");

        await _gate.WaitAsync(ct);
        try
        {
            var requestPath = Path.Combine(options.SupervisorRunDirectory, "backend.restart.request.json");
            if (File.Exists(requestPath))
                throw new InvalidOperationException("A Pudding backend restart request is already pending.");

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
