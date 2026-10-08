using Pudding.Contracts;
using Pudding.Contracts.Desktop;
﻿using System.Runtime.CompilerServices;
using System.Text.Json;
using PuddingBrowser.Abstractions;
using PuddingCode.Tools;

namespace PuddingBrowser.AgentTools.Tests;

public sealed class BrowserAgentToolsTests
{
    private readonly BrowserOperationOriginAccessor _originAccessor = new();

    [Fact]
    public void ToolDescriptors_UseStableNamesAndStructuredSchemas()
    {
        var runtime = new FakeBrowserRuntime();
        var tools = new IPuddingTool[]
        {
            new BrowserContextTool(new FakeCapabilitySurface(runtime), new FakeCapabilitySurface(runtime), new FakeCallContextFactory(), _originAccessor),
            new BrowserTabsTool(new FakeCapabilitySurface(runtime), new FakeCallContextFactory(), _originAccessor),
            new BrowserNavigateTool(new FakeCapabilitySurface(runtime), new FakeCallContextFactory(), _originAccessor),
            new BrowserSnapshotTool(new FakeCapabilitySurface(runtime), new FakeCallContextFactory(), _originAccessor),
            new BrowserLocateTool(new FakeCapabilitySurface(runtime), new FakeCallContextFactory(), _originAccessor),
            new BrowserInteractTool(new FakeCapabilitySurface(runtime), new FakeCallContextFactory(), _originAccessor),
            new BrowserWaitForTool(new FakeCapabilitySurface(runtime), new FakeCallContextFactory(), _originAccessor)
        };

        Assert.Equal(
            ["browser_context", "browser_tabs", "browser_navigate", "browser_snapshot",
                "browser_locate", "browser_interact", "browser_wait_for"],
            tools.Select(tool => tool.Descriptor.ToolId));
        Assert.All(tools.Take(3), tool => Assert.Contains("action", tool.Descriptor.Parameters.Required));
        Assert.Contains("page_id", tools[2].Descriptor.Parameters.Required);
        Assert.All(tools.Skip(3), tool => Assert.Contains("page_id", tool.Descriptor.Parameters.Required));
    }

    [Fact]
    public async Task BrowserContext_CreateListGetAndClose_UseRuntimeAbstraction()
    {
        var runtime = new FakeBrowserRuntime();
        var tool = new BrowserContextTool(new FakeCapabilitySurface(runtime), new FakeCapabilitySurface(runtime), new FakeCallContextFactory(), _originAccessor);

        var created = await ExecuteAsync(tool, """{"action":"create","context_id":"ctx-1"}""");
        var listed = await ExecuteAsync(tool, """{"action":"list"}""");
        var got = await ExecuteAsync(tool, """{"action":"get","context_id":"ctx-1"}""");
        var closed = await ExecuteAsync(tool, """{"action":"close","context_id":"ctx-1"}""");

        Assert.True(created.Success);
        Assert.True(listed.Success);
        Assert.True(got.Success);
        Assert.True(closed.Success);
        Assert.Empty(await runtime.ListContextsAsync(CancellationToken.None));
        AssertJsonOk(created.Output, "ctx-1");
    }

