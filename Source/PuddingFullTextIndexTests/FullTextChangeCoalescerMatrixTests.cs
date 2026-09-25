using PuddingFullTextIndex.Contracts;
using PuddingFullTextIndex.Infrastructure.Maintenance;

namespace PuddingFullTextIndexTests;

/// <summary>
/// S2b：折叠层的**终态 × 来源 × 纯净性**矩阵。
/// <para>
/// 覆盖口径：
/// <list type="number">
/// <item><description><b>终态 8 值全覆盖</b>：每个 <see cref="FullTextPathObservation"/> 取值都要映射到正确动作
/// （<c>Upsert</c> / <c>Delete</c> / 不产出动作 + 进入 <c>DeferredPaths</c>）—— 漏一个就会「要么不更新要么误删」。</description></item>
/// <item><description><b>来源 7 个非零组合</b>：8 × 7 = 56 格交叉矩阵逐格执行，位标必须**逐位**相等（不是只测 1~2 个组合）。</description></item>
/// <item><description><b>确定性 / 幂等 / 无副作用</b>：打乱中间事件（保持「每路径首条 + 末条」位置）⇒ 结果**逐项相同**；
/// 同一输入折叠两次结果相等；传入的 <see cref="List{T}"/> 不得被修改。</description></item>
/// <item><description><b>规范化去重</b>：同一物理路径的大小写 / 分隔符变体 ⇒ 只产出 1 个动作。</description></item>
/// </list>
/// </para>
/// <para>本用例**零 IO / 零线程 / 不读时钟**。</para>
/// </summary>
[TestClass]
public sealed class FullTextChangeCoalescerMatrixTests
{
    private static readonly string ScopeRoot = Path.Combine(Path.GetTempPath(), "pudding-fts-s2b-matrix-scope");
    private static readonly DateTimeOffset ObservedAt = new(2026, 9, 25, 9, 0, 0, TimeSpan.Zero);

    private static string PathIn(string fileName) => Path.Combine(ScopeRoot, "src", fileName);

    private static FullTextCoalesceResult Coalesce(params FullTextChangeObservation[] observations)
        => FullTextChangeCoalescer.Coalesce(observations, ScopeRoot);

    private static string Signature(FullTextCoalesceResult result)
        => string.Join(
                "|",
                result.Changes.Select(c => $"{c.FullPath}~{c.Kind}~{(int)c.Sources}~{c.LastWriteUtc?.UtcTicks}~{c.Length}"))
            + "//" + string.Join("|", result.DeferredPaths)
            + "//" + string.Join("|", result.RejectedPaths.Select(r => $"{r.FullPath}~{r.Reason}"));

    /// <summary>终态 ⇒ 期望动作：Upsert / Delete / Deferred（不产出动作 + 待重试）。</summary>
    private static string ExpectedAction(FullTextPathObservation observation) => observation switch
    {
        FullTextPathObservation.IndexableFile => "Upsert",
        FullTextPathObservation.Unreadable => "Deferred",
        _ => "Delete",
    };

    private static void AssertActionMatches(FullTextPathObservation observation, FullTextCoalesceResult result, string label)
    {
        var expected = ExpectedAction(observation);
        var path = result.Changes.Count == 1 ? result.Changes[0].FullPath : result.DeferredPaths.Count == 1 ? result.DeferredPaths[0] : null;

        switch (expected)
        {
            case "Upsert":
                Assert.AreEqual(1, result.Changes.Count, $"{label}: 可索引文件必须产出恰好一个动作");
                Assert.AreEqual(FullTextChangeKind.Upsert, result.Changes[0].Kind, $"{label}: 可索引文件 ⇒ Upsert");
                Assert.AreEqual(0, result.DeferredPaths.Count, $"{label}: 不得进入待重试");
                break;
            case "Delete":
                Assert.AreEqual(1, result.Changes.Count, $"{label}: 需删除的终态必须产出恰好一个动作");
                Assert.AreEqual(FullTextChangeKind.Delete, result.Changes[0].Kind, $"{label}: 该终态 ⇒ Delete");
                Assert.AreEqual(0, result.DeferredPaths.Count, $"{label}: 不得进入待重试");
                break;
            default:
                Assert.AreEqual(0, result.Changes.Count, $"{label}: 不可读必须**不产出动作**（产出 Delete = 把「读不到」当「不存在」）");
                Assert.AreEqual(1, result.DeferredPaths.Count, $"{label}: 不可读必须进入 DeferredPaths");
                break;
        }

        Assert.IsNotNull(path, $"{label}: 必须能从出口之一取回路径");
        Assert.AreEqual(0, result.RejectedPaths.Count, $"{label}: 合法观察不得进入拒绝集");
    }

