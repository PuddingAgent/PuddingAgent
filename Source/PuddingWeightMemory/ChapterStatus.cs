namespace PuddingWeightMemory;

/// <summary>
/// 章节在某个快照里的可见性。
/// <para>
/// 读侧只有 <see cref="Active"/> 会被返回；其它值一律不可见，因此失效不需要改写历史快照，
/// 也不需要事后修补回滚路径。本切片写入 <see cref="Active"/> 与 <see cref="Deleted"/>；
/// <see cref="Superseded"/> 由章节版本链（合并/更正的旧版本）在后续切片写入，
/// 读侧的"非 Active 不可见"规则已经把它覆盖。
/// </para>
/// </summary>
public enum ChapterStatus
{
    /// <summary>当前有效版本，可被召回。</summary>
    Active = 0,

    /// <summary>被同一章节的更新版本取代，不再返回，但保留用于回放核对。</summary>
    Superseded = 1,

    /// <summary>已删除（用户要求遗忘或回执报删除），任何快照都不再返回。</summary>
    Deleted = 2,
}
