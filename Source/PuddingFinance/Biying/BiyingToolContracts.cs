namespace PuddingFinance.Biying;

/// <summary>行情类动作。按"研究动作"切分，不按 API 分组切。</summary>
public enum BiyingMarketView
{
    /// <summary>单只股票实时快照（含 PE/PB/市值，直接服务估值判断）。</summary>
    Quote,

    /// <summary>指数实时快照。</summary>
    Index,

    /// <summary>历史 K 线（Pro 端点）。</summary>
    Kline,
}

/// <summary>财务类动作。</summary>
public enum BiyingFinancialView
{
    Balance,
    Income,
    Cashflow,
    Indicators,
    Dividend,
}

public sealed record BiyingMarketRequest
{
    public required BiyingMarketView View { get; init; }

    /// <summary>股票/指数代码。K 线需要用 <c>000001.SZ</c> 这种带市场后缀的形式。</summary>
    public string? Code { get; init; }

    /// <summary>K 线起始日（yyyyMMdd），可选。</summary>
    public string? StartDate { get; init; }

    /// <summary>K 线结束日（yyyyMMdd），可选。</summary>
    public string? EndDate { get; init; }

    /// <summary>返回条数上限。</summary>
    public int Limit { get; init; } = 20;
}

public sealed record BiyingFinancialRequest
{
    public required BiyingFinancialView View { get; init; }

    public required string Code { get; init; }

    /// <summary>保留最近几期（默认 5 期，够看趋势又不爆上下文）。</summary>
    public int Years { get; init; } = 5;
}

/// <summary>工具层统一回执。带上配额余量，让模型和人都能看见"今天还能查几次"。</summary>
public sealed record BiyingToolResult(
    string View,
    string Json,
    bool FromCache,
    int RemainingQuota,
    bool QuotaWarning);
