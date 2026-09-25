using PuddingFullTextIndex.Contracts;
using PuddingFullTextIndex.Infrastructure.Maintenance;

namespace PuddingFullTextIndexTests;

/// <summary>
/// S2b：路径越界的**全形态**矩阵。核心风险是「越界判据退化成前缀字符串比较」——
/// <c>D:\proj-2</c> 会被误判为落在 <c>D:\proj</c> 内 ⇒ **索引别的项目的内容**。
/// <para>
/// 每行的**三条**断言：① <see cref="FullTextChangeCoalescer.IsWithinScope"/> 判定；
/// ② <see cref="FullTextChangeCoalescer.Coalesce"/> 给出的 <see cref="FullTextPathRejectionReason"/>（**必须点名原因**，
/// 不能只断言「被拒绝」）；③ 行数 / 形态数**自证**（防某行被静默跳过）。
/// </para>
/// <para>
/// 本用例**零 IO**：所有路径都是字符串构造，**不创建任何目录或文件**。因此「必须存在真实目录才能判定」的形态
/// （例如「scope 目录本身不存在」这类 IO 判定）**不在本片范围**，已在报告中列为「无法在本片验证」。
/// </para>
/// </summary>
[TestClass]
public sealed class MaintenancePathBoundaryTests
{
    private const int DeclaredRowCount = 26;
    private const int DeclaredFormCount = 13;

    private const string FormNormalChild = "01-normal-child";
    private const string FormScopeRootItself = "02-scope-root-itself";
    private const string FormTrailingSeparator = "03-trailing-separator";
    private const string FormCaseDifference = "04-case-difference";
    private const string FormPrefixSibling = "05-prefix-sibling";
    private const string FormMixedSeparators = "06-mixed-separators";
    private const string FormRelative = "07-relative";
    private const string FormUnc = "08-unc";
    private const string FormDotDotTraversal = "09-dotdot-traversal";
    private const string FormBlank = "10-blank";
    private const string FormTrailingSpaceOrDot = "11-trailing-space-or-dot";
    private const string FormDriveRelative = "12-drive-relative";
    private const string FormLongPath = "13-long-path";

    private static readonly string ScopeRoot = Path.Combine(Path.GetTempPath(), "pudding-fts-s2b-path-scope");

    private sealed record PathRow(
        string Form,
        string RowName,
        string ScopeArg,
        string PathArg,
        bool? ExpectWithin,
        FullTextPathRejectionReason? ExpectReason);

