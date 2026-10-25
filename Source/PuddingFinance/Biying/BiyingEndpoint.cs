namespace PuddingFinance.Biying;

/// <summary>端点模板中的一段：固定字面量、调用方填充的占位符，或证书位。</summary>
public enum BiyingSegmentKind
{
    /// <summary>固定字面量，原样出现在路径里（如 <c>hsstock</c>）。</summary>
    Literal,

    /// <summary>由调用方按顺序填充的占位符（如「股票代码」「除权方式」）。</summary>
    Placeholder,

    /// <summary>证书位。永远是路径最后一段，但**不硬编码**这一假设。</summary>
    Licence,
}

public sealed record BiyingSegment(BiyingSegmentKind Kind, string Value);

/// <summary>返回字段词典的一条。原始键是拼音短键（jzrq/plrq），没有词典模型只能猜。</summary>
public sealed record BiyingField(string Key, string Type, string Description);

/// <summary>注册表里的一个接口。字段顺序与 <c>endpoints.json</c> 一致，构造期一次成型、之后只读。</summary>
public sealed record BiyingEndpoint(
    string Id,
    string Name,
    string Category,
    string SubCategory,
    string Host,
    string Path,
    string Template,
    IReadOnlyList<string> Query,
    bool NeedsCode,
    IReadOnlyList<BiyingField> Fields)
{
    /// <summary>模板按 <c>/</c> 切好并分型，避免每次调用都重新解析。</summary>
    public IReadOnlyList<BiyingSegment> Segments { get; init; } = [];

    /// <summary>需要调用方填充的路径参数个数（不含证书位）。</summary>
    public int PlaceholderCount => Segments.Count(segment => segment.Kind == BiyingSegmentKind.Placeholder);

    /// <summary>展示用：<c>313 最新分时交易（Pro版）</c>。</summary>
    public override string ToString() => $"{Id} {Name}";
}

/// <summary>
/// 端点用法错误（参数个数不对、查询键不在注册表里、端点不存在）。
/// 刻意做成可读的异常而不是返回 null：这类错误都要以「告诉调用方该给什么」的形式回给模型，
/// 静默降级只会让模型换一个端点继续瞎试。
/// </summary>
public sealed class BiyingEndpointException(string message) : Exception(message);
