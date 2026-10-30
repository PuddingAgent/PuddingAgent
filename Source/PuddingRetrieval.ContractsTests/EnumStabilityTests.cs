using System.Globalization;
using PuddingRetrieval.Contracts;

namespace PuddingRetrieval.ContractsTests;

/// <summary>
/// 数值钉死测试：这两个枚举是**持久化契约**（CodeSymbols.Kind / CodeRelations.Kind 按整数存储）。
/// 成员顺序一旦被调整，已有索引库里的数据含义会整体错位 —— 而且不会有任何编译错误。
/// <para>
/// 这里把「成员名=数值」整个序列钉死：既检查数值，也检查顺序与成员数量。
/// 用运行时枚举值与期望序列对照（而不是逐条常量比较），避免分析器把断言当成恒真而失效。
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

    /// <summary>
    /// 与 CodeIndex 侧仍并存的同名枚举保持一致 —— 这是迁移的**过渡**要求：
    /// 两份定义期间任何一侧改了成员，都会让「同一份索引库」被两种含义读取。
    /// </summary>
    [TestMethod]
    public void TransitionalDuplicate_HasSameMembersAsDocumentedContract()
    {
        Assert.HasCount(18, Enum.GetValues<CodeSymbolKind>(),
            "过度期成员数必须与 PuddingCodeIndex.Contracts.CodeSymbolKind 一致（18）。");
        Assert.HasCount(9, Enum.GetValues<CodeRelationKind>(),
            "过渡期成员数必须与 PuddingCodeIndex.Contracts.CodeRelationKind 一致（9）。");
    }
}
