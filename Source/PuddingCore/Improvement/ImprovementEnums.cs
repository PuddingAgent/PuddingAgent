namespace PuddingCode.Improvement;

/// <summary>
/// 可被改进的资产种类：L3 落点层用**统一句柄**回答「改的是哪一份既有资产」。
/// </summary>
/// <remarks>
/// <para>
/// 为什么需要这一层枚举：在它之前，技能与记忆是**两条互不相通的通道**
/// （技能走 <c>IAgentSkillEvolutionStore</c>、记忆走 <c>IMemoryLibrary</c>），
/// 两者用**不同类型的 id** 标识目标，因此**无法用同一份提案描述**「更新这个技能」与「更新这条记忆」。
/// </para>
/// <para>
/// 序列化兼容：<see cref="Unknown"/> 必须保持数值 0（字段缺省反序列化值）；
/// 新增成员只允许**追加在枚举末尾**，不得插入或重排。
/// <see cref="Unknown"/> 是「非法 / 未指定」哨兵，<b>不得</b>被当成"默认落到某个通道"使用。
/// </para>
/// </remarks>
public enum ArtifactKind
{
    /// <summary>未指定 / 非法（不得作为落点使用：无种类就无法确定消费通道）。</summary>
    Unknown = 0,

    /// <summary>技能（消费通道：技能自进化存储 → 技能文件 → 注入器）。</summary>
    Skill = 1,

    /// <summary>记忆章节（消费通道：记忆图书馆；该侧**已**具备「取代而非新增」语义）。</summary>
    MemoryChapter = 2,

    /// <summary>规则（如工具审批白/黑名单规则）。</summary>
    Rule = 3,

    /// <summary>文档（如设计文档、README）。</summary>
    Doc = 4,

    /// <summary>代码文件。</summary>
    CodeFile = 5,
}

/// <summary>
/// 改进操作：对<b>既有资产</b>做了什么，而不是"新增了什么"。
/// </summary>
/// <remarks>
/// <para>
/// 这是本基础设施的核心立场：<b>改进的输出货币是「变更」，不是「新增物」</b>。
/// 因此 <see cref="Create"/> 必须携带「为什么不能 Update / Merge 既有资产」的证据
/// （见 <see cref="ImprovementProposal.WhyNotUpdateOrMerge"/>），否则构造期即被拒绝 ——
/// 把「add 不是默认」从口号变成**字段级强制**。
/// </para>
/// <para>
/// 序列化兼容：<see cref="Unknown"/> 必须保持数值 0；新增成员只允许追加在末尾。
/// </para>
/// </remarks>
public enum ImprovementOperation
{
    /// <summary>未指定 / 非法。</summary>
    Unknown = 0,

    /// <summary>新增。<b>不是默认</b>：必须给出不可 Update / Merge 的理由。</summary>
    Create = 1,

    /// <summary>更新既有资产（原地升级：新内容替换旧内容，目标身份不变）。</summary>
    Update = 2,

    /// <summary>合并（把多份既有资产提炼为一份，被合并者降级而非删除）。</summary>
    Merge = 3,

    /// <summary>取代（换一份承载，旧者标记被取代；记忆侧的既有语义即此类）。</summary>
    Replace = 4,

    /// <summary>淘汰（停用 / 弃置）。<b>实现层一律停用而非删除</b>（可回滚是硬约束）。</summary>
    Retire = 5,
}

/// <summary>
/// 变更裁决结果。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Unknown"/> 是「未产生有效裁决」的哨兵，<b>不得当作放行</b>——
/// 与 <c>JudgeOutcome.Unknown</c>（"不得当作放行"）同一条纪律。
/// </para>
/// <para>
/// <see cref="ApplyShadow"/> 是**默认**结果：只记录「如果实施会怎样」，不落盘。
/// 新作业 / 新判据首次上线一律走 shadow，验证过再放开。
/// </para>
/// <para>序列化兼容：<see cref="Unknown"/> 必须保持数值 0；新增成员只允许追加在末尾。</para>
/// </remarks>
public enum ChangeDecision
{
    /// <summary>未产生有效裁决（不得当作放行）。</summary>
    Unknown = 0,

    /// <summary>影子应用：只记录、不落盘（默认）。</summary>
    ApplyShadow = 1,

    /// <summary>实际应用（必须携带回滚句柄，否则构造期即被拒绝）。</summary>
    Apply = 2,

    /// <summary>拒绝（保留原状）。</summary>
    Reject = 3,

    /// <summary>推迟（本轮不处理；推迟必须给出理由码）。</summary>
    Defer = 4,
}
