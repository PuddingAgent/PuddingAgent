using Pudding.Contracts.Computer;
using PuddingComputer.Benchmark;

namespace PuddingComputer.BenchmarkTests;

/// <summary>
/// <see cref="BenchmarkCatalog"/> 的加载 / 往返 / 校验。
/// <para>
/// 这些断言守的不是"能跑"，而是"清单坏了要报错"：重复 Id 会让报告无法归因，
/// 字段缺失会让跑批在半路崩，oracle 类型写错会让判定静默失效。
/// </para>
/// </summary>
[TestClass]
public sealed class BenchmarkCatalogTests
{
    private static string CatalogPath =>
        Path.Combine(AppContext.BaseDirectory, "benchmarks", "native-apps.json");

    /// <summary>随包发布的清单必须能加载且通过校验。</summary>
    [TestMethod]
    public void ShippedCatalog_LoadsAndValidates()
    {
        Assert.IsTrue(File.Exists(CatalogPath), $"找不到清单：{CatalogPath}");

        var tasks = BenchmarkCatalog.Load(File.ReadAllText(CatalogPath));

        Assert.IsGreaterThan(0, tasks.Count, "清单不应为空");
        var problems = BenchmarkCatalog.Validate(tasks);
        Assert.IsEmpty(problems, $"清单校验未通过：{string.Join("; ", problems)}");
    }

    /// <summary>
    /// 清单里的 oracle 必须反序列化成具体类型，不能退化成基类。
    /// <para>
    /// 只断言清单当前实际用到的两种；新增任务类型时再补。
    /// 断言「五种都要有」在清单经实测精简后会变成假命题。
    /// </para>
    /// </summary>
    [TestMethod]
    public void ShippedCatalog_OracleTypesDeserializeConcretely()
    {
        var tasks = BenchmarkCatalog.Load(File.ReadAllText(CatalogPath));

        var kinds = tasks.Select(t => t.Oracle.GetType()).Distinct().ToList();

        Assert.Contains(typeof(UiaPropertyOracle), kinds);
        Assert.Contains(typeof(ElementStateOracle), kinds);

        foreach (var t in tasks)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(t.Oracle.Description),
                $"[{t.Id}] oracle 缺少描述，可能反序列化成了基类");
        }
    }

    /// <summary>
    /// **实测教训**：不能只靠 AutomationId 定位。
    /// <para>
    /// 新版 Windows 记事本的编辑区根本没有 AutomationId，只能靠
    /// ControlType=Document + ClassName=RichEditD2DPT 定位。
    /// 所以清单里必须至少有一个「无 AutomationId 的选择器」。
    /// </para>
    /// </summary>
    [TestMethod]
    public void ShippedCatalog_HasSelectorWithoutAutomationId()
    {
        var tasks = BenchmarkCatalog.Load(File.ReadAllText(CatalogPath));

        bool hasNonIdSelector = tasks.Any(t => t.Oracle switch
        {
            UiaPropertyOracle u => string.IsNullOrWhiteSpace(u.Selector.AutomationId) && u.Selector.HasAny,
            ElementStateOracle e => string.IsNullOrWhiteSpace(e.Selector.AutomationId) && e.Selector.HasAny,
            _ => false,
        });

        Assert.IsTrue(hasNonIdSelector,
            "清单里必须有用属性组合而非 AutomationId 定位的元素");
    }

    /// <summary>加载 → 保存 → 再加载，内容必须一致。</summary>
    [TestMethod]
    public void SaveThenLoad_RoundTrips()
    {
        var original = BenchmarkCatalog.Load(File.ReadAllText(CatalogPath));

        var json = BenchmarkCatalog.Save(original);
        var reloaded = BenchmarkCatalog.Load(json);

        Assert.HasCount(original.Count, reloaded);
        for (int i = 0; i < original.Count; i++)
        {
            Assert.AreEqual(original[i].Id, reloaded[i].Id);
            Assert.AreEqual(original[i].Risk, reloaded[i].Risk);
            Assert.AreEqual(original[i].Oracle.GetType(), reloaded[i].Oracle.GetType());
            Assert.AreEqual(original[i].Oracle.Description, reloaded[i].Oracle.Description);
        }
    }

    /// <summary>
    /// risk 必须序列化成可读的 kebab-case 字符串。
    /// 数字形式在人工审清单时看不出风险等级，那正是最容易漏掉"这是不可盲重试动作"的地方。
    /// </summary>
    [TestMethod]
    public void Save_WritesRiskAsReadableString()
    {
        var tasks = new[]
        {
            new BenchmarkTask("t", "app", "setup", "goal", RiskClass.NonIdempotent, 3,
                new WindowOrDialogOracle("x")),
        };

        var json = BenchmarkCatalog.Save(tasks);

        Assert.Contains("non-idempotent", json);
        Assert.DoesNotContain("\"risk\": 2", json);
    }

    /// <summary>坏的 JSON 必须抛出可读异常，而不是返回空清单。</summary>
    [TestMethod]
    public void Load_OnMalformedJson_Throws()
    {
        Assert.ThrowsExactly<InvalidOperationException>(
            () => BenchmarkCatalog.Load("{ this is not json"));
    }

    /// <summary>Id 重复必须被检出 —— 报告靠它对齐。</summary>
    [TestMethod]
    public void Validate_DetectsDuplicateIds()
    {
        var tasks = new[]
        {
            new BenchmarkTask("dup", "app", "setup", "goal", RiskClass.ReadOnly, 3,
                new WindowOrDialogOracle("x")),
            new BenchmarkTask("dup", "app", "setup", "goal", RiskClass.ReadOnly, 3,
                new WindowOrDialogOracle("y")),
        };

        var problems = BenchmarkCatalog.Validate(tasks);

        Assert.HasCount(1, problems);
        Assert.Contains("Id 重复", problems[0]);
    }

    /// <summary>字段缺失必须被检出（这里用 MaxAttempts=0 与空 Goal）。</summary>
    [TestMethod]
    public void Validate_DetectsMissingFields()
    {
        var tasks = new[]
        {
            new BenchmarkTask("bad", "app", "setup", "", RiskClass.ReadOnly, 0,
                new WindowOrDialogOracle("x")),
        };

        var problems = BenchmarkCatalog.Validate(tasks);

        Assert.IsGreaterThanOrEqualTo(2, problems.Count, "Goal 为空与 MaxAttempts=0 都应被报出");
    }

    /// <summary>空清单通过校验（不算错误，只是没有任务）。</summary>
    [TestMethod]
    public void Validate_EmptyList_IsClean()
    {
        Assert.IsEmpty(BenchmarkCatalog.Validate([]));
    }
}
