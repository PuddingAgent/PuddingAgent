using System.Text;
using github.hyfree.GM.Common;
using github.hyfree.GM.SM2;

namespace PuddingPlatform.Services;

/// <summary>
/// SM2 JWT 载荷签名服务。
/// 采用 SM2 非对称签名（私钥签名、公钥验签）为 JWT payload 提供应用层完整性增强。
/// </summary>
public sealed class Sm2JwtSigner
{
    private const string PrivateKeyConfigPath = "Crypto:SM2:PrivateKey";
    private const string PublicKeyConfigPath = "Crypto:SM2:PublicKey";
    private const string UserIdConfigPath = "Crypto:SM2:UserId";

    private readonly byte[] _privateKey;
    private readonly byte[] _publicKey;
    private readonly byte[] _userId;

    public Sm2JwtSigner(IConfiguration configuration, ILogger<Sm2JwtSigner> logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        var privateKeyHex = configuration[PrivateKeyConfigPath];
        var publicKeyHex = configuration[PublicKeyConfigPath];

        if (string.IsNullOrWhiteSpace(privateKeyHex) || string.IsNullOrWhiteSpace(publicKeyHex))
        {
            SM2Utils.GenerateKeyPairHex(out var generatedPublicKeyHex, out var generatedPrivateKeyHex);
            privateKeyHex = generatedPrivateKeyHex;
            publicKeyHex = generatedPublicKeyHex;

            logger.LogWarning(
                "[SM2-JWT] 未配置完整 SM2 密钥对，已自动生成临时密钥。请尽快持久化到配置：{PrivatePath}={PrivateKey}; {PublicPath}={PublicKey}",
                PrivateKeyConfigPath,
                privateKeyHex,
                PublicKeyConfigPath,
                publicKeyHex);
        }

        _privateKey = ParsePrivateKey(privateKeyHex!);
        _publicKey = ParsePublicKey(publicKeyHex!);

        var userId = configuration[UserIdConfigPath];
        _userId = Encoding.UTF8.GetBytes(string.IsNullOrWhiteSpace(userId) ? "Pudding" : userId);
    }

    /// <summary>
    /// 对 payload JSON 进行 SM2 签名，返回 Hex 编码（64 字节 r||s）。
    /// </summary>
    public string SignPayload(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
            throw new ArgumentException("待签名 payload 不能为空。", nameof(payloadJson));

        var msgBytes = Encoding.UTF8.GetBytes(payloadJson);
        var signature = SM2Utils.Sign(msgBytes, _privateKey, _userId);
        return HexUtil.ByteArrayToHex(signature.ToByteArray());
    }

    /// <summary>
    /// 验证 payload JSON 的 SM2 签名。
    /// </summary>
    public bool VerifyPayload(string payloadJson, string signatureHex)
    {
        if (string.IsNullOrWhiteSpace(payloadJson) || string.IsNullOrWhiteSpace(signatureHex))
            return false;

        var msgBytes = Encoding.UTF8.GetBytes(payloadJson);
        var signatureBytes = HexToBytes(signatureHex);
        if (signatureBytes.Length != 64)
            return false;

        var sm2Signature = new SM2Signature(signatureBytes);
        return SM2Utils.VerifySign(msgBytes, sm2Signature, _publicKey, _userId);
    }

    private static byte[] ParsePrivateKey(string privateKeyHex)
    {
        var bytes = HexToBytes(privateKeyHex);
        if (bytes.Length != 32)
            throw new InvalidOperationException($"SM2 私钥必须为 32 字节（Hex 64 字符），当前长度={bytes.Length} 字节。");

        return bytes;
    }

    private static byte[] ParsePublicKey(string publicKeyHex)
    {
        var normalized = publicKeyHex.Trim();
        if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[2..];

        // 兼容传入无前缀04的坐标（128 hex）
        if (normalized.Length == 128)
            normalized = "04" + normalized;

        var bytes = HexToBytes(normalized);
        if (bytes.Length != 65 || bytes[0] != 0x04)
            throw new InvalidOperationException("SM2 公钥必须是未压缩格式（65 字节，0x04 + X32 + Y32）。");

        return bytes;
    }

    private static byte[] HexToBytes(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            throw new InvalidOperationException("SM2 密钥不能为空。");

        var normalized = hex.Trim();
        if ((normalized.Length & 1) != 0)
            throw new InvalidOperationException("Hex 字符串长度必须为偶数。", new FormatException());

        try
        {
            return HexUtil.HexToByteArray(normalized);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            throw new InvalidOperationException("SM2 密钥格式非法（非 Hex）。", ex);
        }
    }
}
