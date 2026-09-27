namespace PuddingDesktop.Runtime;

public sealed record CoreRestartPolicy
{
    public bool Enabled { get; init; } = true;
    public int MaxAttempts { get; init; } = 3;
    public int WindowSeconds { get; init; } = 60;
    public int InitialDelaySeconds { get; init; } = 2;
    public int MaxDelaySeconds { get; init; } = 30;

    public CoreRestartPolicy Validate()
    {
        if (MaxAttempts is < 1 or > 20)
            throw new InvalidOperationException("自动恢复次数必须为 1 到 20。");
        if (WindowSeconds is < 10 or > 3600)
            throw new InvalidOperationException("自动恢复统计窗口必须为 10 到 3600 秒。");
        if (InitialDelaySeconds is < 0 or > 300)
            throw new InvalidOperationException("自动恢复初始延迟必须为 0 到 300 秒。");
        if (MaxDelaySeconds < InitialDelaySeconds || MaxDelaySeconds > 600)
            throw new InvalidOperationException("自动恢复最大延迟必须不小于初始延迟且不超过 600 秒。");

        return this;
    }

    public TimeSpan GetDelay(int attemptNumber)
    {
        if (attemptNumber <= 0 || InitialDelaySeconds == 0)
            return TimeSpan.Zero;

        var multiplier = Math.Pow(2, attemptNumber - 1);
        var seconds = Math.Min(MaxDelaySeconds, InitialDelaySeconds * multiplier);
        return TimeSpan.FromSeconds(seconds);
    }
}
