namespace PuddingWeightMemory;

/// <summary>
/// 组件的可注入参数：库位置、编码器身份、打分阈值与训练超参。
/// <para>
/// 所有权：这些是"部署时的判定标准"，不是每个调用方的偏好。默认值取自实验仓库
/// <c>PuddingMemoryLab@3a86988</c> 的定案数字（四步、0.08、0.5、温度 0.12、前三负例 + 0.5 空对手）；
/// 打分阈值在实验里是"用四篇训练外文档校准出来的最大值"，产品里必须按模型与目录规模重新标定，
/// 因此这里给的是保守缺省值而不是终值。
/// </para>
/// </summary>
public sealed class WeightMemoryOptions
{
    /// <summary>SQLite 库文件的绝对路径（宿主决定，组件不解析 DataRoot）。</summary>
    public required string DatabasePath { get; init; }

    /// <summary>
    /// 编码器定义标识，例如 <c>BAAI/bge-small-zh-v1.5@7999e1d3/cls-no-prefix-512</c>。
    /// 同一个库只接受同一个标识：换了编码器必须重建分区，否则向量不可比。
    /// </summary>
    public string EncoderId { get; init; } = "unspecified";

    /// <summary>局部梯度的步数。实验定案为 4。</summary>
    public int TrainingSteps { get; init; } = 4;

    /// <summary>每步学习率。实验定案为 0.08。</summary>
    public float LearningRate { get; init; } = 0.08f;

    /// <summary>把权重拉回登记锚点的系数。实验定案为 0.5（锚点防漂移）。</summary>
    public float AnchorWeight { get; init; } = 0.5f;

    /// <summary>对比损失的 softmax 温度。实验定案为 0.12。</summary>
    public float Temperature { get; init; } = 0.12f;

    /// <summary>参与竞争的既有地址数量（取与 cue 最像的前 N 个）。实验定案为 3。</summary>
    public int CompetitorCount { get; init; } = 3;

    /// <summary>固定的"无记忆"对手分数，保证即使没有其它章节也存在被推开的对象。实验定案为 0.5。</summary>
    public float NullCompetitorScore { get; init; } = 0.5f;

    /// <summary>
    /// 最高分门槛：最高分低于它则返回空结果。缺省 0.30 是 bge-small-zh 上的保守值，
    /// 上线前必须用"无相关记忆"标注集重新标定。
    /// </summary>
    public float MinimumTopScore { get; init; } = 0.30f;

    /// <summary>第一二名分差门槛：分差低于它则返回空结果（宁可不想起，也不要拿错章）。</summary>
    public float MinimumScoreGap { get; init; } = 0.01f;

    /// <summary>
    /// 跳过梯度的领先门槛：正确章对 cue 的分数已经比最强对手高出这么多时，不再训练。
    /// 缺省 0.10 与实验的对比损失温度同一量级；调大等于更少训练、更难被反馈拉动。
    /// </summary>
    public float SkipGradientMargin { get; init; } = 0.10f;

    /// <summary>写入准入队列容量。满时登记/反馈记漏做，预测降级为空结果，均不抛异常。</summary>
    public int MaxPendingWrites { get; init; } = 64;

    /// <summary>
    /// 测试专用故障注入点：在指定阶段抛异常，用来证明一次反馈的写入确实整体回滚。
    /// 生产路径不设置它；它不在任何公开签名里，不会成为调用方的可选项。
    /// </summary>
    internal Action<string>? FaultHook { get; init; }

    /// <summary>
    /// 校验参数并把库路径规范化为绝对路径。
    /// </summary>
    /// <param name="databasePath">规范化后的绝对路径。</param>
    /// <returns>校验通过时返回 <c>true</c>。</returns>
    internal bool TryNormalizePath(out string databasePath)
    {
        databasePath = string.Empty;
        if (string.IsNullOrWhiteSpace(DatabasePath))
            return false;

        databasePath = Path.GetFullPath(DatabasePath);
        return true;
    }

    /// <summary>参数越界即抛出，避免组件带着半可用的配置启动。</summary>
    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(DatabasePath, nameof(DatabasePath));
        if (DatabasePath == ":memory:")
            throw new ArgumentException("DatabasePath 必须是文件路径；内存库无法在多次连接之间保持内容。", nameof(DatabasePath));
        ArgumentException.ThrowIfNullOrWhiteSpace(EncoderId, nameof(EncoderId));

        if (TrainingSteps < 1)
            throw new ArgumentOutOfRangeException(nameof(TrainingSteps), TrainingSteps, "训练步数必须至少为 1。");
        if (!(LearningRate > 0f))
            throw new ArgumentOutOfRangeException(nameof(LearningRate), LearningRate, "学习率必须为正。");
        if (!(AnchorWeight >= 0f))
            throw new ArgumentOutOfRangeException(nameof(AnchorWeight), AnchorWeight, "锚点权重不能为负。");
        if (!(Temperature > 0f))
            throw new ArgumentOutOfRangeException(nameof(Temperature), Temperature, "温度必须为正。");
        if (CompetitorCount < 0)
            throw new ArgumentOutOfRangeException(nameof(CompetitorCount), CompetitorCount, "竞争地址数不能为负。");
        if (!float.IsFinite(NullCompetitorScore))
            throw new ArgumentOutOfRangeException(nameof(NullCompetitorScore), NullCompetitorScore, "空对手分数必须有限。");
        if (!float.IsFinite(MinimumTopScore))
            throw new ArgumentOutOfRangeException(nameof(MinimumTopScore), MinimumTopScore, "最高分门槛必须有限。");
        if (!(MinimumScoreGap >= 0f))
            throw new ArgumentOutOfRangeException(nameof(MinimumScoreGap), MinimumScoreGap, "分差门槛不能为负。");
        if (!float.IsFinite(SkipGradientMargin))
            throw new ArgumentOutOfRangeException(nameof(SkipGradientMargin), SkipGradientMargin, "领先门槛必须有限。");
        if (MaxPendingWrites < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxPendingWrites), MaxPendingWrites, "写入队列容量必须至少为 1。");
    }
}
