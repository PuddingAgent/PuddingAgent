namespace PuddingCodeIndex.Contracts;

/// <summary>
/// 关系种类 —— <b>跨层共享的值类型</b>（与 <see cref="CodeSymbolKind"/> 同理）。
/// <para>
/// 数值顺序是<b>持久化契约</b>：<c>CodeRelations.Kind</c> 按整数存储。新增成员只能追加。
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
