using System.Text.Json;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingFinance.Biying;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// 必盈数据源的宿主侧运行时（单例）：负责**凭据读取 + 连接复用 + 配额/缓存跨调用持续**。
///
/// 为什么必须单例：配额是日累计的、缓存是进程内 LRU。如果每次工具调用都新建 client，
/// 配额会永远显示"没用过"，缓存也永不命中——两者都会静默失效，而且不会报错。
///
/// 凭据来源：<c>{DataRoot}/config/biying.api.json</c>（不进仓库）。
/// 缺少配置时**不抛异常**，而是返回可读错误：工具层要能把这个错误原样回给模型，
/// 让模型知道"是配置没配"而不是"端点不存在"。
/// </summary>
public sealed class BiyingFinanceRuntime : IDisposable
{
    private const string ConfigFileName = "biying.api.json";

    private readonly PuddingDataPaths _paths;
    private readonly Lazy<RuntimeState> _state;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public BiyingFinanceRuntime(PuddingDataPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _state = new Lazy<RuntimeState>(CreateState, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>配置文件的绝对路径（诊断与回执用；不含凭据）。</summary>
    public string ConfigPath => _paths.SystemConfigFile(ConfigFileName);

    public RuntimeState Resolve() => _state.Value;

    private RuntimeState CreateState()
    {
        try
        {
            if (!File.Exists(ConfigPath))
                return RuntimeState.Unavailable($"必盈配置缺失：{ConfigPath}。请在该文件写入 licence 后再调用。");

            using var stream = File.OpenRead(ConfigPath);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;

            if (!root.TryGetProperty("licence", out var licenceElement)
                || string.IsNullOrWhiteSpace(licenceElement.GetString()))
                return RuntimeState.Unavailable($"必盈配置缺少 licence 字段：{ConfigPath}。");

            var options = new BiyingOptions
            {
                Licence = licenceElement.GetString()!.Trim(),
                DailyQuota = ReadInt(root, "dailyQuota", 10_000),
                QuotaWarnRatio = ReadDouble(root, "quotaWarnRatio", 0.8d),
            };

            var catalog = BiyingEndpointCatalog.LoadEmbedded();
            var client = new BiyingClient(_http, catalog, options);
            return new RuntimeState(
                new BiyingMarketTool(client),
                new BiyingFinancialTool(client),
                new BiyingSectorTool(client),
                new BiyingFlowTool(client),
                client,
                null);
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException)
        {
            // 配置坏了也要给出可行动信息，而不是让工具直接炸掉
            return RuntimeState.Unavailable($"必盈配置无法解析：{ConfigPath} — {exception.Message}");
        }
    }

    private static int ReadInt(JsonElement root, string name, int fallback)
        => root.TryGetProperty(name, out var element) && element.TryGetInt32(out var value) ? value : fallback;

    private static double ReadDouble(JsonElement root, string name, double fallback)
        => root.TryGetProperty(name, out var element) && element.TryGetDouble(out var value) ? value : fallback;

    public void Dispose() => _http.Dispose();

    /// <summary>解析后的运行时状态：要么四个工具都可用，要么带着一条可读的不可用原因。</summary>
    public sealed record RuntimeState(
        BiyingMarketTool? Market,
        BiyingFinancialTool? Financial,
        BiyingSectorTool? Sector,
        BiyingFlowTool? Flow,
        BiyingClient? Client,
        string? Error)
    {
        public bool IsAvailable => Error is null && Market is not null && Financial is not null
                                   && Sector is not null && Flow is not null;

        public static RuntimeState Unavailable(string error) => new(null, null, null, null, null, error);
    }
}

/// <summary>
/// 必盈行情工具：实时快照 / 指数快照 / 历史 K 线。
///
/// 回执里带 <c>remainingQuota</c>，让模型和人都能看见"今天还能查几次"——
/// 配额耗尽后所有查询都会失败，提前看见比事后排查便宜。
/// </summary>
[Tool(
    id: "biying_market",
    name: "必盈行情",
    description: "查询 A 股行情：view=quote 单只股票实时快照（含 PE/PB/总市值/流通市值，可直接用于估值）、index 指数快照（需 000001.SH 形式）、kline 历史 K 线（需 000001.SZ 形式的代码，默认不复权）。【何时用】看公司当前估值分位、判断信息是否已被计价、拿一段价格序列做位置判断。【怎么用】view 必填；quote/index/kline 都需要 code；kline 可传 startDate/endDate（yyyyMMdd）与 limit。【坑】代码缺市场后缀会直接报错而不是猜——000001 在深市是平安银行、在上证是上证指数，猜错会拿到另一只标的；K 线默认不复权，复权会让历史高点看起来比实际低。返回里的 legend 是本批字段的中文解释，多余的键已被剔除。",
    category: ToolCategory.Query,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe,
    SortOrder = 61)]
public sealed class BiyingMarketQueryTool(BiyingFinanceRuntime runtime) : PuddingToolBase<BiyingMarketArgs>
{
    private readonly BiyingFinanceRuntime _runtime = runtime;

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        BiyingMarketArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var state = _runtime.Resolve();
        if (!state.IsAvailable)
            return ToolExecutionResult.Fail(state.Error!);

        if (!TryParseView(args.View, out var view))
        {
            return ToolExecutionResult.Fail(
                $"未知的 view「{args.View}」。可用：quote（单只实时）、index（指数）、kline（历史 K 线）。");
        }

        try
        {
            var result = await state.Market!.QueryAsync(new BiyingMarketRequest
            {
                View = view,
                Code = args.Code,
                StartDate = args.StartDate,
                EndDate = args.EndDate,
                Limit = args.Limit ?? 20,
            }, ct).ConfigureAwait(false);

            return ToolExecutionResult.Ok(WithQuota(result));
        }
        catch (BiyingEndpointException exception)
        {
            // 参数/端点用法错误：直接回给模型，它可以自己改参数重试
            return ToolExecutionResult.Fail(exception.Message);
        }
        catch (BiyingCallException exception)
        {
            return ToolExecutionResult.Fail(
                exception.IsRetryable
                    ? $"{exception.Message}（可稍后重试）"
                    : exception.Message);
        }
    }

    private static bool TryParseView(string? value, out BiyingMarketView view)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "quote": view = BiyingMarketView.Quote; return true;
            case "index": view = BiyingMarketView.Index; return true;
            case "kline": view = BiyingMarketView.Kline; return true;
            default: view = default; return false;
        }
    }

    /// <summary>把配额余量补进回执：整形层负责数据，配额属于工具层的交付信息。</summary>
    internal static string WithQuota(BiyingToolResult result)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(result.Json)?.AsObject();
        if (node is null)
            return result.Json;
        node["remainingQuota"] = result.RemainingQuota;
        if (result.FromCache)
            node["fromCache"] = true;
        if (result.QuotaWarning)
            node["quotaWarning"] = "今日配额已用超过 80%";
        return node.ToJsonString();
    }
}

