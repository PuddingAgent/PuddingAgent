namespace PuddingBrowser.Protocol;

public static class BrowserBridgeProtocol
{
    /// <summary>
    /// Bridge 线缆契约修订。修订 3 隔离"命令结果合同修复"（2026-10-18 D8：context.getInfo 单对象、
    /// close 空响应、历史导航单页合同、发现只读元数据）。两端必须严格相等；不提供旧新载荷兼容。
    /// </summary>
    public const int CurrentVersion = 3;
    public const int MaxMessageBytes = 1024 * 1024;
    public const string EndpointPath = "/desktop/browser-bridge";
    public const string ControlTokenHeader = "X-Pudding-Desktop-Token";
    public static readonly TimeSpan DefaultHeartbeatInterval = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan DefaultHeartbeatTimeout = TimeSpan.FromSeconds(45);
}
