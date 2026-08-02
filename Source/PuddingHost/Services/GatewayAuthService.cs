using System.Security.Cryptography;
using System.Text;
using github.hyfree.GM;
using github.hyfree.GM.Common;
using PuddingGateway.Models;

namespace PuddingAgent.Services;

/// <summary>
/// 网关鉴权服务 — 对入站连接进行身份验证。
/// 
/// 鉴权策略：
///   SM2 签名：WebSocket / HTTP API → 请求帧携带 SM2 签名，验证签名 + 时间戳窗口
///   白名单：  邮箱 / 飞书 → 配置 Gateway:EmailWhitelist / FeishuWhitelist
/// 
/// 不负责具体连接器逻辑，仅提供鉴权抽象。
/// </summary>
public class GatewayAuthService
{
    private readonly GMService _gm = new();
    private readonly IConfiguration _config;
    private readonly ILogger<GatewayAuthService> _logger;

    /// <summary>SM2 签名时间戳有效期（防重放）</summary>
    private static readonly TimeSpan SignatureTimeWindow = TimeSpan.FromMinutes(5);

    public GatewayAuthService(IConfiguration config, ILogger<GatewayAuthService> logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// 使用 SM2 签名进行鉴权。
    /// 请求方在连接首帧发送 {"type":"auth","timestamp":<unix_ms>} 并在 HTTP Header
    /// X-SM2-Signature 中携带签名。签名内容 = timestamp + body(utf8)。
    /// </summary>
    public ConnectionIdentity? AuthenticateSm2(string connectorId, string? signature, string? timestampStr, string body)
    {
        if (string.IsNullOrWhiteSpace(signature) || string.IsNullOrWhiteSpace(timestampStr))
        {
            _logger.LogWarning("[GatewayAuth] SM2 auth missing signature or timestamp connector={C}", connectorId);
            return null;
        }

        // 验证时间戳窗口
        if (!long.TryParse(timestampStr, out var timestampMs))
        {
            _logger.LogWarning("[GatewayAuth] Invalid SM2 timestamp: {Ts}", timestampStr);
            return null;
        }
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(timestampMs);
        if (Math.Abs((DateTimeOffset.UtcNow - timestamp).TotalMinutes) > SignatureTimeWindow.TotalMinutes)
        {
            _logger.LogWarning("[GatewayAuth] SM2 timestamp expired: {Ts}", timestamp);
            return null;
        }

        // 从配置获取公钥
        var publicKeyHex = _config["Gateway:Sm2PublicKey"];
        if (string.IsNullOrWhiteSpace(publicKeyHex))
        {
            _logger.LogError("[GatewayAuth] SM2 public key not configured");
            return null;
        }

        // 构建签名原文：timestamp + body
        var signContent = timestampStr + body;
        var signContentBytes = Encoding.UTF8.GetBytes(signContent);

        try
        {
            var signatureBytes = HexUtil.HexToByteArray(signature);
            var pubKeyBytes = HexUtil.HexToByteArray(publicKeyHex);

            var valid = _gm.SM2VerifySign(signContentBytes, signatureBytes, pubKeyBytes, null);

            if (!valid)
            {
                _logger.LogWarning("[GatewayAuth] SM2 signature verification failed connector={C}", connectorId);
                return null;
            }

            var user = $"sm2-{timestampMs}";
            _logger.LogInformation("[GatewayAuth] SM2 auth success connector={C} user={User}", connectorId, user);
            return new ConnectionIdentity
            {
                ConnectorId = connectorId,
                SourceType = "sm2",
                AuthenticatedUser = user,
                AuthMethod = "sm2",
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[GatewayAuth] SM2 signature verification error");
            return null;
        }
    }

    /// <summary>白名单鉴权（邮箱 / 飞书等）。</summary>
    public ConnectionIdentity? AuthenticateWhitelist(string connectorId, string identifier, string sourceType)
    {
        var configKey = sourceType switch
        {
            "email" => "Gateway:EmailWhitelist",
            "feishu" => "Gateway:FeishuWhitelist",
            _ => null,
        };

        if (configKey is null)
        {
            _logger.LogWarning("[GatewayAuth] Unknown source type for whitelist: {Type}", sourceType);
            return null;
        }

        var whitelist = _config.GetSection(configKey).Get<string[]>() ?? [];

        if (!whitelist.Contains(identifier, StringComparer.OrdinalIgnoreCase))
        {
            _logger.LogWarning("[GatewayAuth] Whitelist denied: {Type} identifier={Id}", sourceType, identifier);
            return null;
        }

        _logger.LogInformation("[GatewayAuth] Whitelist auth success connector={C} type={T} user={U}", connectorId, sourceType, identifier);
        return new ConnectionIdentity
        {
            ConnectorId = connectorId,
            SourceType = sourceType,
            AuthenticatedUser = identifier,
            AuthMethod = "whitelist",
        };
    }
}
