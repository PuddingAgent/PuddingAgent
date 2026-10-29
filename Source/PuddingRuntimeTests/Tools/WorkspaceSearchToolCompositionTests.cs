using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Contracts.Retrieval;
using PuddingCodeIndex.Services.Retrieval;
using PuddingCodeIndex.Storage;
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
/// <b>2026-10-29 实测教训</b>：本类原先用 <c>AddPuddingRuntime()</c> 组装容器，而产品宿主
/// <b>不调用</b> AddPuddingRuntime —— 它直接扫描工具程序集。于是测试全绿而宿主启动即崩
/// （ValidateOnBuild 抛 AggregateException，exit -532462766，连续三次部署回滚）。
/// 现在一律使用 <see cref="RetrievalServiceCollectionExtensions.AddPuddingRetrievalPort"/>：
/// 那是宿主真正调用的那个方法，测试与生产共用同一份登记。
/// </para>
/// </summary>
[TestClass]
public sealed class WorkspaceSearchToolCompositionTests
{
    /// <summary>组装宿主同款最小容器：索引存储 + 检索端口（宿主用 Platform 组合根补前者）。</summary>
    private static ServiceCollection CreateHostLikeServices()
    {
        var root = Path.Combine(Path.GetTempPath(), "pudding-retrieval-composition", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var services = new ServiceCollection();
        services.AddSingleton<ICodeIndexStore>(
            new SqliteCodeIndexStore(Path.Combine(root, "code_index.db")));
        // 宿主在 PuddingServiceCollectionExtensions.Runtime.cs 里登记它（WorkspaceRootScopeDirectories 的依赖）。
        // 宿主同样先登记 StartupEnvironmentInfo（见 PuddingServiceCollectionExtensions.Runtime.cs）。
        services.AddSingleton(new PuddingRuntime.Services.StartupEnvironmentInfo());
        services.AddSingleton<PuddingCode.Runtime.IExecutionEnvironmentProvider,
            PuddingRuntime.Services.DefaultExecutionEnvironmentProvider>();
        services.AddPuddingRetrievalPort();
        return services;
    }

    [TestMethod]
    public void RetrievalPortResolvesFromSharedCompositionExtension()
    {
        using var provider = CreateHostLikeServices().BuildServiceProvider();

        var port = provider.GetService<IRetrievalPort>();

        Assert.IsNotNull(
            port,
            "IRetrievalPort 必须能从 AddPuddingRetrievalPort 解析 —— 否则 workspace_search 会让宿主在启动期失败。");
        Assert.IsInstanceOfType<RetrievalPortV1>(port);
    }

    [TestMethod]
    public void ToolActivatesFromSharedCompositionExtension()
    {
        // 直接解析工具本身：这才是 PuddingToolRegistry 在启动期做的事（扫描器把工具登记为 IPuddingTool）。
        var services = CreateHostLikeServices();
        services.AddSingleton<WorkspaceSearchTool>();

        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true });

        var tool = provider.GetRequiredService<WorkspaceSearchTool>();

        Assert.IsNotNull(tool);
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