    private static List<PathRow> BuildRows()
    {
        var rows = new List<PathRow>();
        var sep = Path.DirectorySeparatorChar;
        var alt = Path.AltDirectorySeparatorChar;
        var outside = FullTextPathRejectionReason.OutsideScope;

        // ① 正常子路径 ⇒ 在内
        rows.Add(new(FormNormalChild, "direct-child", ScopeRoot, Path.Combine(ScopeRoot, "a.cs"), true, null));
        rows.Add(new(FormNormalChild, "nested-child", ScopeRoot, Path.Combine(ScopeRoot, "sub", "a.cs"), true, null));

        // ② scope 根**自身**（源码语义：不含根自身 —— 根是目录，不可能是文件变更对象）⇒ 在外
        rows.Add(new(FormScopeRootItself, "root-itself", ScopeRoot, ScopeRoot, false, outside));
        rows.Add(new(FormScopeRootItself, "root-with-trailing-separator", ScopeRoot, ScopeRoot + sep, false, outside));

        // ③ 尾随分隔符变体 ⇒ 与正样本一致
        rows.Add(new(FormTrailingSeparator, "child-trailing-separator", ScopeRoot, Path.Combine(ScopeRoot, "a.cs") + sep, true, null));
        rows.Add(new(FormTrailingSeparator, "scope-with-trailing-separator", ScopeRoot + sep, Path.Combine(ScopeRoot, "a.cs"), true, null));

        // ④ 大小写差异（Windows First）⇒ 一致
        rows.Add(new(FormCaseDifference, "scope-upper-path-lower", ScopeRoot.ToUpperInvariant(), Path.Combine(ScopeRoot, "a.cs"), true, null));
        rows.Add(new(FormCaseDifference, "path-upper", ScopeRoot, Path.Combine(ScopeRoot, "A.CS"), true, null));

        // ⑤ ★ 前缀同名兄弟 ⇒ 必须在外（不得被前缀比较误收）
        rows.Add(new(FormPrefixSibling, "dash-suffix", ScopeRoot, ScopeRoot + "-2", false, outside));
        rows.Add(new(FormPrefixSibling, "dash-suffix-child", ScopeRoot, Path.Combine(ScopeRoot + "-2", "a.cs"), false, outside));
        rows.Add(new(FormPrefixSibling, "alpha-suffix", ScopeRoot, ScopeRoot + "X", false, outside));
        rows.Add(new(FormPrefixSibling, "alpha-suffix-child", ScopeRoot, Path.Combine(ScopeRoot + "X", "a.cs"), false, outside));
        rows.Add(new(FormPrefixSibling, "dot-bak-suffix", ScopeRoot, ScopeRoot + ".bak", false, outside));
        rows.Add(new(FormPrefixSibling, "dot-bak-suffix-child", ScopeRoot, Path.Combine(ScopeRoot + ".bak", "a.cs"), false, outside));

        // ⑥ 混用分隔符 ⇒ 与外层一致
        rows.Add(new(FormMixedSeparators, "scope-alt-separators", ScopeRoot.Replace('\\', '/'), Path.Combine(ScopeRoot, "sub", "a.cs"), true, null));
        rows.Add(new(FormMixedSeparators, "path-alt-separators", ScopeRoot, ScopeRoot + alt + "sub" + alt + "a.cs", true, null));

        // ⑦ 相对路径：GetFullPath 会相对**进程当前目录**绝对化 ⇒ 落在 scope 之外（不得抛异常）
        rows.Add(new(FormRelative, "relative", ScopeRoot, "relative" + sep + "a.cs", false, outside));

        // ⑧ UNC：绝对化后仍在 scope 之外（不得抛异常）
        rows.Add(new(FormUnc, "unc", ScopeRoot, @"\\server\share\a.cs", false, outside));

        // ⑨ .. 穿越 ⇒ 在外
        rows.Add(new(FormDotDotTraversal, "direct-parent", ScopeRoot, Path.Combine(ScopeRoot, "..", "outside.cs"), false, outside));
        rows.Add(new(FormDotDotTraversal, "deep-parent", ScopeRoot, Path.Combine(ScopeRoot, "sub", "..", "..", "outside.cs"), false, outside));

        // ⑩ 空白 / 空串：Coalesce 命中 BlankPath；IsWithinScope 则**抛 ArgumentException**（ExpectWithin = null）
        rows.Add(new(FormBlank, "empty-string", ScopeRoot, string.Empty, null, FullTextPathRejectionReason.BlankPath));
        rows.Add(new(FormBlank, "whitespace-only", ScopeRoot, "   ", null, FullTextPathRejectionReason.BlankPath));

        // ⑪ 尾随空格 / 尾随点（Windows 归一化陷阱）：本层规范化只做 GetFullPath + 去尾分隔符，
        //    不裁尾随空格/点 ⇒ 判定与「同名子路径」一致（在内）。真实文件系统会裁掉它们，
        //    因此这是本层规范化口径的**已知局限**（已在报告中登记）。
        rows.Add(new(FormTrailingSpaceOrDot, "trailing-space", ScopeRoot, Path.Combine(ScopeRoot, "a.cs") + " ", true, null));
        rows.Add(new(FormTrailingSpaceOrDot, "trailing-dot", ScopeRoot, Path.Combine(ScopeRoot, "a."), true, null));

        // ⑫ 驱动器相对（E:foo）：GetFullPath 解析到别的盘 ⇒ 在外（不得抛异常）
        rows.Add(new(FormDriveRelative, "drive-relative", ScopeRoot, "E:foo", false, outside));

        // ⑬ 超长路径（≥ 260 字符）⇒ 不抛异常，判定与外层一致
        rows.Add(new(FormLongPath, "over-260-chars", ScopeRoot, Path.Combine(ScopeRoot, new string('a', 300) + ".cs"), true, null));

        return rows;
    }

