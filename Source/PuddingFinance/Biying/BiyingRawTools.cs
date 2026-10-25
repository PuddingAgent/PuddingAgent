namespace PuddingFinance.Biying;

/// <summary>任意端点直通的请求。</summary>
public sealed record BiyingRawRequest
{
    /// <summary>注册表里的端点 ID（如 "255"）。**必须在白名单内**，不接受任意路径或 URL。</summary>
    public required string EndpointId { get; init; }

    /// <summary>按端点模板顺序填充的路径参数（证书位由客户端补齐）。</summary>
    public IReadOnlyList<string>? PathArgs { get; init; }

    /// <summary>查询串参数。键必须在端点自己声明的 Query 白名单里。</summary>
    public IReadOnlyDictionary<string, string>? Query { get; init; }

    /// <summary>只保留这些键（可选）。响应形状未知时，投影是最有效的收窄手段。</summary>
    public IReadOnlyCollection<string>? Fields { get; init; }

    /// <summary>关键词过滤（对超大静态表必需）。</summary>
    public string? Keyword { get; init; }

    /// <summary>返回行数上限。默认 3——比前四个工具更保守，因为 raw 的响应形状不可预知。</summary>
    public int Limit { get; init; } = 3;
}

/// <summary>
/// 任意端点直通，覆盖前四个工具之外的约 170 个端点（股东、分红、公告、行业财务、Pro 行情等）。
///
/// 为什么不给每个端点配一个工具：注册表有 200 个端点，前四个工具只覆盖约 30 个；
/// 每个都做成工具会把工具目录撑爆，模型也记不住该用哪个。
///
/// 三条安全边界，都不可省：
/// 1. <b>端点白名单</b>：只接受注册表里存在的 endpointId。这是 SSRF 边界——
///    若允许任意路径，模型就能让宿主去请求任意地址。
/// 2. <b>查询键白名单</b>：由 BiyingClient/BiyingUrlBuilder 在拼接 URL 前统一校验。
///    工具层不重复实现，避免两套错误措辞漂移。
/// 3. <b>整形不改</b>：raw 不等于原样吐回。单只股票资产负债表实测 229 KB，
///    原样返回等于一次查询毁掉一段上下文。
/// </summary>
public sealed class BiyingRawTool(BiyingClient client)
{
    private readonly BiyingClient _client = client;

    public async Task<BiyingToolResult> QueryAsync(BiyingRawRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var endpointId = (request.EndpointId ?? string.Empty).Trim();
        if (endpointId.Length == 0)
            throw new BiyingEndpointException("必须提供 endpointId（注册表里的端点编号，如 255）。");

        // 白名单：Get 找不到时会抛「未找到 + 候选建议」，不静默降级。
        var endpoint = _client.Catalog.Get(endpointId);

        // 查询键白名单**不在这里重复实现**：BiyingUrlBuilder 已在拼接前校验，
        // 而且它的错误消息更好（带端点名称与「允许的：…」集合）。
        // 校验集中在一处，避免两套措辞随版本漂移——那种不一致只能靠踩坑发现。
        var call = await _client.CallAsync(endpointId, request.PathArgs ?? [], request.Query, cancellationToken)
            .ConfigureAwait(false);

        var shaped = BiyingShaper.Shape(endpoint, call.Body, new BiyingShapeOptions
        {
            MaxRows = request.Limit <= 0 ? 3 : request.Limit,
            Fields = request.Fields,
            Keyword = request.Keyword,
        });

        return new BiyingToolResult($"raw:{endpoint.Id}", shaped.Json, call.FromCache,
            call.RemainingQuota, call.QuotaWarning);
    }
}