using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Observability;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingCode.Tools;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Tools;

[TestClass]
public sealed class HarnessToolCompatibilityAdapterTests
{
    [TestMethod]
    public void Normalize_RgAliasAndArguments_ProducesCanonicalSearchGrepContract()
    {
        var normalized = HarnessToolCompatibilityAdapter.Normalize(
            "rg",
            """{"pattern":"Needle","path":"Source","glob":"*.cs","limit":12}""");

        Assert.IsTrue(normalized.Adapted);
        Assert.AreEqual("search_grep", normalized.ToolName);
        using var json = JsonDocument.Parse(normalized.ArgumentsJson);
        Assert.AreEqual("Needle", json.RootElement.GetProperty("query").GetString());
        Assert.AreEqual("Source", json.RootElement.GetProperty("directory").GetString());
        Assert.AreEqual("*.cs", json.RootElement.GetProperty("pattern").GetString());
        Assert.AreEqual(12, json.RootElement.GetProperty("max_results").GetInt32());
    }

    [TestMethod]
    public void Normalize_ExecCommandAliases_ProducesCanonicalShellContract()
    {
        var normalized = HarnessToolCompatibilityAdapter.Normalize(
            "exec_command",
            """{"cmd":"rg -n Needle Source","workdir":"E:\\repo","shell":"pwsh","timeout_ms":10500}""");

        Assert.IsTrue(normalized.Adapted);
        Assert.AreEqual("shell", normalized.ToolName);
        using var json = JsonDocument.Parse(normalized.ArgumentsJson);
        Assert.AreEqual("rg -n Needle Source", json.RootElement.GetProperty("command").GetString());
        Assert.AreEqual("E:\\repo", json.RootElement.GetProperty("working_directory").GetString());
        Assert.AreEqual("powershell", json.RootElement.GetProperty("shell").GetString());
        Assert.AreEqual(11, json.RootElement.GetProperty("timeout_seconds").GetInt32());
    }

    [TestMethod]
    public void Normalize_UnixShellAlias_UsesExplicitWslLane()
    {
        var normalized = HarnessToolCompatibilityAdapter.Normalize(
            "exec_command",
            """{"cmd":"grep Needle -R .","shell":"unix"}""");

        Assert.AreEqual("shell", normalized.ToolName);
        using var json = JsonDocument.Parse(normalized.ArgumentsJson);
        Assert.AreEqual("wsl", json.RootElement.GetProperty("shell").GetString());
    }

    [TestMethod]
    public void Normalize_WriteStdinAlias_MapsSessionAndChars()
    {
        var normalized = HarnessToolCompatibilityAdapter.Normalize(
            "write_stdin",
            """{"session_id":"job-1","chars":"y"}""");

        Assert.AreEqual("terminal_input", normalized.ToolName);
        using var json = JsonDocument.Parse(normalized.ArgumentsJson);
        Assert.AreEqual("job-1", json.RootElement.GetProperty("job_id").GetString());
        Assert.AreEqual("y", json.RootElement.GetProperty("input").GetString());
    }

    [TestMethod]
    public void Normalize_RawCodexPatch_WrapsPatchText()
    {
        const string patch = """
            *** Begin Patch
            *** Update File: src/app.cs
            -old
            +new
            *** End Patch
            """;

        var normalized = HarnessToolCompatibilityAdapter.Normalize("apply_patch", patch);

        Assert.IsTrue(normalized.Adapted);
        using var json = JsonDocument.Parse(normalized.ArgumentsJson);
        Assert.AreEqual(patch, json.RootElement.GetProperty("patch_text").GetString());
    }

    [TestMethod]
    public void NoMatchClassification_AcceptsRgExitOne_ButNotMissingExecutable()
    {
        Assert.IsTrue(HarnessToolCompatibilityAdapter.IsExpectedNoMatchExit(
            "rg -n Needle Source",
            1,
            ""));
        Assert.IsFalse(HarnessToolCompatibilityAdapter.IsExpectedNoMatchExit(
            "rg -n Needle Source",
            1,
            "CommandNotFoundException: rg was not found"));
        Assert.IsFalse(HarnessToolCompatibilityAdapter.IsExpectedNoMatchExit(
            "dotnet test",
            1,
            "Tests failed"));
    }

    [TestMethod]
    public async Task ToolInvocationService_NormalizesBeforeUnifiedExecution()
    {
        var executor = new RecordingExecutionService();
        var telemetry = new RecordingTelemetrySink();
        var service = new ToolInvocationService(
            executor,
            logger: NullLogger<ToolInvocationService>.Instance,
            telemetrySink: telemetry);

        var result = await service.InvokeAsync(new ToolInvocationRequest
        {
            WorkspaceId = "workspace-1",
            SessionId = "session-1",
            AgentInstanceId = "agent-1",
            ToolCallId = "call-1",
            ToolName = "rg",
            ArgumentsJson = """{"pattern":"Needle","path":"Source"}""",
        });

        Assert.IsTrue(result.Success);
        Assert.AreEqual("search_grep", result.ToolName);
        Assert.AreEqual("search_grep", executor.ToolId);
        using var json = JsonDocument.Parse(executor.ArgumentsJson!);
        Assert.AreEqual("Needle", json.RootElement.GetProperty("query").GetString());
        Assert.AreEqual("Source", json.RootElement.GetProperty("directory").GetString());
        Assert.HasCount(1, telemetry.Metrics);
        var metric = telemetry.Metrics[0];
        Assert.AreEqual("tool.harness_compatibility", metric.Name);
        Assert.AreEqual("rg", metric.Dimensions!["requested_tool"]);
        Assert.AreEqual("search_grep", metric.Dimensions["canonical_tool"]);
        Assert.AreEqual("tool_and_arguments", metric.Dimensions["adaptation_kind"]);
    }

    [TestMethod]
    public void TerminalPolicy_AllowsRgAndNpxEntrypoints()
    {
        Assert.IsTrue(DefaultTerminalCommandPolicy.Instance.Evaluate("rg -n Needle Source", false).Allowed);
        Assert.IsTrue(DefaultTerminalCommandPolicy.Instance.Evaluate("npx vitest run", false).Allowed);
    }

    private sealed class RecordingExecutionService : IPuddingToolExecutionService
    {
        public string? ToolId { get; private set; }
        public string? ArgumentsJson { get; private set; }

        public Task<ToolExecutionResult> ExecuteAsync(
            string toolId,
            string argumentsJson,
            ToolExecutionContext context,
            CapabilityPolicy? policy,
            CancellationToken ct = default)
        {
            ToolId = toolId;
            ArgumentsJson = argumentsJson;
            return Task.FromResult(ToolExecutionResult.Ok("ok"));
        }
    }

    private sealed class RecordingTelemetrySink : ITelemetryMetricSink
    {
        public List<TelemetryMetric> Metrics { get; } = [];

        public Task RecordAsync(TelemetryMetric metric, CancellationToken ct = default)
        {
            Metrics.Add(metric);
            return Task.CompletedTask;
        }
    }
}
