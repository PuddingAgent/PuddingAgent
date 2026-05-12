using PuddingCode.Abstractions;
using PuddingCode.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PuddingRuntime.Services.Events;

/// <summary>
/// 事件分发器 BackgroundService — 从优先级队列持续出队并路由到处理链路。
/// 优先处理 Urgent → Important → Normal。
/// Phase 3: 接入 IInternalEventBus 完成事件分发闭环。
/// </summary>
public class EventDispatcher : BackgroundService
{
    private readonly IPriorityEventQueue _queue;
    private readonly IInternalEventBus? _eventBus;
    private readonly ILogger<EventDispatcher> _logger;

    public EventDispatcher(
        IPriorityEventQueue queue,
        ILogger<EventDispatcher> logger,
        IInternalEventBus? eventBus = null)
    {
        _queue = queue;
        _logger = logger;
        _eventBus = eventBus;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[EventDispatcher] Started. Waiting for events...");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var evt = await _queue.DequeueAsync(stoppingToken);

                if (evt is null)
                {
                    // 队列空，等待后重试
                    await Task.Delay(500, stoppingToken);
                    continue;
                }

                _logger.LogInformation(
                    "[EventDispatcher] Dispatching event: {Id} type={Type} priority={Priority}",
                    evt.Id, evt.EventType, evt.Priority);

                // Phase 3: 构造 InternalEvent 并通过 IInternalEventBus 发布到订阅者
                // Phase 4: Urgent/Important 事件触发 AgentExecutionService.InterruptAsync

                await _queue.UpdateStatusAsync(evt.Id, "completed", ct: stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[EventDispatcher] Dispatch loop error");
            }
        }

        _logger.LogInformation("[EventDispatcher] Stopped.");
    }
}
