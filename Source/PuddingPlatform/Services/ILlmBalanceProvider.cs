using PuddingCode.Configuration;
using PuddingPlatform.Data.Dtos;

namespace PuddingPlatform.Services;

/// <summary>
/// 服务商余额查询适配器（多服务商计费抽象）。
/// 每个厂商实现一个适配器并经 DI 注册进注册表，由 LlmProviderFileService.GetBalanceAsync
/// 按 <see cref="CanHandle"/> 分发——扩展新服务商见
/// Docs/Features/服务商余额查询与多服务商计费适配器设计方案.md。
/// </summary>
public interface ILlmBalanceProvider
{
    /// <summary>是否支持该 provider（按 ProviderId / BaseUrl 判断）。</summary>
    bool CanHandle(PuddingLlmProviderConfig provider);

    /// <summary>
    /// 查询账户余额。apiKey 已由调用方解析（ApiKey → ${ENV} → {{vault:NAME}} → ApiKeyRef/KeyVault 链）。
    /// 网络错误抛 HttpRequestException（控制器映射 502）；上游业务错误（非 2xx / 响应解析失败）
    /// 返回 IsAvailable=false 且 Error 非空的 DTO，不抛异常。
    /// apiKey 绝不能出现在任何日志中（仅 providerId、状态码、耗时等）。
    /// </summary>
    Task<LlmProviderBalanceDto> QueryAsync(
        PuddingLlmProviderConfig provider,
        string apiKey,
        CancellationToken ct = default);
}
