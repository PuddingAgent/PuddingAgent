namespace PuddingRetrieval.Contracts;

/// <summary>
/// 符号种类（跨 provider 的 retrieval 契约需要它，但不该因此依赖 CodeIndex）。
/// <para>
/// ⚠️ <b>过渡状态</b>：本枚举此刻与 <c>PuddingCodeIndex.Contracts.CodeSymbolKind</c> <b>并存</b>，
/// 两者必须逐成员、逐数值一致（由 <c>PuddingRetrieval.ContractsTests</c> 锁住）。
/// 迁移步骤 2 删除 CodeIndex 侧定义后，这里成为唯一真源。
/// </para>
/// <para>
/// 数值顺序是**持久化契约**：CodeSymbols.Kind 按整数存储，调整顺序会让已有索引数据含义错位。
/// 因此新增成员只能追加到末尾。
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
