using PuddingMcp;
using PuddingMcp.Contracts;

namespace PuddingMcpTests;

[TestClass]
public sealed class McpConnectionTests
{
    private static McpConnectionCoordinator Coordinator(FakeMcpTransport transport, FakeCursorStore? cursors = null,
        TimeSpan? startTimeout = null) => new(transport, cursors ?? new FakeCursorStore(),
        new McpFixtures.FixedClock(McpFixtures.Now), startTimeout ?? TimeSpan.FromSeconds(5));

    [TestMethod]
    public async Task Connect_StartTimeout_FailsWithoutReportingReady()
    {
        var transport = new FakeMcpTransport { StartDelay = TimeSpan.FromSeconds(10) };
        var status = await Coordinator(transport, startTimeout: TimeSpan.FromMilliseconds(50))
            .ConnectAsync(McpFixtures.Server());
        Assert.AreEqual(McpConnectionState.Failed, status.State);
        Assert.AreEqual(McpErrorCodes.StartTimeout, status.FailureCode);
        Assert.AreEqual(1, transport.DisposeCalls);
        Assert.AreNotEqual(McpConnectionState.Ready, status.State);
    }

    [TestMethod]
    public async Task Connect_SameGenerationIsIdempotent_AndFailureIsTyped()
    {
        var transport = new FakeMcpTransport();
        var coordinator = Coordinator(transport);
        Assert.AreEqual(McpConnectionState.Ready, (await coordinator.ConnectAsync(McpFixtures.Server())).State);
        Assert.AreEqual(McpConnectionState.Ready, (await coordinator.ConnectAsync(McpFixtures.Server())).State);
        Assert.AreEqual(1, transport.StartCalls);

        var failing = new FakeMcpTransport { StartFailure = new InvalidOperationException("spawn failed") };
        var failed = await Coordinator(failing).ConnectAsync(McpFixtures.Server());
        Assert.AreEqual(McpConnectionState.Failed, failed.State);
        Assert.AreEqual(McpErrorCodes.StartFailed, failed.FailureCode);
    }

    [TestMethod]
    public async Task Request_RequiresReadyStateAndMatchingGeneration()
    {
        var transport = new FakeMcpTransport();
        var coordinator = Coordinator(transport);
        var request = McpFixtures.Request();

        Assert.AreEqual(McpErrorCodes.NotReady,
            (await Assert.ThrowsExactlyAsync<McpException>(async () =>
                await coordinator.RequestAsync(McpFixtures.Server(), request))).Code);

        await coordinator.ConnectAsync(McpFixtures.Server());
        Assert.AreEqual(McpErrorCodes.StaleGeneration,
            (await Assert.ThrowsExactlyAsync<McpException>(async () =>
                await coordinator.RequestAsync(McpFixtures.Server(generation: 2), request))).Code);
        Assert.AreEqual(0, transport.SendCalls);
    }

    [TestMethod]
    public async Task Request_ExpiredDeadlineIsUnknownWithoutCallingTransport()
    {
        var transport = new FakeMcpTransport();
        var coordinator = Coordinator(transport);
        await coordinator.ConnectAsync(McpFixtures.Server());
        var expired = new McpRequest("req-old", "tools/list", "{}", McpFixtures.Now.AddSeconds(-1));

        var response = await coordinator.RequestAsync(McpFixtures.Server(), expired);
        Assert.IsTrue(response.Unknown);
        Assert.IsFalse(response.Success, "An expired request is never a success.");
        Assert.AreEqual(McpErrorCodes.RequestTimeout, response.ErrorCode);
        Assert.AreEqual(0, transport.SendCalls, "An expired request must not reach the transport.");
    }

    [TestMethod]
    public async Task Request_TransportFailureIsUnknownAndNotRetried()
    {
        var transport = new FakeMcpTransport { SendFailure = new IOException("pipe broken") };
        var coordinator = Coordinator(transport);
        await coordinator.ConnectAsync(McpFixtures.Server());

        var response = await coordinator.RequestAsync(McpFixtures.Server(), McpFixtures.Request());
        Assert.IsTrue(response.Unknown);
        Assert.IsFalse(response.Success);
        Assert.AreEqual(McpErrorCodes.RequestUnknown, response.ErrorCode);
        Assert.AreEqual(1, transport.SendCalls, "An uncertain request must not be retried.");
    }

