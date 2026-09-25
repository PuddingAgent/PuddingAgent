using System.Reflection;
using System.Text.RegularExpressions;
using PuddingFullTextIndex.Contracts;
using PuddingFullTextIndex.Infrastructure;
using PuddingFullTextIndex.Infrastructure.Search;

namespace PuddingFullTextIndexTests;

/// <summary>
/// S1b：patterns 指纹（<c>.last_indexed</c> 的 <c>p</c>）**单一真源** <see cref="FullTextPolicyFingerprint"/>
/// 的组件侧断言。
/// <para>
/// <b>§2.3a golden</b>：下表的期望值由<b>父级独立算出</b>（pwsh + BCL），并且 <c>(default)</c> 一行已与
/// <b>生产实际索引</b>交叉核对 —— <c>D:\data\fulltext-index\875d6cb5….\.last_indexed</c>
/// = <c>{"t":"2026-09-25T07:48:46.5108139Z","p":"b3ffbbff2d64"}</c>（根 scope 用默认 patterns 建），
/// 因此这里不是"算给自己看"的同义反复。
/// </para>
/// <para>
/// <b>§2.3c 组件边界</b>：本文件同时冻结依赖方向 —— 组件<b>只</b>允许引用 <c>PuddingPathFiltering</c>，
/// 且必须是"确实找到了引用"之后再判禁列（带对照断言防空集恒真）。
/// </para>
/// </summary>
[TestClass]
public sealed class FullTextPolicyFingerprintTests
{
    // ── §2.3a 冻结的期望值（父级独立算出；不可由被测代码生成）──────────────────

    /// <summary><c>null</c> 与字面量 <c>"(default)"</c> 必须给出**同一个**指纹（缺省值即该字面量）。</summary>
    private const string FrozenDefault = "b3ffbbff2d64";

    /// <summary>大小写敏感：<c>"(DEFAULT)"</c> 是**另一个**值（不做任何归一化）。</summary>
    private const string FrozenUpperCaseDefault = "3d3f863b69d4";

    /// <summary>空串<b>不回退</b>到缺省值 —— <c>e3b0c44298fc</c> 是空输入的 SHA256 前缀。</summary>
    private const string FrozenEmptyString = "e3b0c44298fc";

    // ── §2.3a：8 条 golden（每条一个用例，名字即证据）────────────────────────

    /// <summary>golden ①：<c>null</c> ⇒ 回退字面量 <c>"(default)"</c>。</summary>
    [TestMethod]
    public void S1b_Golden_1_Null_Falls_Back_To_The_Default_Literal()
    {
        Assert.AreEqual(FrozenDefault, FullTextPolicyFingerprint.ComputePatternFingerprint(null));
    }

    /// <summary>golden ②：显式字面量 <c>"(default)"</c> 与 <c>null</c> 同值（缺省值不被"再哈希一次"）。</summary>
    [TestMethod]
    public void S1b_Golden_2_The_Default_Literal_Equals_The_Null_Result()
    {
        Assert.AreEqual(FrozenDefault, FullTextPolicyFingerprint.ComputePatternFingerprint("(default)"));
        Assert.AreEqual(
            FullTextPolicyFingerprint.ComputePatternFingerprint(null),
            FullTextPolicyFingerprint.ComputePatternFingerprint("(default)"),
            "null 与字面量 (default) 必须不可区分 —— 否则现存 .last_indexed 会被判成 patterns 变了");
    }

    /// <summary>golden ③：<c>"*.cs"</c>。</summary>
    [TestMethod]
    public void S1b_Golden_3_Single_Cs_Pattern()
    {
        Assert.AreEqual("5fdf864ecfba", FullTextPolicyFingerprint.ComputePatternFingerprint("*.cs"));
    }

    /// <summary>golden ④：<c>"*.md"</c>。</summary>
    [TestMethod]
    public void S1b_Golden_4_Single_Md_Pattern()
    {
        Assert.AreEqual("ba405b6cd142", FullTextPolicyFingerprint.ComputePatternFingerprint("*.md"));
    }

