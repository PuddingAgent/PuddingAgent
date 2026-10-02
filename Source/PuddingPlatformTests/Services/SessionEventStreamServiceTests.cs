using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Platform;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

/// <summary>
/// B2（2026-10-02 高磁盘读取诊断）：SSE 追赶阶段的边界读取规模。
/// <para>
/// 修复前 <c>FollowAsync</c> 的 replay 循环每读满 256 条就再查一次 <c>GetBoundsAsync</c> 并更新 head，
/// live 通知分支也在批内刷新 head。长会话因此把「一次重连」放大成「反复重扫整段会话索引」。
/// 现在每个阶段只读一次边界并冻结：replay 冻结 <c>replayThrough</c>，live 冻结 <c>drainThrough</c>，
/// 之后追加的事件留给下一次通知/轮询追赶。
/// </para>
/// <para>
/// 本文件的替身同时模拟两种真实语义：① 事件存储的边界与正向分页；② CommittedEventSignal 的<b>单读者</b>
/// 广播（一次 Signal 只唤醒一个等待者），因此可以固定「旧 waiter 不得继续消费新通知」与
/// 「丢了通知也必须由持久轮询补上」两条合同。
/// </para>
/// </summary>
[TestClass]
public sealed class SessionEventStreamServiceTests
{
    private const string SessionId = "session-stream";

    [TestMethod]
    public async Task Replay_ReadsTheHeadOnce_AndMarksEveryHistoricalFrameAsReplay()
    {
        var store = new TestEventStore();
        store.AddEvents(1, 1_024);
        var signal = new TestSignal();
        var service = NewService(store, signal);

        using var cts = new CancellationTokenSource();
        var frames = new List<SessionEventEnvelope>();

        await foreach (var frame in service.FollowAsync(SessionId, afterExclusive: 0, cts.Token))
        {
            frames.Add(frame);
            if (frames.Count >= 1_024)
                cts.Cancel();
        }

        Assert.AreEqual(1_024, frames.Count, "全部历史都必须被回放");
        CollectionAssert.AreEqual(
            Longs(1, 1_024),
            frames.Select(frame => frame.Sequence).ToArray(),
            "回放必须按序号、无重复、无缺口");
        Assert.IsTrue(frames.All(frame => frame.IsReplay), "连接建立时的历史帧必须带 replay 标记");

        // 1024 条历史需要四个 256 条批次；边界只读了一次，每一批的上界都是冻结值。
        Assert.AreEqual(4, store.ForwardReads.Count, $"应为四个批次，实际 {store.ForwardReads.Count}");
        Assert.AreEqual(1, store.BoundsReadCount, "replay 阶段只允许读一次边界（修复前是每批一次）");
        Assert.IsTrue(
            store.ForwardReads.All(read => read.ThroughInclusive == 1_024),
            "每一批都必须以冻结的上界读取：" + string.Join(", ", store.ForwardReads));
    }

    [TestMethod]
    public async Task Events_Appended_During_Replay_DoNot_Extend_It_And_ArriveInTheLivePhase()
    {
        var store = new TestEventStore();
        store.AddEvents(1, 300);
        var signal = new TestSignal();
        var service = NewService(store, signal);

        using var cts = new CancellationTokenSource();
        var frames = new List<SessionEventEnvelope>();

        await foreach (var frame in service.FollowAsync(SessionId, afterExclusive: 0, cts.Token))
        {
            frames.Add(frame);

            if (frames.Count == 100)
            {
                // 回放进行中追加：不得延长本次 replay（否则长会话永远追不上自己）。
                store.AddEvents(301, 550);
                signal.Raise(550);
            }

            if (frames.Count >= 550)
                cts.Cancel();
        }

        Assert.AreEqual(550, frames.Count);
        CollectionAssert.AreEqual(
            Longs(1, 550),
            frames.Select(frame => frame.Sequence).ToArray(),
            "顺序正确且无重复");
        Assert.IsTrue(
            frames.Take(300).All(frame => frame.IsReplay),
            "冻结上界内的事件是历史帧");
        Assert.IsTrue(
            frames.Skip(300).All(frame => !frame.IsReplay),
            "回放开始后追加的事件是实时帧");

        // 一次初读 + live 阶段的一次追赶读取；绝不会每 256 条重查一次。
        Assert.IsTrue(
            store.BoundsReadCount <= 3,
            $"边界读取次数必须有界，实际 {store.BoundsReadCount}");
    }

