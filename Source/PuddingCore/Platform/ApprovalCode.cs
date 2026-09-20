using System.Security.Cryptography;
using System.Text;

namespace PuddingCode.Platform;

/// <summary>
/// 待审单确认码的生成与校验——PuddingController 审批流程的**唯一凭据**
/// （<c>GET /api/approval/*</c> 只下发脱敏投影，确认码仅由持码人离线获得，见
/// <c>ApprovalController</c> 的安全边界注释）。
/// </summary>
/// <remarks>
/// <para><b>审计基线（2026-09-20）</b>：确认码长 <see cref="Length"/> = 8 个十六进制
/// 字符 = <b>32 bit</b> 熵。作为对照，<see cref="ApprovalRecord.ApprovalId"/> 是完整
/// <c>Guid.NewGuid().ToString("N")</c>（128 bit）。32 bit 强于短信 OTP（6 位数字 = 20 bit），
/// 但**必须**与失败尝试限制配套使用，因为它是唯一凭据：一旦猜中即等于本次审批被批准。</para>
///
/// <para><b>已知未决项（未在本类内单方面处理）</b>：</para>
/// <list type="number">
///   <item><description>
///   <b>失败尝试限制：已实现（2026-09-20 迭代 #16）</b>。此前
///   <c>ConfirmAsync</c> 无失败计数，在 24h 有效期内理论上可对已知 <c>ApprovalId</c>
///   暴力枚举 32 bit 空间（折算约需 2.4 万 req/s 持续 24h，单机不现实，
///   但属纵深防御缺口）。现在服务会累计失败次数，达到 <see cref="MaxFailedAttempts"/>
///   即作废该审批单。之所以能落地，是因为审批服务已从 Redis 依赖改为进程内实现
///   （迭代 #15），使其首次可被端到端单元测试。
///   </description></item>
///   <item><description>
///   提高 <see cref="Length"/> 会直接改变用户手输确认码的体验（8 位 → 更长），
///   属产品决策。
///   </description></item>
///   <item><description>
///   ~~<c>ConfirmRequest.ConfirmedBy</c> 由请求体提供并原样写入 <c>ResolvedBy</c>，
///   未与认证主体绑定，确认人身份可被任意填写。~~ <b>已在 2026-09-20（发现 Y）加固</b>：
///   <c>PuddingController.ApprovalController</c> 的 <c>confirm</c>/<c>reject</c> 改为
///   **优先取认证上下文**（<c>ClaimTypes.NameIdentifier</c> → <c>User.Identity.Name</c>），
///   请求体自填姓名仅在无法解析认证主体时作为回退。
///   </description></item>
/// </list>
///
/// <para><b>本类修正的问题</b>：确认码的生成与校验此前散落在
/// <c>InMemoryApprovalService</c> 内（生成用 <c>Guid.NewGuid().ToString("N")[..8]</c>，
/// 校验用 <c>record.ConfirmationCode != confirmationCode</c> 的**逐字节短路比较**），
/// 既无任何测试覆盖，也引入时序侧信道。集中到本类后可独立单元测试，且比较改为
/// <see cref="CryptographicOperations.FixedTimeEquals"/> 恒时比较。</para>
/// </remarks>
public static class ApprovalCode
{
    /// <summary>
    /// 确认码字符数（十六进制）。8 字符 = <b>32 bit</b> 熵。
    /// 这是对外可见的契约（用户手输位数），调整需产品决策。
    /// </summary>
    public const int Length = 8;

    private const string HexDigits = "0123456789abcdef";

    /// <summary>
    /// 生成一个新的确认码，取 <see cref="Length"/> 个十六进制字符（小写）。
    /// </summary>
    /// <remarks>
    /// 使用 <see cref="RandomNumberGenerator"/>（CSPRNG）。历史上实现为
    /// <c>Guid.NewGuid().ToString("N")[..8]</c>：GUID v4 的版本位落在第 13 个十六进制
    /// 字符（<c>xxxxxxxx-xxxx-4xxx-…</c> 中的 <c>4</c>），故前 8 个字符确实等价于
    /// 32 bit 均匀随机；本实现不再依赖这一布局约定，并显式使用 CSPRNG。
    /// </remarks>
    public static string Generate()
    {
        Span<byte> buffer = stackalloc byte[(Length + 1) / 2];
        RandomNumberGenerator.Fill(buffer);

        Span<char> chars = stackalloc char[Length];
        for (var i = 0; i < Length; i++)
        {
            var b = buffer[i / 2];
            var nibble = i % 2 == 0 ? b >> 4 : b & 0x0F;
            chars[i] = HexDigits[nibble];
        }

        return new string(chars);
    }

    /// <summary>
    /// 单个审批单允许的最大确认码失败尝试次数。达到该值后审批单作废
    /// （见 <see cref="IsExhausted"/>），持码人需重新发起审批。
    /// </summary>
    /// <remarks>
    /// 10 次而不是更少：<see cref="Length"/> 只有 32 bit，而合法用户复制/手输出错是常态，
    /// 阈值过低会把正常用户锁死。另一方面，10 次失败后猜中的概率上界约为
    /// 10 / 2^32 ≈ 2.3e-9，已经远低于任何实用攻击面。
    /// </remarks>
    public const int MaxFailedAttempts = 10;

    /// <summary>失败尝试是否已达上限（含等于）。</summary>
    public static bool IsExhausted(int failedAttempts) => failedAttempts >= MaxFailedAttempts;

    /// <summary>
    /// 恒时比较存储值与提交值；任一为 null/空、长度不等或内容不同均返回 <c>false</c>。
    /// </summary>
    /// <remarks>
    /// 长度不等时直接返回，不做恒时比较：<see cref="Length"/> 是公开常量，
    /// 长度本身不构成秘密，而 <see cref="CryptographicOperations.FixedTimeEquals"/> 要求
    /// 两侧等长。长度相等时逐字节恒时比较，不因首个不同字节提前返回。
    /// 比较区分大小写（生成端恒为小写）。
    /// </remarks>
    public static bool Matches(string? stored, string? provided)
    {
        if (string.IsNullOrEmpty(stored) || string.IsNullOrEmpty(provided))
            return false;

        if (stored.Length != provided.Length)
            return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(stored),
            Encoding.UTF8.GetBytes(provided));
    }
}
