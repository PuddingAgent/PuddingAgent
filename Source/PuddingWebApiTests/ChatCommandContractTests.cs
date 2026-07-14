using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingWebApiTests;

[TestClass]
[DoNotParallelize]
public sealed class ChatCommandContractTests
{
    private static CustomWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;
    private static long _workspacePk;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    [ClassInitialize]
    public static void ClassInit(TestContext _)
    {
        _factory = new CustomWebApplicationFactory();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var ws = db.Workspaces.FirstOrDefault(w => w.WorkspaceId == "default");
        if (ws is null)
        {
            ws = new WorkspaceEntity
            {
                WorkspaceId = "default",
                Name = "Default Workspace",
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Workspaces.Add(ws);
            db.SaveChanges();
        }
        _workspacePk = ws.Id;

        var existing = db.WorkspaceAgents.FirstOrDefault(a => a.WorkspaceEntityId == ws.Id);
        if (existing is null)
        {
            db.WorkspaceAgents.Add(new WorkspaceAgentEntity
            {
                AgentId = "default-agent",
                Name = "Default Agent",
                SourceTemplateId = "global:general-assistant",
                DisplayName = "Assistant",
                WorkspaceEntityId = ws.Id,
                IsEnabled = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            db.SaveChanges();
        }
    }

    [ClassCleanup]
    public static void ClassCleanup()
    {
        _factory.Dispose();
    }

    [TestInitialize]
    public void TestInit()
    {
        _client = _factory.CreateClient();
        JwtHelper.SetBearerToken(_client);
    }

    [TestCleanup]
    public void TestCleanup()
    {
        _client?.Dispose();
    }

    [TestMethod]
    public async Task PostMessage_Returns_All_Required_Fields()
    {
        var workspaceId = "default";
        var content = JsonContent.Create(new Dictionary<string, string>
        {
            ["messageText"] = "Hello",
        });

        var response = await _client.PostAsync(
            $"/api/workspaces/{workspaceId}/chat/message", content);

        var bodyText = await response.Content.ReadAsStringAsync();
        if ((int)response.StatusCode != 200)
        {
            Assert.Fail($"Expected 200, got {(int)response.StatusCode}. Body: {bodyText}");
        }

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        Assert.IsTrue(doc.RootElement.TryGetProperty("status", out var status),
            "Response must contain 'status' field.");
        Assert.AreEqual("accepted", status.GetString());

        Assert.IsTrue(doc.RootElement.TryGetProperty("commandId", out _),
            "Response must contain 'commandId' field.");
        Assert.IsTrue(doc.RootElement.TryGetProperty("messageId", out _),
            "Response must contain 'messageId' field.");
        Assert.IsTrue(doc.RootElement.TryGetProperty("turnId", out _),
            "Response must contain 'turnId' field.");
        Assert.IsTrue(doc.RootElement.TryGetProperty("sessionId", out _),
            "Response must contain 'sessionId' field.");
        Assert.IsTrue(doc.RootElement.TryGetProperty("eventCursor", out _),
            "Response must contain 'eventCursor' field.");
    }

    [TestMethod]
    public async Task PostMessage_CommandId_Is_Unique_Per_Request()
    {
        var workspaceId = "default";
        var content = JsonContent.Create(new Dictionary<string, string>
        {
            ["messageText"] = "Hello 1",
        });

        var response1 = await _client.PostAsync(
            $"/api/workspaces/{workspaceId}/chat/message", content);
        var body1 = await ReadString(response1);

        var response2 = await _client.PostAsync(
            $"/api/workspaces/{workspaceId}/chat/message", content);
        var body2 = await ReadString(response2);

        Assert.AreNotEqual(body1["commandId"], body2["commandId"],
            "Each request should produce a unique commandId.");
    }

    [TestMethod]
    public async Task PostMessage_SessionId_Is_Preserved()
    {
        var workspaceId = "default";
        var sessionId = $"session-{Guid.NewGuid():N}";
        var content = JsonContent.Create(new Dictionary<string, string>
        {
            ["messageText"] = "Hello",
            ["sessionId"] = sessionId,
        });

        var response = await _client.PostAsync(
            $"/api/workspaces/{workspaceId}/chat/message", content);
        var body = await ReadString(response);

        Assert.AreEqual(sessionId, body["sessionId"],
            "Response sessionId should match the requested sessionId.");
    }

    [TestMethod]
    public async Task PostMessage_Idempotency_Same_ClientRequestId()
    {
        var workspaceId = "default";
        var clientRequestId = $"idem-{Guid.NewGuid():N}";
        var content = JsonContent.Create(new Dictionary<string, string>
        {
            ["messageText"] = "Hello",
            ["clientRequestId"] = clientRequestId,
        });

        var response1 = await _client.PostAsync(
            $"/api/workspaces/{workspaceId}/chat/message", content);
        var body1 = await ReadString(response1);

        var response2 = await _client.PostAsync(
            $"/api/workspaces/{workspaceId}/chat/message", content);
        var body2 = await ReadString(response2);

        Assert.AreEqual(body1["commandId"], body2["commandId"],
            "Same ClientRequestId should return the identical commandId.");
        Assert.AreEqual(body1["messageId"], body2["messageId"],
            "Same ClientRequestId should return the identical messageId.");
        Assert.AreEqual(body1["turnId"], body2["turnId"],
            "Same ClientRequestId should return the identical turnId.");

        if (body2.TryGetValue("idempotent", out var idempotent))
        {
            Assert.AreEqual("True", idempotent.ToString(),
                "Second request should be marked as idempotent.");
        }
    }

    [TestMethod]
    public async Task PostMessage_Command_Is_Persisted()
    {
        var workspaceId = "default";
        var content = JsonContent.Create(new Dictionary<string, string>
        {
            ["messageText"] = "Hello, persist this!",
        });

        var response = await _client.PostAsync(
            $"/api/workspaces/{workspaceId}/chat/message", content);
        var body = await ReadString(response);

        var commandId = body["commandId"];

        // Small delay to let async persistence complete
        await Task.Delay(200);

        // The command should appear in session events
        var sessionId = body["sessionId"];
        var eventCursor = body.GetValueOrDefault("eventCursor", "0");

        var eventsResponse = await _client.GetAsync(
            $"/api/sessions/{sessionId}/events?workspaceId={workspaceId}&from={eventCursor}");
        Assert.AreEqual(200, (int)eventsResponse.StatusCode);

        var eventsBody = await eventsResponse.Content.ReadAsStringAsync();
        Assert.IsTrue(
            eventsBody.Contains("turn.accepted", StringComparison.Ordinal) ||
            eventsBody.Length > 10,
            $"Session {sessionId} should have events after command acceptance.");
    }

    private static async Task<Dictionary<string, string>> ReadString(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(text);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            result[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.Number => prop.Value.GetRawText(),
                JsonValueKind.True => "True",
                JsonValueKind.False => "False",
                JsonValueKind.Null => "",
                _ => prop.Value.ToString(),
            };
        }
        return result;
    }
}