    [TestMethod]
    public void PathMatrix_EveryRowAssertsIsWithinScopeAndTheRejectionReason()
    {
        var rows = BuildRows();

        Assert.AreEqual(DeclaredRowCount, rows.Count, "行数自证：矩阵实际行数必须等于声明行数");
        Assert.AreEqual(
            DeclaredFormCount,
            rows.Select(r => r.Form).Distinct(StringComparer.Ordinal).Count(),
            "形态数自证：必须覆盖声明的 13 种形态");

        var evaluatedForms = new SortedSet<string>(StringComparer.Ordinal);
        var evaluatedRows = 0;

        foreach (var row in rows)
        {
            evaluatedRows++;
            evaluatedForms.Add(row.Form);
            var label = $"{row.Form}/{row.RowName}";

            // ① IsWithinScope 判定
            if (row.ExpectWithin is null)
            {
                var threw = false;
                try
                {
                    FullTextChangeCoalescer.IsWithinScope(row.ScopeArg, row.PathArg);
                }
                catch (ArgumentException)
                {
                    threw = true;
                }

                Assert.IsTrue(threw, $"{label}: 空白路径必须抛 ArgumentException，而不是静默给出判定");
            }
            else
            {
                Assert.AreEqual(
                    row.ExpectWithin.Value,
                    FullTextChangeCoalescer.IsWithinScope(row.ScopeArg, row.PathArg),
                    $"{label}: IsWithinScope 判定不符（scope={row.ScopeArg}; path={row.PathArg}）");
            }

            // ② Coalesce 的拒绝原因（必须点名 Reason）
            var result = FullTextChangeCoalescer.Coalesce(
                new[] { new FullTextChangeObservation(row.PathArg, FullTextChangeSource.Watcher, FullTextPathObservation.IndexableFile) },
                row.ScopeArg);

            if (row.ExpectReason is null)
            {
                Assert.AreEqual(row.ExpectWithin, true, $"{label}: 行定义自相矛盾（未声明拒绝原因却声明不在 scope 内）");
                Assert.AreEqual(0, result.RejectedPaths.Count, $"{label}: 在 scope 内不得被拒");
                Assert.AreEqual(1, result.Changes.Count, $"{label}: 在 scope 内必须产出恰好一个动作");
                Assert.AreEqual(FullTextChangeKind.Upsert, result.Changes[0].Kind, $"{label}: 可索引文件 ⇒ Upsert");
                Assert.AreEqual(row.PathArg, result.Changes[0].FullPath, $"{label}: 动作必须回填原始写法");
                Assert.AreEqual(0, result.DeferredPaths.Count, $"{label}: 不得进入待重试");
            }
            else
            {
                Assert.AreEqual(0, result.Changes.Count, $"{label}: 被拒路径不得进入变更集");
                Assert.AreEqual(1, result.RejectedPaths.Count, $"{label}: 必须恰好一条拒绝记录");
                Assert.AreEqual(row.ExpectReason.Value, result.RejectedPaths[0].Reason, $"{label}: 拒绝原因不符（必须点名 Reason）");
                Assert.AreEqual(row.PathArg, result.RejectedPaths[0].FullPath, $"{label}: 拒绝记录必须回填原始路径");
                Assert.IsTrue(result.RejectedPaths[0].Message.Length > 0, $"{label}: 拒绝必须带可读消息");

                if (row.ExpectWithin is not null)
                    Assert.IsFalse(row.ExpectWithin.Value, $"{label}: 行定义自相矛盾（声明拒绝却声明在 scope 内）");
            }
        }

        Assert.AreEqual(DeclaredRowCount, evaluatedRows, "矩阵必须逐行执行（行数自证）");
        Assert.AreEqual(DeclaredFormCount, evaluatedForms.Count, "每个形态都必须至少被实际执行一次");
    }