    [TestMethod]
    public async Task Dropped_Notification_Is_Recovered_By_The_Durable_Poll()
    {
        var store = new TestEventStore();
        var signal = new TestSignal();
        var service = NewService(store, signal, pollInterval: TimeSpan.FromMilliseconds(50));

        using var cts = new CancellationTokenSource();
        var frames = new ConcurrentQueue<SessionEventEnvelope>();

        var consumer = ConsumeAsync(service, cts.Token, frames, stopAfter: 1);

        await WaitUntilAsync(() => signal.PendingWaiterCount == 1);

        // 事件落库但通知「丢了」：没有任何 Signal。
        store.AddEvents(1, 1);

        await consumer.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(1, frames.Count, "轮询必须补上丢失的通知");
        Assert.AreEqual(1L, frames.Single().Sequence);
        Assert.IsFalse(frames.Single().IsReplay, "这个事件发生在连接建立之后");
    }

    [TestMethod]
    public async Task A_Replaced_Waiter_Stops_Consuming_Notifications()
    {
        var store = new TestEventStore();
        var signal = new TestSignal();
        var service = NewService(store, signal, pollInterval: TimeSpan.FromMilliseconds(50));

        using var cts = new CancellationTokenSource();
        var frames = new ConcurrentQueue<SessionEventEnvelope>();
        var consumer = ConsumeAsync(service, cts.Token, frames, stopAfter: 2);

        await WaitUntilAsync(() => signal.PendingWaiterCount == 1);

        // 第一条事件通过轮询追赶（通知被丢），追赶后必须换一个新 waiter，且旧 waiter 已被取消：
        // 共享 Channel 一次只交给一个读者，旧 waiter 若继续在等，下一次通知就会被它吞掉。
        store.AddEvents(1, 1);
        await WaitUntilAsync(() => signal.PendingWaiterCount == 1 && signal.CancelledWaiterCount >= 1);

        Assert.AreEqual(
            1,
            signal.PendingWaiterCount,
            "任何时刻只允许一个等待者，避免旧 waiter 抢走通知");

        store.AddEvents(2, 2);
        signal.Raise(2);

        await consumer.WaitAsync(TimeSpan.FromSeconds(10));

        CollectionAssert.AreEqual(
            Longs(1, 2),
            frames.Select(frame => frame.Sequence).ToArray(),
            "替换 waiter 之后的新通知不得丢失");
    }