    // ── ① 终态 8 值全覆盖 ──────────────────────────────────────────────

    [TestMethod]
    public void FinalStates_AllEightObservationValues_MapToTheContractedAction()
    {
        var matrix = new (FullTextPathObservation Observation, string Action)[]
        {
            (FullTextPathObservation.IndexableFile, "Upsert"),
            (FullTextPathObservation.Missing, "Delete"),
            (FullTextPathObservation.Directory, "Delete"),
            (FullTextPathObservation.ExtensionNotAllowed, "Delete"),
            (FullTextPathObservation.Noisy, "Delete"),
            (FullTextPathObservation.Empty, "Delete"),
            (FullTextPathObservation.Oversized, "Delete"),
            (FullTextPathObservation.Unreadable, "Deferred"),
        };

        Assert.AreEqual(8, matrix.Length, "终态矩阵行数自证");
        CollectionAssert.AreEquivalent(
            Enum.GetNames<FullTextPathObservation>(),
            matrix.Select(m => m.Observation.ToString()).ToArray(),
            "覆盖对照：矩阵必须逐一覆盖磁盘上的全部 FullTextPathObservation 取值");

        var evaluated = 0;
        foreach (var (observation, action) in matrix)
        {
            evaluated++;

            var path = PathIn($"{observation}.cs");
            var result = Coalesce(new FullTextChangeObservation(path, FullTextChangeSource.Watcher, observation));

            AssertActionMatches(observation, result, observation.ToString());
            Assert.AreEqual(action, ExpectedAction(observation), $"{observation}: 矩阵声明的动作与契约不符");
        }

        Assert.AreEqual(8, evaluated, "终态矩阵必须逐行执行（行数自证）");
    }

    // ── ② 来源 8 × 7 交叉矩阵（逐位）────────────────────────────────────

    [TestMethod]
    public void SourcesMatrix_EightStatesTimesSevenNonZeroCombinations_AreAllBitwise()
    {
        var combinations = Enumerable.Range(1, 7).Select(bits => (FullTextChangeSource)bits).ToArray();

        Assert.AreEqual(7, combinations.Length, "非零来源组合数自证（2^3 − 1）");
        CollectionAssert.AreEquivalent(
            new[] { 1, 2, 3, 4, 5, 6, 7 },
            combinations.Select(c => (int)c).ToArray(),
            "覆盖对照：必须逐一覆盖 FullTextChangeSource 的全部非零组合");

        var finalStates = Enum.GetValues<FullTextPathObservation>();
        var evaluated = 0;

        foreach (var observation in finalStates)
        {
            foreach (var sources in combinations)
            {
                evaluated++;

                var label = $"[{observation} × {(int)sources}]";
                var path = PathIn($"cross-{(int)observation}-{(int)sources}.cs");
                var result = Coalesce(new FullTextChangeObservation(path, sources, observation));

                AssertActionMatches(observation, result, label);

                if (observation != FullTextPathObservation.Unreadable)
                {
                    Assert.AreEqual(sources, result.Changes[0].Sources, $"{label}: 单来源位标必须逐位原样保留");
                    Assert.AreEqual((int)sources, (int)result.Changes[0].Sources, $"{label}: 逐位断言（int 视角）");
                }
            }
        }

        Assert.AreEqual(56, evaluated, "8 × 7 交叉矩阵必须逐格执行（56 格自证）");
        Assert.AreEqual(finalStates.Length * combinations.Length, evaluated, "行数自证：终态数 × 来源组合数");
    }

