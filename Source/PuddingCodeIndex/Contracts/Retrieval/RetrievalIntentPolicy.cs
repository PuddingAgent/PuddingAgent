using PuddingCodeIndex.Contracts;

namespace PuddingCodeIndex.Contracts.Retrieval;

/// <summary>
/// intent → 过滤 + 层序 的**成文表**（ADR-089 §2 表 + §2.1 反向要求）。
/// <para>
/// 这张表是本设计的核心可解释性来源：任何"这个文件为什么排前面"都要能落回
/// <see cref="LayerOrder"/> 的一行。因此它必须是**数据**（可被测试逐条比对），
/// 而不是散落在引擎里的 if —— 本刀只冻结表，不实现引擎。
/// </para>
/// <para>
/// <b>反向要求（显式意图优先）</b>：Agent 显式给出 intent 时，引擎不得擅自改变分层顺序或覆盖其过滤；
/// <see cref="LayerOrder"/> 对显式 intent 返回的就是它自己的顺序，
/// <see cref="RetrievalHitComparer.ForIntent"/> 必须照用（契约测试 A7）。
/// </para>
/// </summary>
public static class RetrievalIntentPolicy
{
    /// <summary>六层 + Unknown 的**规范层序**（Auto 与 Any 使用）：声明 &gt; 实现 &gt; 引用 &gt; 调用 &gt; 注释 &gt; 正文。</summary>
    private static readonly RetrievalHitKind[] CanonicalLayers =
    [
        RetrievalHitKind.Declaration,
        RetrievalHitKind.Implementation,
        RetrievalHitKind.Reference,
        RetrievalHitKind.CallSite,
        RetrievalHitKind.DocMention,
        RetrievalHitKind.TextHit,
        RetrievalHitKind.Unknown,
    ];

    /// <summary>文件面：路径 / 文本命中为主，符号层靠后。</summary>
    private static readonly RetrievalHitKind[] FileLayers =
    [
        RetrievalHitKind.TextHit,
        RetrievalHitKind.DocMention,
        RetrievalHitKind.Declaration,
        RetrievalHitKind.Implementation,
        RetrievalHitKind.Reference,
        RetrievalHitKind.CallSite,
        RetrievalHitKind.Unknown,
    ];

    /// <summary>命名空间面：声明优先，其余按引用 / 文档 / 正文。</summary>
    private static readonly RetrievalHitKind[] NamespaceLayers =
    [
        RetrievalHitKind.Declaration,
        RetrievalHitKind.Reference,
        RetrievalHitKind.DocMention,
        RetrievalHitKind.Implementation,
        RetrievalHitKind.CallSite,
        RetrievalHitKind.TextHit,
        RetrievalHitKind.Unknown,
    ];

    /// <summary>类型面（设计表）：声明 &gt; 实现 &gt; 引用。</summary>
    private static readonly RetrievalHitKind[] TypeLayers =
    [
        RetrievalHitKind.Declaration,
        RetrievalHitKind.Implementation,
        RetrievalHitKind.Reference,
        RetrievalHitKind.DocMention,
        RetrievalHitKind.CallSite,
        RetrievalHitKind.TextHit,
        RetrievalHitKind.Unknown,
    ];

    /// <summary>成员面（设计表）：声明 &gt; 调用 &gt; 引用。</summary>
    private static readonly RetrievalHitKind[] MemberLayers =
    [
        RetrievalHitKind.Declaration,
        RetrievalHitKind.CallSite,
        RetrievalHitKind.Reference,
        RetrievalHitKind.Implementation,
        RetrievalHitKind.DocMention,
        RetrievalHitKind.TextHit,
        RetrievalHitKind.Unknown,
    ];

    /// <summary>注释面（设计表）：文档注释 &gt; 行内注释 &gt; 正文。</summary>
    private static readonly RetrievalHitKind[] CommentLayers =
    [
        RetrievalHitKind.DocMention,
        RetrievalHitKind.TextHit,
        RetrievalHitKind.Declaration,
        RetrievalHitKind.Reference,
        RetrievalHitKind.Implementation,
        RetrievalHitKind.CallSite,
        RetrievalHitKind.Unknown,
    ];

    /// <summary>实现面（设计表）：实现体 &gt; 声明。</summary>
    private static readonly RetrievalHitKind[] ImplementationLayers =
    [
        RetrievalHitKind.Implementation,
        RetrievalHitKind.Declaration,
        RetrievalHitKind.Reference,
        RetrievalHitKind.CallSite,
        RetrievalHitKind.DocMention,
        RetrievalHitKind.TextHit,
        RetrievalHitKind.Unknown,
    ];

    /// <summary>引用面（设计表）：引用处优先（按引用计数）。</summary>
    private static readonly RetrievalHitKind[] ReferenceLayers =
    [
        RetrievalHitKind.Reference,
        RetrievalHitKind.CallSite,
        RetrievalHitKind.Implementation,
        RetrievalHitKind.Declaration,
        RetrievalHitKind.DocMention,
        RetrievalHitKind.TextHit,
        RetrievalHitKind.Unknown,
    ];

