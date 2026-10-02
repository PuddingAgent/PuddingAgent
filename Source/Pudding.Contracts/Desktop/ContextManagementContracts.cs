namespace Pudding.Contracts.Desktop;

/// <summary>
/// 创建一个浏览器上下文（缺口 #1）。**变更类**：创建出的上下文本身是副作用，
/// 因此取消失败后必须如实标注，不得声称"没创建"。
/// </summary>
public sealed record BrowserContextCreateRequest
{
    public BrowserContextCreateRequest(string? contextId = null, bool persistent = true)
    {
        ContextId = string.IsNullOrWhiteSpace(contextId) ? null : contextId.Trim();
        Persistent = persistent;
    }

    /// <summary>可选的上下文 id（不给就由运行时生成）。</summary>
    public string? ContextId { get; }

    /// <summary>是否持久化（user-data-dir 落盘）。</summary>
    public bool Persistent { get; }
}

/// <summary>关闭一个浏览器上下文（缺口 #1）。**破坏性变更类**：上下文内的未保存状态会丢失。</summary>
public sealed record BrowserContextCloseRequest
{
    public BrowserContextCloseRequest(string contextId)
    {
        if (string.IsNullOrWhiteSpace(contextId))
        {
            throw new ArgumentException("Closing a context requires a context id.", nameof(contextId));
        }

        ContextId = contextId.Trim();
    }

    public string ContextId { get; }
}

/// <summary>关闭结果：如实回带被关闭的上下文 id（不猜测是否真的关掉了）。</summary>
public sealed record DesktopContextClosed
{
    public DesktopContextClosed(string contextId)
    {
        if (string.IsNullOrWhiteSpace(contextId))
        {
            throw new ArgumentException("Closed context id must be non-empty.", nameof(contextId));
        }

        ContextId = contextId;
    }

    public string ContextId { get; }
}

/// <summary>
/// 上下文**管理**的窄端口（缺口 #1）：只放"能力通道能覆盖"的两个写操作。
///
/// 为什么单独一个端口而不是加到 <see cref="IDesktopBrowserCapabilitySurface"/>：
/// ① 现有端口的九个操作都是**页面作用域**，而这两个是**上下文作用域**；
/// ② 加到现有端口会让所有既有实现（含各测试替身）被迫改动，与"能独立就独立"相悖。
/// 读取上下文清单仍在 <see cref="IDesktopBrowserCapabilitySurface.GetContextsAsync"/>。
/// </summary>
public interface IDesktopContextCapabilitySurface
{
    /// <summary>创建上下文；返回新建上下文的摘要（页面列表为空）。</summary>
    Task<CapabilityResult<DesktopContextInfo>> CreateContextAsync(
        BrowserContextCreateRequest request, DesktopCallContext call, CancellationToken cancellationToken = default);

    /// <summary>关闭上下文；返回被关闭的上下文 id。</summary>
    Task<CapabilityResult<DesktopContextClosed>> CloseContextAsync(
        BrowserContextCloseRequest request, DesktopCallContext call, CancellationToken cancellationToken = default);
}
