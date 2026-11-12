using System.Collections.Immutable;
using System.Text.Json;
using Pudding.Api.Contracts;
using Pudding.Samples.CapabilityCatalogClient;

[assembly: Parallelize(Workers = 0, Scope = ExecutionScope.MethodLevel)]

namespace CapabilityCatalogClient.Tests;

[TestClass]
public sealed class CatalogDemoTests
{
    private static readonly string ExpectedUsage = "Usage: CatalogDemo list <kind> [cursor]";

    [TestMethod]
    public async Task List_Success_CallsListOnce_AndWritesPageJson()
    {
        var page = Page(ImmutableArray.Create(
            Entry("tool-1", "tool", "Tool One", "rev-3", "gen-1", "Available", null, true,
                ImmutableArray.Create("tool.invoke", "tool.read")),
            Entry("tool-2", "tool", "Tool Two", "rev-4", "gen-1", "Unavailable", "capability.permission_denied")));
        var api = new FakeCapabilityCatalogApi { Page = page };

        var result = await Run(api, ["list", "tool"]);

        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual(string.Empty, result.Error);
        Assert.AreEqual(1, api.ListCalls, "ListAsync must be called exactly once.");
        Assert.IsNotNull(api.LastQuery);
        Assert.AreEqual(new CapabilityQuery("tool", null, 50), api.LastQuery);
        AssertPageJson(ParseSingleLine(result.Output), page);
    }

    [TestMethod]
    public async Task List_AbsentCursor_PassesNullCursorAndFrozenMaxCount()
    {
        var api = new FakeCapabilityCatalogApi { Page = Page(ImmutableArray<CapabilityCatalogEntry>.Empty) };

        var result = await Run(api, ["list", "mcp"]);

        Assert.AreEqual(0, result.ExitCode);
        Assert.IsNotNull(api.LastQuery);
        Assert.AreEqual("mcp", api.LastQuery!.Kind);
        Assert.IsNull(api.LastQuery.Cursor, "An absent cursor must stay null; it must not be invented.");
        Assert.AreEqual(50, api.LastQuery.MaxCount, "MaxCount must stay at the frozen default 50.");
    }

    [TestMethod]
    public async Task List_PartialEmptyPage_IsSuccess_KeepsPartialAndMissingReason()
    {
        var page = Page(ImmutableArray<CapabilityCatalogEntry>.Empty, cursor: null, complete: false, partial: true,
            generation: "gen-3", missingReason: "catalog.partial");
        var api = new FakeCapabilityCatalogApi { Page = page };

        var result = await Run(api, ["list", "tool"]);

        Assert.AreEqual(0, result.ExitCode, "A partial/empty page is still a successful catalog query.");
        Assert.AreEqual(string.Empty, result.Error);
        Assert.AreEqual(1, api.ListCalls, "A successful list must not page automatically.");
        var json = ParseSingleLine(result.Output);
        AssertPageJson(json, page);
        Assert.IsTrue(json.GetProperty("partial").GetBoolean());
        Assert.AreEqual("catalog.partial", json.GetProperty("missingReason").GetString());
        Assert.IsFalse(result.Output.Contains("没有能力", StringComparison.Ordinal),
            "The presenter must not infer that the catalog has no capabilities.");
    }

    [TestMethod]
    public async Task List_ForbiddenPage_DoesNotFakeEmptyList()
    {
        var page = Page(ImmutableArray<CapabilityCatalogEntry>.Empty, cursor: null, complete: false, partial: true,
            generation: "gen-3", missingReason: "capability.forbidden");
        var api = new FakeCapabilityCatalogApi { Page = page };

        var result = await Run(api, ["list", "context"]);

        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual(string.Empty, result.Error);
        var json = ParseSingleLine(result.Output);
        AssertPageJson(json, page);
        Assert.AreEqual(0, json.GetProperty("capabilities").GetArrayLength());
        Assert.IsTrue(json.GetProperty("partial").GetBoolean(),
            "A Forbidden page must stay partial instead of pretending to be a complete empty list.");
        Assert.AreEqual("capability.forbidden", json.GetProperty("missingReason").GetString(),
            "The Forbidden reason must be preserved, not dropped.");
        Assert.IsFalse(json.GetProperty("complete").GetBoolean());
    }

