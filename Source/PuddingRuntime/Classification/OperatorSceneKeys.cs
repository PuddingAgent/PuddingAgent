namespace PuddingRuntime.Classification;

/// <summary>
/// 场景键的稳定取值与归一（<b>集中定义</b>，不得散落到各消费点；与 S2a 的
/// <c>AcceptanceThresholdPolicyIds</c> 同一「常量只定义一处」的风格）。
/// <para>
/// <b>为什么需要一个显式具名的默认场景键</b>：健康计数按场景分区之后，缺失的场景键若归一为「空字符串键」，
/// 不同来源（未填场景键的调用方、多场景共用同一分类器的调用方）会重新混进同一个桶——那正是本切片要消灭的
/// 计数污染，只是换了个入口。归一到<b>具名常量</b>后，「没有场景键」与「某个真实场景」在读数上可区分，
/// 且不引入新的混桶路径。
/// </para>
/// </summary>
public static class OperatorSceneKeys
{
    /// <summary>
    /// 默认场景键：场景键缺失 / 空 / 纯空白时归一到此。
    /// <para>
    /// 取值刻意带命名空间前缀（<c>operator.</c>），使其<b>不可能</b>与任何真实场景键（例 <c>tool_approval</c>）
    /// 相撞；同时<b>绝不为空串</b>——空串键会让不同来源混进同一桶。
    /// </para>
    /// </summary>
    public const string Default = "operator.default";

    /// <summary>
    /// 归一场景键：<c>null</c>/空/纯空白 ⇒ <see cref="Default"/>；其余<b>原样返回</b>。
    /// <para>
    /// 为何不裁剪 / 不折叠大小写：调用方给的场景键是<b>身份</b>，适配器改写身份会让审计与健康读数
    /// 对不上源头（「谁报的」无法追溯）。这里只处理唯一会造成混桶的那一种输入——<b>缺失</b>。
    /// 纯函数、无副作用、不抛异常。
    /// </para>
    /// </summary>
    /// <param name="sceneKey">原始场景键；可为 null。</param>
    public static string Normalize(string? sceneKey)
        => string.IsNullOrWhiteSpace(sceneKey) ? Default : sceneKey;
}
