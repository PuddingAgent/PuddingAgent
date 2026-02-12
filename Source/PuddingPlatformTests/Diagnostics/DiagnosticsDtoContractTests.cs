using System.Text.Json;
using PuddingCode.Diagnostics;

namespace PuddingPlatformTests.Diagnostics;

[TestClass]
public sealed class DiagnosticsDtoContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [TestMethod]
    public void EventStatsDto_Serializes_CamelCaseShape()
    {
        var dto = new EventStatsDto
        {
            TotalCount = 3,
            ByStatus = new[]
            {
                new EventStatusCountDto { Status = "completed", Count = 2 },
                new EventStatusCountDto { Status = "failed", Count = 1 },
            },
            ByComponent = new[]
            {
                new EventComponentCountDto { Component = "agent_execution", Count = 1 },
                new EventComponentCountDto { Component = "llm_gateway", Count = 2 },
            },
        };

        var json = JsonSerializer.Serialize(dto, JsonOptions);

        StringAssert.Contains(json, "\"totalCount\":3");
        StringAssert.Contains(json, "\"byStatus\"");
        StringAssert.Contains(json, "\"status\":\"completed\"");
        StringAssert.Contains(json, "\"count\":2");
        StringAssert.Contains(json, "\"byComponent\"");
        StringAssert.Contains(json, "\"component\":\"agent_execution\"");
    }

    [TestMethod]
    public void EventStatsDto_Roundtrips()
    {
        var dto = new EventStatsDto
        {
            TotalCount = 5,
            ByStatus = new[] { new EventStatusCountDto { Status = "succeeded", Count = 5 } },
            ByComponent = new[] { new EventComponentCountDto { Component = "connector", Count = 5 } },
        };

        var json = JsonSerializer.Serialize(dto, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<EventStatsDto>(json, JsonOptions);

        Assert.IsNotNull(deserialized);
        Assert.AreEqual(5, deserialized.TotalCount);
        Assert.AreEqual(1, deserialized.ByStatus.Count);
        Assert.AreEqual("succeeded", deserialized.ByStatus[0].Status);
        Assert.AreEqual("connector", deserialized.ByComponent[0].Component);
    }

    [TestMethod]
    public void RuntimeTimelineQueryDto_Serializes_With_SortOrder()
    {
        var dto = new RuntimeTimelineQueryDto
        {
            SessionId = "session_1",
            SortOrder = "asc",
            Page = 1,
            PageSize = 50,
        };

        var json = JsonSerializer.Serialize(dto, JsonOptions);

        StringAssert.Contains(json, "\"sortOrder\":\"asc\"");
        StringAssert.Contains(json, "\"pageSize\":50");
    }

    [TestMethod]
    public void EventStatusCountDto_Roundtrips()
    {
        var dto = new EventStatusCountDto { Status = "running", Count = 10 };
        var json = JsonSerializer.Serialize(dto, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<EventStatusCountDto>(json, JsonOptions);

        Assert.IsNotNull(deserialized);
        Assert.AreEqual("running", deserialized.Status);
        Assert.AreEqual(10, deserialized.Count);
    }
}
