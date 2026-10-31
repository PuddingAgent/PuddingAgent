using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingRetrieval.Adapters;
using PuddingCodeIndex.Contracts;
using PuddingRetrieval.Contracts;
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

    /// <summary>
    /// `workspace_open` 的构造参数全部带默认值 ⇒ **即使容器里什么都没注册也能解析**。
    /// 这条防的是 2026-10-29 那类启动期崩溃（exit -532462766）：工具被扫描登记后
    /// DI 会构造每一个 <c>IPuddingTool</c>，多一个无默认值的依赖就多一个地雷。
    /// </summary>
    [TestMethod]
    public void WorkspaceOpenTool_ResolvesWithEmptyContainer()
    {
        var services = new ServiceCollection();
        // 与 AddPuddingToolsFromAssembly 的登记形态一致：IPuddingTool → 工具类型（实现类型单例）。
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton(typeof(PuddingCode.Tools.IPuddingTool), typeof(WorkspaceOpenTool)));

        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true });

        var tools = provider.GetServices<PuddingCode.Tools.IPuddingTool>().ToList();

        Assert.IsTrue(
            tools.Any(t => t is WorkspaceOpenTool),
            "空容器下 workspace_open 必须能解析（构造参数全带默认值）；否则宿主启动期会抛 AggregateException。");
    }

    /// <summary>
    /// `workspace_search` 只允许**一个必填**依赖（<see cref="IRetrievalPort"/>），其余必须带默认值。
    /// <para>
    /// 这条防的还是 2026-10-29 那类启动期崩溃：工具被扫描登记后 DI 会构造每一个
    /// <c>IPuddingTool</c>，多一个**无默认值**的依赖就多一个地雷。
    /// </para>
    /// <para>
    /// <b>2026-10-30 改口径</b>：断言从「参数个数 == 1」改成「只有第一个没有默认值」。
    /// 前者把「新增可选协作者」误判成缺陷，而真正要防的是「新增**必填**依赖」——
    /// 后者会把整个宿主拖在启动期，前者不会。
    /// </para>
    /// </summary>
    [TestMethod]
    public void ToolTypeHasOnlyOneRequiredDependency()
    {
        var ctors = typeof(WorkspaceSearchTool).GetConstructors();

        Assert.AreEqual(1, ctors.Length, "workspace_search 只应有一个构造函数");

        var parameters = ctors[0].GetParameters();

        Assert.IsTrue(parameters.Length >= 1, "至少要有检索端口");
        Assert.AreEqual(typeof(IRetrievalPort), parameters[0].ParameterType);
        Assert.IsFalse(
            parameters[0].HasDefaultValue,
            "IRetrievalPort 必须是必填依赖（否则检索端口缺失会被静默当成空壳工具）");

        foreach (var optional in parameters.Skip(1))
        {
            Assert.IsTrue(
                optional.HasDefaultValue,
                $"新增依赖 {optional.Name} 必须带默认值 —— 否则宿主启动期会抛 AggregateException。");
        }
    }
}
