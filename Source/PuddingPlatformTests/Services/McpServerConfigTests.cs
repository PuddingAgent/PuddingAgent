using System.Net;
using System.Text.Json;
using PuddingPlatform.Services.Mcp;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class McpServerConfigTests
{
    [TestMethod]
    public void TryParse_Accepts_Explicit_Trusted_Local_Streamable_Http_Config()
    {
        const string json = """
            {
              "endpoint": "http://127.0.0.1:3100/mcp",
              "transport": "STREAMABLE_HTTP",
              "allowPrivateNetwork": true,
              "connectionTimeoutSeconds": 10,
              "callTimeoutSeconds": 30,
              "maxResultChars": 4096,
              "maxReconnectionAttempts": 2
            }
            """;

        var parsed = McpServerConfig.TryParse(json, out var config, out var error);

        Assert.IsTrue(parsed, error);
        Assert.IsNotNull(config);
        Assert.AreEqual("streamable_http", config.Transport);
        Assert.AreEqual("http://127.0.0.1:3100/mcp", config.Endpoint);
        using var canonical = JsonDocument.Parse(config.ToCanonicalJson());
        Assert.IsFalse(canonical.RootElement.TryGetProperty("endpointUri", out _));
    }

    [TestMethod]
    public void TryParse_Rejects_Local_Endpoint_Without_Explicit_Private_Network_Opt_In()
    {
        var parsed = McpServerConfig.TryParse(
            """{"endpoint":"http://localhost:3100/mcp","transport":"streamable_http"}""",
            out _,
            out var error);

        Assert.IsFalse(parsed);
        StringAssert.Contains(error, "Public MCP endpoints must use HTTPS");
    }

    [TestMethod]
    public void TryParse_Rejects_Unknown_Config_Fields()
    {
        var parsed = McpServerConfig.TryParse(
            """{"endpoint":"https://mcp.example.com/mcp","transport":"streamable_http","token":"plaintext"}""",
            out _,
            out var error);

        Assert.IsFalse(parsed);
        StringAssert.Contains(error, "Invalid MCP config JSON");
    }

    [TestMethod]
    public void TryParse_Accepts_Stdio_Config_For_A_Direct_Executable()
    {
        var workingDirectory = Path.GetFullPath(".");
        var json = JsonSerializer.Serialize(new
        {
            transport = "STDIO",
            command = "codex",
            arguments = new[] { "mcp-server" },
            workingDirectory,
            connectionTimeoutSeconds = 30,
            callTimeoutSeconds = 3_600,
            maxResultChars = 262_144,
            shutdownTimeoutSeconds = 10,
        });

        var parsed = McpServerConfig.TryParse(json, out var config, out var error);

        Assert.IsTrue(parsed, error);
        Assert.IsNotNull(config);
        Assert.AreEqual("stdio", config.Transport);
        Assert.AreEqual("codex", config.Command);
        CollectionAssert.AreEqual(new[] { "mcp-server" }, config.Arguments.ToArray());
        Assert.AreEqual(workingDirectory, config.WorkingDirectory);
        Assert.AreEqual(3_600, config.CallTimeoutSeconds);
    }

    [TestMethod]
    public void TryParse_Rejects_Ambiguous_Stdio_Endpoint()
    {
        var parsed = McpServerConfig.TryParse(
            """{"transport":"stdio","endpoint":"https://example.com/mcp","command":"codex"}""",
            out _,
            out var error);

        Assert.IsFalse(parsed);
        StringAssert.Contains(error, "endpoint must be empty");
    }

    [TestMethod]
    public void TryParse_Rejects_Shell_Command_Line_Instead_Of_Executable_And_Arguments()
    {
        var parsed = McpServerConfig.TryParse(
            """{"transport":"stdio","command":"codex mcp-server"}""",
            out _,
            out var error);

        Assert.IsFalse(parsed);
        StringAssert.Contains(error, "bare executable name");
    }

    [TestMethod]
    public void TryParse_Rejects_Stdio_Process_Fields_For_Http_Transport()
    {
        var parsed = McpServerConfig.TryParse(
            """{"transport":"streamable_http","endpoint":"https://example.com/mcp","command":"codex"}""",
            out _,
            out var error);

        Assert.IsFalse(parsed);
        StringAssert.Contains(error, "only supported when transport is stdio");
    }

    [TestMethod]
    public void NetworkPolicy_Rejects_Common_Private_And_Reserved_Addresses()
    {
        Assert.IsFalse(McpNetworkPolicy.IsPublicAddress(IPAddress.Loopback));
        Assert.IsFalse(McpNetworkPolicy.IsPublicAddress(IPAddress.Parse("10.1.2.3")));
        Assert.IsFalse(McpNetworkPolicy.IsPublicAddress(IPAddress.Parse("172.16.1.2")));
        Assert.IsFalse(McpNetworkPolicy.IsPublicAddress(IPAddress.Parse("192.168.1.2")));
        Assert.IsFalse(McpNetworkPolicy.IsPublicAddress(IPAddress.Parse("fc00::1")));
        Assert.IsFalse(McpNetworkPolicy.IsPublicAddress(IPAddress.Parse("::ffff:127.0.0.1")));
        Assert.IsTrue(McpNetworkPolicy.IsPublicAddress(IPAddress.Parse("1.1.1.1")));
    }

    [TestMethod]
    public void SchemaAdapter_Preserves_The_Exact_Nested_Json_Schema()
    {
        using var document = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "query": { "type": "string", "enum": ["one", "two"] },
                "filters": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": { "field": { "type": ["string", "null"] } }
                  }
                }
              },
              "required": ["query"],
              "additionalProperties": false
            }
            """);

        var schema = McpSchemaAdapter.ToParameterSchema(document.RootElement);

        Assert.IsNotNull(schema.RawJsonSchema);
        Assert.IsTrue(JsonElement.DeepEquals(document.RootElement, schema.RawJsonSchema.Value));
        CollectionAssert.AreEqual(new[] { "query" }, schema.Required.ToArray());
        Assert.AreEqual("array", schema.Properties.Single(item => item.Name == "filters").Type);
    }

    [TestMethod]
    public void ToolId_Is_Stable_Valid_And_Namespaced()
    {
        var first = McpToolId.Create("skill-123", "GitHub Server", "issues/search");
        var second = McpToolId.Create("skill-123", "GitHub Server", "issues/search");

        Assert.AreEqual(first, second);
        StringAssert.Matches(first, new System.Text.RegularExpressions.Regex("^[a-zA-Z0-9_]+$"));
        StringAssert.StartsWith(first, "mcp__github_serve_");
    }
}
