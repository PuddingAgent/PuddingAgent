using System.Globalization;

namespace PuddingFinance.Biying;

/// <summary>
/// 行情工具：把"研究动作"翻译成端点 + 参数 + 字段投影。
///
/// 为什么需要这一层：端点 id（54/253/314）对模型毫无意义，而字段投影（只留 16 个键）
/// 决定了回执体积。让模型直接面对 200 个端点和 21 个原始键，它会既选错又撑爆上下文。
/// </summary>
public sealed class BiyingMarketTool(BiyingClient client)
{
    private readonly BiyingClient _client = client;

    /// <summary>行情快照投影：价格、量、估值三组，够做初筛与估值，不含冷门字段。</summary>
    internal static readonly IReadOnlyCollection<string> QuoteFields =
    [
        "t", "p", "pc", "ud", "o", "h", "l", "yc", "v", "cje", "hs", "pe", "sjl", "sz", "lt", "zf",
    ];

    internal static readonly IReadOnlyCollection<string> IndexFields =
    [
        "t", "o", "h", "l", "c", "v", "a", "pc",
    ];

    internal static readonly IReadOnlyCollection<string> KlineFields =
    [
        "t", "o", "h", "l", "c", "v", "a", "pc",
    ];

    public async Task<BiyingToolResult> QueryAsync(BiyingMarketRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (endpointId, pathArgs, query, fields) = Resolve(request);

        var call = await _client.CallAsync(endpointId, pathArgs, query, cancellationToken).ConfigureAwait(false);
        var shaped = BiyingShaper.Shape(_client.Catalog.Get(endpointId), call.Body, new BiyingShapeOptions
        {
            MaxRows = request.Limit <= 0 ? 20 : request.Limit,
            Fields = fields,
        });

        return new BiyingToolResult(request.View.ToString(), shaped.Json, call.FromCache,
            call.RemainingQuota, call.QuotaWarning);
    }

    private static (string EndpointId, List<string> PathArgs, Dictionary<string, string> Query, IReadOnlyCollection<string> Fields)
        Resolve(BiyingMarketRequest request)
    {
        switch (request.View)
        {
            case BiyingMarketView.Quote:
                RequireCode(request.Code, "实时快照");
                return ("54", [NormalizeCode(request.Code!)], [], QuoteFields);

            case BiyingMarketView.Index:
                RequireCode(request.Code, "指数快照");
                // 253 实测需要两个路径参数：指数代码.市场 与 分时级别（d=日线）
                return ("253", [RequireMarketSuffix(request.Code!, "指数快照"), "d"], [], IndexFields);

            case BiyingMarketView.Kline:
                RequireCode(request.Code, "历史 K 线");
                var query = new Dictionary<string, string>();
                if (!string.IsNullOrWhiteSpace(request.StartDate))
                    query["st"] = NormalizeDate(request.StartDate!, "st");
                if (!string.IsNullOrWhiteSpace(request.EndDate))
                    query["et"] = NormalizeDate(request.EndDate!, "et");
                query["lt"] = (request.Limit <= 0 ? 20 : request.Limit).ToString(CultureInfo.InvariantCulture);
                // 314 的第二个占位符是除权方式：默认 n（不复权），避免默认给复权价误导估值
                return ("314", [RequireMarketSuffix(request.Code!, "历史 K 线"), "n"], query, KlineFields);

            default:
                throw new BiyingEndpointException($"未知的行情 view：{request.View}。可用：Quote / Index / Kline。");
        }
    }

    private static void RequireCode(string? code, string action)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new BiyingEndpointException($"{action}必须提供 code（如 000001）。");
    }

    /// <summary>
    /// 券商类端点要 <c>000001.SZ</c> 形式；公开端点（54）实测接受纯 6 位。
    /// 这里保持调用方传入，只做去空格——**不擅自补市场后缀**，因为深市 000001 与
    /// 上证指数 000001 是两回事，猜错会返回另一只标的。
    /// </summary>
    private static string NormalizeCode(string code) => code.Trim();

    private static string RequireMarketSuffix(string code, string action)
    {
        var trimmed = code.Trim();
        return trimmed.Contains('.', StringComparison.Ordinal)
            ? trimmed
            : throw new BiyingEndpointException(
                $"{action}需要带市场后缀的代码（如 000001.SZ / 000001.SH），收到「{trimmed}」。" +
                "补错后缀会拿到另一只标的的数据，所以这里不做猜测。");
    }

    private static string NormalizeDate(string date, string parameter)
    {
        var trimmed = date.Trim();
        if (trimmed.Length == 8 && trimmed.All(char.IsAsciiDigit))
            return trimmed;
        throw new BiyingEndpointException($"参数 {parameter} 需要 yyyyMMdd 形式，收到「{date}」。");
    }
}