    [Fact]
    public async Task BrowserTabs_NewListActivateAndClose_ManageVisiblePages()
    {
        var runtime = new FakeBrowserRuntime();
        var context = await runtime.CreateContextAsync(
            new BrowserContextOptions { Id = new BrowserContextId("ctx-1") },
            CancellationToken.None);
        var tool = new BrowserTabsTool(new FakeCapabilitySurface(runtime), new FakeCallContextFactory(), _originAccessor);

        var created = await ExecuteAsync(tool,
            """{"action":"new","context_id":"ctx-1","url":"https://example.com"}""");
        var pageId = GetJson(created.Output, "pageId").GetString();
        var listed = await ExecuteAsync(tool, """{"action":"list","context_id":"ctx-1"}""");
        var activated = await ExecuteAsync(tool,
            $$"""{"action":"activate","context_id":"ctx-1","page_id":"{{pageId}}"}""");
        var closed = await ExecuteAsync(tool,
            $$"""{"action":"close","context_id":"ctx-1","page_id":"{{pageId}}"}""");

        Assert.True(created.Success);
        Assert.True(listed.Success);
        Assert.True(activated.Success);
        Assert.True(closed.Success);
        Assert.Equal(pageId, ((FakeBrowserContext)context).LastActivatedPageId?.Value);
        Assert.Empty(await context.ListPagesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task BrowserNavigate_GotoReturnsNavigationAndUpdatedPage()
    {
        var runtime = new FakeBrowserRuntime();
        var context = await runtime.CreateContextAsync(
            new BrowserContextOptions { Id = new BrowserContextId("ctx-1") },
            CancellationToken.None);
        var page = await context.NewPageAsync(new PageCreateOptions(), CancellationToken.None);
        var tool = new BrowserNavigateTool(new FakeCapabilitySurface(runtime), new FakeCallContextFactory(), _originAccessor);

        var result = await ExecuteAsync(tool,
            $$"""{"action":"goto","context_id":"ctx-1","page_id":"{{page.Id.Value}}","url":"https://example.org"}""");

        Assert.True(result.Success);
        Assert.Equal("https://example.org/", page.Info.Url);
        Assert.True(GetJson(result.Output, "value").GetProperty("navigationOk").GetBoolean());
    }

    [Fact]
    public async Task InvalidAction_ReturnsStableStructuredFailure()
    {
        var result = await ExecuteAsync(
            new BrowserTabsTool(new FakeCapabilitySurface(new FakeBrowserRuntime()), new FakeCallContextFactory(), _originAccessor),
            """{"action":"teleport"}""");

        Assert.False(result.Success);
        using var document = JsonDocument.Parse(result.Error!);
        Assert.Equal("browser_invalid_arguments",
            document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task BrowserDomainFailure_PreservesStableErrorCode()
    {
        var runtime = new FakeBrowserRuntime
        {
            Failure = new BrowserOperationException("browser_not_available", "Desktop is disconnected")
        };

        var result = await ExecuteAsync(
            new BrowserContextTool(new FakeCapabilitySurface(runtime), new FakeCapabilitySurface(runtime), new FakeCallContextFactory(), _originAccessor),
            """{"action":"list"}""");

        Assert.False(result.Success);
        using var document = JsonDocument.Parse(result.Error!);
        Assert.Equal("browser_not_available",
            document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task CallerCancellation_IsNotConvertedToOrdinaryToolFailure()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ExecuteAsync(new BrowserContextTool(new FakeCapabilitySurface(new FakeBrowserRuntime()), new FakeCapabilitySurface(new FakeBrowserRuntime()), new FakeCallContextFactory(), _originAccessor),
                """{"action":"list"}""", cts.Token));
    }

    [Fact]
    public async Task SnapshotAndLocate_ReturnVersionedRefsAndBoundedMetadata()
    {
        var runtime = new FakeBrowserRuntime();
        var context = await runtime.CreateContextAsync(
            new BrowserContextOptions { Id = new BrowserContextId("ctx-1") }, CancellationToken.None);
        var page = await context.NewPageAsync(new PageCreateOptions(), CancellationToken.None);

        var snapshot = await ExecuteAsync(new BrowserSnapshotTool(new FakeCapabilitySurface(runtime), new FakeCallContextFactory(), _originAccessor),
            $$"""{"page_id":"{{page.Id.Value}}","context_id":"ctx-1","max_nodes":100}""");
        var locate = await ExecuteAsync(new BrowserLocateTool(new FakeCapabilitySurface(runtime), new FakeCallContextFactory(), _originAccessor),
            JsonSerializer.Serialize(new
            {
                page_id = page.Id.Value,
                context_id = "ctx-1",
                locator = new { kind = "role", value = "button", name = "Save" }
            }));

        Assert.True(snapshot.Success);
        Assert.True(locate.Success);
        Assert.Contains("v1-n1", snapshot.Output, StringComparison.Ordinal);
        Assert.Equal("v1-n1", GetJson(locate.Output, "value").GetProperty("elements")[0].GetProperty("ref").GetString());
    }

    [Fact]
    public async Task Locate_OmitsFormValueByDefault_AndReturnsItWhenRequested()
    {
        // 设计方案 §6.2：value/selectedValues 是「输入是否写入成功」的唯一可靠回执，
        // 但**只在显式请求时**返回（默认省掉才能守住输出预算）。
        var runtime = new FakeBrowserRuntime();
        var context = await runtime.CreateContextAsync(
            new BrowserContextOptions { Id = new BrowserContextId("ctx-1") }, CancellationToken.None);
        var page = await context.NewPageAsync(new PageCreateOptions(), CancellationToken.None);

        var locator = new { kind = "role", value = "button", name = "Save" };
        var byDefault = await ExecuteAsync(
            new BrowserLocateTool(new FakeCapabilitySurface(runtime), new FakeCallContextFactory(), _originAccessor),
            JsonSerializer.Serialize(new { page_id = page.Id.Value, context_id = "ctx-1", locator }));

        var requested = await ExecuteAsync(
            new BrowserLocateTool(new FakeCapabilitySurface(runtime), new FakeCallContextFactory(), _originAccessor),
            JsonSerializer.Serialize(new { page_id = page.Id.Value, context_id = "ctx-1", locator, include_value = true }));

        Assert.True(byDefault.Success);
        Assert.True(requested.Success);

        var defaultElement = GetJson(byDefault.Output, "value").GetProperty("elements")[0];
        var requestedElement = GetJson(requested.Output, "value").GetProperty("elements")[0];

        // 默认：字段为 null（契约里 null = 未请求/未知），且输出里**不含**真实值。
        Assert.Equal(JsonValueKind.Null, defaultElement.GetProperty("value").ValueKind);
        Assert.DoesNotContain("Pudding", byDefault.Output, StringComparison.Ordinal);

        // 显式请求：回读到真实值。
        Assert.Equal("Pudding", requestedElement.GetProperty("value").GetString());
    }

    [Fact]
    public async Task InteractAndWait_UsePageAbstractionWithoutEchoingFillValue()
    {
        var runtime = new FakeBrowserRuntime();
        var context = await runtime.CreateContextAsync(
            new BrowserContextOptions { Id = new BrowserContextId("ctx-1") }, CancellationToken.None);
        var page = (FakeBrowserPage)await context.NewPageAsync(new PageCreateOptions(), CancellationToken.None);
        const string secret = "do-not-echo-this-value";

        var interact = await ExecuteAsync(new BrowserInteractTool(new FakeCapabilitySurface(runtime), new FakeCallContextFactory(), _originAccessor),
            JsonSerializer.Serialize(new
            {
                action = "fill",
                page_id = page.Id.Value,
                context_id = "ctx-1",
                locator = new { kind = "label", value = "Name" },
                text = secret
            }));
        var wait = await ExecuteAsync(new BrowserWaitForTool(new FakeCapabilitySurface(runtime), new FakeCallContextFactory(), _originAccessor),
            $$"""{"page_id":"{{page.Id.Value}}","context_id":"ctx-1","selector":"#saved","timeout_ms":1000}""");

        Assert.True(interact.Success);
        Assert.True(wait.Success);
        Assert.Equal("fill", page.LastAction);
        Assert.Equal(secret, page.LastValue);
        Assert.DoesNotContain(secret, interact.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Interact_FillWithExpectValue_ConfirmsTheWriteInOneCall()
    {
        // 门禁 G7：一次 fill 的回执就足以确认写入成功，不需要再发一次 locate/snapshot。
        var runtime = new FakeBrowserRuntime();
        var context = await runtime.CreateContextAsync(
            new BrowserContextOptions { Id = new BrowserContextId("ctx-1") }, CancellationToken.None);
        var page = await context.NewPageAsync(new PageCreateOptions(), CancellationToken.None);

        var matched = await ExecuteAsync(
            new BrowserInteractTool(new FakeCapabilitySurface(runtime), new FakeCallContextFactory(), _originAccessor),
            JsonSerializer.Serialize(new
            {
                action = "fill",
                page_id = page.Id.Value,
                context_id = "ctx-1",
                locator = new { kind = "label", value = "Name" },
                text = "Pudding",
                expect_value = "Pudding"
            }));

        var mismatched = await ExecuteAsync(
            new BrowserInteractTool(new FakeCapabilitySurface(runtime), new FakeCallContextFactory(), _originAccessor),
            JsonSerializer.Serialize(new
            {
                action = "fill",
                page_id = page.Id.Value,
                context_id = "ctx-1",
                locator = new { kind = "label", value = "Name" },
                text = "Pudding",
                expect_value = "something-else"
            }));

        var withoutExpectation = await ExecuteAsync(
            new BrowserInteractTool(new FakeCapabilitySurface(runtime), new FakeCallContextFactory(), _originAccessor),
            JsonSerializer.Serialize(new
            {
                action = "fill",
                page_id = page.Id.Value,
                context_id = "ctx-1",
                locator = new { kind = "label", value = "Name" },
                text = "Pudding"
            }));

        Assert.True(matched.Success);
        var verified = GetJson(matched.Output, "value");
        Assert.Equal("verified", verified.GetProperty("completion").GetString());
        Assert.True(verified.GetProperty("ok").GetBoolean());
        Assert.True(verified.GetProperty("assertions")[0].GetProperty("matched").GetBoolean());

        var failed = GetJson(mismatched.Output, "value");
        Assert.Equal("failed", failed.GetProperty("completion").GetString());
        Assert.False(failed.GetProperty("ok").GetBoolean());
        Assert.False(failed.GetProperty("assertions")[0].GetProperty("matched").GetBoolean());

        // 没有声明期望 ⇒ 如实报 dispatched，不假装成功。
        var dispatched = GetJson(withoutExpectation.Output, "value");
        Assert.Equal("dispatched", dispatched.GetProperty("completion").GetString());
        Assert.True(dispatched.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Interact_Receipt_NeverEchoesTheFormValue()
    {
        // 证明「写进去了」但不能把输入内容带进工具输出。
        const string secret = "hunter2-never-echo";
        var runtime = new FakeBrowserRuntime();
        var context = await runtime.CreateContextAsync(
            new BrowserContextOptions { Id = new BrowserContextId("ctx-1") }, CancellationToken.None);
        var page = await context.NewPageAsync(new PageCreateOptions(), CancellationToken.None);

        var result = await ExecuteAsync(
            new BrowserInteractTool(new FakeCapabilitySurface(runtime), new FakeCallContextFactory(), _originAccessor),
            JsonSerializer.Serialize(new
            {
                action = "fill",
                page_id = page.Id.Value,
                context_id = "ctx-1",
                locator = new { kind = "label", value = "Name" },
                text = secret,
                expect_value = secret
            }));

        Assert.True(result.Success);
        var assertion = GetJson(result.Output, "value").GetProperty("assertions")[0];

        // 敏感断言：只给 matched 与长度。
        Assert.True(assertion.GetProperty("matched").GetBoolean());
        Assert.Equal(JsonValueKind.Null, assertion.GetProperty("actual").ValueKind);
        Assert.Equal(secret.Length, assertion.GetProperty("length").GetInt32());
        Assert.DoesNotContain(secret, result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Snapshot_DefaultsToOutline_AndDoesNotRequestTheDomTree()
    {
        // 设计方案 §6.1：默认 outline —— 只请求无障碍树（DOM 树是体积大头），
        // 并用**内部扫描预算**采集，输出再按 limit/max_bytes 收敛。
        var runtime = new FakeBrowserRuntime();
        var context = await runtime.CreateContextAsync(
            new BrowserContextOptions { Id = new BrowserContextId("ctx-1") }, CancellationToken.None);
        var page = (FakeBrowserPage)await context.NewPageAsync(new PageCreateOptions(), CancellationToken.None);

        var snapshot = await ExecuteAsync(
            new BrowserSnapshotTool(new FakeCapabilitySurface(runtime), new FakeCallContextFactory(), _originAccessor),
            JsonSerializer.Serialize(new { page_id = page.Id.Value, context_id = "ctx-1" }));

        Assert.True(snapshot.Success);

        // ① 发给驱动的采集预算：不要 DOM、不要 HTML，只要无障碍树，且扫描预算明显大于输出预算。
        var options = page.LastSnapshotOptions;
        Assert.NotNull(options);
        Assert.False(options!.IncludeDom);
        Assert.False(options.IncludeHtml);
        Assert.True(options.IncludeAccessibilityTree);
        Assert.Equal(10_000, options.MaxNodes);

        // ② 输出形状：outline 投影 + 字节口径，domText 为 null。
        var value = GetJson(snapshot.Output, "value");
        Assert.Equal("outline", value.GetProperty("mode").GetString());
        Assert.Equal(JsonValueKind.Null, value.GetProperty("domText").ValueKind);
        Assert.True(value.GetProperty("bytes").GetInt32() > 0);
        Assert.Equal("none", value.GetProperty("truncationReason").GetString());

        // ③ outline 保留结构行与可交互行，丢掉无语义的容器行。
        var tree = value.GetProperty("accessibilityTree").GetString()!;
        Assert.Contains("heading", tree, StringComparison.Ordinal);
        Assert.Contains("v1-n1", tree, StringComparison.Ordinal);
        Assert.DoesNotContain("document", tree, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Snapshot_InteractiveMode_KeepsOnlyRefLines()
    {
        var runtime = new FakeBrowserRuntime();
        var context = await runtime.CreateContextAsync(
            new BrowserContextOptions { Id = new BrowserContextId("ctx-1") }, CancellationToken.None);
        var page = await context.NewPageAsync(new PageCreateOptions(), CancellationToken.None);

        var snapshot = await ExecuteAsync(
            new BrowserSnapshotTool(new FakeCapabilitySurface(runtime), new FakeCallContextFactory(), _originAccessor),
            JsonSerializer.Serialize(new { page_id = page.Id.Value, context_id = "ctx-1", mode = "interactive" }));

        Assert.True(snapshot.Success);
        var value = GetJson(snapshot.Output, "value");
        Assert.Equal("interactive", value.GetProperty("mode").GetString());

        var tree = value.GetProperty("accessibilityTree").GetString()!;
        foreach (var line in tree.Split('\n'))
        {
            Assert.Contains("ref=", line);
        }

        Assert.DoesNotContain("heading", tree, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Snapshot_FullMode_KeepsTheLegacyShape()
    {
        var runtime = new FakeBrowserRuntime();
        var context = await runtime.CreateContextAsync(
            new BrowserContextOptions { Id = new BrowserContextId("ctx-1") }, CancellationToken.None);
        var page = (FakeBrowserPage)await context.NewPageAsync(new PageCreateOptions(), CancellationToken.None);

        var snapshot = await ExecuteAsync(
            new BrowserSnapshotTool(new FakeCapabilitySurface(runtime), new FakeCallContextFactory(), _originAccessor),
            JsonSerializer.Serialize(new
            {
                page_id = page.Id.Value,
                context_id = "ctx-1",
                mode = "full",
                include_dom = true,
                max_nodes = 100
            }));

        Assert.True(snapshot.Success);
        var value = GetJson(snapshot.Output, "value");
        Assert.Equal("full", value.GetProperty("mode").GetString());
        Assert.NotNull(value.GetProperty("domText").GetString());
        Assert.Equal(100, page.LastSnapshotOptions!.MaxNodes);
        Assert.Equal(JsonValueKind.Null, value.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task Snapshot_TextMode_IsExplicitlyUnsupported_NotASilentFallback()
    {
        // 正文 markdown 需要驱动侧提取；绝不能静默降级成 full（那会让调用方以为拿到了正文）。
        var runtime = new FakeBrowserRuntime();
        var context = await runtime.CreateContextAsync(
            new BrowserContextOptions { Id = new BrowserContextId("ctx-1") }, CancellationToken.None);
        var page = await context.NewPageAsync(new PageCreateOptions(), CancellationToken.None);

        var snapshot = await ExecuteAsync(
            new BrowserSnapshotTool(new FakeCapabilitySurface(runtime), new FakeCallContextFactory(), _originAccessor),
            JsonSerializer.Serialize(new { page_id = page.Id.Value, context_id = "ctx-1", mode = "text" }));

        Assert.False(snapshot.Success);
        using var document = JsonDocument.Parse(snapshot.Error!);
        var error = document.RootElement.GetProperty("error");
        Assert.Equal("browser_operation_not_supported", error.GetProperty("code").GetString());
        Assert.Contains("not implemented", error.GetProperty("message").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Snapshot_OutputBudget_ReportsCursor_AndScanTruncationReportsIncomplete()
    {
        var runtime = new FakeBrowserRuntime();
        var context = await runtime.CreateContextAsync(
            new BrowserContextOptions { Id = new BrowserContextId("ctx-1") }, CancellationToken.None);
        var page = await context.NewPageAsync(new PageCreateOptions(), CancellationToken.None);

        // ① 扫描完整、只是行数收敛 ⇒ output_budget + 游标可续读。
        var limited = await ExecuteAsync(
            new BrowserSnapshotTool(new FakeCapabilitySurface(runtime), new FakeCallContextFactory(), _originAccessor),
            JsonSerializer.Serialize(new { page_id = page.Id.Value, context_id = "ctx-1", limit = 1 }));

        var limitedValue = GetJson(limited.Output, "value");
        Assert.True(limitedValue.GetProperty("truncated").GetBoolean());
        Assert.Equal("output_budget", limitedValue.GetProperty("truncationReason").GetString());
        Assert.Equal(1, limitedValue.GetProperty("nextCursor").GetInt32());

        // ② 扫描侧被截断 ⇒ incomplete_scan，且不给游标（续读也保证不了完整）。
        var truncatedPage = (FakeBrowserPage)await context.NewPageAsync(new PageCreateOptions(), CancellationToken.None);
        truncatedPage.SnapshotTruncated = true;

        var incomplete = await ExecuteAsync(
            new BrowserSnapshotTool(new FakeCapabilitySurface(runtime), new FakeCallContextFactory(), _originAccessor),
            JsonSerializer.Serialize(new { page_id = truncatedPage.Id.Value, context_id = "ctx-1" }));

        var incompleteValue = GetJson(incomplete.Output, "value");
        Assert.Equal("incomplete_scan", incompleteValue.GetProperty("truncationReason").GetString());
        Assert.Equal(JsonValueKind.Null, incompleteValue.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task Snapshot_RejectsUnknownModeAndOutOfRangeBudgets()
    {
        var runtime = new FakeBrowserRuntime();
        var context = await runtime.CreateContextAsync(
            new BrowserContextOptions { Id = new BrowserContextId("ctx-1") }, CancellationToken.None);
        var page = await context.NewPageAsync(new PageCreateOptions(), CancellationToken.None);

        var badMode = await ExecuteAsync(
            new BrowserSnapshotTool(new FakeCapabilitySurface(runtime), new FakeCallContextFactory(), _originAccessor),
            JsonSerializer.Serialize(new { page_id = page.Id.Value, context_id = "ctx-1", mode = "brand_new" }));

        var badBudget = await ExecuteAsync(
            new BrowserSnapshotTool(new FakeCapabilitySurface(runtime), new FakeCallContextFactory(), _originAccessor),
            JsonSerializer.Serialize(new { page_id = page.Id.Value, context_id = "ctx-1", limit = 0 }));

        Assert.False(badMode.Success);
        Assert.False(badBudget.Success);
    }

    [Fact]
    public async Task Interact_Failure_CarriesAReceipt_SoDispatchFactsAreNotLost()
    {
        // 设计 §4.1：`unknown`/`blocked`/`failed` 都是 ok=false，但**不能丢失已经派发的事实**。
        // 只给 code/message 会让调用方把"可能已经执行"误读成"没执行"（然后重做一次副作用）。
        var runtime = new FakeBrowserRuntime();
        var context = await runtime.CreateContextAsync(
            new BrowserContextOptions { Id = new BrowserContextId("ctx-1") }, CancellationToken.None);
        var page = (FakeBrowserPage)await context.NewPageAsync(new PageCreateOptions(), CancellationToken.None);

        // 未知页面 ⇒ InvalidTarget：动作未开始、无副作用；该码在错误目录里**不可重试**（目标不会自己出现）。
        var result = await ExecuteAsync(
            new BrowserInteractTool(new FakeCapabilitySurface(runtime), new FakeCallContextFactory(), _originAccessor),
            JsonSerializer.Serialize(new
            {
                action = "fill",
                page_id = "page-does-not-exist",
                context_id = "ctx-1",
                locator = new { kind = "label", value = "Name" },
                text = "Pudding"
            }));

        Assert.False(result.Success);
        using var document = JsonDocument.Parse(result.Error!);
        Assert.Equal("browser_page_not_found",
            document.RootElement.GetProperty("error").GetProperty("code").GetString());

        // 失败同样带结构化回执：调用方能看出"确实没执行"（而不是靠猜）。
        var receipt = document.RootElement.GetProperty("value");
        Assert.Equal("failed", receipt.GetProperty("completion").GetString());
        Assert.Equal("not_started", receipt.GetProperty("execution").GetString());
        Assert.False(receipt.GetProperty("mayHaveSideEffects").GetBoolean());
        Assert.Equal("not_retryable", receipt.GetProperty("retryDisposition").GetString());
    }

    [Fact]
    public async Task Interact_Failure_ThatIsSafeToRetry_SaysSo()
    {
        // 连接不可用：明确未开始 + 可重试 + 无副作用 ⇒ 这是唯一允许自动重试的组合（设计 §4.3）。
        var runtime = new FakeBrowserRuntime();
        var context = await runtime.CreateContextAsync(
            new BrowserContextOptions { Id = new BrowserContextId("ctx-1") }, CancellationToken.None);
        var page = await context.NewPageAsync(new PageCreateOptions(), CancellationToken.None);
        var surface = new FakeCapabilitySurface(runtime)
        {
            InteractFailure = DesktopCapabilityError.NotConnected("desktop not connected"),
        };

        var result = await ExecuteAsync(
            new BrowserInteractTool(surface, new FakeCallContextFactory(), _originAccessor),
            JsonSerializer.Serialize(new
            {
                action = "fill",
                page_id = page.Id.Value,
                context_id = "ctx-1",
                locator = new { kind = "label", value = "Name" },
                text = "Pudding"
            }));

        Assert.False(result.Success);
        using var document = JsonDocument.Parse(result.Error!);
        var receipt = document.RootElement.GetProperty("value");
        Assert.Equal("failed", receipt.GetProperty("completion").GetString());
        Assert.Equal("not_started", receipt.GetProperty("execution").GetString());
        Assert.True(receipt.GetProperty("safeToRetry").GetBoolean());
        Assert.Equal("safe_to_retry", receipt.GetProperty("retryDisposition").GetString());
    }

    [Fact]
    public async Task Interact_Failure_OnAGateDenial_IsBlocked_NotJustFailed()
    {
        // 闸门拒绝（接管/暂停/授权）与普通失败语义不同：不得自动重试，要先重新准入。
        var runtime = new FakeBrowserRuntime();
        var context = await runtime.CreateContextAsync(
            new BrowserContextOptions { Id = new BrowserContextId("ctx-1") }, CancellationToken.None);
        var page = await context.NewPageAsync(new PageCreateOptions(), CancellationToken.None);
        var surface = new FakeCapabilitySurface(runtime)
        {
            InteractFailure = DesktopCapabilityError.Unauthorized("not the agent target"),
        };

        var result = await ExecuteAsync(
            new BrowserInteractTool(surface, new FakeCallContextFactory(), _originAccessor),
            JsonSerializer.Serialize(new
            {
                action = "fill",
                page_id = page.Id.Value,
                context_id = "ctx-1",
                locator = new { kind = "label", value = "Name" },
                text = "Pudding"
            }));

        Assert.False(result.Success);
        using var document = JsonDocument.Parse(result.Error!);
        var receipt = document.RootElement.GetProperty("value");
        Assert.Equal("blocked", receipt.GetProperty("completion").GetString());
        Assert.Equal("not_retryable", receipt.GetProperty("retryDisposition").GetString());
        Assert.False(receipt.GetProperty("mayHaveSideEffects").GetBoolean());
    }

    [Fact]
    public async Task Interact_Success_CarriesTheRetryDisposition()
    {
        var runtime = new FakeBrowserRuntime();
        var context = await runtime.CreateContextAsync(
            new BrowserContextOptions { Id = new BrowserContextId("ctx-1") }, CancellationToken.None);
        var page = await context.NewPageAsync(new PageCreateOptions(), CancellationToken.None);

        var result = await ExecuteAsync(
            new BrowserInteractTool(new FakeCapabilitySurface(runtime), new FakeCallContextFactory(), _originAccessor),
            JsonSerializer.Serialize(new
            {
                action = "click",
                page_id = page.Id.Value,
                context_id = "ctx-1",
                locator = new { kind = "label", value = "Name" }
            }));

        Assert.True(result.Success);
        var value = GetJson(result.Output, "value");
        // click 没有声明期望 ⇒ dispatched ⇒ 先观察，勿重做。
        Assert.Equal("dispatched", value.GetProperty("completion").GetString());
        Assert.Equal("dispatched_observe_only", value.GetProperty("retryDisposition").GetString());
        Assert.True(value.GetProperty("requiresFollowUp").GetBoolean());
    }

    [Fact]
    public async Task Interact_StaleRefException_AlsoCarriesAReceipt_WithARefreshHint()
    {
        // 驱动抛出的异常（引用过期）同样要给回执 + 最小修复提示：
        // 只给 code/message 会让调用方不知道"到底派发没有"，也不知道下一步该做什么。
        // 并且"重试同一次调用"毫无意义（同一个 ref 一定再次失败）⇒ 不可自动重试。
        var runtime = new FakeBrowserRuntime();
        var context = await runtime.CreateContextAsync(
            new BrowserContextOptions { Id = new BrowserContextId("ctx-1") }, CancellationToken.None);
        var page = (FakeBrowserPage)await context.NewPageAsync(new PageCreateOptions(), CancellationToken.None);

        var result = await ExecuteAsync(
            new BrowserInteractTool(new FakeCapabilitySurface(runtime), new FakeCallContextFactory(), _originAccessor),
            JsonSerializer.Serialize(new
            {
                action = "click",
                page_id = page.Id.Value,
                context_id = "ctx-1",
                locator = new { kind = "ref", value = "v0-n1" }
            }));

        Assert.False(result.Success);
        using var document = JsonDocument.Parse(result.Error!);
        var root = document.RootElement;

        Assert.Equal("stale_element_reference", root.GetProperty("error").GetProperty("code").GetString());

        var receipt = root.GetProperty("value");
        Assert.Equal("failed", receipt.GetProperty("completion").GetString());
        Assert.Equal("not_started", receipt.GetProperty("execution").GetString());
        Assert.False(receipt.GetProperty("mayHaveSideEffects").GetBoolean());
        // 同一个 ref 一定再次失败 ⇒ 不可自动重试，但必须告诉调用方**该怎么刷新**。
        Assert.Equal("not_retryable", receipt.GetProperty("retryDisposition").GetString());
        Assert.Contains("re-observe", receipt.GetProperty("retryDetail").GetString()!, StringComparison.OrdinalIgnoreCase);

        // 提示同时作为 warning 暴露（调用方不必解析回执才能看到）。
        Assert.Contains("re-observe", result.Error!, StringComparison.OrdinalIgnoreCase);

        // 什么都没派发：替身页面没有记录任何动作。
        Assert.Null(page.LastAction);
    }

    [Theory]
    [InlineData("browser_locator_ambiguous", "not_retryable", "nth")]
    [InlineData("browser_element_not_visible", "not_retryable", "scroll")]
    [InlineData("browser_element_disabled", "not_retryable", "enabled")]
    [InlineData("browser_element_not_found", "safe_to_retry", "wait")]
    [InlineData("browser_element_not_in_viewport", "not_retryable", "scrolled into view")]
    [InlineData("browser_element_obstructed", "not_retryable", "covered by another element")]
    [InlineData("browser_locator_matches_filtered", "not_retryable", "includeHidden")]
    public async Task Interact_DriverRejections_MapToTheRightRetryAdvice(
        string driverCode, string expectedDisposition, string expectedHintFragment)
    {
        // 设计 §6.2：歧义必须明确拒绝（不静默选第一个）；零命中不等于"定位方式全坏"（可以等）。
        // 这些拒绝都**没派发**，但修复方式不同 ⇒ 重试建议与提示必须分别正确。
        var runtime = new FakeBrowserRuntime();
        var context = await runtime.CreateContextAsync(
            new BrowserContextOptions { Id = new BrowserContextId("ctx-1") }, CancellationToken.None);
        var page = (FakeBrowserPage)await context.NewPageAsync(new PageCreateOptions(), CancellationToken.None);
        page.ActionFailure = new BrowserOperationException(driverCode, "driver rejected the action");

        var result = await ExecuteAsync(
            new BrowserInteractTool(new FakeCapabilitySurface(runtime), new FakeCallContextFactory(), _originAccessor),
            JsonSerializer.Serialize(new
            {
                action = "click",
                page_id = page.Id.Value,
                context_id = "ctx-1",
                locator = new { kind = "text", value = "Learn more" }
            }));

        Assert.False(result.Success);
        using var document = JsonDocument.Parse(result.Error!);
        var root = document.RootElement;
        Assert.Equal(driverCode, root.GetProperty("error").GetProperty("code").GetString());

        var receipt = root.GetProperty("value");
        Assert.Equal("not_started", receipt.GetProperty("execution").GetString());
        Assert.False(receipt.GetProperty("mayHaveSideEffects").GetBoolean());
        Assert.Equal(expectedDisposition, receipt.GetProperty("retryDisposition").GetString());
        Assert.Contains(
            expectedHintFragment,
            receipt.GetProperty("retryDetail").GetString()!,
            StringComparison.OrdinalIgnoreCase);

        // 一次驱动动作都没发生。
        Assert.Null(page.LastAction);
    }

    [Fact]
    public async Task Locate_AppliesTheDesignOutputRules_AndTheDefaultLimit()
    {
        // 设计 §6.2：text 与 name 相同则省略、checked 显式请求、bbox 最多一位小数、默认 limit=10。
        var runtime = new FakeBrowserRuntime();
        var context = await runtime.CreateContextAsync(
            new BrowserContextOptions { Id = new BrowserContextId("ctx-1") }, CancellationToken.None);
        var page = (FakeBrowserPage)await context.NewPageAsync(new PageCreateOptions(), CancellationToken.None);
        var surface = new FakeCapabilitySurface(runtime);

        var locate = await ExecuteAsync(
            new BrowserLocateTool(surface, new FakeCallContextFactory(), _originAccessor),
            JsonSerializer.Serialize(new
            {
                page_id = page.Id.Value,
                context_id = "ctx-1",
                locator = new { kind = "role", value = "button", name = "Save" }
            }));

        Assert.True(locate.Success);

        // 默认 limit=10 确实传到了能力层。
        Assert.Equal(10, surface.LastLocateRequest!.MaxResults);

        var element = GetJson(locate.Output, "value").GetProperty("elements")[0];

        // text == name（替身里都是 "Save"）⇒ 省略 text。
        Assert.Equal(JsonValueKind.Null, element.GetProperty("text").ValueKind);
        // checked 属显式请求：默认不给。
        Assert.Equal(JsonValueKind.Null, element.GetProperty("checked").ValueKind);
        // ✓ bbox 展示保留**最多一位小数**（驱动给的是 10.25 / 20.5 / 80.04 / 30.75）。
        var box = element.GetProperty("boundingBox");
        Assert.Equal(10.3, box.GetProperty("x").GetDouble());
        Assert.Equal(20.5, box.GetProperty("y").GetDouble());
        Assert.Equal(80.0, box.GetProperty("width").GetDouble());
        Assert.Equal(30.8, box.GetProperty("height").GetDouble());

        // 显式请求时才给 value（checked 由替身未设而保持 null）。
        var explicitState = await ExecuteAsync(
            new BrowserLocateTool(new FakeCapabilitySurface(runtime), new FakeCallContextFactory(), _originAccessor),
            JsonSerializer.Serialize(new
            {
                page_id = page.Id.Value,
                context_id = "ctx-1",
                locator = new { kind = "role", value = "button", name = "Save" },
                include_value = true,
                include_checked = true
            }));

        var explicitElement = GetJson(explicitState.Output, "value").GetProperty("elements")[0];
        Assert.Equal("Pudding", explicitElement.GetProperty("value").GetString());
    }

    [Fact]
    public async Task Locate_PassesIncludeHiddenThroughToTheCapabilityLayer()
    {
        // 设计 §6.2：locate 默认 includeHidden=false（少噪音），显式打开才带上隐藏元素。
        var runtime = new FakeBrowserRuntime();
        var context = await runtime.CreateContextAsync(
            new BrowserContextOptions { Id = new BrowserContextId("ctx-1") }, CancellationToken.None);
        var page = await context.NewPageAsync(new PageCreateOptions(), CancellationToken.None);
        var surface = new FakeCapabilitySurface(runtime);

        var byDefault = await ExecuteAsync(
            new BrowserLocateTool(surface, new FakeCallContextFactory(), _originAccessor),
            JsonSerializer.Serialize(new
            {
                page_id = page.Id.Value,
                context_id = "ctx-1",
                locator = new { kind = "css", value = ".item" }
            }));

        Assert.True(byDefault.Success);
        Assert.False(surface.LastLocateRequest!.Locator.IncludeHidden);

        var explicitHidden = await ExecuteAsync(
            new BrowserLocateTool(surface, new FakeCallContextFactory(), _originAccessor),
            JsonSerializer.Serialize(new
            {
                page_id = page.Id.Value,
                context_id = "ctx-1",
                // ⚠️ 嵌套字段必须用 camelCase：工具框架的 snake_case 归一化**只作用于顶层参数**
                // （PuddingToolContracts.DeserializeArgs 只重写 RootElement 的属性名），
                // `locator.include_hidden` 不会被绑定。
                locator = new { kind = "css", value = ".item", includeHidden = true }
            }));

        Assert.True(explicitHidden.Success);
        Assert.True(surface.LastLocateRequest!.Locator.IncludeHidden);
    }

    [Fact]
    public async Task Locate_SeparatesVisibleFromInViewport_AndPassesInViewportOnly()
    {
        // 设计 §6.2：visible 与 inViewport 是两个事实；inViewportOnly 是正交的筛选。
        var runtime = new FakeBrowserRuntime();
        var context = await runtime.CreateContextAsync(
            new BrowserContextOptions { Id = new BrowserContextId("ctx-1") }, CancellationToken.None);
        var page = await context.NewPageAsync(new PageCreateOptions(), CancellationToken.None);
        var surface = new FakeCapabilitySurface(runtime);

        var located = await ExecuteAsync(
            new BrowserLocateTool(surface, new FakeCallContextFactory(), _originAccessor),
            JsonSerializer.Serialize(new
            {
                page_id = page.Id.Value,
                context_id = "ctx-1",
                locator = new { kind = "role", value = "button", name = "Save", inViewportOnly = true }
            }));

        Assert.True(located.Success);

        // 两个事实都出现在输出里（不是一个代替另一个）。
        var element = GetJson(located.Output, "value").GetProperty("elements")[0];
        Assert.True(element.GetProperty("visible").GetBoolean());
        Assert.True(element.GetProperty("inViewport").GetBoolean());

        // inViewportOnly 真的传到了能力层（默认 false）。
        Assert.True(surface.LastLocateRequest!.Locator.InViewportOnly);
    }

    [Fact]
    public async Task Locate_DistinguishesZeroHitFromMatchesThatWereFilteredAway()
    {
        // 设计 §6.2：「不能把一次零命中推广成所有 text 定位都坏」。
        // 「命中了但全被隐藏/视口筛掉」是**这次筛选没用对**，与"真的没有"下一步完全不同。
        var runtime = new FakeBrowserRuntime();
        var context = await runtime.CreateContextAsync(
            new BrowserContextOptions { Id = new BrowserContextId("ctx-1") }, CancellationToken.None);
        var page = await context.NewPageAsync(new PageCreateOptions(), CancellationToken.None);

        var zeroHit = await ExecuteAsync(
            new BrowserLocateTool(
                new FakeCapabilitySurface(runtime)
                {
                    LocateFailure = new BrowserOperationException(
                        "browser_element_not_found", "No element matched the locator"),
                },
                new FakeCallContextFactory(), _originAccessor),
            JsonSerializer.Serialize(new
            {
                page_id = page.Id.Value,
                context_id = "ctx-1",
                locator = new { kind = "css", value = ".missing" }
            }));

        var filtered = await ExecuteAsync(
            new BrowserLocateTool(
                new FakeCapabilitySurface(runtime)
                {
                    LocateFailure = new BrowserOperationException(
                        "browser_locator_matches_filtered",
                        "Matched 3 element(s) but all were hidden or outside the viewport"),
                },
                new FakeCallContextFactory(), _originAccessor),
            JsonSerializer.Serialize(new
            {
                page_id = page.Id.Value,
                context_id = "ctx-1",
                locator = new { kind = "text", value = "Learn more" }
            }));

        Assert.False(zeroHit.Success);
        Assert.False(filtered.Success);

        // 真的没命中：可等可重试，提示指向"等待或重新观察"。
        Assert.Contains("wait", zeroHit.Error!, StringComparison.OrdinalIgnoreCase);

        // 命中了但被筛掉：不可自动重试，提示直接指向筛选参数。
        Assert.Contains("includeHidden", filtered.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("3 element", filtered.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Interact_NativeInputMode_FailsExplicitly_InsteadOfSilentlyFallingBackToDom()
    {
        // 设计 §7：native 不可用时**明确 unsupported**，不静默切换语义 ——
        // 否则调用方会以为走了真实输入路径，而实际只是 DOM 事件。
        var runtime = new FakeBrowserRuntime();
        var context = await runtime.CreateContextAsync(
            new BrowserContextOptions { Id = new BrowserContextId("ctx-1") }, CancellationToken.None);
        var page = (FakeBrowserPage)await context.NewPageAsync(new PageCreateOptions(), CancellationToken.None);

        var result = await ExecuteAsync(
            new BrowserInteractTool(new FakeCapabilitySurface(runtime), new FakeCallContextFactory(), _originAccessor),
            JsonSerializer.Serialize(new
            {
                action = "click",
                page_id = page.Id.Value,
                context_id = "ctx-1",
                locator = new { kind = "role", value = "button", name = "Save" },
                input_mode = "native"
            }));

        Assert.False(result.Success);
        using var document = JsonDocument.Parse(result.Error!);
        var root = document.RootElement;

        Assert.Equal("browser_input_mode_not_supported", root.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("not implemented", root.GetProperty("error").GetProperty("message").GetString()!, StringComparison.OrdinalIgnoreCase);

        // 明确拒绝同时给回执：未开始、无副作用、不可重试（不是"观察一下"）。
        var receipt = root.GetProperty("value");
        Assert.Equal("not_started", receipt.GetProperty("execution").GetString());
        Assert.False(receipt.GetProperty("mayHaveSideEffects").GetBoolean());
        Assert.Equal("not_retryable", receipt.GetProperty("retryDisposition").GetString());

        // 修复路径直接写进提示（warnings + retryDetail 都带）。
        Assert.Contains(
            "input_mode=dom",
            receipt.GetProperty("retryDetail").GetString()!,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "input_mode=dom",
            string.Join(' ', root.GetProperty("warnings").EnumerateArray().Select(item => item.GetString())),
            StringComparison.OrdinalIgnoreCase);

        // **一次驱动调用都没发生**：拒绝发生在触达页面之前。
        Assert.Null(page.LastAction);
    }

    [Fact]
    public async Task Interact_ReportsTheActualInputBackend()
    {
        var runtime = new FakeBrowserRuntime();
        var context = await runtime.CreateContextAsync(
            new BrowserContextOptions { Id = new BrowserContextId("ctx-1") }, CancellationToken.None);
        var page = await context.NewPageAsync(new PageCreateOptions(), CancellationToken.None);

        var byDefault = await ExecuteAsync(
            new BrowserInteractTool(new FakeCapabilitySurface(runtime), new FakeCallContextFactory(), _originAccessor),
            JsonSerializer.Serialize(new
            {
                action = "click",
                page_id = page.Id.Value,
                context_id = "ctx-1",
                locator = new { kind = "role", value = "button", name = "Save" }
            }));

        var explicitDom = await ExecuteAsync(
            new BrowserInteractTool(new FakeCapabilitySurface(runtime), new FakeCallContextFactory(), _originAccessor),
            JsonSerializer.Serialize(new
            {
                action = "click",
                page_id = page.Id.Value,
                context_id = "ctx-1",
                locator = new { kind = "role", value = "button", name = "Save" },
                input_mode = "dom"
            }));

        var unknown = await ExecuteAsync(
            new BrowserInteractTool(new FakeCapabilitySurface(runtime), new FakeCallContextFactory(), _originAccessor),
            JsonSerializer.Serialize(new
            {
                action = "click",
                page_id = page.Id.Value,
                context_id = "ctx-1",
                locator = new { kind = "role", value = "button", name = "Save" },
                input_mode = "cdp"
            }));

        // 回执报告的是**实际** backend，而不是请求值。
        Assert.Equal("dom", GetJson(byDefault.Output, "value").GetProperty("inputBackend").GetString());
        Assert.Equal("dom", GetJson(explicitDom.Output, "value").GetProperty("inputBackend").GetString());

        // 未登记的线名 fail closed。
        Assert.False(unknown.Success);
        using var document = JsonDocument.Parse(unknown.Error!);
        Assert.Equal("browser_invalid_arguments", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Interact_ScrollReportsFacts_AndNoNewContentIsNotAFailure()
    {
        // 设计 §7：「无限滚动另等新增内容/条目数，**不能把无内容加载当滚动失败**」。
        // 这里构造一个最容易被误判的场景：滚到边界、一个像素都没动、也没加载出新内容。
        var runtime = new FakeBrowserRuntime();
        var context = await runtime.CreateContextAsync(
            new BrowserContextOptions { Id = new BrowserContextId("ctx-1") }, CancellationToken.None);
        var page = (FakeBrowserPage)await context.NewPageAsync(new PageCreateOptions(), CancellationToken.None);
        page.ScrollResult = new ScrollOutcome
        {
            Container = "document",
            BeforeY = 4000,
            AfterY = 4000,
            ConsumedDeltaX = 0,
            ConsumedDeltaY = 0,
            AtBoundary = true,
            ContentChanged = false,
        };

        var result = await ExecuteAsync(
            new BrowserInteractTool(new FakeCapabilitySurface(runtime), new FakeCallContextFactory(), _originAccessor),
            JsonSerializer.Serialize(new
            {
                action = "scroll",
                page_id = page.Id.Value,
                context_id = "ctx-1",
                delta_y = 800,
                scroll_target = "to_bottom"
            }));

        // 事实如实带回：容器、前后位置、消耗位移、边界、内容变化。
        var value = GetJson(result.Output, "value");
        var scroll = value.GetProperty("scroll");
        Assert.Equal("document", scroll.GetProperty("container").GetString());
        Assert.Equal(4000, scroll.GetProperty("afterY").GetDouble());
        Assert.Equal(0, scroll.GetProperty("consumedDeltaY").GetDouble());
        Assert.True(scroll.GetProperty("atBoundary").GetBoolean());
        Assert.False(scroll.GetProperty("contentChanged").GetBoolean());
        Assert.True(scroll.GetProperty("notScrolled").GetBoolean());

        // **但这不是失败**：动作确实派发并完成了，只是没有新内容可等。
        Assert.True(result.Success);
        Assert.True(value.GetProperty("ok").GetBoolean());
        Assert.Equal("completed", value.GetProperty("execution").GetString());

        // 目标模型确实下传到了驱动（to_bottom 覆盖了推导结果）。
        Assert.Equal(ScrollTarget.ToBottom, page.LastScroll!.Target);
    }

    [Fact]
    public async Task Interact_ScrollTargetIsDerivedWhenOmitted()
    {
        var runtime = new FakeBrowserRuntime();
        var context = await runtime.CreateContextAsync(
            new BrowserContextOptions { Id = new BrowserContextId("ctx-1") }, CancellationToken.None);
        var page = (FakeBrowserPage)await context.NewPageAsync(new PageCreateOptions(), CancellationToken.None);

        var result = await ExecuteAsync(
            new BrowserInteractTool(new FakeCapabilitySurface(runtime), new FakeCallContextFactory(), _originAccessor),
            JsonSerializer.Serialize(new
            {
                action = "scroll",
                page_id = page.Id.Value,
                context_id = "ctx-1",
                delta_y = 500
            }));

        Assert.True(result.Success);
        // 无 locator ⇒ page（不是"随手滚一个容器"）。
        Assert.Equal(ScrollTarget.Page, page.LastScroll!.Target);

        var badTarget = await ExecuteAsync(
            new BrowserInteractTool(new FakeCapabilitySurface(runtime), new FakeCallContextFactory(), _originAccessor),
            JsonSerializer.Serialize(new
            {
                action = "scroll",
                page_id = page.Id.Value,
                context_id = "ctx-1",
                delta_y = 500,
                scroll_target = "wheel"
            }));

        Assert.False(badTarget.Success);
        using var document = JsonDocument.Parse(badTarget.Error!);
        Assert.Equal("browser_invalid_arguments", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Locate_FieldsWhitelist_TrimsThePayloadAndUnknownNamesAreRejected()
    {
        // 设计 §6.2：locate 提供 fields —— 只回调用方要的字段（payload 大头是 text/name/bbox）。
        var runtime = new FakeBrowserRuntime();
        var context = await runtime.CreateContextAsync(
            new BrowserContextOptions { Id = new BrowserContextId("ctx-1") }, CancellationToken.None);
        var page = await context.NewPageAsync(new PageCreateOptions(), CancellationToken.None);

        var trimmed = await ExecuteAsync(
            new BrowserLocateTool(new FakeCapabilitySurface(runtime), new FakeCallContextFactory(), _originAccessor),
            JsonSerializer.Serialize(new
            {
                page_id = page.Id.Value,
                context_id = "ctx-1",
                locator = new { kind = "role", value = "button", name = "Save" },
                fields = new[] { "name" }
            }));

        Assert.True(trimmed.Success);
        var element = GetJson(trimmed.Output, "value").GetProperty("elements")[0];

        // 要的留下，没要的**真的不在**（不是"恰好为空"）。
        Assert.Equal("Save", element.GetProperty("name").GetString());
        AssertAbsent(element, "text");
        AssertAbsent(element, "boundingBox");
        AssertAbsent(element, "role");
        AssertAbsent(element, "inViewport");

        // 核心字段恒在：ref 是继续操作的唯一把手。
        Assert.False(string.IsNullOrEmpty(element.GetProperty("ref").GetString()));
        Assert.True(element.GetProperty("visible").GetBoolean());
        Assert.True(element.GetProperty("enabled").GetBoolean());

        // 白名单就是权威：列了 value 就等于要值（不必再传 include_value）。
        var withValue = await ExecuteAsync(
            new BrowserLocateTool(new FakeCapabilitySurface(runtime), new FakeCallContextFactory(), _originAccessor),
            JsonSerializer.Serialize(new
            {
                page_id = page.Id.Value,
                context_id = "ctx-1",
                locator = new { kind = "role", value = "button", name = "Save" },
                fields = new[] { "value" }
            }));

        Assert.Equal("Pudding", GetJson(withValue.Output, "value").GetProperty("elements")[0].GetProperty("value").GetString());

        // 未知字段名明确拒绝，并把合法清单告诉调用方。
        var unknown = await ExecuteAsync(
            new BrowserLocateTool(new FakeCapabilitySurface(runtime), new FakeCallContextFactory(), _originAccessor),
            JsonSerializer.Serialize(new
            {
                page_id = page.Id.Value,
                context_id = "ctx-1",
                locator = new { kind = "role", value = "button", name = "Save" },
                fields = new[] { "nope" }
            }));

        Assert.False(unknown.Success);
        using var document = JsonDocument.Parse(unknown.Error!);
        var error = document.RootElement.GetProperty("error");
        Assert.Equal("browser_invalid_arguments", error.GetProperty("code").GetString());
        Assert.Contains("nope", error.GetProperty("message").GetString()!, StringComparison.Ordinal);
        Assert.Contains("boundingBox", error.GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Locate_FieldNamesMatchTheOutputKeys()
    {
        // 契约里的字段名必须就是输出里的 JSON 键：调用方照着看到的键名写就得能用。
        var runtime = new FakeBrowserRuntime();
        var context = await runtime.CreateContextAsync(
            new BrowserContextOptions { Id = new BrowserContextId("ctx-1") }, CancellationToken.None);
        var page = await context.NewPageAsync(new PageCreateOptions(), CancellationToken.None);

        var result = await ExecuteAsync(
            new BrowserLocateTool(new FakeCapabilitySurface(runtime), new FakeCallContextFactory(), _originAccessor),
            JsonSerializer.Serialize(new
            {
                page_id = page.Id.Value,
                context_id = "ctx-1",
                locator = new { kind = "role", value = "button", name = "Save" },
                include_value = true,
                include_checked = true,
                fields = BrowserElementFields.All.ToArray()
            }));

        Assert.True(result.Success);
        var element = GetJson(result.Output, "value").GetProperty("elements")[0];

        foreach (var name in BrowserElementFields.All)
        {
            Assert.True(element.TryGetProperty(name, out _), $"字段名 '{name}' 与输出键不对应");
        }
    }

    [Fact]
    public async Task Snapshot_FieldsTrimTheOutputAndOnlyFetchTheRequestedRepresentation()
    {
        // 设计 §151：快照 fields —— 三种表示是 payload 绝对大头，且要求「同次默认只返回一种表示」。
        var runtime = new FakeBrowserRuntime();
        var context = await runtime.CreateContextAsync(
            new BrowserContextOptions { Id = new BrowserContextId("ctx-1") }, CancellationToken.None);
        var page = await context.NewPageAsync(new PageCreateOptions(), CancellationToken.None);
        var surface = new FakeCapabilitySurface(runtime);

        var trimmed = await ExecuteAsync(
            new BrowserSnapshotTool(surface, new FakeCallContextFactory(), _originAccessor),
            JsonSerializer.Serialize(new
            {
                page_id = page.Id.Value,
                context_id = "ctx-1",
                mode = "full",
                fields = new[] { "domText" }
            }));

        Assert.True(trimmed.Success);
        var value = GetJson(trimmed.Output, "value");

        // 只要了 DOM 文本 ⇒ 只有它（另两种表示不在输出里）。
        Assert.False(string.IsNullOrEmpty(value.GetProperty("domText").GetString()));
        AssertAbsent(value, "accessibilityTree");
        AssertAbsent(value, "html");

        // **并且只向驱动要了那一种**（三种一起抓再丢掉是纯浪费）。
        var options = surface.LastSnapshotRequest!.Options;
        Assert.True(options.IncludeDom);
        Assert.False(options.IncludeAccessibilityTree);
        Assert.False(options.IncludeHtml);

        // 核心字段恒在：模式与截断事实（判断这份结果能不能信）。
        Assert.Equal("full", value.GetProperty("mode").GetString());
        Assert.False(value.GetProperty("truncated").GetBoolean());

        // 未知字段名 ⇒ 明确拒绝并回合法清单。
        var unknown = await ExecuteAsync(
            new BrowserSnapshotTool(new FakeCapabilitySurface(runtime), new FakeCallContextFactory(), _originAccessor),
            JsonSerializer.Serialize(new
            {
                page_id = page.Id.Value,
                context_id = "ctx-1",
                fields = new[] { "nope" }
            }));

        Assert.False(unknown.Success);
        using var document = JsonDocument.Parse(unknown.Error!);
        var error = document.RootElement.GetProperty("error");
        Assert.Equal("browser_invalid_arguments", error.GetProperty("code").GetString());
        Assert.Contains("domText", error.GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Snapshot_OutlineKeepsTheTextButCanDropBudgetMetadata()
    {
        // outline 模式下可裁剪的是预算事实（nodeCount/returnedLines/bytes），文本本身仍在。
        var runtime = new FakeBrowserRuntime();
        var context = await runtime.CreateContextAsync(
            new BrowserContextOptions { Id = new BrowserContextId("ctx-1") }, CancellationToken.None);
        var page = await context.NewPageAsync(new PageCreateOptions(), CancellationToken.None);

        var result = await ExecuteAsync(
            new BrowserSnapshotTool(new FakeCapabilitySurface(runtime), new FakeCallContextFactory(), _originAccessor),
            JsonSerializer.Serialize(new
            {
                page_id = page.Id.Value,
                context_id = "ctx-1",
                mode = "outline",
                fields = new[] { "accessibilityTree" }
            }));

        Assert.True(result.Success);
        var value = GetJson(result.Output, "value");

        Assert.False(string.IsNullOrEmpty(value.GetProperty("accessibilityTree").GetString()));
        AssertAbsent(value, "returnedLines");
        AssertAbsent(value, "bytes");
        Assert.Equal(0, value.GetProperty("nodeCount").GetInt32());
    }

    [Fact]
    public async Task Snapshot_ScopeUsesTheRefDirectly_OrResolvesASingleMatch()
    {
        // 设计 §151：scope(locator/ref)。ref 直接用（不多一跳）；其它定位方式先解析成**唯一** ref。
        var runtime = new FakeBrowserRuntime();
        var context = await runtime.CreateContextAsync(
            new BrowserContextOptions { Id = new BrowserContextId("ctx-1") }, CancellationToken.None);
        var page = (FakeBrowserPage)await context.NewPageAsync(new PageCreateOptions(), CancellationToken.None);
        var surface = new FakeCapabilitySurface(runtime);

        var byRef = await ExecuteAsync(
            new BrowserSnapshotTool(surface, new FakeCallContextFactory(), _originAccessor),
            JsonSerializer.Serialize(new
            {
                page_id = page.Id.Value,
                context_id = "ctx-1",
                scope = new { kind = "ref", value = $"v{page.PageVersion}-n7" }
            }));

        Assert.True(byRef.Success);
        Assert.Equal($"v{page.PageVersion}-n7", surface.LastSnapshotRequest!.Options.ScopeRef);
        // ref 直接用 ⇒ **不产生**额外定位跳（省一跳，也就少一次被页面变化打断的机会）。
        Assert.Null(surface.LastLocateRequest);

        var byLocator = await ExecuteAsync(
            new BrowserSnapshotTool(surface, new FakeCallContextFactory(), _originAccessor),
            JsonSerializer.Serialize(new
            {
                page_id = page.Id.Value,
                context_id = "ctx-1",
                scope = new { kind = "css", value = "table" }
            }));

        Assert.True(byLocator.Success);
        Assert.NotNull(surface.LastLocateRequest);
        Assert.Equal($"v{page.PageVersion}-n1", surface.LastSnapshotRequest!.Options.ScopeRef);
    }

    [Fact]
    public async Task Snapshot_ScopeLocatorMustResolveToExactlyOneElement()
    {
        // "大概就是这个"是错位读取的常见来源：0 命中与多命中都必须明确拒绝。
        var runtime = new FakeBrowserRuntime();
        var context = await runtime.CreateContextAsync(
            new BrowserContextOptions { Id = new BrowserContextId("ctx-1") }, CancellationToken.None);
        var page = (FakeBrowserPage)await context.NewPageAsync(new PageCreateOptions(), CancellationToken.None);

        page.MatchCount = 0;
        var none = await ExecuteAsync(
            new BrowserSnapshotTool(new FakeCapabilitySurface(runtime), new FakeCallContextFactory(), _originAccessor),
            JsonSerializer.Serialize(new
            {
                page_id = page.Id.Value,
                context_id = "ctx-1",
                scope = new { kind = "css", value = "table" }
            }));

        Assert.False(none.Success);
        using (var document = JsonDocument.Parse(none.Error!))
        {
            Assert.Equal("browser_element_not_found", document.RootElement.GetProperty("error").GetProperty("code").GetString());
        }

        page.MatchCount = 3;
        var many = await ExecuteAsync(
            new BrowserSnapshotTool(new FakeCapabilitySurface(runtime), new FakeCallContextFactory(), _originAccessor),
            JsonSerializer.Serialize(new
            {
                page_id = page.Id.Value,
                context_id = "ctx-1",
                scope = new { kind = "css", value = "table" }
            }));

        Assert.False(many.Success);
        using (var document = JsonDocument.Parse(many.Error!))
        {
            Assert.Equal("browser_locator_ambiguous", document.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
    }

    /// <summary>断言某字段**不在**输出里（要么缺键，要么显式为 null）。</summary>
    private static void AssertAbsent(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var value))
        {
            Assert.Equal(JsonValueKind.Null, value.ValueKind);
        }
    }

    [Fact]
    public async Task Locate_PagesWithCursor_AndExpiredCursorSaysReRead()
    {
        // 设计 §6.2：locate 提供 cursor 分页；旧游标（版本/查询变了）必须**明确过期**，
        // 并给出"重新读取"的入口，而不是拿旧游标去套新内容。
        var runtime = new FakeBrowserRuntime();
        var context = await runtime.CreateContextAsync(
            new BrowserContextOptions { Id = new BrowserContextId("ctx-1") }, CancellationToken.None);
        var page = await context.NewPageAsync(new PageCreateOptions(), CancellationToken.None);

        var surface = new FakeCapabilitySurface(runtime)
        {
            LocateNextCursor = BrowserLocateCursor.Create(
                page.PageVersion,
                BrowserLocateCursor.FingerprintOf(new DesktopLocator(DesktopLocatorKind.Css, ".item", includeHidden: false, inViewportOnly: false)),
                10).Encode(),
        };

        var first = await ExecuteAsync(
            new BrowserLocateTool(surface, new FakeCallContextFactory(), _originAccessor),
            JsonSerializer.Serialize(new
            {
                page_id = page.Id.Value,
                context_id = "ctx-1",
                locator = new { kind = "css", value = ".item", includeHidden = false, inViewportOnly = false }
            }));

        // 宿主签发的游标原样出现在输出里（模型只回传、不解释）。
        Assert.NotNull(surface.LocateNextCursor);
        Assert.Equal(surface.LocateNextCursor, GetJson(first.Output, "value").GetProperty("nextCursor").GetString());

        // 原样回传 ⇒ 到达能力层。
        var second = await ExecuteAsync(
            new BrowserLocateTool(surface, new FakeCallContextFactory(), _originAccessor),
            JsonSerializer.Serialize(new
            {
                page_id = page.Id.Value,
                context_id = "ctx-1",
                locator = new { kind = "css", value = ".item", includeHidden = false, inViewportOnly = false },
                cursor = surface.LocateNextCursor
            }));

        Assert.True(second.Success);
        Assert.Equal(surface.LocateNextCursor, surface.LastLocateRequest!.Cursor);

        // 过期游标（换了查询条件）⇒ 独立的 cursor_expired，并提示重新读取。
        var expired = await ExecuteAsync(
            new BrowserLocateTool(surface, new FakeCallContextFactory(), _originAccessor),
            JsonSerializer.Serialize(new
            {
                page_id = page.Id.Value,
                context_id = "ctx-1",
                locator = new { kind = "css", value = ".other", includeHidden = false, inViewportOnly = false },
                cursor = surface.LocateNextCursor
            }));

        Assert.False(expired.Success);
        using var document = JsonDocument.Parse(expired.Error!);
        Assert.Equal("cursor_expired", document.RootElement.GetProperty("error").GetProperty("code").GetString());

        // 提示进 warnings（"重新读取"，而不是重试同一个请求）。
        Assert.Contains(
            "re-read",
            string.Join(' ', document.RootElement.GetProperty("warnings").EnumerateArray().Select(item => item.GetString())),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Locate_StaleRefPreservesStableFailureCode()
    {
        var runtime = new FakeBrowserRuntime();
        var context = await runtime.CreateContextAsync(
            new BrowserContextOptions { Id = new BrowserContextId("ctx-1") }, CancellationToken.None);
        var page = await context.NewPageAsync(new PageCreateOptions(), CancellationToken.None);

        var result = await ExecuteAsync(new BrowserLocateTool(new FakeCapabilitySurface(runtime), new FakeCallContextFactory(), _originAccessor),
            JsonSerializer.Serialize(new
            {
                page_id = page.Id.Value,
                context_id = "ctx-1",
                locator = new { kind = "ref", value = "v0-n1" }
            }));

        Assert.False(result.Success);
        using var document = JsonDocument.Parse(result.Error!);
        Assert.Equal("stale_element_reference",
            document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private static Task<ToolExecutionResult> ExecuteAsync(
        IPuddingTool tool,
        string arguments,
        CancellationToken ct = default)
        => ExecuteAsync(tool, arguments, permissionEvidence: null, ct);

    /// <summary>
    /// 同上，并带上本次调用的**权限证据**（权限证据链第一阶段的携带断言用）。
    /// </summary>
    private static Task<ToolExecutionResult> ExecuteAsync(
        IPuddingTool tool,
        string arguments,
        ToolPermissionEvidence? permissionEvidence,
        CancellationToken ct = default)
        => tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = Guid.NewGuid().ToString("N"),
            ArgumentsJson = arguments,
            Context = new ToolExecutionContext
            {
                WorkspaceId = "default",
                SessionId = "session-1",
                AgentInstanceId = "agent-1",
                PermissionEvidence = permissionEvidence
            }
        }, ct);

    [Fact]
    public async Task MigratedTool_CarriesPermissionEvidence_IntoTheCapabilityCallContext()
    {
        // 权限证据链第一阶段：证据从 ToolExecutionContext 经工具作用域到达能力调用上下文。
        var runtime = new FakeBrowserRuntime();
        var port = new FakeCapabilitySurface(runtime);
        var factory = new FakeCallContextFactory();
        var tool = new BrowserContextTool(port, port, factory, _originAccessor);

        await ExecuteAsync(
            tool,
            """{"action":"list"}""",
            new ToolPermissionEvidence("call-1", "allowed", Source: "workspace-guard"));

        Assert.Equal("decision=allowed;source=workspace-guard", factory.LastPermissionEvidenceSummary);
    }

    [Fact]
    public async Task MigratedTool_WithoutPermissionEvidence_PassesNothing_AndDoesNotFakeADenial()
    {
        // 未评估是合法状态：摘要为 null，**不得**伪造成 denied（否则接缝将来会误判）。
        var runtime = new FakeBrowserRuntime();
        var port = new FakeCapabilitySurface(runtime);
        var factory = new FakeCallContextFactory();
        var tool = new BrowserContextTool(port, port, factory, _originAccessor);

        await ExecuteAsync(tool, """{"action":"list"}""");

        Assert.Null(factory.LastPermissionEvidenceSummary);
    }

    private static void AssertJsonOk(string json, string expectedContextId)
    {
        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(expectedContextId, document.RootElement.GetProperty("contextId").GetString());
    }

    private static JsonElement GetJson(string json, string property)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty(property).Clone();
    }
}

/// <summary>
/// 窄端口的测试替身：把请求**委托**给同一个 FakeBrowserRuntime（而不是返回罐头值），
/// 因此迁移后的工具测试仍然在验证真实行为。只有 locate 用到的三个方法有实现。
/// </summary>
internal sealed class FakeCapabilitySurface(IBrowserRuntime runtime)
    : IDesktopBrowserCapabilitySurface, IDesktopContextCapabilitySurface
{
    /// <summary>让替身直接以某个领域错误结束交互（用于验证闸门拒绝 vs 普通失败的回执差异）。</summary>
    public DesktopCapabilityError? InteractFailure { get; init; }

    /// <summary>最近一次定位请求（用于断言默认 limit 等参数确实传到了能力层）。</summary>
    public BrowserLocateRequest? LastLocateRequest { get; private set; }

    /// <summary>让驱动替身按真实驱动的拒绝语义抛异常（与交互侧的 ActionFailure 对称）。</summary>
    public BrowserOperationException? LocateFailure { get; init; }

    /// <summary>让替身报告"还有下一页"（宿主签发的游标）。</summary>
    public string? LocateNextCursor { get; init; }

    /// <summary>最近一次快照请求（断言"只抓要的表示"确实生效）。</summary>
    public BrowserSnapshotRequest? LastSnapshotRequest { get; private set; }

    public async Task<CapabilityResult<DesktopContexts>> GetContextsAsync(
        DesktopCallContext call, CancellationToken ct = default)
    {
        var contexts = new List<DesktopContextInfo>();
        var observed = 0L;
        foreach (var summary in await runtime.ListContextsAsync(ct))
        {
            if (await runtime.GetContextAsync(summary.Id, ct) is not { } context)
            {
                continue;
            }

            var pages = new List<DesktopPageInfo>();
            foreach (var page in await context.ListPagesAsync(ct))
            {
                if (page.PageVersion <= 0)
                {
                    continue;
                }

                pages.Add(new DesktopPageInfo(
                    new DesktopPageTarget(summary.Id.Value, page.Id.Value),
                    DesktopPageVersion.Require(page.PageVersion),
                    page.Title,
                    Uri.TryCreate(page.Url, UriKind.Absolute, out var url) ? url : null));
                observed = Math.Max(observed, page.PageVersion);
            }

            contexts.Add(new DesktopContextInfo(summary.Id.Value, DesktopContextTrust.Untrusted, pages));
        }

        return CapabilityResult<DesktopContexts>.Success(new DesktopContexts(
            contexts, observed > 0 ? DesktopPageVersion.Require(observed) : default));
    }

    public async Task<CapabilityResult<DesktopPageState>> GetPageStateAsync(
        DesktopPageTarget target, DesktopCallContext call, CancellationToken ct = default)
    {
        if (await ResolvePageAsync(target, ct) is not { } page)
        {
            return CapabilityResult<DesktopPageState>.Failure(
                DesktopCapabilityError.InvalidTarget($"page '{target.Key}' is not known"));
        }

        return CapabilityResult<DesktopPageState>.Success(new DesktopPageState(
            target,
            Uri.TryCreate(page.Info.Url, UriKind.Absolute, out var url) ? url : null,
            page.PageVersion > 0 ? DesktopPageVersion.Require(page.PageVersion) : DesktopPageVersion.Unknown,
            page.IsLoading ? DesktopPageReadiness.Loading : DesktopPageReadiness.Unknown));
    }

    public async Task<CapabilityResult<DesktopLocateResult>> LocateAsync(
        BrowserLocateRequest request, DesktopCallContext call, CancellationToken ct = default)
    {
        LastLocateRequest = request;

        if (LocateFailure is not null)
        {
            throw LocateFailure;
        }

        if (await ResolvePageAsync(request.Target, ct) is not { } page)
        {
            return CapabilityResult<DesktopLocateResult>.Failure(
                DesktopCapabilityError.InvalidTarget($"page '{request.Target.Key}' is not known"));
        }

        var version = page.PageVersion > 0 ? DesktopPageVersion.Require(page.PageVersion) : DesktopPageVersion.Unknown;
        if (request.ExpectedPageVersion.IsKnown && version.IsKnown
            && request.ExpectedPageVersion.Value != version.Value)
        {
            return CapabilityResult<DesktopLocateResult>.Failure(new DesktopCapabilityError(
                DesktopCapabilityErrorCode.PageVersionMismatch,
                $"page '{request.Target.Key}' is at v{version.Value}, request pinned v{request.ExpectedPageVersion.Value}"));
        }

        var locator = new Locator
        {
            Kind = Enum.Parse<LocatorKind>(request.Locator.Kind.ToString()),
            Value = request.Locator.Value,
            Name = request.Locator.Name,
            Exact = request.Locator.Exact,
            Nth = request.Locator.Nth,
            HasText = request.Locator.HasText,
        };

        var handles = await page.QueryAllAsync(locator, ct);
        var elements = handles
            .Select(handle => new DesktopElementRef(
                handle.Id.Value,
                handle.Info.Tag,
                DesktopPageVersion.Require(handle.PageVersion),
                handle.Info.Role,
                handle.Info.Name,
                handle.Info.Text,
                handle.Info.Visible,
                handle.Info.Enabled,
                handle.Info.Checked,
                handle.Info.Value,
                handle.Info.SelectedValues,
                handle.Info.InViewport)
            {
                // 驱动会带上几何（展示层再收敛到一位小数）；替身也必须给，否则定位输出永远没有 bbox。
                BoundingBox = handle.Info.BoundingBox is { } b
                    ? new DesktopElementBox(b.X, b.Y, b.Width, b.Height)
                    : null,
            })
            .ToArray();

        // 游标语义（§6.2）由真实宿主实现；替身按同一套规则响应：带游标就返回它的下一页结论。
        if (request.Cursor is { Length: > 0 } cursor)
        {
            if (!BrowserLocateCursor.TryParse(cursor, out var parsed)
                || !parsed.Matches(version.IsKnown ? version.Value : 0, BrowserLocateCursor.FingerprintOf(request.Locator)))
            {
                return CapabilityResult<DesktopLocateResult>.Failure(new DesktopCapabilityError(
                    DesktopCapabilityErrorCode.CursorExpired,
                    "the locate cursor was issued for a different page version or locator"));
            }
        }

        return CapabilityResult<DesktopLocateResult>.Success(
            new DesktopLocateResult(request.Target, request.Locator, elements, false, version, LocateNextCursor));
    }

    public async Task<CapabilityResult<DesktopSnapshot>> SnapshotAsync(
        BrowserSnapshotRequest request, DesktopCallContext call, CancellationToken ct = default)
    {
        LastSnapshotRequest = request;

        if (await ResolvePageAsync(request.Target, ct) is not { } page)
        {
            return CapabilityResult<DesktopSnapshot>.Failure(
                DesktopCapabilityError.InvalidTarget($"page '{request.Target.Key}' is not known"));
        }

        var options = request.Options;
        var snapshot = await page.SnapshotAsync(new SnapshotOptions
        {
            IncludeDom = options.IncludeDom,
            IncludeAccessibilityTree = options.IncludeAccessibilityTree,
            IncludeHidden = options.IncludeHidden,
            IncludeIframes = options.IncludeIframes,
            IncludeShadowDom = options.IncludeShadowDom,
            IncludeHtml = options.IncludeHtml,
            MaxNodes = options.MaxNodes,
            MaxTextLength = options.MaxTextLength,
            MaxDepth = options.MaxDepth,
        }, ct);

        return CapabilityResult<DesktopSnapshot>.Success(new DesktopSnapshot(
            request.Target,
            snapshot.DomText,
            snapshot.AccessibilityTree,
            snapshot.Html,
            snapshot.Truncated,
            snapshot.NodeCount,
            page.PageVersion > 0 ? DesktopPageVersion.Require(page.PageVersion) : DesktopPageVersion.Unknown));
    }
    public async Task<CapabilityResult<DesktopWaitResult>> WaitForAsync(
        BrowserWaitForRequest request, DesktopCallContext call, CancellationToken ct = default)
    {
        if (await ResolvePageAsync(request.Target, ct) is not { } page)
        {
            return CapabilityResult<DesktopWaitResult>.Failure(
                DesktopCapabilityError.InvalidTarget($"page '{request.Target.Key}' is not known"));
        }

        var result = await page.WaitForAsync(new WaitCondition
        {
            Selector = request.Condition.Kind == DesktopWaitConditionKind.Selector ? request.Condition.Value : null,
            SelectorToHide = request.Condition.Kind == DesktopWaitConditionKind.SelectorHidden ? request.Condition.Value : null,
            UrlPattern = request.Condition.Kind == DesktopWaitConditionKind.UrlPattern ? request.Condition.Value : null,
            TimeoutMs = request.TimeoutMs,
        }, ct);

        var version = page.PageVersion > 0
            ? DesktopPageVersion.Require(page.PageVersion)
            : DesktopPageVersion.Unknown;
        return CapabilityResult<DesktopWaitResult>.Success(new DesktopWaitResult(
            request.Target,
            request.Condition,
            result.TimedOut,
            new DesktopPageState(
                request.Target,
                Uri.TryCreate(page.Info.Url, UriKind.Absolute, out var url) ? url : null,
                version,
                DesktopPageReadiness.Unknown,
                page.Info.Title),
            result.Error));
    }
    public async Task<CapabilityResult<DesktopInteractionResult>> InteractAsync(
        BrowserInteractRequest request, DesktopCallContext call, CancellationToken ct = default)
    {
        if (InteractFailure is not null)
        {
            return CapabilityResult<DesktopInteractionResult>.Failure(InteractFailure);
        }

        if (await ResolvePageAsync(request.Target, ct) is not { } page)
        {
            return CapabilityResult<DesktopInteractionResult>.Failure(
                DesktopCapabilityError.InvalidTarget($"page '{request.Target.Key}' is not known"));
        }

        var locator = request.Locator is null
            ? null
            : new Locator
            {
                Kind = Enum.Parse<LocatorKind>(request.Locator.Kind.ToString()),
                Value = request.Locator.Value,
                Name = request.Locator.Name,
                Exact = request.Locator.Exact,
                Nth = request.Locator.Nth,
                HasText = request.Locator.HasText,
            };

        DesktopScrollReport? scroll = null;

        switch (request.Action)
        {
            case DesktopInteractionAction.Click:
                await page.ClickAsync(locator!, new ClickOptions(), ct);
                break;
            case DesktopInteractionAction.Fill:
                await page.FillAsync(locator!, request.Text!, new FillOptions(), ct);
                break;
            case DesktopInteractionAction.Type:
                await page.TypeAsync(locator!, request.Text!, new TypeOptions(), ct);
                break;
            case DesktopInteractionAction.Press:
                await page.PressAsync(locator!, request.Text!, new KeyOptions(), ct);
                break;
            case DesktopInteractionAction.Hover:
                await page.HoverAsync(locator!, new PointerOptions(), ct);
                break;
            case DesktopInteractionAction.Select:
                await page.SelectAsync(locator!, request.Values!, ct);
                break;
            case DesktopInteractionAction.Check:
                await page.CheckAsync(locator!, true, ct);
                break;
            case DesktopInteractionAction.Uncheck:
                await page.CheckAsync(locator!, false, ct);
                break;
            case DesktopInteractionAction.Scroll:
                // 与真实传输同形：把驱动报告的滚动事实带进结果（§7）。
                var scrollFacts = await page.ScrollAsync(
                    new ScrollOptions
                    {
                        DeltaX = request.DeltaX,
                        DeltaY = request.DeltaY,
                        Target = (ScrollTarget)(int)request.EffectiveScrollTarget,
                        Locator = locator,
                    },
                    ct);
                scroll = new DesktopScrollReport(
                    scrollFacts.Container,
                    scrollFacts.BeforeX,
                    scrollFacts.BeforeY,
                    scrollFacts.AfterX,
                    scrollFacts.AfterY,
                    scrollFacts.ConsumedDeltaX,
                    scrollFacts.ConsumedDeltaY,
                    scrollFacts.AtBoundary,
                    scrollFacts.ContentChanged);
                break;
            default:
                return CapabilityResult<DesktopInteractionResult>.Failure(
                    DesktopCapabilityError.UnsupportedCapability($"action '{request.Action}' has no fake equivalent"));
        }

        var version = page.PageVersion > 0
            ? DesktopPageVersion.Require(page.PageVersion)
            : DesktopPageVersion.Unknown;

        // 真实驱动在动作之后会**在同一节点上回读**表单值（见 WebView2DomClient 的交互脚本），
        // 因此替身也必须回读：否则「fill 是否写入成功」在工具层无从判定（门禁 G7）。
        var element = version.IsKnown
            ? new DesktopElementRef(
                "e1",
                "input",
                version,
                role: "textbox",
                name: request.Locator?.Name ?? "Name",
                // 回读值：替身用 LastValue 代表"页面里现在的值"（非字符串形态=没读到值）。
                value: page is FakeBrowserPage fake ? fake.LastValue as string : null)
            : null;

        return CapabilityResult<DesktopInteractionResult>.Success(new DesktopInteractionResult(
            request.Target,            new DesktopPageState(
                request.Target,
                Uri.TryCreate(page.Info.Url, UriKind.Absolute, out var url) ? url : null,
                version,
                DesktopPageReadiness.Unknown,
                page.Info.Title),
            element,
            scroll));
    }
    public async Task<CapabilityResult<DesktopContextInfo>> CreateContextAsync(
        BrowserContextCreateRequest request, DesktopCallContext call, CancellationToken ct = default)
    {
        var created = await runtime.CreateContextAsync(
            new BrowserContextOptions
            {
                Id = request.ContextId is { } id ? new BrowserContextId(id) : null,
                Persistent = request.Persistent,
            }, ct);
        return CapabilityResult<DesktopContextInfo>.Success(
            new DesktopContextInfo(created.Info.Id.Value, DesktopContextTrust.Untrusted, [])
            {
                Persistent = request.Persistent,
            });
    }

    public async Task<CapabilityResult<DesktopContextClosed>> CloseContextAsync(
        BrowserContextCloseRequest request, DesktopCallContext call, CancellationToken ct = default)
    {
        var id = new BrowserContextId(request.ContextId);
        if (await runtime.GetContextAsync(id, ct) is null)
        {
            return CapabilityResult<DesktopContextClosed>.Failure(
                DesktopCapabilityError.InvalidTarget($"context '{request.ContextId}' is not known"));
        }

        await runtime.CloseContextAsync(id, ct);
        return CapabilityResult<DesktopContextClosed>.Success(new DesktopContextClosed(request.ContextId));
    }

    public async Task<CapabilityResult<DesktopTabsResult>> TabsAsync(
        BrowserTabsRequest request, DesktopCallContext call, CancellationToken ct = default)
    {
        if (request.Action == DesktopTabAction.New)
        {
            if (await runtime.GetContextAsync(new BrowserContextId(request.ContextId), ct) is not { } newContext)
            {
                return CapabilityResult<DesktopTabsResult>.Failure(
                    DesktopCapabilityError.InvalidTarget($"context '{request.ContextId}' is not known"));
            }

            // 说明：替身按「建页 → 可选导航 → 可选置前」执行（生产实现把初始地址/激活一次性交给运行时）。
            var created = await newContext.NewPageAsync(new PageCreateOptions(), ct);
            if (request.Url is { } initialUrl)
            {
                await created.GotoAsync(initialUrl, new NavigationOptions(), ct);
            }

            if (request.Activate)
            {
                await created.BringToFrontAsync(ct);
            }

            var createdTarget = new DesktopPageTarget(request.ContextId, created.Id.Value);
            return CapabilityResult<DesktopTabsResult>.Success(new DesktopTabsResult(
                createdTarget, request.Action, State(createdTarget, created), tabClosed: false, await ContextsAsync(ct)));
        }

        var tabsTarget = request.Target!;
        if (await runtime.GetContextAsync(new BrowserContextId(tabsTarget.ContextId), ct) is not { } context
            || await context.GetPageAsync(new PageId(tabsTarget.PageId), ct) is not { } page)
        {
            return CapabilityResult<DesktopTabsResult>.Failure(
                DesktopCapabilityError.InvalidTarget($"page '{tabsTarget.Key}' is not known"));
        }

        if (request.Action == DesktopTabAction.Close)
        {
            await context.ClosePageAsync(new PageId(tabsTarget.PageId), ct);
        }
        else
        {
            await page.BringToFrontAsync(ct);
        }

        return CapabilityResult<DesktopTabsResult>.Success(new DesktopTabsResult(
            tabsTarget,
            request.Action,
            State(tabsTarget, page),
            tabClosed: request.Action == DesktopTabAction.Close,
            await ContextsAsync(ct)));
    }

    private static DesktopPageState State(DesktopPageTarget target, IBrowserPage page) => new(
        target,
        Uri.TryCreate(page.Info.Url, UriKind.Absolute, out var url) ? url : null,
        page.PageVersion > 0 ? DesktopPageVersion.Require(page.PageVersion) : DesktopPageVersion.Unknown,
        DesktopPageReadiness.Unknown,
        page.Info.Title);

    private async Task<DesktopContexts> ContextsAsync(CancellationToken ct)
    {
        var contexts = new List<DesktopContextInfo>();
        foreach (var summary in await runtime.ListContextsAsync(ct))
        {
            if (await runtime.GetContextAsync(summary.Id, ct) is not { } context)
            {
                continue;
            }

            var pages = (await context.ListPagesAsync(ct))
                .Where(p => p.PageVersion > 0)
                .Select(p => new DesktopPageInfo(
                    new DesktopPageTarget(summary.Id.Value, p.Id.Value),
                    DesktopPageVersion.Require(p.PageVersion),
                    p.Title,
                    Uri.TryCreate(p.Url, UriKind.Absolute, out var u) ? u : null))
                .ToArray();
            contexts.Add(new DesktopContextInfo(summary.Id.Value, DesktopContextTrust.Untrusted, pages));
        }

        return new DesktopContexts(contexts);
    }

    public async Task<CapabilityResult<NavigateResult>> NavigateAsync(
        NavigateRequest request, DesktopCallContext call, CancellationToken ct = default)
    {
        if (await ResolvePageAsync(request.Target, ct) is not { } page)
        {
            return CapabilityResult<NavigateResult>.Failure(
                DesktopCapabilityError.InvalidTarget($"page '{request.Target.Key}' is not known"));
        }

        NavigationResult? navigation = null;
        switch (request.Action)
        {
            case DesktopNavigationAction.Goto:
                navigation = await page.GotoAsync(
                    request.Url!, new NavigationOptions { TimeoutMs = request.TimeoutMs }, ct);
                break;
            case DesktopNavigationAction.Back:
                await page.GoBackAsync(ct);
                break;
            case DesktopNavigationAction.Forward:
                await page.GoForwardAsync(ct);
                break;
            case DesktopNavigationAction.Reload:
                await page.ReloadAsync(ct);
                break;
            default:
                await page.StopAsync(ct);
                break;
        }

        var version = page.PageVersion > 0
            ? DesktopPageVersion.Require(page.PageVersion)
            : DesktopPageVersion.Unknown;
        return CapabilityResult<NavigateResult>.Success(new NavigateResult(
            NavigateDisposition.Completed,
            navigation?.Url ?? (Uri.TryCreate(page.Info.Url, UriKind.Absolute, out var current) ? current : null),
            version,
            navigation?.Ok,
            navigation?.StatusCode,
            navigation?.ErrorText,
            page.Info.Title));
    }



    public Task<CapabilityResult<JavascriptResult>> ExecuteJavascriptAsync(
        JavascriptRequest request, DesktopCallContext call, CancellationToken ct = default) =>
        throw new NotSupportedException();

    private async Task<IBrowserPage?> ResolvePageAsync(DesktopPageTarget target, CancellationToken ct)
    {
        if (await runtime.GetContextAsync(new BrowserContextId(target.ContextId), ct) is not { } context)
        {
            return null;
        }

        return await context.GetPageAsync(new PageId(target.PageId), ct);
    }
}

internal sealed class FakeCallContextFactory : IDesktopCapabilityCallContextFactory
{
    /// <summary>最近一次收到的权限证据摘要（用于断言工具确实把证据带到能力调用上下文）。</summary>
    public string? LastPermissionEvidenceSummary { get; private set; }

    public DesktopCallContext? TryCreate(string? permissionEvidenceSummary, TimeSpan? timeout = null)
    {
        LastPermissionEvidenceSummary = permissionEvidenceSummary;
        return TryCreate(timeout);
    }

    public DesktopCallContext? TryCreate(TimeSpan? timeout = null) => new(
        new DesktopInstanceId("desk-test"),
        OperationId.NewId(),
        DateTimeOffset.UtcNow.AddMinutes(1));
}

internal sealed class FakeBrowserRuntime : IBrowserRuntime
{
    private readonly Dictionary<BrowserContextId, FakeBrowserContext> _contexts = [];
    public BrowserOperationException? Failure { get; init; }
    public BrowserRuntimeState State { get; private set; } = BrowserRuntimeState.Created;

    public Task<IBrowserContext> CreateContextAsync(BrowserContextOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ThrowIfScripted();
        var id = options.Id ?? new BrowserContextId(Guid.NewGuid().ToString("N"));
        var context = new FakeBrowserContext(id);
        _contexts[id] = context;
        State = BrowserRuntimeState.Ready;
        return Task.FromResult<IBrowserContext>(context);
    }

    public Task<IBrowserContext?> GetContextAsync(BrowserContextId id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ThrowIfScripted();
        _contexts.TryGetValue(id, out var context);
        return Task.FromResult<IBrowserContext?>(context);
    }

    public Task<IReadOnlyList<BrowserContextInfo>> ListContextsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ThrowIfScripted();
        return Task.FromResult<IReadOnlyList<BrowserContextInfo>>(
            _contexts.Values.Select(context => context.Info).ToArray());
    }

    public Task CloseContextAsync(BrowserContextId id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ThrowIfScripted();
        _contexts.Remove(id);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<BrowserEvent> WatchEventsAsync(
        BrowserEventFilter filter,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        ct.ThrowIfCancellationRequested();
        yield break;
    }

    private void ThrowIfScripted()
    {
        if (Failure is not null)
            throw Failure;
    }

    public ValueTask DisposeAsync()
    {
        State = BrowserRuntimeState.Disposed;
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeBrowserContext : IBrowserContext
{
    private readonly Dictionary<PageId, FakeBrowserPage> _pages = [];
    public BrowserContextId Id { get; }
    public BrowserContextInfo Info { get; private set; }
    public PageId? LastActivatedPageId { get; set; }

    public FakeBrowserContext(BrowserContextId id)
    {
        Id = id;
        Info = new BrowserContextInfo
        {
            Id = id,
            UserDataDirectory = "C:/fake",
            Persistent = true,
            PageCount = 0
        };
    }

    public Task<IBrowserPage> NewPageAsync(PageCreateOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var page = new FakeBrowserPage(
            new PageId(Guid.NewGuid().ToString("N")), Id, this);
        _pages[page.Id] = page;
        Info = Info with { PageCount = _pages.Count };
        return Task.FromResult<IBrowserPage>(page);
    }

    public Task<IBrowserPage?> GetPageAsync(PageId id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _pages.TryGetValue(id, out var page);
        return Task.FromResult<IBrowserPage?>(page);
    }

    public Task<IReadOnlyList<PageInfo>> ListPagesAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<PageInfo>>(_pages.Values.Select(page => page.Info).ToArray());
    }

    public Task ClosePageAsync(PageId id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _pages.Remove(id);
        Info = Info with { PageCount = _pages.Count };
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<BrowserCookie>> GetCookiesAsync(IReadOnlyList<Uri>? urls, CancellationToken ct) => Unsupported<IReadOnlyList<BrowserCookie>>();
    public Task SetCookiesAsync(IReadOnlyList<BrowserCookie> cookies, CancellationToken ct) => Unsupported();
    public Task ClearCookiesAsync(CancellationToken ct) => Unsupported();
    public Task GrantPermissionsAsync(Uri origin, IReadOnlyList<BrowserPermission> permissions, CancellationToken ct) => Unsupported();
    public Task ResetPermissionsAsync(CancellationToken ct) => Unsupported();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    private static Task Unsupported() => Task.FromException(new NotSupportedException());
    private static Task<T> Unsupported<T>() => Task.FromException<T>(new NotSupportedException());
}

internal sealed class FakeBrowserPage : IBrowserPage
{
    private readonly FakeBrowserContext _context;
    public PageId Id { get; }
    public BrowserContextId ContextId { get; }
    public long PageVersion => Info.PageVersion;
    public PageInfo Info { get; private set; }
    public bool CanGoBack { get; private set; }
    public bool CanGoForward { get; private set; }
    public bool IsLoading { get; private set; }
    public string? LastAction { get; private set; }
    public object? LastValue { get; private set; }

    /// <summary>最近一次滚动选项（用于断言滚动目标模型确实传到了驱动）。</summary>
    public ScrollOptions? LastScroll { get; private set; }

    /// <summary>让滚动替身返回指定的事实（例如"到边界但没加载出新内容"）。</summary>
    public ScrollOutcome? ScrollResult { get; set; }

    /// <summary>让驱动替身按真实驱动的拒绝语义抛异常（歧义/零命中/不可见/禁用）。</summary>
    public BrowserOperationException? ActionFailure { get; set; }

    public FakeBrowserPage(PageId id, BrowserContextId contextId, FakeBrowserContext context)
    {
        Id = id;
        ContextId = contextId;
        _context = context;
        Info = new PageInfo
        {
            Id = id,
            ContextId = contextId,
            Title = "New Tab",
            Url = "about:blank",
            PageVersion = 1
        };
    }

    public Task<NavigationResult> GotoAsync(Uri url, NavigationOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Info = Info with { Url = url.ToString(), PageVersion = PageVersion + 1 };
        CanGoBack = true;
        return Task.FromResult(new NavigationResult { Url = url, Ok = true, StatusCode = 200 });
    }

    public Task GoBackAsync(CancellationToken ct) { CanGoBack = false; CanGoForward = true; return Task.CompletedTask; }
    public Task GoForwardAsync(CancellationToken ct) { CanGoBack = true; CanGoForward = false; return Task.CompletedTask; }
    public Task ReloadAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StopAsync(CancellationToken ct) { IsLoading = false; return Task.CompletedTask; }
    public Task BringToFrontAsync(CancellationToken ct) { _context.LastActivatedPageId = Id; return Task.CompletedTask; }
    /// <summary>最近一次快照请求的选项：用来断言模式确实改变了**发给驱动**的采集预算。</summary>
    public SnapshotOptions? LastSnapshotOptions { get; private set; }

    /// <summary>让替身模拟「扫描侧被截断」，用于验证 incomplete_scan 与 output_budget 的区分。</summary>
    public bool SnapshotTruncated { get; set; }

    public Task<PageSnapshot> SnapshotAsync(SnapshotOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        LastSnapshotOptions = options;
        return Task.FromResult(new PageSnapshot
        {
            DomText = "<div><button ref=v1-n1> Save</button><input ref=v1-n2></div>",
            // 结构行没有 ref（只有 outline 保留它），可交互行带 ref（驱动自己的判定）。
            AccessibilityTree =
                "document name=\"Example\"\nheading name=\"Form\"\n"
                + "button ref=v1-n1 name=\"Save\"\ntextbox ref=v1-n2 name=\"Search\"",
            NodeCount = 4,
            Truncated = SnapshotTruncated
        });
    }
    public async Task<IElementHandle?> QueryAsync(Locator locator, CancellationToken ct)
        => (await QueryAllAsync(locator, ct)).FirstOrDefault();
    public Task<IReadOnlyList<IElementHandle>> QueryAllAsync(Locator locator, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (locator.Kind == LocatorKind.Ref && !locator.Value.StartsWith($"v{PageVersion}-", StringComparison.Ordinal))
            throw new BrowserOperationException("stale_element_reference", "stale ref");

        // 命中数可控：作用域解析要能验证"0 命中/多命中都明确拒绝，不猜大概就是这个"。
        return Task.FromResult<IReadOnlyList<IElementHandle>>(
            Enumerable.Range(1, MatchCount).Select(index => (IElementHandle)new FakeElementHandle(Id, PageVersion, new BrowserElementInfo
            {
                Ref = $"v{PageVersion}-n{index}",
                Tag = "button",
                Role = "button",
                Name = "Save",
                Text = "Save",
                Visible = true,
                Enabled = true,
                // 视口内与可见是两个独立事实（设计 §6.2）：替身报告"在视口内"。
                InViewport = true,
                // 表单回读（设计方案 §6.2）：text 不含表单值，只有它能让调用方确认写入成功。
                Value = "Pudding",
                BoundingBox = new BoundingBox { X = 10.25, Y = 20.5, Width = 80.04, Height = 30.75 }
            })).ToArray());
    }

    /// <summary>该页面每次定位返回的命中数（默认 1）。</summary>
    public int MatchCount { get; set; } = 1;
    public Task<BrowserScriptValue> EvaluateAsync(BrowserScript script, CancellationToken ct) => Unsupported<BrowserScriptValue>();
    public Task<IJsHandle> EvaluateHandleAsync(BrowserScript script, CancellationToken ct) => Unsupported<IJsHandle>();
    public Task<JsonDocument> SendCdpAsync(string method, JsonElement? parameters, CancellationToken ct) => Unsupported<JsonDocument>();
    public Task<BrowserSubscriptionId> SubscribeCdpAsync(string eventName, CancellationToken ct) => Unsupported<BrowserSubscriptionId>();
    public Task UnsubscribeAsync(BrowserSubscriptionId subscriptionId, CancellationToken ct) => Unsupported();
    public Task ClickAsync(Locator locator, ClickOptions options, CancellationToken ct) => Record("click", null, ct, locator);
    public Task FillAsync(Locator locator, string value, FillOptions options, CancellationToken ct) => Record("fill", value, ct, locator);
    public Task TypeAsync(Locator locator, string text, TypeOptions options, CancellationToken ct) => Record("type", text, ct, locator);
    public Task PressAsync(Locator locator, string key, KeyOptions options, CancellationToken ct) => Record("press", key, ct, locator);
    public Task HoverAsync(Locator locator, PointerOptions options, CancellationToken ct) => Record("hover", null, ct, locator);
    public Task<ScrollOutcome> ScrollAsync(ScrollOptions options, CancellationToken ct)
    {
        Record("scroll", null, ct);
        LastScroll = options;
        return Task.FromResult(ScrollResult ?? new ScrollOutcome
        {
            Container = options.Target == ScrollTarget.Page ? "document" : "element",
            ConsumedDeltaX = options.DeltaX ?? 0,
            ConsumedDeltaY = options.DeltaY ?? 0,
            AfterY = options.DeltaY ?? 0,
        });
    }
    public Task DragAsync(Locator source, Locator target, DragOptions options, CancellationToken ct) => Unsupported();
    public Task SelectAsync(Locator locator, IReadOnlyList<string> values, CancellationToken ct) => Record("select", values, ct, locator);
    public Task CheckAsync(Locator locator, bool isChecked, CancellationToken ct) => Record("check", isChecked, ct, locator);
    public Task SetInputFilesAsync(Locator locator, IReadOnlyList<string> paths, CancellationToken ct) => Unsupported();
    public Task<WaitResult> WaitForAsync(WaitCondition condition, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new WaitResult { TimedOut = false });
    }
    public Task<ScreenshotResult> ScreenshotAsync(ScreenshotOptions options, CancellationToken ct) => Unsupported<ScreenshotResult>();
    public Task<PdfResult> PrintToPdfAsync(PdfOptions options, CancellationToken ct) => Unsupported<PdfResult>();
    public Task OpenDevToolsAsync(CancellationToken ct) => Unsupported();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    /// <summary>
    /// 记录一次动作。与真实驱动一致：**任何元素解析都要先校验引用**，
    /// 因此这里在记录之前也校验 ref（否则"过期引用"在替身上永远不会失败，
    /// 而真实路径会在触碰页面前就抛 stale_element_reference）。
    /// </summary>
    private Task Record(string action, object? value, CancellationToken ct, Locator? locator = null)
    {
        ct.ThrowIfCancellationRequested();

        // 与真实驱动一致：任何元素解析都要先校验引用，过期引用在触碰页面前就失败。
        if (locator is { Kind: LocatorKind.Ref }
            && !locator.Value.StartsWith($"v{PageVersion}-", StringComparison.Ordinal))
        {
            throw new BrowserOperationException("stale_element_reference", "stale ref");
        }

        if (ActionFailure is not null)
        {
            throw ActionFailure;
        }

        LastAction = action;
        LastValue = value;
        return Task.CompletedTask;
    }
    private static Task Unsupported() => Task.FromException(new NotSupportedException());
    private static Task<T> Unsupported<T>() => Task.FromException<T>(new NotSupportedException());
}

internal sealed class FakeElementHandle : IElementHandle
{
    public FakeElementHandle(PageId pageId, long pageVersion, BrowserElementInfo info)
    {
        PageId = pageId;
        PageVersion = pageVersion;
        Info = info;
        Id = new ElementHandleId(info.Ref);
    }
    public ElementHandleId Id { get; }
    public PageId PageId { get; }
    public long PageVersion { get; }
    public int? BackendNodeId => null;
    public string LocatorFingerprint => "fake";
    public BrowserElementInfo Info { get; }
    public Task<BoundingBox?> GetBoundingBoxAsync(CancellationToken ct) => Task.FromResult(Info.BoundingBox);
    public Task<BrowserScriptValue> EvaluateAsync(BrowserScript script, CancellationToken ct) => throw new NotSupportedException();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
