using System.Text.Json;
using PuddingHomeAssistant.Contracts;
using PuddingHomeAssistant.Policy;

namespace PuddingHomeAssistantTests;

[TestClass]
public sealed class HaOutputBudgetTests
{
    private static HaState State(string entityId, string state = "on", int attributeBytes = 0)
    {
        Dictionary<string, JsonElement>? attrs = null;
        if (attributeBytes > 0)
        {
            attrs = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["blob"] = JsonDocument.Parse($"\"{new string('x', attributeBytes)}\"").RootElement.Clone(),
            };
        }

        return new HaState(entityId, state, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, attrs);
    }

    [DataTestMethod]
    [DataRow(null, 50)]
    [DataRow(0, 50)]
    [DataRow(-5, 50)]
    [DataRow(1, 1)]
    [DataRow(499, 499)]
    [DataRow(500, 500)]
    [DataRow(501, 500)]
    [DataRow(100000, 500)]
    public void NormalizeLimit_ClampsToBounds(int? requested, int expected)
    {
        Assert.AreEqual(expected, HaOutputBudget.NormalizeLimit(requested));
    }

    [TestMethod]
    public void Project_FiltersByDomain_AndReportsTotals()
    {
        var states = new[]
        {
            State("light.a"), State("light.b"), State("switch.c"),
        };

        var page = HaOutputBudget.Project(states, 10, domains: ["light"]);

        Assert.AreEqual(2, page.Total);
        Assert.AreEqual(2, page.Returned);
        Assert.IsFalse(page.Truncated);
        CollectionAssert.AreEqual(new[] { "light.a", "light.b" }, page.Items.Select(x => x.EntityId).ToArray());
    }

    [TestMethod]
    public void Project_EntityIdTakesPriorityOverDomains()
    {
        var states = new[] { State("light.a"), State("light.b"), State("switch.c") };
        var page = HaOutputBudget.Project(states, 10, domains: ["switch"], entityId: "light.b");

        Assert.AreEqual(1, page.Total);
        Assert.AreEqual("light.b", page.Items[0].EntityId);
    }

    [TestMethod]
    public void Project_TruncatesWithExplicitFlags()
    {
        var states = Enumerable.Range(0, 100).Select(i => State($"light.l{i}")).ToArray();
        var page = HaOutputBudget.Project(states, 50);

        Assert.AreEqual(100, page.Total);
        Assert.AreEqual(50, page.Returned);
        Assert.IsTrue(page.Truncated);
    }

    [TestMethod]
    public void Project_OmitsAttributesUnlessRequested()
    {
        var states = new[] { State("light.a", attributeBytes: 32) };

        var withoutAttributes = HaOutputBudget.Project(states, 10);
        Assert.IsNull(withoutAttributes.Items[0].Attributes);

        var withAttributes = HaOutputBudget.Project(states, 10, fields: ["state", "attributes"]);
        Assert.IsNotNull(withAttributes.Items[0].Attributes);
    }

    [TestMethod]
    public void Project_StaysWithinByteBudget_WhenAttributesAreHuge()
    {
        // 20 条 × 20 KiB attributes，远超 256 KiB 预算
        var states = Enumerable.Range(0, 20)
            .Select(i => State($"light.big{i}", attributeBytes: 20 * 1024))
            .ToArray();

        var page = HaOutputBudget.Project(states, 500, fields: ["attributes"]);

        Assert.IsTrue(page.EstimatedBytes <= HaOutputBudget.MaxBytes,
            $"EstimatedBytes={page.EstimatedBytes} 超过预算 {HaOutputBudget.MaxBytes}");
        Assert.IsTrue(page.Returned < states.Length, "应当在字节预算处被截断");
        Assert.IsTrue(page.Truncated);
        Assert.AreEqual(states.Length, page.Total);
    }

    [TestMethod]
    public void Project_EmptyInput_IsNotTruncated()
    {
        var page = HaOutputBudget.Project([], 10);
        Assert.AreEqual(0, page.Total);
        Assert.AreEqual(0, page.Returned);
        Assert.IsFalse(page.Truncated);
    }
}
