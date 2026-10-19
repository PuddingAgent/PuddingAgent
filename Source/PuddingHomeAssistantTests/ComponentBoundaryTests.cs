using System.Reflection;
using PuddingHomeAssistant.Rest;

namespace PuddingHomeAssistantTests;

/// <summary>
/// 组件边界断言（组件化交付规程 §2.2 的 S4 部分）。
/// 判据是**编译期失败**：组件 csproj 的 ProjectReference 必须为 0；
/// 这里在运行期复核产物程序集的引用闭包，防止有人用别的方式把上层拉进来。
/// </summary>
[TestClass]
public sealed class ComponentBoundaryTests
{
    private static Assembly ComponentAssembly => typeof(HaRestClient).Assembly;

    private static string[] ReferencedNames() =>
        ComponentAssembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty).ToArray();

    [TestMethod]
    public void Component_DoesNotReferenceAnyPuddingUpperLayer()
    {
        var refs = ReferencedNames();
        var violations = refs
            .Where(n => n.StartsWith("Pudding", StringComparison.Ordinal) &&
                        !string.Equals(n, "PuddingHomeAssistant", StringComparison.Ordinal))
            .ToArray();

        Assert.AreEqual(0, violations.Length,
            "叶子组件不得引用上层工程，但发现：" + string.Join(", ", violations));
    }

    [TestMethod]
    public void Component_DoesNotReferenceEfCoreOrAspNetCore()
    {
        var refs = ReferencedNames();
        var violations = refs
            .Where(n => n.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
                        n.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) ||
                        n.StartsWith("Microsoft.Extensions.DependencyInjection", StringComparison.Ordinal))
            .ToArray();

        Assert.AreEqual(0, violations.Length,
            "叶子组件不得依赖 EF Core / ASP.NET Core / DI，但发现：" + string.Join(", ", violations));
    }

    [TestMethod]
    public void ControlGroup_SystemTextJsonIsActuallyReferenced()
    {
        // 控制组：如果上面的扫描器失效（例如 API 返回空集合），本断言会失败。
        var refs = ReferencedNames();
        Assert.IsTrue(
            refs.Any(n => n.Contains("System.Text.Json", StringComparison.Ordinal)),
            "控制组失败：引用闭包中没有 System.Text.Json，说明扫描本身不可信");
    }

    [TestMethod]
    public void ControlGroup_ComponentItselfIsInClosure()
    {
        var refs = ReferencedNames();
        Assert.IsTrue(refs.Any(n => string.Equals(n, "System.Runtime", StringComparison.Ordinal)),
            "控制组失败：连 System.Runtime 都没有，引用闭包异常");
    }
}