    [TestMethod]
    public async Task List_OldGeneration_IsPreserved()
    {
        var page = Page(ImmutableArray.Create(Entry("tool-legacy", "tool", generation: "gen-1")),
            generation: "gen-1");
        var api = new FakeCapabilityCatalogApi { Page = page };

        var result = await Run(api, ["list", "tool"]);

        Assert.AreEqual(0, result.ExitCode);
        var json = ParseSingleLine(result.Output);
        Assert.AreEqual("gen-1", json.GetProperty("generation").GetString());
        Assert.AreEqual("gen-1", json.GetProperty("capabilities")[0].GetProperty("generation").GetString(),
            "An older generation must be preserved verbatim, never rewritten.");
    }

    [TestMethod]
    public async Task List_PassesKindAndCursorVerbatim_IncludingChineseCursorAndSurroundingSpaces()
    {
        const string cursor = "  游标-甲  ";
        var page = Page(ImmutableArray.Create(Entry("mcp-1", "mcp")), cursor: cursor);
        var api = new FakeCapabilityCatalogApi { Page = page };

        var result = await Run(api, ["list", "mcp", cursor]);

        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual(string.Empty, result.Error);
        Assert.AreEqual(1, api.ListCalls);
        Assert.IsNotNull(api.LastQuery);
        Assert.AreEqual("mcp", api.LastQuery!.Kind);
        Assert.AreEqual(cursor, api.LastQuery.Cursor, "The cursor must not be trimmed.");
        Assert.AreEqual(new CapabilityQuery("mcp", cursor, 50), api.LastQuery);
        Assert.AreEqual(cursor, ParseSingleLine(result.Output).GetProperty("cursor").GetString());
    }

    [TestMethod]
    public async Task NoArguments_ReturnUsageWithoutCallingApi() => await AssertUsageError([]);

    [TestMethod]
    public async Task UnknownCommand_ReturnsUsageWithoutCallingApi()
    {
        string[][] cases =
        [
            ["get", "tool"],
            ["LIST", "tool"],
            ["List", "tool"],
            ["listAll", "tool"],
            ["tool"]
        ];
        foreach (var args in cases)
        {
            await AssertUsageError(args);
        }
    }

    [TestMethod]
    public async Task WrongArgumentCount_ReturnsUsageWithoutCallingApi()
    {
        string[][] cases =
        [
            ["list"],
            ["list", "tool", "cursor", "extra"],
            ["list", "tool", "cursor", "extra", "more"]
        ];
        foreach (var args in cases)
        {
            await AssertUsageError(args);
        }
    }

    [TestMethod]
    public async Task InvalidKind_ReturnsUsageWithoutCallingApi()
    {
        string[][] cases =
        [
            ["list", "Tool"],
            ["list", "TOOL"],
            ["list", "plugin"],
            ["list", "tools"],
            ["list", "Context"],
            ["list", "mcp "]
        ];
        foreach (var args in cases)
        {
            await AssertUsageError(args);
        }
    }

    [TestMethod]
    public async Task BlankKindOrCursor_ReturnsUsageWithoutCallingApi()
    {
        string[][] cases =
        [
            ["list", " "],
            ["list", ""],
            ["list", "\t"],
            ["list", "tool", ""],
            ["list", "tool", "  "],
            ["list", "tool", "\t"]
        ];
        foreach (var args in cases)
        {
            await AssertUsageError(args);
        }
    }

    [TestMethod]
    public async Task NullPage_WritesSourceUnavailableCode_AndReturnsOne()
    {
        var api = new FakeCapabilityCatalogApi { Page = null };

        var result = await Run(api, ["list", "tool"]);

        Assert.AreEqual(1, result.ExitCode);
        Assert.AreEqual(string.Empty, result.Output);
        Assert.AreEqual("code=capability.source_unavailable" + Environment.NewLine, result.Error);
        Assert.AreEqual(1, api.ListCalls, "A null page must not trigger a retry or a second query.");
    }

    [TestMethod]
    public async Task CapabilityCatalogException_WritesOnlyCode_AndReturnsOne()
    {
        const string message = "internal catalog detail for tool that must never leak";
        var api = new FakeCapabilityCatalogApi
        {
            ListFailure = new CapabilityCatalogException("capability.forbidden", message)
        };

        var result = await Run(api, ["list", "tool"]);

        Assert.AreEqual(1, result.ExitCode);
        Assert.AreEqual(string.Empty, result.Output);
        Assert.AreEqual("code=capability.forbidden" + Environment.NewLine, result.Error);
        Assert.IsFalse(result.Error.Contains("internal catalog detail", StringComparison.Ordinal),
            "Server messages must never reach stderr.");
        Assert.AreEqual(1, api.ListCalls);
    }