    /// <summary>
    /// ★ 前缀同名兄弟专项：6 种后缀形态 × 2 个层级，逐个断言 <c>OutsideScope</c>。
    /// 这条是 M2 变异（让 <see cref="FullTextChangeCoalescer"/> 的越界判据恒返回 true）的判别断言。
    /// </summary>
    [TestMethod]
    public void PathMatrix_PrefixSiblings_AreNeverSwallowedAsInScope()
    {
        var siblings = new[]
        {
            ScopeRoot + "-2",
            Path.Combine(ScopeRoot + "-2", "a.cs"),
            ScopeRoot + "X",
            Path.Combine(ScopeRoot + "X", "a.cs"),
            ScopeRoot + ".bak",
            Path.Combine(ScopeRoot + ".bak", "a.cs"),
        };

        Assert.AreEqual(6, siblings.Length, "前缀同名兄弟形态数自证");

        var evaluated = 0;
        foreach (var sibling in siblings)
        {
            evaluated++;

            Assert.IsFalse(
                FullTextChangeCoalescer.IsWithinScope(ScopeRoot, sibling),
                $"前缀同名兄弟 {sibling} 不得被判为在 scope 内（前缀比较退化的典型误收）");

            var result = FullTextChangeCoalescer.Coalesce(
                new[] { new FullTextChangeObservation(sibling, FullTextChangeSource.Watcher, FullTextPathObservation.IndexableFile) },
                ScopeRoot);

            Assert.AreEqual(0, result.Changes.Count, $"{sibling} 不得进入变更集");
            Assert.AreEqual(1, result.RejectedPaths.Count, $"{sibling} 必须被如实拒绝");
            Assert.AreEqual(FullTextPathRejectionReason.OutsideScope, result.RejectedPaths[0].Reason, $"{sibling} 的拒绝原因必须是 OutsideScope");
        }

        Assert.AreEqual(6, evaluated, "前缀同名兄弟矩阵必须逐行执行");
    }

    /// <summary>分隔符与大小写的**等价类**：任一变体都不得改变判定（Windows First）。</summary>
    [TestMethod]
    public void PathMatrix_SeparatorAndCaseVariants_AllResolveToTheSameVerdict()
    {
        var child = Path.Combine(ScopeRoot, "sub", "a.cs");

        var scopeVariants = new[]
        {
            ScopeRoot,
            ScopeRoot.ToUpperInvariant(),
            ScopeRoot.ToLowerInvariant(),
            ScopeRoot + Path.DirectorySeparatorChar,
            ScopeRoot + Path.AltDirectorySeparatorChar,
            ScopeRoot.Replace('\\', '/'),
            ScopeRoot.ToUpperInvariant().Replace('\\', '/'),
        };

        var pathVariants = new[]
        {
            child,
            child.ToUpperInvariant(),
            child.Replace('\\', '/'),
            child.ToUpperInvariant().Replace('\\', '/'),
            child + Path.DirectorySeparatorChar,
        };

        var evaluated = 0;
        foreach (var scope in scopeVariants)
        {
            foreach (var path in pathVariants)
            {
                evaluated++;

                Assert.IsTrue(
                    FullTextChangeCoalescer.IsWithinScope(scope, path),
                    $"(scope={scope}; path={path}) 分隔符/大小写变体必须判为在 scope 内");

                var result = FullTextChangeCoalescer.Coalesce(
                    new[] { new FullTextChangeObservation(path, FullTextChangeSource.IntegrityCheck, FullTextPathObservation.IndexableFile) },
                    scope);

                Assert.AreEqual(0, result.RejectedPaths.Count, $"(scope={scope}; path={path}) 变体不得被拒");
                Assert.AreEqual(1, result.Changes.Count, $"(scope={scope}; path={path}) 必须恰好一个动作");
            }
        }

        Assert.AreEqual(scopeVariants.Length * pathVariants.Length, evaluated, "等价类矩阵行数自证");
    }
}