    /// <summary>golden ⑤：<c>"*.cs;*.md"</c>（分号列表按**原文**哈希，不拆分、不排序）。</summary>
    [TestMethod]
    public void S1b_Golden_5_Semicolon_List()
    {
        Assert.AreEqual("76d435e433a6", FullTextPolicyFingerprint.ComputePatternFingerprint("*.cs;*.md"));
    }

    /// <summary>golden ⑥：<c>"*.ts"</c>。</summary>
    [TestMethod]
    public void S1b_Golden_6_Single_Ts_Pattern()
    {
        Assert.AreEqual("f084c4065fc9", FullTextPolicyFingerprint.ComputePatternFingerprint("*.ts"));
    }

    /// <summary>golden ⑦：大小写敏感 —— <c>"(DEFAULT)"</c> 与 <c>"(default)"</c> 必须是两个值。</summary>
    [TestMethod]
    public void S1b_Golden_7_Patterns_Are_Case_Sensitive()
    {
        Assert.AreEqual(FrozenUpperCaseDefault, FullTextPolicyFingerprint.ComputePatternFingerprint("(DEFAULT)"));
        Assert.AreNotEqual(
            FrozenDefault,
            FullTextPolicyFingerprint.ComputePatternFingerprint("(DEFAULT)"),
            "必须大小写敏感：任何「顺手归一化」都会让现存索引失配");
    }

    /// <summary>golden ⑧：空串<b>不回退</b>（缺省与「空 patterns」必须可区分）。</summary>
    [TestMethod]
    public void S1b_Golden_8_Empty_String_Does_Not_Fall_Back()
    {
        Assert.AreEqual(FrozenEmptyString, FullTextPolicyFingerprint.ComputePatternFingerprint(""));
        Assert.AreNotEqual(
            FrozenDefault,
            FullTextPolicyFingerprint.ComputePatternFingerprint(""),
            "只有 null 才回退；空串必须算出自己的指纹");
    }

    /// <summary>
    /// §2.1 三条"防顺手优化"的机械形状：输出恒为 <b>12 位小写</b> hex（不是大写、不是 16 位、不是 64 位）。
    /// </summary>
    [TestMethod]
    public void S1b_Shape_Output_Is_Always_Twelve_Lowercase_Hex_Chars()
    {
        string?[] inputs = { null, "", "(default)", "(DEFAULT)", "*.cs", "*.md", "*.cs;*.md", "*.ts", "  ", "*.cs;*.cs" };

        foreach (var input in inputs)
        {
            var shown = input is null ? "null" : "[" + input + "]";
            var actual = FullTextPolicyFingerprint.ComputePatternFingerprint(input);

            Assert.AreEqual(12, actual.Length, $"指纹长度必须是 12（输入：{shown}）");
            Assert.AreEqual(actual.ToLowerInvariant(), actual, $"必须是小写 hex（输入：{shown}）");
            StringAssert.Matches(actual, new Regex("^[0-9a-f]{12}$"), $"必须是小写 hex（输入：{shown}）");
        }
    }

    // ── §2.3c：组件边界断言 ────────────────────────────────────────────────

    /// <summary>禁止出现在组件 <c>ProjectReference</c> 里的工程（依赖方向必须由**编译期/断言**强制）。</summary>
    internal static readonly string[] ForbiddenProjectNames =
    {
        "PuddingHost",
        "PuddingRuntime",
        "PuddingCodeIndex",
        "PuddingPlatform",
        "PuddingFullTextIndex.Cli",
    };

    /// <summary>组件**唯一**允许引用的工程（ADR-089 U4-4 D4：排除名单的真源）。</summary>
    internal static readonly string[] AllowedProjectNames = { "PuddingPathFiltering" };

    /// <summary>
    /// §2.3c：读组件 <c>csproj</c> 文本，断言不存在指向 <see cref="ForbiddenProjectNames"/> 的
    /// <c>ProjectReference</c>；并带**对照断言**（"确实找到了 ≥1 个引用"），
    /// 否则文件被挪走 / 正则失效时会退化成"空集恒真"的假绿。
    /// </summary>
    [TestMethod]
    public void S1b_Boundary_Component_Csproj_References_Only_Allowed_Projects()
    {
        var csprojPath = ResolveComponentCsprojPath();
        AssertCsprojHasNoForbiddenProjectReference(csprojPath);
    }

