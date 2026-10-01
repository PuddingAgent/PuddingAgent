using System.Security.Cryptography;
using System.Text;
using Pudding.Contracts.Desktop;

namespace Pudding.CapabilityBroker;

/// <summary>
/// 端点命名与描述（计划 §7）：管道名按「产品前缀 + 用户 + 产品实例」派生，
/// 使不同用户、不同 DataRoot/产品实例的 Core <b>不会串接</b>；描述里不含任何凭据。
/// </summary>
public static class CapabilityEndpointNaming
{
    public const string ProductPrefix = "pudding-capability";

    public const int ScopeHashLength = 16;

    /// <summary>
    /// 派生管道名：同样的 (用户, 产品实例) 恒定得到同一个名字；任一不同即得到不同名字。
    /// 输出只含 <c>[a-z0-9-]</c>，长度固定，不含路径分隔符。
    /// </summary>
    public static string NamedPipeName(string userScope, string productInstanceId)
    {
        var scope = ComputeScopeHash(userScope, productInstanceId);
        return $"{ProductPrefix}-{scope}";
    }

    /// <summary>命名管道端点描述（供启动就绪协议发布）。</summary>
    public static DesktopCapabilityEndpoint NamedPipeEndpoint(
        string userScope,
        string productInstanceId,
        string? coreInstanceId = null,
        int protocolVersion = Contracts.DesktopProtocolVersion.Current) =>
        DesktopCapabilityEndpoint.NamedPipe(
            NamedPipeName(userScope, productInstanceId), protocolVersion, coreInstanceId);

    /// <summary>调试备用端点：只允许回环地址（不允许因 IPC 失败自动切到公网 TCP）。</summary>
    public static DesktopCapabilityEndpoint LoopbackEndpoint(
        int port,
        string? coreInstanceId = null,
        int protocolVersion = Contracts.DesktopProtocolVersion.Current) =>
        DesktopCapabilityEndpoint.LoopbackHttp2(
            new Uri($"http://127.0.0.1:{RequirePort(port)}"), protocolVersion, coreInstanceId);

    /// <summary>远端端点：必须显式给出 host（调用方负责证书与凭据策略）。</summary>
    public static DesktopCapabilityEndpoint TlsEndpoint(
        string host,
        int port,
        string? coreInstanceId = null,
        int protocolVersion = Contracts.DesktopProtocolVersion.Current)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Length > 253)
        {
            throw new ArgumentException("TLS endpoint requires a host name.", nameof(host));
        }

        return DesktopCapabilityEndpoint.Tls(
            new Uri($"https://{host}:{RequirePort(port)}"), protocolVersion, coreInstanceId);
    }

    private static int RequirePort(int port) => port is > 0 and <= 65535
        ? port
        : throw new ArgumentOutOfRangeException(nameof(port), port, "Port must be in [1, 65535].");

    /// <summary>
    /// 作用域哈希：不做「可读名字拼接」而是哈希，避免把用户名或 DataRoot 明文暴露在管道名里
    /// （管道名会出现在系统工具与日志中）。
    /// </summary>
    public static string ComputeScopeHash(string userScope, string productInstanceId)
    {
        if (string.IsNullOrWhiteSpace(userScope) || string.IsNullOrWhiteSpace(productInstanceId))
        {
            throw new ArgumentException("Endpoint scope requires both a user scope and a product instance id.");
        }

        var canonical = string.Concat(userScope.Trim(), "\u0000", productInstanceId.Trim());
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash)[..ScopeHashLength].ToLowerInvariant();
    }
}
