namespace PuddingFinance.Collection;

/// <summary>
/// 官方数据源白名单（SKILL 的 P0/P1 层：交易所、监管、统计、海关、指数公司）。
///
/// 刻意做成 **fail-closed 后缀白名单**，而不是"任意 URL 都能抓"：
/// 一个接受任意地址的抓取组件在服务端就是 SSRF 通道，而研究场景真正需要的
/// 只有这十几家官方站。取舍是"少一点通用性，换一条可被编译期/单测钉死的边界"。
///
/// 匹配规则：去尾点 + 小写后，要么与后缀完全相等，要么以 ".后缀" 结尾。
/// 因此 <c>evil-cninfo.com.cn.attacker.com</c> 不会被误放行（它匹配的是 attacker.com 侧）。
/// </summary>
public static class OfficialSourceHosts
{
    private static readonly string[] Suffixes =
    [
        "cninfo.com.cn",   // 巨潮资讯（深交所指定披露平台）
        "sse.com.cn",      // 上交所
        "sseinfo.com",     // 上证所信息网络
        "szse.cn",         // 深交所
        "bse.cn",          // 北交所
        "csrc.gov.cn",     // 证监会
        "stats.gov.cn",    // 国家统计局
        "miit.gov.cn",     // 工信部
        "customs.gov.cn",  // 海关总署
        "pbc.gov.cn",      // 人民银行
        "csindex.com.cn",  // 中证指数
        "chinaclear.cn",   // 中国结算
    ];

    /// <summary>白名单后缀快照，供诊断/文档使用。</summary>
    public static IReadOnlyList<string> All => Suffixes;

    /// <summary>主机是否属于官方源。</summary>
    public static bool IsAllowed(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var normalized = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (normalized.Length == 0)
        {
            return false;
        }

        foreach (var suffix in Suffixes)
        {
            if (normalized.Equals(suffix, StringComparison.Ordinal))
            {
                return true;
            }

            if (normalized.EndsWith("." + suffix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
