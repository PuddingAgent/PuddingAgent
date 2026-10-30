namespace PuddingCodeIndex.Contracts;

/// <summary>
/// 符号种类 —— <b>跨层共享的值类型</b>，不是检索契约特有的概念。
/// 由 CodeIndex（存储）、CodeIntelligence（抽取）、Runtime（工具）与测试共同使用，
/// 因此独立放在 PuddingDomain.Contracts，而不是塞进某个上层契约组件。
/// <para>
/// 数值顺序是<b>持久化契约</b>：<c>CodeSymbols.Kind</c> 按整数存储。调整成员顺序不会有任何
/// 编译错误，但已有索引库里的数据含义会整体错位（Class 被读成 Struct）。新增成员只能追加。
/// </para>
/// </summary>
public enum CodeSymbolKind
{
    Unknown = 0,
    Namespace = 1,
    Type = 2,
    Struct = 3,
    Class = 4,
    Interface = 5,
    Enum = 6,
    Delegate = 7,
    Method = 8,
    Constructor = 9,
    Property = 10,
    Field = 11,
    Event = 12,
    Parameter = 13,
    Local = 14,
    Variable = 15,
    Constant = 16,
    Operator = 17,
}
