using PuddingRuntime.Services.AgentLoop;

namespace PuddingRuntimeTests.Services.AgentLoop;

/// <summary>
/// 方案 3 的临时入口：从配置创建目标。
/// <para>
/// 这一层刻意做成纯函数 + 注入读取器，所以测试**完全不碰真实环境变量** ——
/// 否则跑测试会污染开发者的 shell 环境。
/// </para>
/// </summary>
[TestClass]
public sealed class GoalBootstrapTests
{
    private static GoalRegistry NewRegistry() => new();

    [TestMethod]
    public void No_objective_creates_nothing()
    {
        var registry = NewRegistry();

        var state = GoalBootstrap.CreateIfConfigured(registry, "s1", () => null);

        Assert.IsNull(state);
        Assert.IsNull(registry.Get("s1"), "未配置时不得留下任何目标");
    }

    [TestMethod]
    public void Blank_objective_creates_nothing()
    {
        var registry = NewRegistry();

        Assert.IsNull(GoalBootstrap.CreateIfConfigured(registry, "s1", () => "   "));
        Assert.IsNull(registry.Get("s1"));
    }

    [TestMethod]
    public void Invalid_session_creates_nothing()
    {
        var registry = NewRegistry();

        Assert.IsNull(GoalBootstrap.CreateIfConfigured(registry, "  ", () => "目标"));
        Assert.IsNull(GoalBootstrap.CreateIfConfigured(registry, null, () => "目标"));
    }

    [TestMethod]
    public void Objective_is_trimmed_and_goal_is_created()
    {
        var registry = NewRegistry();

        var state = GoalBootstrap.CreateIfConfigured(registry, "s1", () => "  接完 goal 链路  ");

        Assert.IsNotNull(state);
        Assert.AreEqual("接完 goal 链路", state!.Objective);
        Assert.AreEqual(GoalBootstrap.DefaultMaxRounds, state.MaxRounds);
        Assert.AreEqual(GoalActivation.Armed, state.Activation, "新建目标默认武装，才可能自动续跑");
    }

    [TestMethod]
    public void Max_rounds_is_parsed_and_clamped()
    {
        Assert.AreEqual(8, GoalBootstrap.ParseMaxRounds(null));
        Assert.AreEqual(8, GoalBootstrap.ParseMaxRounds("abc"));
        Assert.AreEqual(8, GoalBootstrap.ParseMaxRounds("  "));
        Assert.AreEqual(3, GoalBootstrap.ParseMaxRounds(" 3 "));
        Assert.AreEqual(GoalBootstrap.MinMaxRounds, GoalBootstrap.ParseMaxRounds("0"));
        Assert.AreEqual(GoalBootstrap.MinMaxRounds, GoalBootstrap.ParseMaxRounds("-5"));
        Assert.AreEqual(GoalBootstrap.MaxMaxRounds, GoalBootstrap.ParseMaxRounds("9999"));
    }

    [TestMethod]
    public void Existing_goal_is_returned_instead_of_creating_another()
    {
        var registry = NewRegistry();
        var first = GoalBootstrap.CreateIfConfigured(registry, "s1", () => "第一个目标");

        var second = GoalBootstrap.CreateIfConfigured(registry, "s1", () => "第二个目标");

        Assert.AreEqual(first!.GoalId, second!.GoalId, "重复调用必须幂等，不能抛错也不能换目标");
        Assert.AreEqual("第一个目标", registry.Get("s1")!.Objective);
    }
}
