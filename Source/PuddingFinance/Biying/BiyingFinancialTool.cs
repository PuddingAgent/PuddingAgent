namespace PuddingFinance.Biying;

/// <summary>
/// 财务工具：年报穿透的主力（对应技能阶段 3）。
///
/// 三表**不做字段投影**——整张报表本身就是研究对象，砍字段会让模型看不到
/// "其他应收款异常膨胀"这类关键信号。控制体积靠**只取最近 N 期**（默认 5）而不是砍字段。
///
/// 财务指标反过来做投影：85 个指标一次给全没人看得过来，只留盈利/成长/偿债/运营四组。
/// </summary>
public sealed class BiyingFinancialTool(BiyingClient client)
{
    private readonly BiyingClient _client = client;

    /// <summary>
    /// 指标投影：盈利（每股收益/ROE/毛利/净利率）、成长（收入/利润/净资产/总资产增速）、
    /// 偿债（资产负债率/流动/速动）、运营（应收/存货/总资产周转）。这些正好对应技能阶段 3 的核查表。
    /// </summary>
    internal static readonly IReadOnlyCollection<string> IndicatorFields =
    [
        "date",
        "mgsy", "kfmg", "mgjz", "jzsy", "kflr",
        "xsml", "xsjl", "yylr",
        "zysr", "jlzz", "jzzz", "zzzz",
        "zcfzl", "ldbl", "sdbl",
        "yszz", "yszzt", "chzz", "chzzl", "zzzzl",
        "zzc",
    ];

    public async Task<BiyingToolResult> QueryAsync(BiyingFinancialRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Code))
            throw new BiyingEndpointException("财务查询必须提供 code（如 000001）。");

        var (endpointId, fields) = Resolve(request.View);
        var code = NormalizeCode(request.Code);
        var rows = request.Years <= 0 ? 5 : request.Years;

        var call = await _client.CallAsync(endpointId, [code], null, cancellationToken).ConfigureAwait(false);
        var shaped = BiyingShaper.Shape(_client.Catalog.Get(endpointId), call.Body, new BiyingShapeOptions
        {
            MaxRows = rows,
            Fields = fields,
        });

        return new BiyingToolResult(request.View.ToString(), shaped.Json, call.FromCache,
            call.RemainingQuota, call.QuotaWarning);
    }

    internal static (string EndpointId, IReadOnlyCollection<string>? Fields) Resolve(BiyingFinancialView view) => view switch
    {
        BiyingFinancialView.Balance => ("255", null),
        BiyingFinancialView.Income => ("256", null),
        BiyingFinancialView.Cashflow => ("257", null),
        BiyingFinancialView.Indicators => ("44", IndicatorFields),
        BiyingFinancialView.Dividend => ("38", null),
        _ => throw new BiyingEndpointException($"未知的财务 view：{view}。可用：Balance / Income / Cashflow / Indicators / Dividend。"),
    };

    /// <summary>
    /// 只去空格，**不补市场后缀**：财务端点实测接受 6 位纯代码；
    /// 而 000001 在深市是平安银行、在上证是上证指数，猜后缀等于换标的。
    /// </summary>
    private static string NormalizeCode(string code) => code.Trim();
}