    /// <summary>
    /// 多来源命中**同一路径** ⇒ <c>Sources</c> 按位或合并：对 7 个非零组合逐一构造
    /// 「组合里出现的每个来源各一条观察」，断言合并结果与该组合**逐位相等**（不多一位、不少一位）。
    /// </summary>
    [TestMethod]
    public void SourcesMerge_AllSevenCombinations_MergeBitwiseOrWithoutExtraBits()
    {
        var candidates = new[]
        {
            FullTextChangeSource.Watcher,
            FullTextChangeSource.MTimeRecovery,
            FullTextChangeSource.IntegrityCheck,
        };

        var evaluated = 0;
        for (var bits = 1; bits <= 7; bits++)
        {
            evaluated++;

            var expected = (FullTextChangeSource)bits;
            var path = PathIn($"merge-{bits}.cs");
            var observations = new List<FullTextChangeObservation>();

            foreach (var source in candidates)
            {
                if ((expected & source) != 0)
                    observations.Add(new(path, source, FullTextPathObservation.IndexableFile, ObservedAt, bits));
            }

            Assert.IsTrue(observations.Count >= 1, $"{bits}: 每个非零组合至少一条来源观察");

            var result = Coalesce(observations.ToArray());

            Assert.AreEqual(1, result.Changes.Count, $"{bits}: 同路径多个来源只能剩一个动作");
            Assert.AreEqual(expected, result.Changes[0].Sources, $"{bits}: Sources 必须按位或合并");
            Assert.AreEqual(bits, (int)result.Changes[0].Sources & bits, $"{bits}: 组合内每一位都必须在结果里");
            Assert.AreEqual(0, (int)result.Changes[0].Sources & ~bits, $"{bits}: 不得引入组合外的来源位");
        }

        Assert.AreEqual(7, evaluated, "来源合并矩阵行数自证");
    }

    // ── ③ 确定性（顺序无关）────────────────────────────────────────────

    /// <summary>
    /// 强确定性：保持「每条路径的**首条**与**末条**事件」相对位置不变，只打乱**中间事件**。
    /// 此时首次出现顺序与最终动作都不变 ⇒ 结果必须**逐项（含顺序）相同**。
    /// 这一条用来抓「字典迭代顺序泄漏到结果里」（实现若遍历 Dictionary 产出输出，顺序就会漂移）。
    /// </summary>
    [TestMethod]
    public void Determinism_ShufflingIntermediatesOnly_YieldsTheIdenticalSequence()
    {
        var a = PathIn("det-a.cs");
        var b = PathIn("det-b.cs");
        var c = PathIn("det-c.cs");

        var firsts = new[]
        {
            new FullTextChangeObservation(a, FullTextChangeSource.Watcher, FullTextPathObservation.Missing),
            new FullTextChangeObservation(b, FullTextChangeSource.Watcher, FullTextPathObservation.IndexableFile, ObservedAt, 20),
            new FullTextChangeObservation(c, FullTextChangeSource.MTimeRecovery, FullTextPathObservation.Unreadable),
        };
        var intermediates = new[]
        {
            new FullTextChangeObservation(a, FullTextChangeSource.MTimeRecovery, FullTextPathObservation.Directory),
            new FullTextChangeObservation(b, FullTextChangeSource.Watcher, FullTextPathObservation.Missing),
            new FullTextChangeObservation(c, FullTextChangeSource.IntegrityCheck, FullTextPathObservation.Empty),
        };
        var lasts = new[]
        {
            new FullTextChangeObservation(a, FullTextChangeSource.IntegrityCheck, FullTextPathObservation.IndexableFile, ObservedAt, 10),
            new FullTextChangeObservation(b, FullTextChangeSource.MTimeRecovery, FullTextPathObservation.IndexableFile, ObservedAt, 21),
            new FullTextChangeObservation(c, FullTextChangeSource.IntegrityCheck, FullTextPathObservation.Unreadable),
        };

        var baseline = Coalesce(firsts.Concat(intermediates).Concat(lasts).ToArray());
        var baselineSignature = Signature(baseline);

        var permutations = new[]
        {
            new[] { 0, 1, 2 },
            new[] { 0, 2, 1 },
            new[] { 1, 0, 2 },
            new[] { 1, 2, 0 },
            new[] { 2, 0, 1 },
            new[] { 2, 1, 0 },
        };

        Assert.AreEqual(6, permutations.Length, "中间事件的排列数自证（3! = 6）");

        var evaluated = 0;
        foreach (var permutation in permutations)
        {
            evaluated++;

            var shuffled = firsts
                .Concat(permutation.Select(index => intermediates[index]))
                .Concat(lasts)
                .ToArray();

            var result = Coalesce(shuffled);

            Assert.AreEqual(
                baselineSignature,
                Signature(result),
                $"中间事件顺序 [{string.Join(",", permutation)}] 不得改变折叠结果（字典迭代顺序泄漏？）");
        }

        Assert.AreEqual(6, evaluated, "确定性矩阵行数自证");

        // 结果自证（防止「两边都是空」导致同义反复）：2 个动作（a,b，顺序 = 首次出现顺序）+ 1 条待重试
        CollectionAssert.AreEqual(new[] { a, b }, baseline.Changes.Select(x => x.FullPath).ToArray(), "动作顺序必须是首次出现顺序，稳定");
        CollectionAssert.AreEqual(
            new[]
            {
                FullTextChangeSource.Watcher | FullTextChangeSource.MTimeRecovery | FullTextChangeSource.IntegrityCheck,
                FullTextChangeSource.Watcher | FullTextChangeSource.MTimeRecovery,
            },
            baseline.Changes.Select(x => x.Sources).ToArray(),
            "Sources 必须位或合并（a 三源、b 两源）");
        Assert.AreEqual(FullTextChangeKind.Upsert, baseline.Changes[0].Kind);
        Assert.AreEqual(FullTextChangeKind.Upsert, baseline.Changes[1].Kind);
        CollectionAssert.AreEqual(new[] { c }, baseline.DeferredPaths.ToArray(), "c 的末条事件是不可读 ⇒ 待重试");
        Assert.AreEqual(0, baseline.RejectedPaths.Count);
    }