    [TestMethod]
    public async Task CallerCancellation_Returns130_WithoutExtraCalls()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var api = new FakeCapabilityCatalogApi { Page = Page(ImmutableArray.Create(Entry())) };

        var result = await Run(api, ["list", "tool"], cancellation.Token);

        Assert.AreEqual(130, result.ExitCode);
        Assert.AreEqual(1, api.ListCalls, "Cancellation must not trigger a retry or a second page.");
        Assert.AreEqual(string.Empty, result.Output);
        Assert.AreEqual(string.Empty, result.Error);
    }

    [TestMethod]
    public async Task CancellationNotRequestedByCaller_IsNotSwallowed()
    {
        var api = new FakeCapabilityCatalogApi
        {
            ListFailure = new OperationCanceledException("timeout-like cancellation the caller did not request")
        };
        using var output = new StringWriter();
        using var error = new StringWriter();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            CatalogDemo.RunAsync(["list", "tool"], api, output, error, CancellationToken.None));
        Assert.AreEqual(1, api.ListCalls);
        Assert.AreEqual(string.Empty, output.ToString());
    }

    [TestMethod]
    public async Task UnexpectedException_IsNotCaughtOrConvertedToSuccess()
    {
        var api = new FakeCapabilityCatalogApi { ListFailure = new InvalidOperationException("boom") };
        using var output = new StringWriter();
        using var error = new StringWriter();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            CatalogDemo.RunAsync(["list", "tool"], api, output, error, CancellationToken.None));
        Assert.AreEqual(1, api.ListCalls);
        Assert.AreEqual(string.Empty, output.ToString());
    }

    [TestMethod]
    public async Task List_Success_WritesExactlyOneCamelCaseJsonLine()
    {
        var api = new FakeCapabilityCatalogApi { Page = Page(ImmutableArray.Create(Entry())) };

        var result = await Run(api, ["list", "tool"]);

        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual(1, result.Output.Count(character => character == '\n'), "stdout must be exactly one line.");
        var body = result.Output[..^Environment.NewLine.Length];
        Assert.IsFalse(body.Contains('\r') || body.Contains('\n'), "stdout must not contain extra line breaks.");
        using var document = JsonDocument.Parse(body);
        CollectionAssert.AreEqual(
            new[] { "capabilities", "cursor", "complete", "partial", "generation", "missingReason" },
            document.RootElement.EnumerateObject().Select(property => property.Name).ToArray(),
            "Every capability catalog page field must be present as a camelCase JSON property.");
    }

    [TestMethod]
    public async Task NullReferenceArguments_ThrowArgumentNullException()
    {
        var api = new FakeCapabilityCatalogApi { Page = Page(ImmutableArray.Create(Entry())) };
        string[] args = ["list", "tool"];

        await AssertNullArgument("args",
            () => CatalogDemo.RunAsync(null!, api, new StringWriter(), new StringWriter()));
        await AssertNullArgument("api",
            () => CatalogDemo.RunAsync(args, null!, new StringWriter(), new StringWriter()));
        await AssertNullArgument("output",
            () => CatalogDemo.RunAsync(args, api, null!, new StringWriter()));
        await AssertNullArgument("error",
            () => CatalogDemo.RunAsync(args, api, new StringWriter(), null!));
    }

    private static async Task AssertNullArgument(string parameterName, Func<Task> action)
    {
        var exception = await Assert.ThrowsExactlyAsync<ArgumentNullException>(action);
        Assert.AreEqual(parameterName, exception.ParamName);
    }

    private static async Task AssertUsageError(string[] args)
    {
        var api = new FakeCapabilityCatalogApi { Page = Page(ImmutableArray.Create(Entry())) };
        var result = await Run(api, args);
        Assert.AreEqual(2, result.ExitCode, $"Expected usage exit for: [{string.Join(' ', args)}]");
        Assert.AreEqual(string.Empty, result.Output);
        Assert.AreEqual(ExpectedUsage + Environment.NewLine, result.Error);
        Assert.AreEqual(0, api.ListCalls, "Argument errors must not call ListAsync.");
    }

    private static async Task<(int ExitCode, string Output, string Error)> Run(ICapabilityCatalogApi api,
        string[] args, CancellationToken ct = default)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await CatalogDemo.RunAsync(args, api, output, error, ct);
        return (exitCode, output.ToString(), error.ToString());
    }

    private static JsonElement ParseSingleLine(string text)
    {
        Assert.IsTrue(text.EndsWith(Environment.NewLine, StringComparison.Ordinal),
            "stdout must end with exactly one line break.");
        var body = text[..^Environment.NewLine.Length];
        Assert.IsFalse(body.Contains('\r') || body.Contains('\n'), "stdout must be exactly one line.");
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static string[] Names(JsonElement element) =>
        element.EnumerateObject().Select(property => property.Name).ToArray();

    private static string[] Strings(JsonElement element) =>
        element.EnumerateArray().Select(item => item.GetString()!).ToArray();

    private static void AssertPageJson(JsonElement json, CapabilityCatalogPage page)
    {
        CollectionAssert.AreEqual(
            new[] { "capabilities", "cursor", "complete", "partial", "generation", "missingReason" },
            Names(json));
        Assert.AreEqual(page.Capabilities.Length, json.GetProperty("capabilities").GetArrayLength());
        for (var index = 0; index < page.Capabilities.Length; index++)
        {
            AssertEntryJson(json.GetProperty("capabilities")[index], page.Capabilities[index]);
        }
        Assert.AreEqual(page.Cursor, json.GetProperty("cursor").GetString());
        Assert.AreEqual(page.Complete, json.GetProperty("complete").GetBoolean());
        Assert.AreEqual(page.Partial, json.GetProperty("partial").GetBoolean());
        Assert.AreEqual(page.Generation, json.GetProperty("generation").GetString());
        Assert.AreEqual(page.MissingReason, json.GetProperty("missingReason").GetString());
    }

    private static void AssertEntryJson(JsonElement json, CapabilityCatalogEntry entry)
    {
        CollectionAssert.AreEqual(
            new[]
            {
                "capabilityId", "kind", "displayName", "revision", "generation", "availability", "missingReason",
                "requiresApproval", "requiredCapabilities"
            },
            Names(json));
        Assert.AreEqual(entry.CapabilityId, json.GetProperty("capabilityId").GetString());
        Assert.AreEqual(entry.Kind, json.GetProperty("kind").GetString());
        Assert.AreEqual(entry.DisplayName, json.GetProperty("displayName").GetString());
        Assert.AreEqual(entry.Revision, json.GetProperty("revision").GetString());
        Assert.AreEqual(entry.Generation, json.GetProperty("generation").GetString());
        Assert.AreEqual(entry.Availability, json.GetProperty("availability").GetString());
        Assert.AreEqual(entry.MissingReason, json.GetProperty("missingReason").GetString());
        Assert.AreEqual(entry.RequiresApproval, json.GetProperty("requiresApproval").GetBoolean());
        CollectionAssert.AreEqual(entry.RequiredCapabilities.ToArray(),
            Strings(json.GetProperty("requiredCapabilities")));
    }

    private static CapabilityCatalogPage Page(
        ImmutableArray<CapabilityCatalogEntry> capabilities,
        string? cursor = null,
        bool complete = true,
        bool partial = false,
        string generation = "gen-1",
        string? missingReason = null) =>
        new(capabilities, cursor, complete, partial, generation, missingReason);

    private static CapabilityCatalogEntry Entry(
        string capabilityId = "capability-1",
        string kind = "tool",
        string displayName = "Capability One",
        string revision = "rev-1",
        string generation = "gen-1",
        string availability = "Available",
        string? missingReason = null,
        bool requiresApproval = false,
        ImmutableArray<string>? requiredCapabilities = null) =>
        new(capabilityId, kind, displayName, revision, generation, availability, missingReason, requiresApproval,
            requiredCapabilities ?? ImmutableArray<string>.Empty);

    private sealed class FakeCapabilityCatalogApi : ICapabilityCatalogApi
    {
        public int ListCalls { get; private set; }
        public CapabilityQuery? LastQuery { get; private set; }
        public CapabilityCatalogPage? Page { get; init; }
        public Exception? ListFailure { get; init; }

        public Task<CapabilityCatalogPage> ListAsync(CapabilityQuery query, CancellationToken ct = default)
        {
            ListCalls++;
            LastQuery = query;
            ct.ThrowIfCancellationRequested();
            return ListFailure is null
                ? Task.FromResult(Page!)
                : Task.FromException<CapabilityCatalogPage>(ListFailure);
        }
    }
}
