using System.Reflection;
using PuddingTokenization;

namespace PuddingTokenizationTests;

/// <summary>
/// S4 边界断言：组件必须保持叶子身份。
/// </summary>
/// <remarks>
/// 这条测试的价值不在「今天通过」，而在于**未来有人想偷懒引用 Core 时会立刻失败**。
/// 依赖方向必须由编译期/测试强制，而不是靠文档约定——否则半年后没人记得。
/// </remarks>
[TestClass]
public sealed class ComponentBoundaryTests
{
    private static readonly Assembly ComponentAssembly = typeof(TokenizerProvider).Assembly;

    [TestMethod]
    public void Component_HasNoProjectReferences()
    {
        var referenced = ComponentAssembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name =>
                name.StartsWith("Pudding", StringComparison.OrdinalIgnoreCase) &&
                // 自身不算引用
                !name.Equals(ComponentAssembly.GetName().Name, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.IsEmpty(
            referenced,
            "PuddingTokenization 必须保持叶子身份，但检测到对以下 Pudding 程序集的引用：" +
            string.Join(", ", referenced));
    }

    [TestMethod]
    public void Component_DoesNotReferenceCoreOrRuntimeTypes()
    {
        // 双重保险：即使将来通过其他包间接引入，也不允许出现宿主类型名。
        var forbidden = new[] { "ChatMessage", "LlmToolDefinition", "ContextUsageSnapshot" };

        foreach (var typeName in forbidden)
        {
            var found = ComponentAssembly.GetTypes()
                .Any(t => t.Name.Contains(typeName, StringComparison.Ordinal) ||
                          (t.FullName?.Contains(typeName, StringComparison.Ordinal) ?? false));

            Assert.IsFalse(found, $"组件内不应出现宿主类型 {typeName}。");
        }
    }
}
