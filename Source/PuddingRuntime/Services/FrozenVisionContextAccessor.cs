using PuddingCode.Platform;
using System.Threading;

namespace PuddingRuntime.Services;

/// <summary>
/// Async-flow-local 冻结视觉上下文（ADR-077 V5 能力声明单源化）。
/// Agent 执行入口把 Run 启动时冻结的 <see cref="LlmRouteSnapshot"/>（由 AgentExecutionSnapshot
/// 派生，含 CapabilityTags/Protocol/VisionPolicy）推入当前异步流；DirectLlmClient 创建
/// Gateway 时读取，使视觉能力判定与视觉预算策略以执行快照为唯一可信源，
/// 不再在请求路径二次读取可热变的模型目录。
/// 无冻结上下文的调用路径 fail closed（不支持视觉），不静默放宽任何能力。
/// </summary>
public sealed class FrozenVisionContextAccessor
{
    private readonly AsyncLocal<LlmRouteSnapshot?> _current = new();

    /// <summary>当前异步流的冻结路由快照；未经 Agent 执行入口派发时为 null。</summary>
    public LlmRouteSnapshot? Current => _current.Value;

    /// <summary>在 Agent 执行入口推送本次 Run 的冻结快照；返回的 Scope Dispose 后恢复原值。</summary>
    public IDisposable Push(LlmRouteSnapshot? snapshot)
    {
        var previous = _current.Value;
        _current.Value = snapshot;
        return new Scope(this, previous);
    }

    private sealed class Scope(FrozenVisionContextAccessor owner, LlmRouteSnapshot? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            owner._current.Value = previous;
        }
    }
}
