using Microsoft.Extensions.Hosting;
using PuddingRuntime.Services;

namespace PuddingHost.Hosting;

/// <summary>
/// 复核 P1（停机接线）：把终态后可选维护调度的 StopAsync 接进宿主停机流程。
/// <para>
/// 为什么需要：可选维护在终态之后异步运行（可能正在做远端摘要）。停机若不取消它，
/// 进程退出会留下半途的模型调用与"未见终态"的任务；若直接强杀则丢掉可诊断状态。
/// 本宿主服务在 StopAsync 里协作取消并等待退出（有窗口上限），失败不影响停机结果。
/// </para>
/// </summary>
internal sealed class BackgroundMaintenanceShutdownHostedService : IHostedService
{
    private readonly AgentExecutionService _agentExecutionService;
    private readonly ILogger<BackgroundMaintenanceShutdownHostedService> _logger;

    public BackgroundMaintenanceShutdownHostedService(
        AgentExecutionService agentExecutionService,
        ILogger<BackgroundMaintenanceShutdownHostedService> logger)
    {
        _agentExecutionService = agentExecutionService;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _agentExecutionService.StopBackgroundMaintenanceAsync(TimeSpan.FromSeconds(5));
            _logger.LogInformation(
                "[BackgroundMaintenanceShutdown] Post-turn maintenance stop signalled and awaited");
        }
        catch (Exception ex)
        {
            // 停机不得因可选维护而失败。
            _logger.LogWarning(
                ex,
                "[BackgroundMaintenanceShutdown] Failed to stop post-turn maintenance cleanly");
        }
    }
}
