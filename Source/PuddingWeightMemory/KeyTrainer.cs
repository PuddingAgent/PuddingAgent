namespace PuddingWeightMemory;

/// <summary>
/// 单行权重的局部梯度下降：把某个章节的向量往 cue 推，同时被登记锚点拉住，
/// 并被"最像 cue 的其它地址"推开。
/// <para>
/// 数学含义（等价于实验仓库 <c>pudding_memory_lab/keys.py::train_key</c> 的同一形式）：
/// 记 <c>s = key·cue</c>，负例集合 <c>C</c>（最难的 N 个既有地址，加一个固定的"无记忆"对手 0.5），
/// 损失取 <c>L = log(1 + Σ exp((c + 0.1 − s)/τ))</c>。对 <c>s</c> 求导得下降量
/// <c>force = σ(odds)/τ</c>，其中 <c>odds = Σ exp((c + 0.1 − s)/τ)</c>；
/// 在单位球上 <c>key·cue</c> 对 key 的切向梯度是 <c>cue − s·key</c>，
/// 锚点项同理为 <c>anchor − (anchor·key)·key</c>。于是每步
/// <c>key ← unit(key + η·(force·(cue − s·key) + w_a·(anchor − (anchor·key)·key)))</c>，
/// 重复 <see cref="WeightMemoryOptions.TrainingSteps"/> 次。敌人与编码器都不动，所以这是"局部"的。
/// </para>
/// <para>
/// 不变量：每步之后权重都是单位向量——库里的分数因此只需比较点积，不需要再除以范数。
/// </para>
/// </summary>
internal static class KeyTrainer
{
    /// <summary>参考实现对负例分数加的固定偏置（<c>competing + 0.1</c>）：让 0.5 这类中位对手也持续施压。</summary>
    private const float CompetitorBias = 0.1f;

    /// <summary>
    /// 从 <paramref name="initial"/> 出发训练一条章节权重。
    /// </summary>
    /// <param name="initial">起点（活动权重或上一次的候选权重），不要求已归一化。</param>
    /// <param name="anchor">登记锚点，防止 cue 学习把章节拉离原意。</param>
    /// <param name="cue">本次反馈的归一化 cue 向量。</param>
    /// <param name="competitors">非负例章节的当前权重（只取与 cue 最像的前 N 个）。</param>
    /// <param name="options">步数、学习率、温度、锚点权重、负例数量等超参。</param>
    /// <returns>训练后的单位向量。</returns>
    internal static float[] Train(
        ReadOnlySpan<float> initial,
        ReadOnlySpan<float> anchor,
        ReadOnlySpan<float> cue,
        IReadOnlyList<float[]> competitors,
        WeightMemoryOptions options)
    {
        if (anchor.Length != cue.Length || anchor.Length != initial.Length)
            throw new WeightMemoryException("训练向量维度不一致。");

        var competing = SelectCompetitors(cue, competitors, options);

        var key = VectorMath.Normalize(initial);
        for (var step = 0; step < options.TrainingSteps; step++)
        {
            var positive = VectorMath.Dot(key, cue);

            var odds = 0.0;
            foreach (var score in competing)
                odds += Math.Exp((score + CompetitorBias - positive) / options.Temperature);

            // force 是 -dL/ds：正例分越高、负例越少，推动力越小，因此"已经领先"的情形天然接近不动。
            var force = odds / (1.0 + odds) / options.Temperature;
            var anchorProjection = VectorMath.Dot(anchor, key);

            var next = new float[key.Length];
            for (var index = 0; index < key.Length; index++)
            {
                var towardCue = cue[index] - positive * key[index];
                var towardAnchor = anchor[index] - anchorProjection * key[index];
                next[index] = (float)(key[index]
                    + options.LearningRate * (force * towardCue + options.AnchorWeight * towardAnchor));
            }

            key = VectorMath.Normalize(next);
        }

        return key;
    }

    /// <summary>
    /// 取与 cue 最像的前 N 个既有地址分数，再追加固定的"无记忆"对手。
    /// 顺序无关：训练只用它们的指数和，所以这里只做选择，不排序对外暴露。
    /// </summary>
    private static double[] SelectCompetitors(
        ReadOnlySpan<float> cue,
        IReadOnlyList<float[]> competitors,
        WeightMemoryOptions options)
    {
        var kept = Math.Min(options.CompetitorCount, competitors.Count);
        var scores = new List<double>(kept + 1);
        if (kept > 0)
        {
            var all = new double[competitors.Count];
            for (var index = 0; index < competitors.Count; index++)
                all[index] = VectorMath.Dot(competitors[index], cue);

            // 全部升序，取尾部即最难的负例；只保留 kept 个，其余不参与损失。
            Array.Sort(all);
            for (var index = 0; index < kept; index++)
                scores.Add(all[all.Length - 1 - index]);
        }

        scores.Add(options.NullCompetitorScore);
        return scores.ToArray();
    }
}
