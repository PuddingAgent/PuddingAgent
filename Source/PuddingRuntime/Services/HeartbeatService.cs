namespace PuddingRuntime.Services;

/// <summary>
/// 心跳后台服务——周期扫描超时会话并清理 Agent 实例资源。
/// </summary>
public sealed class HeartbeatService : BackgroundService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromHours(1);

    private readonly InMemoryRuntimeSessionStore _sessionStore;
    private readonly AgentSessionManager _agentSessionManager;
    private readonly ILogger<HeartbeatService> _logger;

    public HeartbeatService(
        InMemoryRuntimeSessionStore sessionStore,
        AgentSessionManager agentSessionManager,
        ILogger<HeartbeatService> logger)
    {
        _sessionStore = sessionStore;
        _agentSessionManager = agentSessionManager;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[Heartbeat] Service started. Scan interval={Interval}, Timeout={Timeout}",
            ScanInterval, SessionTimeout);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(ScanInterval, stoppingToken);
                ScanAndCleanup();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Heartbeat] Unhandled error during scan");
            }
        }

        _logger.LogInformation("[Heartbeat] Service stopping.");
    }

    private void ScanAndCleanup()
    {
        var expired = _sessionStore.GetExpired(SessionTimeout);
        if (expired.Count == 0) return;

        _logger.LogInformation("[Heartbeat] Found {Count} expired sessions to cleanup", expired.Count);

        foreach (var session in expired)
        {
            try
            {
                _agentSessionManager.Terminate(session.SessionId);
                _agentSessionManager.Remove(session.SessionId);
                _sessionStore.Terminate(session.SessionId, "timeout");
                _logger.LogInformation("[Heartbeat] Cleaned up session={SessionId} (last active={LastActive})",
                    session.SessionId, session.LastActiveAt);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Heartbeat] Failed to cleanup session={SessionId}", session.SessionId);
            }
        }
    }
}
