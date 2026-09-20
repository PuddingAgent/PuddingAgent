namespace PuddingRuntime.Classification;

/// <summary>
/// 仲裁位注册状态的进程内记录（切片 S6a）：由 DI 组装分类器管线时写入
/// （<see cref="ClassifierHealthReporter"/> 同一批装配），供 <c>classifier_status</c> 工具
/// 在生产未注册仲裁位时展示 fail-closed 占位（如 <c>arbiter.not-registered</c>），
/// 让「探查健康」能直接看出当前管线会走 fail-closed 降级。
/// <para>
/// 只承载布尔位与分类器稳定标识（数据），不感知任何具体厂商实现（§8.2 厂商中立）。
/// </para>
/// </summary>
public sealed class ClassifierArbiterRegistrationState
{
    private readonly object _gate = new();

    /// <summary>仲裁位是否已注册真实实现（false = 当前是 fail-closed 占位）。</summary>
    public bool ArbiterRegistered { get; private set; }

    /// <summary>仲裁位的分类器稳定标识（数据；未写入前为 null）。</summary>
    public string? ArbiterClassifierId { get; private set; }

    /// <summary>状态是否已被管线组装写入（工具据此判断读数是否有效）。</summary>
    public bool Reported { get; private set; }

    /// <summary>由管线组装处写入（幂等、线程安全）。</summary>
    public void Report(bool arbiterRegistered, string arbiterClassifierId)
    {
        lock (_gate)
        {
            ArbiterRegistered = arbiterRegistered;
            ArbiterClassifierId = arbiterClassifierId;
            Reported = true;
        }
    }
}
