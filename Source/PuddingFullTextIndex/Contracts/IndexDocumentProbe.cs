namespace PuddingFullTextIndex.Contracts;

/// <summary>
/// 「语料根 → 本索引根下的索引目录」的**文档数探针**结果（A22a R1）。
/// <para>
/// 三态必须可区分 —— 这正是 2026-09-25 生产事故的分野：一次只含 0/99 文档的 staged 构建
/// <b>通过了体积闸门</b>并被原子切换，静默替换掉约 98 MB 的 live 满索引
/// （详见 <c>Docs</c> 侧事故记录与 <c>temp/a22a-swap-regression-gate-task.md</c>）：
/// <list type="bullet">
/// <item><see cref="Exists"/>=<c>false</c>：索引目录<b>不存在</b>（全新 scope 的首次构建前）。</item>
/// <item><see cref="Exists"/>=<c>true</c> 且 <see cref="Documents"/>=<c>0</c>：目录存在<b>且可读</b>，里面确实 0 篇文档。</item>
/// <item><see cref="Exists"/>=<c>true</c> 且 <see cref="Documents"/>=<c>null</c>：目录存在但**读不出**文档数
/// （索引损坏/无索引结构/读取抛异常）—— <b>不得伪报 0</b>，因为「读不出」与「确实是 0」在判据上完全不同：
/// 前者不能作为「staging 是空的」的证据，也不能作为「live 是空的」的依据。</item>
/// </list>
/// </para>
/// </summary>
/// <param name="Exists">索引目录是否存在。</param>
/// <param name="Documents">文档数；<c>null</c> = 目录存在但读不出（**不是** 0）。</param>
public readonly record struct IndexDocumentProbe(bool Exists, long? Documents)
{
    /// <summary>目录存在且文档数已读出（<c>0</c> 也算已读出）。</summary>
    public bool IsReadable => Exists && Documents is not null;

    /// <summary>单行摘要（进消息与断言；不用区域性数字格式，避免消息随区域变化）。</summary>
    public override string ToString() =>
        Exists
            ? string.Concat("exists, docs=", Documents?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "<null>")
            : "missing";
}
