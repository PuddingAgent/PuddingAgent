namespace Pudding.CapabilityBroker.AspNetCore;

/// <summary>能力通道端点形态（配置取值，见 <see cref="CapabilityChannelConfiguration.Transport"/>）。</summary>
public static class CapabilityChannelTransport
{
    /// <summary>产品默认：Windows Named Pipe + 显式 HTTP/2。</summary>
    public const string NamedPipe = "named-pipe";

    /// <summary>调试备用：Loopback h2c（复用既有监听器或独立端口）。</summary>
    public const string LoopbackHttp2 = "loopback-h2c";

    /// <summary>同时监听两者（排查/过渡期）。</summary>
    public const string Both = "both";
}