    [TestMethod]
    public async Task Two_Subscribers_Both_Catch_Up_When_Only_One_Gets_The_Notification()
    {
        var store = new TestEventStore();
        var signal = new TestSignal();
        var service = NewService(store, signal, pollInterval: TimeSpan.FromMilliseconds(50));

        using var cts = new CancellationTokenSource();
        var first = new ConcurrentQueue<SessionEventEnvelope>();
        var second = new ConcurrentQueue<SessionEventEnvelope>();

        var firstConsumer = ConsumeAsync(service, cts.Token, first, stopAfter: 1);
        var secondConsumer = ConsumeAsync(service, cts.Token, second, stopAfter: 1);

        await WaitUntilAsync(() => signal.PendingWaiterCount == 2);

        store.AddEvents(1, 1);
        // 单读者广播：这次 Signal 只会唤醒一个 subscriber。
        signal.Raise(1);

        await Task.WhenAll(
            firstConsumer.WaitAsync(TimeSpan.FromSeconds(10)),
            secondConsumer.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.AreEqual(1, first.Count, "拿到通知的 subscriber 必须收到事件");
        Assert.AreEqual(1, second.Count, "没拿到通知的 subscriber 必须由持久轮询补上，而不是永久漏事件");
    }

    [TestMethod]
    public async Task Empty_Session_Waits_And_Delivers_TheFirstEventLive()
    {
        var store = new TestEventStore();
        var signal = new TestSignal();
        var service = NewService(store, signal, pollInterval: TimeSpan.FromMilliseconds(50));

        using var cts = new CancellationTokenSource();
        var frames = new ConcurrentQueue<SessionEventEnvelope>();
        var consumer = ConsumeAsync(service, cts.Token, frames, stopAfter: 1);

        await WaitUntilAsync(() => signal.PendingWaiterCount == 1);
        Assert.IsEmpty(frames, "空会话在第一个事件之前不得产出帧");

        store.AddEvents(7, 7);
        signal.Raise(7);

        await consumer.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(7L, frames.Single().Sequence, "稀疏序号必须原样下发");
        Assert.IsFalse(frames.Single().IsReplay);
    }

    [TestMethod]
    public async Task Sparse_Sequences_ReplayInOrder_AndCancellationEndsTheStream()
    {
        var store = new TestEventStore();
        store.AddEvents(5, 5);
        store.AddEvents(12, 12);
        store.AddEvents(400, 400);
        var signal = new TestSignal();
        var service = NewService(store, signal);

        using var cts = new CancellationTokenSource();
        var frames = new List<SessionEventEnvelope>();

        await foreach (var frame in service.FollowAsync(SessionId, afterExclusive: 0, cts.Token))
        {
            frames.Add(frame);
            if (frames.Count == 3)
                cts.Cancel();
        }

        CollectionAssert.AreEqual(
            new long[] { 5, 12, 400 },
            frames.Select(frame => frame.Sequence).ToArray());
        Assert.IsTrue(frames.All(frame => frame.IsReplay));
        Assert.AreEqual(1, store.BoundsReadCount);
    }

    [TestMethod]
    public async Task An_Empty_Read_Batch_DoesNotSpin_TheReplay_Loop()
    {
        var store = new TestEventStore();
        // 边界报告 head=1_000，但存储没有任何可取事件（例如已被裁剪/压缩）。
        store.SetBoundsOverride(new EventBounds(1_000, 1_000));
        var signal = new TestSignal();
        // 轮询故意放到 5 秒之外，这样这条用例只观察回放阶段的行为。
        var service = NewService(store, signal, pollInterval: TimeSpan.FromSeconds(5));

        using var cts = new CancellationTokenSource();
        var consumer = Task.Run(async () =>
        {
            await foreach (var _ in service.FollowAsync(SessionId, 0, cts.Token))
            {
                // 空库不该产出任何帧
            }
        });

        await WaitUntilAsync(() => store.ForwardReads.Count >= 1);
        await Task.Delay(100);
        cts.Cancel();
        await consumer.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(1, store.ForwardReads.Count, "空批必须结束回放，而不是空转重读");
        Assert.AreEqual(1, store.BoundsReadCount, "回放边界只读一次");
    }

    [TestMethod]
    public async Task Heartbeat_Is_Emitted_While_TheSession_IsIdle()
    {
        var store = new TestEventStore();
        var signal = new TestSignal();
        var service = NewService(
            store,
            signal,
            pollInterval: TimeSpan.FromMilliseconds(20),
            heartbeatInterval: TimeSpan.FromMilliseconds(80));

        using var cts = new CancellationTokenSource();
        var gotHeartbeat = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var consumer = Task.Run(async () =>
        {
            await foreach (var frame in service.FollowAsync(SessionId, 0, cts.Token))
            {
                if (frame.Sequence == -1 && frame.EventType == "heartbeat")
                    gotHeartbeat.TrySetResult();
            }
        });

        await gotHeartbeat.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        await consumer.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static long[] Longs(long fromInclusive, long toInclusive) =>
        Enumerable.Range(0, (int)(toInclusive - fromInclusive + 1))
            .Select(offset => fromInclusive + offset)
            .ToArray();

    private static SessionEventStreamService NewService(
        TestEventStore store,
        TestSignal signal,
        TimeSpan? pollInterval = null,
        TimeSpan? heartbeatInterval = null) =>
        new(
            store,
            signal,
            NullLogger<SessionEventStreamService>.Instance,
            new StreamMetrics(),
            pollInterval,
            heartbeatInterval);

    /// <summary>Consumes frames until <paramref name="stopAfter"/> were seen.</summary>
    private static Task ConsumeAsync(
        SessionEventStreamService service,
        CancellationToken ct,
        ConcurrentQueue<SessionEventEnvelope> sink,
        int stopAfter) =>
        Task.Run(async () =>
        {
            var count = 0;
            await foreach (var frame in service.FollowAsync(SessionId, 0, ct))
            {
                sink.Enqueue(frame);
                if (++count >= stopAfter)
                    return;
            }
        });

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;

            await Task.Delay(10);
        }

        Assert.Fail("条件在 10 秒内没有成立");
    }

    /// <summary>
    /// Event store double: real sequence-ordered paging over an in-memory list, plus counters for the two
    /// observables this slice is about — how often the head is read and what upper bound each read used.
    /// </summary>
    private sealed class TestEventStore : IConversationEventStore
    {
        private readonly object _gate = new();
        private readonly List<ConversationEvent> _events = [];
        private EventBounds? _boundsOverride;

        public int BoundsReadCount { get; private set; }

        public List<(long After, long? ThroughInclusive, int Limit)> ForwardReads { get; } = [];

