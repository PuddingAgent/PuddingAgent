using System.Globalization;
using PuddingCodeIndex.Contracts;

namespace PuddingDomain.ContractsTests;

/// <summary>
/// 数值钉死测试：这两个枚举是**持久化契约**（CodeSymbols.Kind / CodeRelations.Kind 按整数存储）。
/// 成员顺序一旦被调整，已有索引库里的数据含义会整体错位 —— 而且不会有任何编译错误。
/// <para>
/// 这里把「成员名=数值」整个序列钉死：既检查数值，也检查顺序与成员数量。
/// 用运行时枚举值与期望序列整体对照（逐条常量比较会被分析器判为恒真而失去保护）。
/// </para>
/// </summary>
[TestClass]
public sealed class EnumStabilityTests
{
    private static string[] PairsOf<TEnum>() where TEnum : struct, Enum =>
        Enum.GetValues<TEnum>()
            .Select(value => string.Create(
                CultureInfo.InvariantCulture,
                $"{value}={Convert.ToInt32(value, CultureInfo.InvariantCulture)}"))
            .ToArray();

    [TestMethod]
    public void CodeSymbolKind_NumericValues_ArePinned()
    {
        var expected = new[]
        {
            "Unknown=0", "Namespace=1", "Type=2", "Struct=3", "Class=4", "Interface=5",
            "Enum=6", "Delegate=7", "Method=8", "Constructor=9", "Property=10", "Field=11",
            "Event=12", "Parameter=13", "Local=14", "Variable=15", "Constant=16", "Operator=17",
        };

        CollectionAssert.AreEqual(expected, PairsOf<CodeSymbolKind>(),
            "符号种类是持久化契约：数值或顺序变化会让已有索引数据含义错位。");
    }

    [TestMethod]
    public void CodeRelationKind_NumericValues_ArePinned()
    {
        var expected = new[]
        {
            "Unknown=0", "Contains=1", "Calls=2", "References=3", "Inherits=4",
            "Implements=5", "Overrides=6", "Overloads=7", "Uses=8",
        };

        CollectionAssert.AreEqual(expected, PairsOf<CodeRelationKind>(),
            "关系种类是持久化契约：数值或顺序变化会让已有索引数据含义错位。");
    }

    [TestMethod]
    public void MemberCounts_ArePinned()
    {
        Assert.HasCount(18, Enum.GetValues<CodeSymbolKind>());
        Assert.HasCount(9, Enum.GetValues<CodeRelationKind>());
    }
}