    /// <summary>
    /// 弱确定性（集合口径）：任意打乱「保持每路径末条事件仍在末位」的输入 ⇒
    /// **按路径取回的动作映射**必须逐条相同（顺序按契约可以是首次出现顺序，故比较映射而不是序列）。
    /// </summary>
    [TestMethod]
    public void Determinism_ArbitraryShuffleKeepingLastEventPerPath_YieldsTheSamePerPathMap()
    {
        var paths = Enumerable.Range(0, 5).Select(i => PathIn($"set-{i}.cs")).ToArray();
        var perPath = new List<FullTextChangeObservation>[paths.Length];

        for (var i = 0; i < paths.Length; i++)
        {
            perPath[i] = new List<FullTextChangeObservation>
            {
                new(paths[i], FullTextChangeSource.Watcher, FullTextPathObservation.Missing),
                new(paths[i], FullTextChangeSource.MTimeRecovery, FullTextPathObservation.Directory),
                new(paths[i], FullTextChangeSource.IntegrityCheck, FullTextPathObservation.IndexableFile, ObservedAt.AddMinutes(i), i + 1),
            };
        }

        var baseline = Coalesce(perPath.SelectMany(group => group).ToArray());
        var baselineMap = baseline.Changes.ToDictionary(change => change.FullPath, StringComparer.Ordinal);

        Assert.AreEqual(paths.Length, baselineMap.Count, "基线必须每个路径恰好一个动作");
        Assert.AreEqual(0, baseline.DeferredPaths.Count);
        Assert.AreEqual(0, baseline.RejectedPaths.Count);

        var random = new Random(20260925);   // 固定种子 ⇒ 本用例自身确定，失败可复现
        var evaluatedRounds = 0;

        for (var round = 0; round < 12; round++)
        {
            // 随机**交错**各路径的事件，但每条路径内部保持原发生顺序（末条恒为该路径最后一条）。
            // 刻意**不能**全局 OrderBy：那会把某路径的末条挪到它自己的更早事件之前，
            // 违反「observations 顺序即发生顺序」的入参契约（那是调用方错误，不属本层职责）。
            var cursors = new int[perPath.Length];
            var shuffled = new List<FullTextChangeObservation>();
            var total = perPath.Sum(group => group.Count);

            for (var step = 0; step < total; step++)
            {
                var available = Enumerable.Range(0, perPath.Length)
                    .Where(index => cursors[index] < perPath[index].Count)
                    .ToArray();

                var pick = available[random.Next(available.Length)];
                shuffled.Add(perPath[pick][cursors[pick]]);
                cursors[pick]++;
            }

            Assert.AreEqual(total, shuffled.Count, $"第 {round} 轮：交错不得增删事件");

            var result = Coalesce(shuffled.ToArray());
            var map = result.Changes.ToDictionary(change => change.FullPath, StringComparer.Ordinal);

            Assert.AreEqual(baselineMap.Count, map.Count, $"第 {round} 轮：动作数必须不变");

            foreach (var (path, expected) in baselineMap)
            {
                Assert.IsTrue(map.TryGetValue(path, out var actual), $"第 {round} 轮：{path} 缺失动作");
                Assert.AreEqual(expected.Kind, actual!.Kind, $"第 {round} 轮：{path} 动作不符");
                Assert.AreEqual(expected.Sources, actual.Sources, $"第 {round} 轮：{path} Sources 不符");
                Assert.AreEqual(expected.LastWriteUtc, actual.LastWriteUtc, $"第 {round} 轮：{path} mtime 不符");
                Assert.AreEqual(expected.Length, actual.Length, $"第 {round} 轮：{path} 长度不符");
            }

            evaluatedRounds++;
        }

        Assert.AreEqual(12, evaluatedRounds, "交错轮数自证");
    }