/// <summary>必盈行情参数。</summary>
public sealed record BiyingMarketArgs
{
    /// <summary>quote / index / kline。</summary>
    public string? View { get; init; }

    /// <summary>股票或指数代码；kline/index 需带市场后缀（000001.SZ / 000001.SH）。</summary>
    public string? Code { get; init; }

    /// <summary>K 线起始日 yyyyMMdd。</summary>
    public string? StartDate { get; init; }

    /// <summary>K 线结束日 yyyyMMdd。</summary>
    public string? EndDate { get; init; }

    /// <summary>返回条数上限（默认 20）。</summary>
    public int? Limit { get; init; }
}

/// <summary>
/// 必盈财务工具：三表 / 财务指标 / 分红。
///
/// 三表**不做字段投影**（砍字段会看不到"其他应收款异常膨胀"这类排雷信号），
/// 控制体积靠只取最近 N 期；财务指标则投影到 23 个核心项。
/// </summary>
[Tool(
    id: "biying_financial",
    name: "必盈财务",
    description: "查询 A 股财务数据：view=balance 资产负债表 / income 利润表 / cashflow 现金流量表（三表保留全部字段，只取最近 years 期）/ indicators 财务指标（投影到盈利·成长·偿债·运营四组核心项）/ dividend 近年分红。【何时用】财报穿透与财务排雷：收入质量、利润现金化、存货与应收异常、商誉与资本化、分红连续性。【怎么用】view 与 code 必填，code 用 6 位代码（如 000001）；years 默认 5，控制返回期数。【坑】返回里的 total/returned/truncated 是**显式截断标记**——truncated=true 表示公司还有更早的期数，不要当成「只有这几期」；legend 给出本批字段的中文解释。",
    category: ToolCategory.Query,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe,
    SortOrder = 62)]
public sealed class BiyingFinancialQueryTool(BiyingFinanceRuntime runtime) : PuddingToolBase<BiyingFinancialArgs>
{
    private readonly BiyingFinanceRuntime _runtime = runtime;

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        BiyingFinancialArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var state = _runtime.Resolve();
        if (!state.IsAvailable)
            return ToolExecutionResult.Fail(state.Error!);

        if (!TryParseView(args.View, out var view))
        {
            return ToolExecutionResult.Fail(
                $"未知的 view「{args.View}」。可用：balance、income、cashflow、indicators、dividend。");
        }

        if (string.IsNullOrWhiteSpace(args.Code))
            return ToolExecutionResult.Fail("必须提供 code（6 位股票代码，如 000001）。");

        try
        {
            var result = await state.Financial!.QueryAsync(new BiyingFinancialRequest
            {
                View = view,
                Code = args.Code,
                Years = args.Years ?? 5,
            }, ct).ConfigureAwait(false);

            return ToolExecutionResult.Ok(BiyingMarketQueryTool.WithQuota(result));
        }
        catch (BiyingEndpointException exception)
        {
            return ToolExecutionResult.Fail(exception.Message);
        }
        catch (BiyingCallException exception)
        {
            return ToolExecutionResult.Fail(
                exception.IsRetryable ? $"{exception.Message}（可稍后重试）" : exception.Message);
        }
    }

    private static bool TryParseView(string? value, out BiyingFinancialView view)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "balance": view = BiyingFinancialView.Balance; return true;
            case "income": view = BiyingFinancialView.Income; return true;
            case "cashflow": view = BiyingFinancialView.Cashflow; return true;
            case "indicators": view = BiyingFinancialView.Indicators; return true;
            case "dividend": view = BiyingFinancialView.Dividend; return true;
            default: view = default; return false;
        }
    }
}

/// <summary>必盈财务参数。</summary>
public sealed record BiyingFinancialArgs
{
    /// <summary>balance / income / cashflow / indicators / dividend。</summary>
    public string? View { get; init; }

    /// <summary>6 位股票代码。</summary>
    public string? Code { get; init; }

    /// <summary>返回最近几期（默认 5）。</summary>
    public int? Years { get; init; }
}
