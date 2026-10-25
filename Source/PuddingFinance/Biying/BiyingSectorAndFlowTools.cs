namespace PuddingFinance.Biying;

/// <summary>板块/概念类动作。</summary>
public enum BiyingSectorView
{
    /// <summary>指数、行业、概念树（全量树，配合 keyword 定位）。</summary>
    Tree,

    /// <summary>按板块代码找成分股。</summary>
    Constituents,

    /// <summary>按股票代码找所属板块。</summary>
    Membership,

    /// <summary>全市场股票列表——**必须带 keyword**，否则拒答（全量 250,566 字符）。</summary>
    StockList,
}

public enum BiyingFlowView
{
    /// <summary>龙虎榜每日详情。</summary>
    LhbDaily,

    /// <summary>龙虎榜个股上榜统计（近 N 日）。</summary>
    LhbStock,

    /// <summary>个股资金流向（按日）。</summary>
    StockFlow,

    /// <summary>个股指定日期的历史资金流向。</summary>
    StockFlowHistory,
}

public sealed record BiyingSectorRequest
{
    public required BiyingSectorView View { get; init; }

    /// <summary>板块代码（Constituents）或股票代码（Membership）。</summary>
    public string? Code { get; init; }

    /// <summary>关键词过滤。StockList 必填；Tree 强烈建议填。</summary>
    public string? Keyword { get; init; }

    public int Limit { get; init; } = 20;
}

public sealed record BiyingFlowRequest
{
    public required BiyingFlowView View { get; init; }

    /// <summary>股票代码（StockFlow / StockFlowHistory）。</summary>
    public string? Code { get; init; }

    /// <summary>交易日期 yyyyMMdd（StockFlowHistory 必填）。</summary>
    public string? Date { get; init; }

    /// <summary>近几日（LhbStock，默认 5）。</summary>
    public int Days { get; init; } = 5;

    public int Limit { get; init; } = 10;
}

/// <summary>
/// 板块/概念工具。
///
/// 全量股票列表（端点 1）实测 <b>250,566 字符</b>，行业概念树 178,716 字符——
/// 这两个端点**不能**在没有关键词的情况下返回，否则一次查询就能把上下文毁掉。
/// 所以 StockList 在缺 keyword 时直接拒答并说明原因，而不是"先给 5 行看看"。
/// </summary>
public sealed class BiyingSectorTool(BiyingClient client)
{
    private readonly BiyingClient _client = client;

    public async Task<BiyingToolResult> QueryAsync(BiyingSectorRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (endpointId, pathArgs, requireCode) = request.View switch
        {
            BiyingSectorView.Tree => ("4", Array.Empty<string>(), false),
            BiyingSectorView.Constituents => ("5", new[] { request.Code ?? string.Empty }, true),
            BiyingSectorView.Membership => ("6", new[] { request.Code ?? string.Empty }, true),
            BiyingSectorView.StockList => ("1", Array.Empty<string>(), false),
            _ => throw new BiyingEndpointException($"未知的板块 view：{request.View}。"),
        };

        if (requireCode && string.IsNullOrWhiteSpace(request.Code))
            throw new BiyingEndpointException($"{request.View} 必须提供 code。");

        // 超大静态表强制要求关键词：没有它就只能全量吐出，那是必然的上下文事故
        if (request.View == BiyingSectorView.StockList && string.IsNullOrWhiteSpace(request.Keyword))
        {
            throw new BiyingEndpointException(
                "StockList 必须提供 keyword（如「平安」或「000001」）。" +
                "全市场列表实测 250,566 字符，不能整表返回；不猜你要哪一行。");
        }

        var call = await _client.CallAsync(endpointId, pathArgs, null, cancellationToken).ConfigureAwait(false);
        var shaped = BiyingShaper.Shape(_client.Catalog.Get(endpointId), call.Body, new BiyingShapeOptions
        {
            MaxRows = request.Limit <= 0 ? 20 : request.Limit,
            Keyword = request.Keyword,
        });

        return new BiyingToolResult(request.View.ToString(), shaped.Json, call.FromCache,
            call.RemainingQuota, call.QuotaWarning);
    }
}

/// <summary>
/// 资金与龙虎榜工具（对应技能阶段 5：筹码与资金结构）。
///
/// 这些端点回答的是"谁在买"，而不是"公司值多少钱"——两者必须分开看：
/// 资金流入不等于基本面改善，反过来也一样。
/// </summary>
public sealed class BiyingFlowTool(BiyingClient client)
{
    private readonly BiyingClient _client = client;

    public async Task<BiyingToolResult> QueryAsync(BiyingFlowRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        string endpointId;
        string[] pathArgs;

        switch (request.View)
        {
            case BiyingFlowView.LhbDaily:
                endpointId = "78";
                pathArgs = [];
                break;

            case BiyingFlowView.LhbStock:
                endpointId = "79";
                pathArgs = [(request.Days <= 0 ? 5 : request.Days).ToString(System.Globalization.CultureInfo.InvariantCulture)];
                break;

            case BiyingFlowView.StockFlow:
                RequireCode(request.Code, request.View);
                endpointId = "273";
                pathArgs = [request.Code!.Trim()];
                break;

            case BiyingFlowView.StockFlowHistory:
                RequireCode(request.Code, request.View);
                if (string.IsNullOrWhiteSpace(request.Date))
                    throw new BiyingEndpointException("StockFlowHistory 必须提供 date（yyyyMMdd）。");
                endpointId = "303";
                pathArgs = [NormalizeDate(request.Date!), request.Code!.Trim()];
                break;

            default:
                throw new BiyingEndpointException(
                    $"未知的资金 view：{request.View}。可用：LhbDaily / LhbStock / StockFlow / StockFlowHistory。");
        }

        var call = await _client.CallAsync(endpointId, pathArgs, null, cancellationToken).ConfigureAwait(false);
        var shaped = BiyingShaper.Shape(_client.Catalog.Get(endpointId), call.Body, new BiyingShapeOptions
        {
            MaxRows = request.Limit <= 0 ? 10 : request.Limit,
        });

        return new BiyingToolResult(request.View.ToString(), shaped.Json, call.FromCache,
            call.RemainingQuota, call.QuotaWarning);
    }

    private static void RequireCode(string? code, BiyingFlowView view)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new BiyingEndpointException($"{view} 必须提供 code（如 000001）。");
    }

    private static string NormalizeDate(string date)
    {
        var trimmed = date.Trim();
        if (trimmed.Length == 8 && trimmed.All(char.IsAsciiDigit))
            return trimmed;
        throw new BiyingEndpointException($"date 需要 yyyyMMdd 形式，收到「{date}」。");
    }
}
