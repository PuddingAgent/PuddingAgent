namespace PuddingCodeIndex.Contracts;

public enum CodeSymbolKind
{
    Unknown,
    Namespace,
    Type,
    Struct,
    Class,
    Interface,
    Enum,
    Delegate,
    Method,
    Constructor,
    Property,
    Field,
    Event,
    Parameter,
    Local,
    Variable,
    Constant,
    Operator,
}

public sealed record CodeSymbolRecord(
    string WorkspaceId,
    string ProjectId,
    string FilePath,
    string SymbolId,
    string Name,
    CodeSymbolKind Kind,
    int StartLine,
    int EndLine,
    string? Signature = null,
    string? Container = null);

/// <summary>
/// ADR-089 §2.3：符号检索的**匹配域**（七个正交过滤面之一）。
/// 修复前匹配域恒为三列全开，于是"搜类名却返回一堆构造器"——构造器的 Name 是 `.ctor`，
/// 命中实际发生在 Signature 列（其签名里含该词），调用方**无法表达**"只关注符号名"。
/// 本枚举让匹配域显式可控；默认值 <see cref="All"/> 与修复前逐字一致（既有调用零行为变化）。
/// </summary>
[Flags]
public enum CodeSymbolMatchTarget
{
    None = 0,
    Name = 1,
    Signature = 2,
    Container = 4,

    /// <summary>默认：三列全开（保持既有召回；要聚焦符号名请显式传 Name）。</summary>
    All = Name | Signature | Container,
}

public sealed record CodeSymbolSearchRequest(
    string WorkspaceId,
    string Query,
    string? ProjectId = null,
    CodeSymbolKind? Kind = null,
    int Limit = 50,
    int Skip = 0,
    CodeSymbolMatchTarget MatchTarget = CodeSymbolMatchTarget.All,

    /// <summary>
    /// ADR-089 §2.3「文件类型」过滤面：只保留这些扩展名文件里的符号
    /// （如 <c>cs</c> / <c>.cs</c> / <c>ts</c>）。前导点可选、大小写不敏感；
    /// <c>null</c> 或空集合 = 不过滤（既有行为不变）。
    /// </summary>
    IReadOnlyList<string>? FileExtensions = null,

    /// <summary>
    /// 多值 kind 过滤（ADR-089 R6 前置）：<c>RetrievalIntent.Type</c> 需要
    /// Class/Interface/Struct/Enum/Delegate 五种、<c>RetrievalIntent.Member</c> 需要
    /// Method/Constructor/Property/Field/Event/Operator 六种，而 <see cref="Kind"/> 只能表达单值。
    /// <para>
    /// <b>语义</b>：非空时<b>优先于 <see cref="Kind"/></b>（两者同给以本项为准）；null 或空集合 =
    /// 回到 <see cref="Kind"/> 的既有语义。重复值会被去重。
    /// </para>
    /// </summary>
    IReadOnlyList<CodeSymbolKind>? Kinds = null);

public sealed record CodeSymbolDetail(
    CodeSymbolRecord Symbol,
    CodeFileRecord? File = null,
    string? DisplayName = null);