    /// <summary>
    /// §2.3c 建议的第二条（运行时）：程序集引用列表里不得出现被禁工程名。
    /// 与文本断言**互补** —— 文本断言守 csproj，运行时断言守"实际编译产物"。
    /// </summary>
    [TestMethod]
    public void S1b_Boundary_Component_Assembly_References_No_Forbidden_Assembly()
    {
        var names = typeof(LuceneSearchEngine).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToArray();

        // ★ 对照断言：先证明"确实拿到了引用列表"，否则下面的循环在空集上恒真（假绿）
        Assert.IsTrue(names.Length >= 1, "对照断言失败：程序集引用列表为空 —— 断言输入为空集（假绿）");
        Assert.IsTrue(
            names.Any(n => n.StartsWith("System.", StringComparison.Ordinal)),
            "对照断言失败：引用列表里没有任何 System.* 程序集，说明这份列表不可信：" + string.Join(", ", names));

        foreach (var forbidden in ForbiddenProjectNames)
        {
            Assert.IsFalse(
                names.Any(n => string.Equals(n, forbidden, StringComparison.OrdinalIgnoreCase)),
                $"组件程序集不得引用 {forbidden}（实际引用：{string.Join(", ", names)}）");
        }
    }

    /// <summary>
    /// 边界断言的**可复用实现**（同时供 M3 变异取红时喂"被注入禁列引用的 csproj 副本"使用）。
    /// </summary>
    internal static void AssertCsprojHasNoForbiddenProjectReference(string csprojPath)
    {
        Assert.IsTrue(File.Exists(csprojPath), $"找不到组件工程文件：{csprojPath}");

        var text = File.ReadAllText(csprojPath);
        var references = Regex
            .Matches(text, "<ProjectReference\\s+Include=\"([^\"]+)\"")
            .Select(m => m.Groups[1].Value)
            .ToArray();

        // ★ 对照断言（防空集恒真）：先证明"确实找到了引用"，再判禁列
        Assert.IsTrue(
            references.Length >= 1,
            $"对照断言失败：在 {csprojPath} 里一个 <ProjectReference> 都没找到 —— " +
            "断言输入退化为空集（文件被挪走或正则失效时会假绿）");

        var names = references
            .Select(r => Path.GetFileNameWithoutExtension(r.Replace('\\', '/').TrimEnd('/')))
            .ToArray();

        foreach (var forbidden in ForbiddenProjectNames)
        {
            Assert.IsFalse(
                names.Contains(forbidden, StringComparer.OrdinalIgnoreCase),
                $"{csprojPath} 不得引用 {forbidden}（实际引用：{string.Join(", ", names)}）");
        }

        foreach (var name in names)
        {
            Assert.IsTrue(
                AllowedProjectNames.Contains(name, StringComparer.OrdinalIgnoreCase),
                $"组件只允许引用 {string.Join(", ", AllowedProjectNames)}，但发现了 {name}（来自 {csprojPath}）");
        }
    }

    /// <summary>
    /// 从测试程序集所在目录向上找仓库根（判据 = <c>Source\PuddingFullTextIndex\…csproj</c> 存在），
    /// 并**钉住身份**（该文件必须含守卫本文所用的 <c>InternalsVisibleTo</c>），
    /// 避免断言读到的其实是另一个同名文件。
    /// </summary>
    private static string ResolveComponentCsprojPath()
    {
        var searched = new List<string>();
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir != null)
        {
            searched.Add(dir.FullName);
            var candidate = Path.Combine(
                dir.FullName, "Source", "PuddingFullTextIndex", "PuddingFullTextIndex.csproj");
            if (File.Exists(candidate))
            {
                // 身份钉死：确认真的是「被测组件」的工程（否则断言可能在读一个陌生同名文件）
                var text = File.ReadAllText(candidate);
                Assert.IsTrue(
                    text.Contains("PuddingFullTextIndexTests", StringComparison.Ordinal),
                    $"解析到的工程文件不像被测组件（缺少 InternalsVisibleTo）：{candidate}");
                return candidate;
            }

            dir = dir.Parent;
        }

        Assert.Fail($"从 {AppContext.BaseDirectory} 向上未能定位组件工程；已搜索：{string.Join(" | ", searched)}");
        return string.Empty; // 不可达
    }
}
