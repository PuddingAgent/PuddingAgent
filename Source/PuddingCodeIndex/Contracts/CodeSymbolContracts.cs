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
    CodeSymbolMatchTarget MatchTarget = CodeSymbolMatchTarget.All);

public sealed record CodeSymbolDetail(
    CodeSymbolRecord Symbol,
    CodeFileRecord? File = null,
    string? DisplayName = null);
