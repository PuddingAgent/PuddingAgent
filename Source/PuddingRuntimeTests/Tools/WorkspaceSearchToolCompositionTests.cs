using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCodeIndex.Contracts.Retrieval;
using PuddingCodeIndex.Services.Retrieval;
using PuddingRuntime;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// <c>workspace_search</c> 的装配冒烟测试。
/// <para>
/// <b>为什么必须有这一条</b>：<c>PuddingToolRegistry</c> 构造函数会把
/// <c>IEnumerable&lt;IPuddingTool&gt;</c> 全量材料化 ⇒ 任何一个工具解析失败都是
/// <b>启动期</b>失败。工具类一旦带 <c>[Tool]</c> 特性就会被扫描到，
/// 所以"依赖注册齐全"必须由一条会失败的测试来证明，而不是靠人工核对注册表。
/// </para>
/// <para>
/// 只解析 <see cref="IRetrievalPort"/>：WorkspaceSearchTool 的唯一依赖就是它，
/// 端口能解析 ⇒ 工具能构造。不去全量枚举 IPuddingTool，避免把无关工具的外部依赖
/// （Everything SDK、浏览器桥等）拖进这条测试。
/// </para>
/// </summary>
[TestClass]
public sealed class WorkspaceSearchToolCompositionTests
{
    [TestMethod]
    public void RetrievalPortResolvesFromRuntimeContainer()
    {
        var services = new ServiceCollection();
        // 宿主会在更早的阶段注册它；测试里手工补上，否则校验的就不是检索链而是环境本身。
        services.AddSingleton(PuddingCode.Configuration.PuddingDataPaths.FromRoot(
            Path.Combine(Path.GetTempPath(), "pudding-retrieval-composition")));
        services.AddPuddingRuntime();

        using var provider = services.BuildServiceProvider();

        var port = provider.GetService<IRetrievalPort>();

        Assert.IsNotNull(
            port,
            "IRetrievalPort 必须能从运行时容器解析 —— 否则 workspace_search 会让宿主在启动期失败。");
        Assert.IsInstanceOfType<RetrievalPortV1>(port);
    }

    [TestMethod]
    public void ToolTypeDependsOnlyOnRetrievalPort()
    {
        var ctors = typeof(WorkspaceSearchTool).GetConstructors();

        Assert.AreEqual(1, ctors.Length, "workspace_search 只应有一个构造函数");

        var parameters = ctors[0].GetParameters();

        Assert.AreEqual(1, parameters.Length, "workspace_search 只应依赖 IRetrievalPort");
        Assert.AreEqual(
            typeof(IRetrievalPort),
            parameters[0].ParameterType,
            "新增依赖会扩大启动期失败面；若确实需要，请同时补一条解析冒烟。");
    }
}
