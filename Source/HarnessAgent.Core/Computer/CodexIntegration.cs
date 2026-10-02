using System.Text.Json;
using System.Text.RegularExpressions;

namespace HarnessAgent.Core.Computer;

/// <summary>
/// High-level Codex integration — wraps SelfHealRestart with
/// common operations: send command, restart Pudding, apply yolo.
/// </summary>
public sealed class CodexIntegration
{
    private readonly SelfHealRestart _restart;

    public CodexIntegration(SelfHealRestart? restart = null)
    {
        _restart = restart ?? new SelfHealRestart();
    }

    /// <summary>Ensure Codex layout is discovered or loaded.</summary>
    public CodexLayout? EnsureLayout()
    {
        return _restart.Layout ?? _restart.LoadCachedLayout() ?? _restart.Discover();
    }

    // ── Commands ──

    /// <summary>Send an arbitrary text command to Codex.</summary>
    public async Task<RestartResult> SendCommandAsync(string command,
        int waitMs = 15000, CancellationToken ct = default)
    {
        return await _restart.ExecuteRestartAsync(command, waitMs, ct);
    }

    /// <summary>
    /// Restart Pudding via Codex, with self-healing.
    /// Command: "帮我重启，如果有问题请修复。重启之后，输入 /yolo，然后，告诉 pudding 已经重启完成了。"
    /// </summary>
    public async Task<RestartResult> RestartPuddingWithHealAsync(
        int waitAfterSendMs = 60000, CancellationToken ct = default)
    {
        const string cmd = "帮我重启，如果有问题请修复。重启之后，输入 /yolo，然后，告诉 pudding 已经重启完成了。";

        var result = await _restart.ExecuteRestartAsync(cmd, waitAfterSendMs, ct);

        if (!result.IsSuccess)
        {
            // Retry with fix command
            const string fixCmd = "重启失败了，请检查错误并修复，然后重新启动。启动后输入 /yolo 并通知 pudding。";
            result = await _restart.ExecuteRestartAsync(fixCmd, 60000, ct);
        }

        return result;
    }

    /// <summary>Send /yolo command to give Pudding full autonomy.</summary>
    public async Task<RestartResult> SendYoloAsync(CancellationToken ct = default)
    {
        return await _restart.ExecuteRestartAsync("/yolo", 5000, ct);
    }

    // ── Monitoring ──

    /// <summary>
    /// Check if Pudding is running by looking for its process or localhost response.
    /// </summary>
    public static async Task<bool> IsPuddingRunningAsync(int timeoutMs = 3000)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
            var resp = await client.GetAsync("http://localhost:8080/health");
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Wait for Pudding to become available after restart.
    /// Polls localhost:8080/health every 5 seconds.
    /// </summary>
    public static async Task<bool> WaitForPuddingAsync(int maxWaitSeconds = 120, CancellationToken ct = default)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(maxWaitSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await IsPuddingRunningAsync(2000))
                return true;
            await Task.Delay(5000, ct);
        }
        return false;
    }

    // ── Full Workflow ──

    /// <summary>
    /// Complete restart workflow:
    /// 1. Send restart command to Codex
    /// 2. Wait for Pudding to go down
    /// 3. Wait for Pudding to come back up
    /// 4. Send /yolo
    /// 5. Report success
    /// </summary>
    public async Task<RestartResult> FullRestartWorkflowAsync(CancellationToken ct = default)
    {
        // Ensure layout
        var layout = EnsureLayout();
        if (layout == null)
            return RestartResult.Failed("Could not find Codex window.");

        // Step 1: Send restart command
        var result = await RestartPuddingWithHealAsync(waitAfterSendMs: 30000, ct: ct);
        if (!result.IsSuccess)
            return result;

        // Step 2-3: Wait for Pudding to cycle
        var isBack = await WaitForPuddingAsync(120, ct);
        if (!isBack)
            return RestartResult.Failed("Pudding did not come back up within 120 seconds.");

        // Step 4: Send /yolo
        var yolo = await SendYoloAsync(ct);
        return yolo.IsSuccess
            ? RestartResult.Success("Pudding restarted and /yolo sent successfully.")
            : RestartResult.Failed($"Restart OK but /yolo failed: {yolo.Error}");
    }
}
