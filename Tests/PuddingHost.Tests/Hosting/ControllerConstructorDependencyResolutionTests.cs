using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using PuddingHost.Hosting;

namespace PuddingHost.Tests.Hosting;

/// <summary>
/// 回归防护网（对应缺陷：Admin 存储管理页全端点 500 —— DI 激活失败）。
/// <para>
/// 缺陷现场：<c>StorageInventorySampler</c> 只用 <c>AddHostedService&lt;T&gt;()</c> 注册，
/// 而 .NET 的该方法实现是 <c>TryAddEnumerable(ServiceDescriptor.Singleton&lt;IHostedService, T&gt;())</c>，
/// 即只登记 <c>IHostedService</c> 这个<b>服务类型</b>、不登记具体类型 <c>T</c>；
/// <c>StorageAdminController</c> 却直接注入具体类型 <c>StorageInventorySampler</c>，
/// 于是 MVC 控制器工厂在<b>首个请求</b>时抛
/// <c>InvalidOperationException: Unable to resolve service for type ... while attempting to activate ...</c>，
/// 该控制器全部端点 500（包括不依赖任何服务的纯静态端点）。
/// 容器启动期（含本宿主显式开启的 <c>ValidateOnBuild = true</c>）<b>不报任何错</b>，
/// 因为控制器默认不作为 DI 服务注册、只在请求期激活。
/// </para>
/// <para>
/// 本测试使用<b>真实组合根</b>：与生产完全相同的
/// <see cref="PuddingApplicationHost.CreateBuilder"/>（内部调用生产注册扩展
/// <c>AddPuddingApplicationServices</c>）+ <see cref="PuddingApplicationHost.Build"/>，
/// 因而继承生产同一套 <c>ValidateScopes</c>/<c>ValidateOnBuild</c> 设置与全部注册。
/// 随后反射 PuddingHost 程序集内全部带 <c>[ApiController]</c> 的类型，逐个在子 scope 里
/// 解析其公共构造函数的每个参数类型，让"控制器构造函数依赖漏注册"在测试期暴露，
/// 而不是等线上返回 500。
/// </para>
/// <para>
/// <b>覆盖边界</b>：只覆盖 PuddingHost 程序集内的 <c>[ApiController]</c> 类型
/// （本缺陷所属程序集，也是任务书限定的范围），不覆盖其它 application part
/// （PuddingPlatform / PuddingRuntime 的控制器）。
/// 只断言"构造函数参数类型可从容器解析"，不断言运行时行为，也不覆盖
/// <c>[FromServices]</c> 方法参数、<c>IHttpContextAccessor</c> 等请求期能力。
/// 对存在多个公共构造函数的类型，本测试比 <c>ActivatorUtilities</c> 更严格：
/// 要求<b>每个</b>公共构造函数都可解析（当前所有目标控制器都只有一个公共构造函数）。
/// </para>
/// </summary>
[Collection("Pudding application host composition")]
public sealed class ControllerConstructorDependencyResolutionTests
{
    [Fact]
    public void EveryApiController_ConstructorDependency_IsResolvableFromCompositionRoot()
    {
        var dataRoot = Path.Combine(
            Path.GetTempPath(),
            "PuddingAgent",
            $"controller-di-{Guid.NewGuid():N}");

        try
        {
            var options = PuddingHostOptionsFactory.ForDesktopChild(
            [
                "--desktop-child",
                "--desktop-parent-pid", Environment.ProcessId.ToString(),
                "--data-root", dataRoot,
                "--urls", "http://0.0.0.0:18091",
            ]);

            var builder = PuddingApplicationHost.CreateBuilder([], options);
            using var app = PuddingApplicationHost.Build(builder);

            var controllerTypes = typeof(PuddingApplicationHost).Assembly
                .GetTypes()
                .Where(type => type is { IsClass: true, IsAbstract: false })
                .Where(type => type.GetCustomAttribute<ApiControllerAttribute>(inherit: true) is not null)
                .OrderBy(type => type.FullName, StringComparer.Ordinal)
                .ToList();

            // 防护网自身有效性：反射口径一旦失效（例如特性被移除/程序集改名），
            // 必须让本测试失败，而不是退化成一条永远通过的"空断言"。
            Assert.NotEmpty(controllerTypes);
            Assert.Contains(
                controllerTypes,
                type => type.FullName == "PuddingHost.Controllers.StorageAdminController");

            var failures = new List<string>();
            foreach (var controller in controllerTypes)
            {
                var constructors = controller.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
                Assert.NotEmpty(constructors);

                foreach (var constructor in constructors)
                {
                    // 每个控制器独立 scope：Scoped 依赖只能在 scope 内解析，
                    // 且一个控制器的解析失败不得污染其它控制器的检查。
                    using var scope = app.Services.CreateScope();
                    foreach (var parameter in constructor.GetParameters())
                    {
                        try
                        {
                            scope.ServiceProvider.GetRequiredService(parameter.ParameterType);
                        }
                        catch (Exception ex)
                        {
                            failures.Add(
                                $"{controller.FullName} 的构造函数参数 " +
                                $"{parameter.ParameterType.FullName} {parameter.Name} 无法从宿主容器解析：" +
                                $"{ex.GetType().Name}: {ex.Message}");
                        }
                    }
                }
            }

            Assert.True(
                failures.Count == 0,
                "存在无法从宿主 DI 容器解析的 [ApiController] 构造函数依赖（控制器激活时会 500）："
                + Environment.NewLine
                + string.Join(Environment.NewLine, failures));
        }
        finally
        {
            Serilog.Log.CloseAndFlush();
            try
            {
                if (Directory.Exists(dataRoot))
                    Directory.Delete(dataRoot, recursive: true);
            }
            catch (IOException)
            {
                // Windows 句柄延迟释放时忽略清理失败。
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
