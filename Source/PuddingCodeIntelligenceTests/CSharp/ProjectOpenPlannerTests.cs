using PuddingCodeIntelligence.CSharp;

namespace PuddingCodeIntelligenceTests.CSharp;

/// <summary>
/// 门禁：显式打开项目之前必须去掉「重复」与「已作为依赖装载」两类项目。
/// <para>
/// 判据不是"少打开一个"，而是这条链路的两端：
/// ① 同一路径在候选里出现两次 → 只留一个（否则第二次必定抛"已经是工作区的一部分"）；
/// ② 已装载的项目（大小写不同也算同一个）→ 不再调用；
/// ③ 顺序保持输入顺序，且 <c>null</c>/空白被忽略而不是让整批失败。
/// </para>
/// </summary>
[TestClass]
public sealed class ProjectOpenPlannerTests
{
    private static readonly string Root =
        Path.Combine(Path.GetTempPath(), "pudding-open-planner", "Repo");

    private static string Project(string relative) => Path.Combine(Root, relative);

    [TestMethod]
    public void SamePathListedTwice_OpensOnce()
    {
        var path = Project(@"Source\A\A.csproj");

        var planned = ProjectOpenPlanner.PlanOpenOrder([path, path], null);

        Assert.AreEqual(1, planned.Count);
        Assert.AreEqual(path, planned[0]);
    }

    [TestMethod]
    public void SamePathInDifferentCase_IsTreatedAsOneProject()
    {
        var upper = Project(@"Source\A\A.csproj");
        var lower = Project(@"source\a\a.csproj");

        var planned = ProjectOpenPlanner.PlanOpenOrder([upper, lower], null);

        Assert.AreEqual(
            1,
            planned.Count,
            "Windows 路径大小写不敏感；用序数比较会把同一项目当成两个，去重静默失效。");
    }

    [TestMethod]
    public void ProjectAlreadyLoadedAsDependency_IsNotOpenedAgain()
    {
        var dependency = Project(@"Source\Cli\Cli.csproj");
        var test = Project(@"Source\Cli.Tests\Cli.Tests.csproj");

        // MSBuild 打开 Cli.Tests 时会把 Cli 作为依赖一并装载。
        var planned = ProjectOpenPlanner.PlanOpenOrder(
            [test, dependency],
            [test, dependency]);

        Assert.AreEqual(0, planned.Count);
    }

    [TestMethod]
    public void OnlyTheMissingProjectIsOpened_OrderPreserved()
    {
        var a = Project(@"Source\A\A.csproj");
        var b = Project(@"Source\B\B.csproj");
        var c = Project(@"Source\C\C.csproj");

        var planned = ProjectOpenPlanner.PlanOpenOrder([a, b, c], [b]);

        CollectionAssert.AreEqual(new[] { a, c }, planned.ToArray());
    }

    [TestMethod]
    public void NullEmptyAndWhitespaceEntries_AreIgnoredWithoutFailingTheBatch()
    {
        var real = Project(@"Source\A\A.csproj");

        var planned = ProjectOpenPlanner.PlanOpenOrder(
            [null!, "", "   ", real],
            [null, "", "   "]);

        Assert.AreEqual(1, planned.Count);
        Assert.AreEqual(real, planned[0]);
    }

    [TestMethod]
    public void RelativeAndAbsolutePathsForTheSameProject_DedupeToTheFirstOne()
    {
        var absolute = Project(@"Source\A\A.csproj");
        var relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), absolute);

        var planned = ProjectOpenPlanner.PlanOpenOrder([absolute, relative], null);

        Assert.AreEqual(
            1,
            planned.Count,
            "相对路径必须先归一化为绝对路径，否则同一个项目会被当成两个。");
        Assert.AreEqual(absolute, planned[0]);
    }

    [TestMethod]
    public void NullCandidates_YieldEmptyPlanInsteadOfThrowing()
    {
        var planned = ProjectOpenPlanner.PlanOpenOrder(null, [Project(@"Source\A\A.csproj")]);

        Assert.AreEqual(0, planned.Count);
    }
}