        public void AddEvents(long fromInclusive, long toInclusive)
        {
            lock (_gate)
            {
                for (var sequence = fromInclusive; sequence <= toInclusive; sequence++)
                {
                    _events.Add(new ConversationEvent
                    {
                        EventId = $"evt-{sequence}",
                        ConversationId = SessionId,
                        Sequence = sequence,
                        WorkspaceId = "ws",
                        TurnId = "turn",
                        Type = "message.delta",
                        SchemaVersion = 1,
                        OccurredAt = DateTimeOffset.UnixEpoch,
                        CommittedAt = DateTimeOffset.UnixEpoch,
                        Payload = JsonDocument.Parse("{}").RootElement,
                    });
                }

                _events.Sort((left, right) => left.Sequence.CompareTo(right.Sequence));
            }
        }

        public void SetBoundsOverride(EventBounds bounds)
        {
            lock (_gate)
            {
                _boundsOverride = bounds;
            }
        }

        public Task<EventBounds> GetBoundsAsync(string conversationId, CancellationToken ct)
        {
            lock (_gate)
            {
                BoundsReadCount++;
                if (_boundsOverride is { } bounds)
                    return Task.FromResult(bounds);

                return Task.FromResult(_events.Count == 0
                    ? new EventBounds(null, null)
                    : new EventBounds(_events[0].Sequence, _events[^1].Sequence));
            }
        }

        public Task<EventPage> ReadForwardAsync(
            string conversationId,
            long afterExclusive,
            long? throughInclusive,
            int limit,
            CancellationToken ct)
        {
            lock (_gate)
            {
                ForwardReads.Add((afterExclusive, throughInclusive, limit));

                var page = _events
                    .Where(evt => evt.Sequence > afterExclusive
                        && (throughInclusive is null || evt.Sequence <= throughInclusive))
                    .Take(limit)
                    .ToArray();

                return Task.FromResult(new EventPage(
                    page,
                    page.Length > 0 ? page[^1].Sequence : null,
                    HasMore: page.Length == limit));
            }
        }

        public Task<AppendResult> AppendAsync(
            string conversationId,
            long expectedVersion,
            IReadOnlyList<NewConversationEvent> events,
            EventWriteCondition condition,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<EventPage> ReadBackwardAsync(
            string conversationId,
            long beforeExclusive,
            int limit,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<EventPage> ReadByTypePrefixBackwardAsync(
            string conversationId,
            string typePrefix,
            long beforeExclusive,
            int limit,
            CancellationToken ct) => throw new NotSupportedException();

        public Task EnsureTablesAsync(CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>
    /// Signal double that reproduces the real *single-reader* broadcast: one <see cref="Raise"/> wakes exactly
    /// one pending waiter (like the shared bounded channel of <see cref="CommittedEventSignal"/>). It also
    /// counts cancellations, which is how the waiter-replacement contract becomes observable.
    /// </summary>
    private sealed class TestSignal : ICommittedEventSignal
    {
        private readonly object _gate = new();
        private readonly List<Waiter> _waiters = [];
        private long _head;
        private int _cancelledWaiterCount;

        private sealed record Waiter(long KnownHead, TaskCompletionSource Tcs);

        public int PendingWaiterCount
        {
            get { lock (_gate) return _waiters.Count; }
        }

        public int CancelledWaiterCount => Volatile.Read(ref _cancelledWaiterCount);

        /// <inheritdoc />
        public void Signal(string conversationId, long committedThroughSequence) => Raise(committedThroughSequence);

        /// <summary>Test-side publish of a committed head (the interface member above just forwards here).</summary>
        public void Raise(long head)
        {
            Waiter? target;

            lock (_gate)
            {
                if (head > _head)
                    _head = head;

                // 单读者：只唤醒一个等待者，与 Channel 语义一致。
                target = _waiters.FirstOrDefault(waiter => head > waiter.KnownHead);
                if (target is not null)
                    _waiters.Remove(target);
            }

            target?.Tcs.TrySetResult();
        }

        public ValueTask WaitForChangeAsync(string conversationId, long knownHead, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Waiter waiter;

            lock (_gate)
            {
                if (_head > knownHead)
                    return new ValueTask(Task.CompletedTask);

                waiter = new Waiter(knownHead, tcs);
                _waiters.Add(waiter);
            }

            ct.Register(() =>
            {
                lock (_gate)
                {
                    if (_waiters.Remove(waiter))
                        Interlocked.Increment(ref _cancelledWaiterCount);
                }

                tcs.TrySetCanceled(ct);
            });

            return new ValueTask(tcs.Task);
        }
    }
}