    [TestMethod]
    public async Task Request_RealTimeoutIsUnknownAndNotFiledAsSuccess()
    {
        var transport = new FakeMcpTransport
        {
            OnSend = async request =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10), CancellationToken.None);
                return new McpResponse(request.RequestId, true, "{}", null, false);
            },
        };
        var coordinator = Coordinator(transport);
        await coordinator.ConnectAsync(McpFixtures.Server());
        var shortDeadline = new McpRequest("req-slow", "tools/call", "{}", McpFixtures.Now.AddMilliseconds(80));

        var response = await coordinator.RequestAsync(McpFixtures.Server(), shortDeadline);
        Assert.IsTrue(response.Unknown);
        Assert.AreEqual(McpErrorCodes.RequestTimeout, response.ErrorCode);
        Assert.AreEqual(1, transport.SendCalls);
    }

    [TestMethod]
    public async Task Drain_DisposesTransport_AndRejectsRequests()
    {
        var transport = new FakeMcpTransport();
        var coordinator = Coordinator(transport);
        await coordinator.ConnectAsync(McpFixtures.Server());
        var status = await coordinator.DrainAsync(McpFixtures.Server());
        Assert.AreEqual(McpConnectionState.Stopped, status.State);
        Assert.AreEqual(1, transport.DisposeCalls);

        Assert.AreEqual(McpErrorCodes.NotReady,
            (await Assert.ThrowsExactlyAsync<McpException>(async () =>
                await coordinator.RequestAsync(McpFixtures.Server(), McpFixtures.Request()))).Code);
    }

    [TestMethod]
    public async Task Events_OrderedDedupedGapReportedAndCursorPersisted()
    {
        var transport = new FakeMcpTransport
        {
            Events =
            [
                new(2, "notifications/one", "{}"),
                new(1, "notifications/zero", "{}"),
                new(2, "notifications/duplicate", "{}"),
                new(4, "notifications/three", "{}"),
            ],
        };
        var cursors = new FakeCursorStore();
        var coordinator = Coordinator(transport, cursors);
        await coordinator.ConnectAsync(McpFixtures.Server());

        var batch = await coordinator.AcceptEventsAsync(McpFixtures.Server());
        Assert.AreEqual(3, batch.Accepted.Length);
        CollectionAssert.AreEqual(new long[] { 1, 2, 4 }, batch.Accepted.Select(e => e.Sequence).ToArray());
        Assert.AreEqual(4L, batch.Cursor);
        Assert.IsTrue(batch.GapDetected);
        Assert.AreEqual(4L, cursors.Cursors["mcp-1"]);
    }

    [TestMethod]
    public async Task Reconnect_PreservesCursorAndDropsAlreadySeenEvents()
    {
        var cursors = new FakeCursorStore { Cursors = { ["mcp-1"] = 5 } };
        var transport = new FakeMcpTransport
        {
            Events = [new(5, "seen", "{}"), new(6, "new", "{}")],
        };
        var coordinator = Coordinator(transport, cursors);
        await coordinator.ConnectAsync(McpFixtures.Server());

        var batch = await coordinator.AcceptEventsAsync(McpFixtures.Server());
        CollectionAssert.AreEqual(new long[] { 6 }, batch.Accepted.Select(e => e.Sequence).ToArray());
        Assert.IsFalse(batch.GapDetected);
    }

    [TestMethod]
    public async Task InvalidRefsAreTyped_AndCallerCancellationPropagates()
    {
        var transport = new FakeMcpTransport();
        var coordinator = Coordinator(transport);
        Assert.AreEqual(McpErrorCodes.ServerInvalid,
            (await Assert.ThrowsExactlyAsync<McpException>(async () =>
                await coordinator.ConnectAsync(new McpServerRef("", "ws-1", 1, McpTransportKind.Stdio)))).Code);
        Assert.AreEqual(McpErrorCodes.RequestInvalid,
            (await Assert.ThrowsExactlyAsync<McpException>(async () =>
                await coordinator.RequestAsync(McpFixtures.Server(), new McpRequest("", "m", "{}", McpFixtures.Now.AddMinutes(1))))).Code);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await coordinator.ConnectAsync(McpFixtures.Server(), cts.Token));
        Assert.AreEqual(McpConnectionState.New, coordinator.Status.State);
    }
}
