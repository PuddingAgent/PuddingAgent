namespace PuddingPlatform.Services;

/// <summary>
/// DB 索引重建结果（ADR-093 A93-2，缺口 G3）。
/// <para>
/// 重建是**显式修复操作**，因此不使用"只留一行日志"的静默降级语义：
/// 失败必须由返回值携带（<see cref="Failed"/> + <see cref="Detail"/>），
/// 使调用方无法在"修好了"与"没修成"之间被混淆。
/// </para>
/// </summary>
/// <param name="Action">created / updated / unchanged / failed 之一。</param>
public sealed record SubAgentIndexRebuildResult(string RunId, string Action, string? Detail)
{
    /// <summary>权威归档存在而索引行缺失 ⇒ 补建。</summary>
    public const string Created = "created";

    /// <summary>索引行存在但与权威 manifest 不一致 ⇒ 修正。</summary>
    public const string Updated = "updated";

    /// <summary>索引行已与权威 manifest 一致 ⇒ <b>不写库</b>（幂等：重复重建不产生副作用）。</summary>
    public const string Unchanged = "unchanged";

    /// <summary>无法重建（权威缺失/不可解析/DB 失败）；原因见 <see cref="Detail"/>。</summary>
    public const string Failed = "failed";

    public bool Succeeded => Action is Created or Updated or Unchanged;
}