    /// <summary>关系子图面（Callers / Callees / Impact 共用）：调用层优先（"距离升序"本刀不表达）。</summary>
    private static readonly RetrievalHitKind[] RelationTraversalLayers =
    [
        RetrievalHitKind.CallSite,
        RetrievalHitKind.Reference,
        RetrievalHitKind.Implementation,
        RetrievalHitKind.Declaration,
        RetrievalHitKind.DocMention,
        RetrievalHitKind.TextHit,
        RetrievalHitKind.Unknown,
    ];

    private static readonly IReadOnlyDictionary<RetrievalIntent, RetrievalHitKind[]> LayerOrders =
        new Dictionary<RetrievalIntent, RetrievalHitKind[]>
        {
            [RetrievalIntent.Auto] = CanonicalLayers,
            [RetrievalIntent.Any] = CanonicalLayers,
            [RetrievalIntent.File] = FileLayers,
            [RetrievalIntent.Namespace] = NamespaceLayers,
            [RetrievalIntent.Type] = TypeLayers,
            [RetrievalIntent.Member] = MemberLayers,
            [RetrievalIntent.Comment] = CommentLayers,
            [RetrievalIntent.Implementation] = ImplementationLayers,
            [RetrievalIntent.References] = ReferenceLayers,
            [RetrievalIntent.Callers] = RelationTraversalLayers,
            [RetrievalIntent.Callees] = RelationTraversalLayers,
            [RetrievalIntent.Impact] = RelationTraversalLayers,
        };

    private static readonly IReadOnlyList<CodeSymbolKind> NamespaceKinds = [CodeSymbolKind.Namespace];

    private static readonly IReadOnlyList<CodeSymbolKind> TypeKinds =
    [
        CodeSymbolKind.Class,
        CodeSymbolKind.Interface,
        CodeSymbolKind.Struct,
        CodeSymbolKind.Enum,
        CodeSymbolKind.Delegate,
        CodeSymbolKind.Type,
    ];

    private static readonly IReadOnlyList<CodeSymbolKind> MemberKinds =
    [
        CodeSymbolKind.Method,
        CodeSymbolKind.Constructor,
        CodeSymbolKind.Property,
        CodeSymbolKind.Field,
        CodeSymbolKind.Event,
        CodeSymbolKind.Operator,
    ];

    private static readonly IReadOnlyList<RetrievalRelationKind> ImplementationRelations =
        [RetrievalRelationKind.Implements, RetrievalRelationKind.Overrides];

    private static readonly IReadOnlyList<RetrievalRelationKind> ReferenceRelations =
    [
        RetrievalRelationKind.References,
        RetrievalRelationKind.Calls,
        RetrievalRelationKind.Inherits,
        RetrievalRelationKind.Imports,
    ];

    private static readonly IReadOnlyList<RetrievalRelationKind> CallRelations = [RetrievalRelationKind.Calls];

    private static readonly IReadOnlyList<RetrievalRelationKind> ImpactRelations =
    [
        RetrievalRelationKind.Calls,
        RetrievalRelationKind.References,
        RetrievalRelationKind.Inherits,
        RetrievalRelationKind.Implements,
    ];

    /// <summary>intent → 层序（成文表）。未定义取值 ⇒ 退回规范层序（即 Auto 的行为），不抛异常。</summary>
    public static IReadOnlyList<RetrievalHitKind> LayerOrder(RetrievalIntent intent) =>
        LayerOrders.TryGetValue(intent, out var order) ? order : CanonicalLayers;

    /// <summary>是否"不问意图"（缺省）。</summary>
    public static bool IsAuto(RetrievalIntent intent) => intent == RetrievalIntent.Auto;

    /// <summary>是否显式给出意图（显式意图不得被 Auto 逻辑改写）。</summary>
    public static bool IsExplicit(RetrievalIntent intent) => intent != RetrievalIntent.Auto;

    /// <summary>
    /// 该 intent 是否属于"天然命中很多且完全正当"（ADR-089 §2.2 表）：
    /// 符号明确、关系单一的关系子图查询 ⇒ 过载判定**必须豁免**，
    /// 否则会把正当结果误报成"查询过泛"（契约测试 A10 的反向对照）。
    /// </summary>
    public static bool IsLegitimateBulkIntent(RetrievalIntent intent) => intent
        is RetrievalIntent.References
        or RetrievalIntent.Callers
        or RetrievalIntent.Callees
        or RetrievalIntent.Impact;

    /// <summary>intent → 符号种类过滤（null = 不按符号种类过滤）。复用既有 <c>CodeSymbolKind</c>，不重造。</summary>
    public static IReadOnlyList<CodeSymbolKind>? SymbolKindFilter(RetrievalIntent intent) => intent switch
    {
        RetrievalIntent.Namespace => NamespaceKinds,
        RetrievalIntent.Type => TypeKinds,
        RetrievalIntent.Member => MemberKinds,
        _ => null,
    };

    /// <summary>intent → 关系过滤（null = 不按关系过滤）。</summary>
    public static IReadOnlyList<RetrievalRelationKind>? RelationFilter(RetrievalIntent intent) => intent switch
    {
        RetrievalIntent.Implementation => ImplementationRelations,
        RetrievalIntent.References => ReferenceRelations,
        RetrievalIntent.Callers => CallRelations,
        RetrievalIntent.Callees => CallRelations,
        RetrievalIntent.Impact => ImpactRelations,
        _ => null,
    };
}