    // ── ④ 无副作用 + 幂等 ──────────────────────────────────────────────

    [TestMethod]
    public void Purity_DoesNotMutateTheInputList_AndIsIdempotent()
    {
        var input = new List<FullTextChangeObservation>
        {
            new(PathIn("pure-a.cs"), FullTextChangeSource.Watcher, FullTextPathObservation.Missing),
            new(PathIn("pure-a.cs"), FullTextChangeSource.MTimeRecovery, FullTextPathObservation.IndexableFile, ObservedAt, 1),
            new(PathIn("pure-b.cs"), FullTextChangeSource.IntegrityCheck, FullTextPathObservation.Unreadable),
            new(PathIn("pure-c.cs"), default, FullTextPathObservation.IndexableFile),
            new("   ", FullTextChangeSource.Watcher, FullTextPathObservation.IndexableFile),
        };
        var snapshot = input.ToArray();

        var first = FullTextChangeCoalescer.Coalesce(input, ScopeRoot);

        CollectionAssert.AreEqual(snapshot, input.ToArray(), "折叠不得修改传入的序列（无副作用）");
        Assert.AreEqual(snapshot.Length, input.Count, "折叠不得增删输入元素");

        var second = FullTextChangeCoalescer.Coalesce(input, ScopeRoot);

        CollectionAssert.AreEqual(first.Changes.ToArray(), second.Changes.ToArray(), "同一输入连续折叠两次必须幂等（动作）");
        CollectionAssert.AreEqual(first.DeferredPaths.ToArray(), second.DeferredPaths.ToArray(), "幂等（待重试）");
        CollectionAssert.AreEqual(first.RejectedPaths.ToArray(), second.RejectedPaths.ToArray(), "幂等（拒绝集）");
        Assert.AreEqual(Signature(first), Signature(second), "幂等（签名兜底）");

        // 自证输入确实同时触发了三类出口（防止「全空 ⇒ 断言同义反复」）
        Assert.AreEqual(1, first.Changes.Count, "pure-a 的末条是可索引文件 ⇒ 1 个 Upsert");
        Assert.AreEqual(1, first.DeferredPaths.Count, "pure-b 不可读 ⇒ 1 条待重试");
        Assert.AreEqual(2, first.RejectedPaths.Count, "pure-c 无来源 + 空白路径 ⇒ 2 条拒绝");
        Assert.AreEqual(FullTextPathRejectionReason.NoSource, first.RejectedPaths[0].Reason);
        Assert.AreEqual(FullTextPathRejectionReason.BlankPath, first.RejectedPaths[1].Reason);
    }

    // ── ⑤ 规范化去重 ───────────────────────────────────────────────────

