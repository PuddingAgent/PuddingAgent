using PuddingSsh.Contracts;

namespace PuddingSsh.Execution;

/// <summary>
/// 有界排空（ADR-096 D3）：排空、TERM、关闭与 Dispose **共用同一份清理预算**，
/// 任何一步超时都不得再无条件 await 原任务；超时即请求中止（Abort），
/// 让关闭状态机去结束通道与 reader。
/// </summary>
internal static class SshBoundedDrain
{
    /// <summary>在预算内等待排空任务；超时返回 false 并调用 <paramref name="onTimeout"/>（不得阻塞）。</summary>
    public static async Task<bool> WaitAsync(
        Task drains,
        TimeSpan budget,
        Action onTimeout,
        Func<Task>? onLateCompletion = null)
    {
        ArgumentNullException.ThrowIfNull(drains);

        if (drains.IsCompleted)
        {
            await ObserveAsync(drains).ConfigureAwait(false);
            return true;
        }

        var delay = budget > TimeSpan.Zero
            ? Task.Delay(budget, TimeProvider.System)
            : Task.CompletedTask;

        var winner = await Task.WhenAny(drains, delay).ConfigureAwait(false);
        if (winner == drains)
        {
            await ObserveAsync(drains).ConfigureAwait(false);
            return true;
        }

        // 预算用尽：只登记中止请求，绝不在这里等待（否则「宽限」形同虚设）。
        try
        {
            onTimeout();
        }
        catch (Exception)
        {
            // 中止请求本身不得把清理路径变成失败路径。
        }

        ObserveLate(drains, onLateCompletion);
        return false;
    }

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 排空任务的异常由调用方按「读取失败」另行表达，这里只负责观察掉。
        }
    }

    private static void ObserveLate(Task task, Func<Task>? onLateCompletion)
    {
        _ = task.ContinueWith(
            static (completed, state) =>
            {
                var owner = (Func<Task>?)state;

                // 读取异常即视为已观察。
                _ = completed.Exception;
                if (owner is null || completed.IsFaulted || completed.IsCanceled)
                {
                    return;
                }

                try
                {
                    _ = owner();
                }
                catch (Exception)
                {
                    // 迟到收尾不得抛出未观察异常。
                }
            },
            onLateCompletion,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
