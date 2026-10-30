namespace PuddingRetrieval.Contracts;

/// <summary>
/// 关系种类（跨 provider 的 retrieval 契约需要它，但不该因此依赖 CodeIndex）。
/// <para>
/// ⚠️ <b>过渡状态</b>：本枚举此刻与 <c>PuddingCodeIndex.Contracts.CodeRelationKind</c> <b>并存</b>，
/// 两者必须逐成员、逐数值一致（由 <c>PuddingRetrieval.ContractsTests</c> 锁住）。
/// </para>
/// <para>
/// 数值顺序是**持久化契约**：CodeRelations.Kind 按整数存储，调整顺序会让已有索引数据含义错位。
/// 新增成员只能追加到末尾。
/// </para>
/// </summary>
public enum CodeRelationKind
{
    Unknown = 0,
    Contains = 1,
    Calls = 2,
    References = 3,
    Inherits = 4,
    Implements = 5,
    Overrides = 6,
    Overloads = 7,
    Uses = 8,
}