    [TestMethod]
    public void Normalization_CaseAndSeparatorVariants_ProduceExactlyOneActionPerPhysicalPath()
    {
        var canonical = PathIn("Dup.CS");
        var variants = new[]
        {
            canonical,
            PathIn("dup.cs").ToUpperInvariant(),
            ScopeRoot.Replace('\\', '/') + "/src/dup.cs",
        };

        Assert.AreEqual(3, variants.Distinct(StringComparer.Ordinal).Count(), "三种写法必须字面不同，否则测不到规范化");

        var duplicated = Coalesce(variants
            .Select(variant => new FullTextChangeObservation(variant, FullTextChangeSource.Watcher, FullTextPathObservation.Missing))
            .ToArray());

        Assert.AreEqual(1, duplicated.Changes.Count, "同一物理路径的不同写法必须只产出 1 个动作");
        Assert.AreEqual(FullTextChangeKind.Delete, duplicated.Changes[0].Kind);
        Assert.AreEqual(variants[^1], duplicated.Changes[0].FullPath, "输出保留最后一条观察的原始写法（诊断保真）");
        Assert.AreEqual(0, duplicated.RejectedPaths.Count);

        // 不可读的同路径变体：只留 1 条待重试
        var deferred = Coalesce(
            new(PathIn("Locked.CS"), FullTextChangeSource.Watcher, FullTextPathObservation.Unreadable),
            new(PathIn("locked.cs"), FullTextChangeSource.MTimeRecovery, FullTextPathObservation.Unreadable));

        Assert.AreEqual(0, deferred.Changes.Count);
        Assert.AreEqual(1, deferred.DeferredPaths.Count, "同一物理路径的重复不可读 ⇒ 待重试只留 1 条");
        Assert.AreEqual(0, deferred.RejectedPaths.Count);

        // 越界重复：拒绝集按源码**不去重**（每条被拒观察都如实返回，不静默丢信息）
        var outsideRoot = Path.Combine(Path.GetTempPath(), "pudding-fts-s2b-outside");
        var rejected = Coalesce(
            new(Path.Combine(outsideRoot, "a.cs"), FullTextChangeSource.Watcher, FullTextPathObservation.IndexableFile),
            new(Path.Combine(outsideRoot, "A.CS"), FullTextChangeSource.Watcher, FullTextPathObservation.IndexableFile));

        Assert.AreEqual(0, rejected.Changes.Count);
        Assert.AreEqual(2, rejected.RejectedPaths.Count, "拒绝集不去重：每条被拒观察必须如实返回");
        Assert.IsTrue(rejected.RejectedPaths.All(r => r.Reason == FullTextPathRejectionReason.OutsideScope));
    }

    // ── ⑥ 空输入 / 全拒绝输入 ──────────────────────────────────────────

    [TestMethod]
    public void EmptyAndFullyRejectedInput_ReturnEmptyChangesWithoutThrowing()
    {
        var empty = FullTextChangeCoalescer.Coalesce(Array.Empty<FullTextChangeObservation>(), ScopeRoot);

        Assert.AreEqual(0, empty.Changes.Count);
        Assert.AreEqual(0, empty.DeferredPaths.Count);
        Assert.AreEqual(0, empty.RejectedPaths.Count);
        Assert.AreEqual("////", Signature(empty), "空输入的三类出口都必须是空集合（而不是 null）");

        var rejected = Coalesce(new FullTextChangeObservation[]
        {
            null!,
            new(string.Empty, FullTextChangeSource.Watcher, FullTextPathObservation.IndexableFile),
            new(PathIn("no-source.cs"), default, FullTextPathObservation.IndexableFile),
        });

        Assert.AreEqual(0, rejected.Changes.Count, "全拒绝输入不得产出任何动作");
        Assert.AreEqual(0, rejected.DeferredPaths.Count);
        Assert.AreEqual(2, rejected.RejectedPaths.Count, "空路径 + 无来源 各一条拒绝；null 元素被忽略而不是抛错");
        Assert.AreEqual(FullTextPathRejectionReason.BlankPath, rejected.RejectedPaths[0].Reason);
        Assert.AreEqual(FullTextPathRejectionReason.NoSource, rejected.RejectedPaths[1].Reason);
    }
}
