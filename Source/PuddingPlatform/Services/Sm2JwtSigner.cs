using System.Security.Cryptography;
using System.Text;

namespace PuddingPlatform.Services;

/// <summary>
/// ECDSA JWT 载荷签名服务（开发阶段替代 SM2，生产环境应替换为 SM2 算法）。
/// 采用 ECDSA P-256 非对称签名为 JWT payload 提供应用层完整性增强。
/// </summary>
public sealed class Sm2JwtSigner
{
    private const string PrivateKeyConfigPath = "Crypto:SM2:PrivateKey";
    private const string PublicKeyConfigPath = "Crypto:SM2:PublicKey";

    private readonly ECDsa _ecdsa;

    public Sm2JwtSigner(IConfiguration configuration, ILogger<Sm2JwtSigner> logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        var privateKeyHex = configuration[PrivateKeyConfigPath];
        var publicKeyHex = configuration[PublicKeyConfigPath];

        if (string.IsNullOrWhiteSpace(privateKeyHex) || string.IsNullOrWhiteSpace(publicKeyHex))
        {
            _ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var exportedPrivate = _ecdsa.ExportECPrivateKey();
            var exportedPublic = _ecdsa.ExportSubjectPublicKeyInfo();

            logger.LogWarning(
                "[ECDSA-JWT] 未配置完整密钥对，已自动生成临时密钥。请尽快持久化到配置。私钥Base64={PrivateKey}; 公钥Base64={PublicKey}",
                Convert.ToBase64String(exportedPrivate),
                Convert.ToBase64String(exportedPublic));
        }
        else
        {
            _ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            _ecdsa.ImportECPrivateKey(Convert.FromHexString(privateKeyHex), out _);
        }
    }

    /// <summary>
    /// 对 payload JSON 进行 ECDSA 签名，返回 Base64 编码。
    /// </summary>
    public string SignPayload(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
            throw new ArgumentException("待签名 payload 不能为空。", nameof(payloadJson));

        var msgBytes = Encoding.UTF8.GetBytes(payloadJson);
        var signature = _ecdsa.SignData(msgBytes, HashAlgorithmName.SHA256);
        return Convert.ToBase64String(signature);
    }

    /// <summary>
    /// 验证 payload JSON 的 ECDSA 签名。
    /// </summary>
    public bool VerifyPayload(string payloadJson, string signatureBase64)
    {
        if (string.IsNullOrWhiteSpace(payloadJson) || string.IsNullOrWhiteSpace(signatureBase64))
            return false;

        try
        {
            var msgBytes = Encoding.UTF8.GetBytes(payloadJson);
            var signatureBytes = Convert.FromBase64String(signatureBase64);
            return _ecdsa.VerifyData(msgBytes, signatureBytes, HashAlgorithmName.SHA256);
        }
        catch
        {
            return false;
        }
    }
}
