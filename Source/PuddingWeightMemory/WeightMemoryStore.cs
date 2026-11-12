using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace PuddingWeightMemory;

/// <summary>
/// 权重记忆的 SQLite 持久化层：分区、章节版本、快照与事件回执。
/// <para>
/// 数据布局（为什么这样分表）：
/// <list type="bullet">
///   <item><description><c>snapshot_weight</c> 每个快照一份**实体**权重，而不是"活动 + 差量"。
///   反馈只改候选那份，读数时不需要叠加差量，回滚也只是把指针指回一份仍然完整的旧权重。</description></item>
///   <item><description><c>chapter</c> 的 <c>Status</c> 是跨快照的：失效一次，活动与候选以及历史快照
///   同时看不见它，不需要事后修补回滚路径。</description></item>
///   <item><description><c>applied_event</c> 是幂等台账：主键是 <c>(分区, 事件, 类型)</c>，
///   另有 <c>(分区, 类型, 消息, 主体)</c> 唯一索引，防止"同一消息换个事件号重投"造成二次训练。</description></item>
/// </list>
/// </para>
/// <para>
/// 并发：所有写操作在一个进程内信号量下串行（单分区单写者），事务边界包住"读权重—训练—写权重—写回执"，
/// 因此不存在两条反馈交错读同一份基础权重的情况。读走 WAL 下的独立连接，不会被写者阻塞。
/// 局限：多进程写同一个库时，这个信号量只管住本进程。
/// </para>
/// </summary>
internal sealed class WeightMemoryStore
{
    private const string EventRegister = "register";
    private const string EventFeedback = "feedback";
    private const string EventInvalidate = "invalidate";

    /// <summary>分区键分隔符：不能再出现在 ID 里（写入前校验），因此三元组拼接不会产生歧义。</summary>
    private const char ScopeSeparator = '\u001F';

    private const string SchemaSql = """
        PRAGMA journal_mode = WAL;

        CREATE TABLE IF NOT EXISTS model (
            Singleton INTEGER NOT NULL PRIMARY KEY CHECK (Singleton = 1),
            EncoderId TEXT NOT NULL,
            Dimension INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS scope (
            ScopeKey TEXT NOT NULL PRIMARY KEY,
            WorkspaceId TEXT NOT NULL,
            UserId TEXT NOT NULL,
            AgentId TEXT NOT NULL,
            ActiveSnapshotId TEXT NOT NULL,
            CandidateSnapshotId TEXT NOT NULL,
            NextOrdinal INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS snapshot (
            ScopeKey TEXT NOT NULL,
            SnapshotId TEXT NOT NULL,
            Ordinal INTEGER NOT NULL,
            PRIMARY KEY (ScopeKey, SnapshotId)
        );

        CREATE TABLE IF NOT EXISTS chapter (
            ScopeKey TEXT NOT NULL,
            ChapterId TEXT NOT NULL,
            BookId TEXT NOT NULL,
            Revision INTEGER NOT NULL,
            Status INTEGER NOT NULL,
            Anchor BLOB NOT NULL,
            SourceCue TEXT NULL,
            ConversationId TEXT NOT NULL,
            TurnId TEXT NOT NULL,
            MessageId TEXT NOT NULL,
            RegisteredEvent TEXT NOT NULL,
            PRIMARY KEY (ScopeKey, ChapterId)
        );

        CREATE TABLE IF NOT EXISTS snapshot_weight (
            ScopeKey TEXT NOT NULL,
            SnapshotId TEXT NOT NULL,
            ChapterId TEXT NOT NULL,
            Weight BLOB NOT NULL,
            PRIMARY KEY (ScopeKey, SnapshotId, ChapterId)
        );

        CREATE TABLE IF NOT EXISTS applied_event (
            ScopeKey TEXT NOT NULL,
            EventId TEXT NOT NULL,
            Kind TEXT NOT NULL,
            Signature TEXT NOT NULL,
            SubjectId TEXT NOT NULL,
            MessageId TEXT NOT NULL,
            PRIMARY KEY (ScopeKey, EventId, Kind)
        );

        CREATE UNIQUE INDEX IF NOT EXISTS UX_applied_event_subject
            ON applied_event (ScopeKey, Kind, MessageId, SubjectId)
            WHERE MessageId <> '';
        """;

    private readonly WeightMemoryOptions _options;
    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly int _dimension;
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _initialized;

    internal WeightMemoryStore(WeightMemoryOptions options, int dimension)
    {
        _options = options;
        _dimension = dimension;
        if (!options.TryNormalizePath(out _databasePath))
            throw new ArgumentException("DatabasePath 不能为空。", nameof(options));

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
            Pooling = true,
        }.ToString();
    }

    /// <summary>分区键：三元组以不可出现在 ID 中的分隔符拼接。</summary>
    internal static string ScopeKeyOf(MemoryScope scope)
    {
        foreach (var part in new[] { scope.WorkspaceId, scope.UserId, scope.AgentId })
        {
            if (part.Contains(ScopeSeparator, StringComparison.Ordinal))
                throw new WeightMemoryException("分区标识不得包含 \\u001F 分隔符。");
        }

        return string.Join(ScopeSeparator, scope.WorkspaceId, scope.UserId, scope.AgentId);
    }

    /// <summary>读取一个快照上的可见章节权重；<paramref name="requestedSnapshotId"/> 为空表示活动快照。</summary>
    internal async Task<SnapshotRead> ReadSnapshotAsync(
        MemoryScope scope,
        string requestedSnapshotId,
        CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        var scopeKey = ScopeKeyOf(scope);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: true);

        string snapshotId;
        if (string.IsNullOrEmpty(requestedSnapshotId))
        {
            var state = await ReadScopeStateAsync(connection, transaction, scopeKey, ct).ConfigureAwait(false);
            if (state is null)
                return SnapshotRead.Missing(string.Empty);

            snapshotId = state.ActiveSnapshotId;
        }
        else
        {
            if (!await SnapshotExistsAsync(connection, transaction, scopeKey, requestedSnapshotId, ct).ConfigureAwait(false))
                return SnapshotRead.Missing(requestedSnapshotId);

            snapshotId = requestedSnapshotId;
        }

        var chapters = await ReadVisibleWeightsAsync(connection, transaction, scopeKey, snapshotId, ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return new SnapshotRead(snapshotId, true, chapters);
    }

    /// <summary>读取分区的活动与候选快照号。</summary>
    internal async Task<WeightMemorySnapshots?> ReadSnapshotsAsync(MemoryScope scope, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        var scopeKey = ScopeKeyOf(scope);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: true);
        var state = await ReadScopeStateAsync(connection, transaction, scopeKey, ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);

        return state is null ? null : new WeightMemorySnapshots(state.ActiveSnapshotId, state.CandidateSnapshotId);
    }

    /// <summary>
    /// 登记章节：写章节记录与所有快照的同一份初始权重。
    /// <para>
    /// 版本语义：新的登记事件把 <c>Revision</c> 推进一格，并把权重重置为新的归一化线索向量
    /// （参考实现在新版本时同样以锚点为起点，不沿用旧权重）；对已失效的章节重新登记
    /// 会把状态恢复为 Active——"再存一次"就是重新让它可被召回。
    /// </para>
    /// </summary>
    internal async Task<RegisterOutcome> RegisterAsync(
        MemoryScope scope,
        SavedChapter chapter,
        float[] anchor,
        CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        var scopeKey = ScopeKeyOf(scope);
        var signature = SignatureOf(
            chapter.ChapterId,
            chapter.BookId,
            Convert.ToHexString(VectorMath.ToBytes(anchor)),
            chapter.SourceCue ?? string.Empty,
            chapter.Source.ConversationId,
            chapter.Source.TurnId,
            chapter.Source.MessageId);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(ct).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction();

            var applied = await ReadAppliedSignatureAsync(
                connection, transaction, scopeKey, chapter.EventId, EventRegister, ct).ConfigureAwait(false);
            if (applied is not null)
            {
                if (!string.Equals(applied, signature, StringComparison.Ordinal))
                    throw new WeightMemoryException($"登记事件 {chapter.EventId} 已用于不同内容。");

                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return RegisterOutcome.Duplicate;
            }

            // 分区与两个快照必须存在，章节才能被寻址；登记本身不改快照指针。
            await EnsureScopeAsync(connection, transaction, scopeKey, scope, ct).ConfigureAwait(false);
            var previous = await ReadChapterAsync(connection, transaction, scopeKey, chapter.ChapterId, ct).ConfigureAwait(false);
            var revision = previous is null ? 1 : previous.Revision + 1;

            await UpsertChapterAsync(connection, transaction, scopeKey, chapter, revision, anchor, ct).ConfigureAwait(false);

            var snapshots = await ReadSnapshotIdsAsync(connection, transaction, scopeKey, ct).ConfigureAwait(false);
            foreach (var snapshotId in snapshots)
            {
                await UpsertWeightAsync(connection, transaction, scopeKey, snapshotId, chapter.ChapterId, anchor, ct)
                    .ConfigureAwait(false);
            }

            await InsertAppliedAsync(
                connection, transaction, scopeKey, chapter.EventId, EventRegister, signature,
                chapter.ChapterId, chapter.Source.MessageId, ct).ConfigureAwait(false);

            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return RegisterOutcome.Applied;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>用一条反馈推进候选快照的权重（单个事务，含幂等台账）。</summary>
    internal async Task<FeedbackOutcome> ApplyFeedbackAsync(
        MemoryScope scope,
        Feedback feedback,
        float[] cue,
        CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        var scopeKey = ScopeKeyOf(scope);
        var acceptable = AcceptableSet(feedback);
        var signature = SignatureOf(
            feedback.PrimaryChapterId,
            string.Join('\u001E', acceptable.Order(StringComparer.Ordinal)),
            Convert.ToHexString(VectorMath.ToBytes(cue)),
            feedback.Source.ConversationId,
            feedback.Source.TurnId,
            feedback.Source.MessageId);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(ct).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction();

            var applied = await ReadAppliedSignatureAsync(
                connection, transaction, scopeKey, feedback.EventId, EventFeedback, ct).ConfigureAwait(false);
            if (applied is not null)
            {
                if (!string.Equals(applied, signature, StringComparison.Ordinal))
                    throw new WeightMemoryException($"反馈事件 {feedback.EventId} 已用于不同内容。");

                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return FeedbackOutcome.Duplicate;
            }

            // 同一消息对同一章节只训练一次：宿主重试换事件号时不会二次训练。
            if (await AppliedSubjectExistsAsync(
                    connection, transaction, scopeKey, EventFeedback,
                    feedback.Source.MessageId, feedback.PrimaryChapterId, ct).ConfigureAwait(false))
            {
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return FeedbackOutcome.Duplicate;
            }

            var state = await EnsureScopeAsync(connection, transaction, scopeKey, scope, ct).ConfigureAwait(false);
            var chapter = await ReadChapterAsync(connection, transaction, scopeKey, feedback.PrimaryChapterId, ct)
                .ConfigureAwait(false)
                ?? throw new WeightMemoryException(
                    $"反馈 {feedback.EventId} 指向未登记的章节 {feedback.PrimaryChapterId}。");

            if (chapter.Status != ChapterStatus.Active)
            {
                await InsertAppliedAsync(
                    connection, transaction, scopeKey, feedback.EventId, EventFeedback, signature,
                    feedback.PrimaryChapterId, feedback.Source.MessageId, ct).ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return FeedbackOutcome.SkippedDeleted;
            }

            var weights = await ReadWeightsAsync(connection, transaction, scopeKey, state.CandidateSnapshotId, ct)
                .ConfigureAwait(false);

            if (!weights.TryGetValue(feedback.PrimaryChapterId, out var primaryWeight))
                throw new WeightMemoryException(
                    $"章节 {feedback.PrimaryChapterId} 在候选快照 {state.CandidateSnapshotId} 中没有权重。");

            var negatives = new List<float[]>(weights.Count);
            var bestNegative = (double)_options.NullCompetitorScore;
            foreach (var (chapterId, weight) in weights)
            {
                if (string.Equals(chapterId, feedback.PrimaryChapterId, StringComparison.Ordinal))
                    continue;
                if (acceptable.Contains(chapterId))
                    continue;

                negatives.Add(weight);
                bestNegative = Math.Max(bestNegative, VectorMath.Dot(weight, cue));
            }

            var primaryScore = VectorMath.Dot(primaryWeight, cue);
            if (primaryScore - bestNegative >= _options.SkipGradientMargin)
            {
                // 正确章已经领先到门槛以上：继续推只会把权重推离锚点，按设计跳过梯度但照常记账。
                await InsertAppliedAsync(
                    connection, transaction, scopeKey, feedback.EventId, EventFeedback, signature,
                    feedback.PrimaryChapterId, feedback.Source.MessageId, ct).ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return FeedbackOutcome.SkippedByMargin;
            }

            var updated = KeyTrainer.Train(primaryWeight, chapter.Anchor, cue, negatives, _options);
            await UpsertWeightAsync(
                connection, transaction, scopeKey, state.CandidateSnapshotId, feedback.PrimaryChapterId, updated, ct)
                .ConfigureAwait(false);

            _options.FaultHook?.Invoke("feedback.after-weight-write");

            await InsertAppliedAsync(
                connection, transaction, scopeKey, feedback.EventId, EventFeedback, signature,
                feedback.PrimaryChapterId, feedback.Source.MessageId, ct).ConfigureAwait(false);

            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return FeedbackOutcome.Applied;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>把章节标记为已删除；跨快照生效。</summary>
    internal async Task<InvalidateOutcome> InvalidateChapterAsync(
        MemoryScope scope,
        string chapterId,
        string eventId,
        string messageId,
        CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        var scopeKey = ScopeKeyOf(scope);
        var signature = SignatureOf(chapterId, messageId);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(ct).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction();

            var applied = await ReadAppliedSignatureAsync(
                connection, transaction, scopeKey, eventId, EventInvalidate, ct).ConfigureAwait(false);
            if (applied is not null)
            {
                if (!string.Equals(applied, signature, StringComparison.Ordinal))
                    throw new WeightMemoryException($"失效事件 {eventId} 已用于不同内容。");

                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return InvalidateOutcome.Duplicate;
            }

            var chapter = await ReadChapterAsync(connection, transaction, scopeKey, chapterId, ct).ConfigureAwait(false);

            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE chapter SET Status = $deleted
                    WHERE ScopeKey = $scope AND ChapterId = $chapter;
                    """;
                command.Parameters.AddWithValue("$deleted", (int)ChapterStatus.Deleted);
                command.Parameters.AddWithValue("$scope", scopeKey);
                command.Parameters.AddWithValue("$chapter", chapterId);
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await InsertAppliedAsync(
                connection, transaction, scopeKey, eventId, EventInvalidate, signature, chapterId, messageId, ct)
                .ConfigureAwait(false);

            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return chapter is null ? InvalidateOutcome.NotFound : InvalidateOutcome.Applied;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// 比较交换发布：活动指针从 <paramref name="expectedActiveSnapshotId"/> 切到
    /// <paramref name="candidateSnapshotId"/>，并从它克隆出一个新的候选快照。
    /// </summary>
    internal async Task<bool> TryPublishAsync(
        MemoryScope scope,
        string expectedActiveSnapshotId,
        string candidateSnapshotId,
        CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        var scopeKey = ScopeKeyOf(scope);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(ct).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction();

            var state = await ReadScopeStateAsync(connection, transaction, scopeKey, ct).ConfigureAwait(false);
            if (state is null
                || !string.Equals(state.ActiveSnapshotId, expectedActiveSnapshotId, StringComparison.Ordinal)
                || !await SnapshotExistsAsync(connection, transaction, scopeKey, candidateSnapshotId, ct).ConfigureAwait(false))
            {
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return false;
            }

            var nextOrdinal = state.NextOrdinal;
            var nextSnapshotId = SnapshotIdOf(nextOrdinal);

            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO snapshot (ScopeKey, SnapshotId, Ordinal) VALUES ($scope, $snapshot, $ordinal);
                    INSERT INTO snapshot_weight (ScopeKey, SnapshotId, ChapterId, Weight)
                        SELECT ScopeKey, $snapshot, ChapterId, Weight
                        FROM snapshot_weight
                        WHERE ScopeKey = $scope AND SnapshotId = $source;
                    UPDATE scope
                        SET ActiveSnapshotId = $active, CandidateSnapshotId = $snapshot, NextOrdinal = $next
                        WHERE ScopeKey = $scope;
                    """;
                command.Parameters.AddWithValue("$scope", scopeKey);
                command.Parameters.AddWithValue("$snapshot", nextSnapshotId);
                command.Parameters.AddWithValue("$ordinal", nextOrdinal);
                command.Parameters.AddWithValue("$source", candidateSnapshotId);
                command.Parameters.AddWithValue("$active", candidateSnapshotId);
                command.Parameters.AddWithValue("$next", nextOrdinal + 1);
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    internal async ValueTask DisposeAsync()
    {
        _initializeLock.Dispose();
        _writeLock.Dispose();

        // 连接池按连接串缓存连接；临时库被删掉之前先清掉本串的池，避免句柄留在池里。
        await using var connection = new SqliteConnection(_connectionString);
        SqliteConnection.ClearPool(connection);
    }

    private static string SignatureOf(params string[] parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(ScopeSeparator, parts))));

    private static HashSet<string> AcceptableSet(Feedback feedback)
    {
        var acceptable = new HashSet<string>(StringComparer.Ordinal) { feedback.PrimaryChapterId };
        if (feedback.AcceptableChapterIds is null)
            return acceptable;

        foreach (var chapterId in feedback.AcceptableChapterIds)
        {
            if (!string.IsNullOrWhiteSpace(chapterId))
                acceptable.Add(chapterId);
        }

        return acceptable;
    }

    private static string SnapshotIdOf(int ordinal) =>
        "snap-" + ordinal.ToString("D6", CultureInfo.InvariantCulture);

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized)
            return;

        await _initializeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized)
                return;

            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var directory = Path.GetDirectoryName(_databasePath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                await using var connection = await OpenAsync(ct).ConfigureAwait(false);
                await using (var command = connection.CreateCommand())
                {
                    command.CommandText = SchemaSql;
                    await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await EnsureModelAsync(connection, ct).ConfigureAwait(false);
                _initialized = true;
            }
            finally
            {
                _writeLock.Release();
            }
        }
        finally
        {
            _initializeLock.Release();
        }
    }

    /// <summary>
    /// 把编码器身份与维度钉在库里：同一个库只接受同一套向量口径，
    /// 否则"点积可比"这个前提就不成立了。
    /// </summary>
    private async Task EnsureModelAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT EncoderId, Dimension FROM model WHERE Singleton = 1;";
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var encoderId = reader.GetString(0);
                var dimension = reader.GetInt32(1);
                if (dimension != _dimension || !string.Equals(encoderId, _options.EncoderId, StringComparison.Ordinal))
                {
                    throw new WeightMemoryException(
                        $"库内权重由 {encoderId}（{dimension} 维）产生，与当前配置 {_options.EncoderId}（{_dimension} 维）不一致；更换编码器必须重建分区。");
                }

                return;
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO model (Singleton, EncoderId, Dimension) VALUES (1, $encoder, $dimension);";
            command.Parameters.AddWithValue("$encoder", _options.EncoderId);
            command.Parameters.AddWithValue("$dimension", _dimension);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA busy_timeout = 5000;";
            await pragma.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>读到分区就返回，读不到就建：两个内容相同的快照（活动与候选）。</summary>
    private async Task<ScopeState> EnsureScopeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string scopeKey,
        MemoryScope scope,
        CancellationToken ct)
    {
        var state = await ReadScopeStateAsync(connection, transaction, scopeKey, ct).ConfigureAwait(false);
        if (state is not null)
            return state;

        var active = SnapshotIdOf(1);
        var candidate = SnapshotIdOf(2);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO scope (ScopeKey, WorkspaceId, UserId, AgentId, ActiveSnapshotId, CandidateSnapshotId, NextOrdinal)
                VALUES ($scope, $workspace, $user, $agent, $active, $candidate, 3);
            INSERT INTO snapshot (ScopeKey, SnapshotId, Ordinal) VALUES ($scope, $active, 1);
            INSERT INTO snapshot (ScopeKey, SnapshotId, Ordinal) VALUES ($scope, $candidate, 2);
            """;
        command.Parameters.AddWithValue("$scope", scopeKey);
        command.Parameters.AddWithValue("$workspace", scope.WorkspaceId);
        command.Parameters.AddWithValue("$user", scope.UserId);
        command.Parameters.AddWithValue("$agent", scope.AgentId);
        command.Parameters.AddWithValue("$active", active);
        command.Parameters.AddWithValue("$candidate", candidate);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        return new ScopeState(active, candidate, 3);
    }

    private static async Task<ScopeState?> ReadScopeStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string scopeKey,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT ActiveSnapshotId, CandidateSnapshotId, NextOrdinal
            FROM scope WHERE ScopeKey = $scope;
            """;
        command.Parameters.AddWithValue("$scope", scopeKey);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;

        return new ScopeState(reader.GetString(0), reader.GetString(1), reader.GetInt32(2));
    }

    private static async Task<bool> SnapshotExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string scopeKey,
        string snapshotId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM snapshot WHERE ScopeKey = $scope AND SnapshotId = $snapshot LIMIT 1;";
        command.Parameters.AddWithValue("$scope", scopeKey);
        command.Parameters.AddWithValue("$snapshot", snapshotId);
        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is not null;
    }

    private static async Task<List<string>> ReadSnapshotIdsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string scopeKey,
        CancellationToken ct)
    {
        var snapshots = new List<string>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT SnapshotId FROM snapshot WHERE ScopeKey = $scope ORDER BY Ordinal;";
        command.Parameters.AddWithValue("$scope", scopeKey);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            snapshots.Add(reader.GetString(0));

        return snapshots;
    }

    /// <summary>
    /// 读取一个快照的活动章节权重。
    /// <para>
    /// 这里刻意按 <c>ChapterId DESC</c> 返回：读取顺序不是契约的一部分，排序完全由
    /// <c>SqliteWeightMemory.CompareHits</c>（分数降序、同分章节 ID 序数序）决定。
    /// 如果读回来的顺序碰巧就是契约顺序，那个比较器就永远不会被检验——降序读取让
    /// 任何“依赖扫描顺序”的疏漏在测试里立刻变红。
    /// </para>
    /// </summary>
    private static async Task<List<ChapterWeight>> ReadVisibleWeightsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string scopeKey,
        string snapshotId,
        CancellationToken ct)
    {
        var chapters = new List<ChapterWeight>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT w.ChapterId, w.Weight
            FROM snapshot_weight w
            JOIN chapter c ON c.ScopeKey = w.ScopeKey AND c.ChapterId = w.ChapterId
            WHERE w.ScopeKey = $scope AND w.SnapshotId = $snapshot AND c.Status = $active
            ORDER BY w.ChapterId DESC;
            """;
        command.Parameters.AddWithValue("$scope", scopeKey);
        command.Parameters.AddWithValue("$snapshot", snapshotId);
        command.Parameters.AddWithValue("$active", (int)ChapterStatus.Active);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var chapterId = reader.GetString(0);
            var weight = VectorMath.FromBytes(reader.GetFieldValue<byte[]>(1));
            chapters.Add(new ChapterWeight(chapterId, weight));
        }

        return chapters;
    }

    /// <summary>读取某快照的全部权重（含非 Active 章节），供反馈训练挑选负例。</summary>
    private static async Task<Dictionary<string, float[]>> ReadWeightsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string scopeKey,
        string snapshotId,
        CancellationToken ct)
    {
        var weights = new Dictionary<string, float[]>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT w.ChapterId, w.Weight
            FROM snapshot_weight w
            JOIN chapter c ON c.ScopeKey = w.ScopeKey AND c.ChapterId = w.ChapterId
            WHERE w.ScopeKey = $scope AND w.SnapshotId = $snapshot AND c.Status = $active;
            """;
        command.Parameters.AddWithValue("$scope", scopeKey);
        command.Parameters.AddWithValue("$snapshot", snapshotId);
        command.Parameters.AddWithValue("$active", (int)ChapterStatus.Active);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            weights[reader.GetString(0)] = VectorMath.FromBytes(reader.GetFieldValue<byte[]>(1));

        return weights;
    }

    private static async Task<ChapterRecord?> ReadChapterAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string scopeKey,
        string chapterId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT BookId, Revision, Status, Anchor
            FROM chapter WHERE ScopeKey = $scope AND ChapterId = $chapter;
            """;
        command.Parameters.AddWithValue("$scope", scopeKey);
        command.Parameters.AddWithValue("$chapter", chapterId);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;

        return new ChapterRecord(
            reader.GetString(0),
            reader.GetInt32(1),
            (ChapterStatus)reader.GetInt32(2),
            VectorMath.FromBytes(reader.GetFieldValue<byte[]>(3)));
    }

    private static async Task<string?> ReadAppliedSignatureAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string scopeKey,
        string eventId,
        string kind,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT Signature FROM applied_event
            WHERE ScopeKey = $scope AND EventId = $event AND Kind = $kind;
            """;
        command.Parameters.AddWithValue("$scope", scopeKey);
        command.Parameters.AddWithValue("$event", eventId);
        command.Parameters.AddWithValue("$kind", kind);
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
    }

    private static async Task<bool> AppliedSubjectExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string scopeKey,
        string kind,
        string messageId,
        string subjectId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1 FROM applied_event
            WHERE ScopeKey = $scope AND Kind = $kind AND MessageId = $message AND SubjectId = $subject
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$scope", scopeKey);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$message", messageId);
        command.Parameters.AddWithValue("$subject", subjectId);
        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is not null;
    }

    private static async Task InsertAppliedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string scopeKey,
        string eventId,
        string kind,
        string signature,
        string subjectId,
        string messageId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO applied_event (ScopeKey, EventId, Kind, Signature, SubjectId, MessageId)
            VALUES ($scope, $event, $kind, $signature, $subject, $message);
            """;
        command.Parameters.AddWithValue("$scope", scopeKey);
        command.Parameters.AddWithValue("$event", eventId);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$signature", signature);
        command.Parameters.AddWithValue("$subject", subjectId);
        command.Parameters.AddWithValue("$message", messageId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task UpsertChapterAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string scopeKey,
        SavedChapter chapter,
        int revision,
        float[] anchor,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO chapter
                (ScopeKey, ChapterId, BookId, Revision, Status, Anchor, SourceCue,
                 ConversationId, TurnId, MessageId, RegisteredEvent)
            VALUES
                ($scope, $chapter, $book, $revision, $status, $anchor, $cue,
                 $conversation, $turn, $message, $event)
            ON CONFLICT(ScopeKey, ChapterId) DO UPDATE SET
                BookId = excluded.BookId,
                Revision = excluded.Revision,
                Status = excluded.Status,
                Anchor = excluded.Anchor,
                SourceCue = excluded.SourceCue,
                ConversationId = excluded.ConversationId,
                TurnId = excluded.TurnId,
                MessageId = excluded.MessageId,
                RegisteredEvent = excluded.RegisteredEvent;
            """;
        command.Parameters.AddWithValue("$scope", scopeKey);
        command.Parameters.AddWithValue("$chapter", chapter.ChapterId);
        command.Parameters.AddWithValue("$book", chapter.BookId);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$status", (int)ChapterStatus.Active);
        command.Parameters.AddWithValue("$anchor", VectorMath.ToBytes(anchor));
        command.Parameters.AddWithValue("$cue", (object?)chapter.SourceCue ?? DBNull.Value);
        command.Parameters.AddWithValue("$conversation", chapter.Source.ConversationId);
        command.Parameters.AddWithValue("$turn", chapter.Source.TurnId);
        command.Parameters.AddWithValue("$message", chapter.Source.MessageId);
        command.Parameters.AddWithValue("$event", chapter.EventId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task UpsertWeightAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string scopeKey,
        string snapshotId,
        string chapterId,
        float[] weight,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO snapshot_weight (ScopeKey, SnapshotId, ChapterId, Weight)
            VALUES ($scope, $snapshot, $chapter, $weight)
            ON CONFLICT(ScopeKey, SnapshotId, ChapterId) DO UPDATE SET Weight = excluded.Weight;
            """;
        command.Parameters.AddWithValue("$scope", scopeKey);
        command.Parameters.AddWithValue("$snapshot", snapshotId);
        command.Parameters.AddWithValue("$chapter", chapterId);
        command.Parameters.AddWithValue("$weight", VectorMath.ToBytes(weight));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private sealed record ScopeState(string ActiveSnapshotId, string CandidateSnapshotId, int NextOrdinal);

    private sealed record ChapterRecord(string BookId, int Revision, ChapterStatus Status, float[] Anchor);
}

/// <summary>一个快照上的可读章节权重。</summary>
/// <param name="ChapterId">章节标识。</param>
/// <param name="Weight">单位权重向量。</param>
internal sealed record ChapterWeight(string ChapterId, float[] Weight);

/// <summary>一次快照读取的结果。<see cref="Exists"/> 为 false 时表示分区或快照不存在。</summary>
/// <param name="SnapshotId">实际读取（或被要求）的快照号。</param>
/// <param name="Exists">快照是否存在。</param>
/// <param name="Chapters">可见章节权重。</param>
internal sealed record SnapshotRead(string SnapshotId, bool Exists, IReadOnlyList<ChapterWeight> Chapters)
{
    internal static SnapshotRead Missing(string snapshotId) => new(snapshotId, false, Array.Empty<ChapterWeight>());
}

/// <summary>登记结果：已写入 / 事件重复。</summary>
internal enum RegisterOutcome
{
    Applied,
    Duplicate,
}

/// <summary>反馈结果：已训练 / 重复 / 已领先跳过 / 目标已失效。</summary>
internal enum FeedbackOutcome
{
    Applied,
    Duplicate,
    SkippedByMargin,
    SkippedDeleted,
}

/// <summary>失效结果：已标记 / 事件重复 / 章节本就不存在。</summary>
internal enum InvalidateOutcome
{
    Applied,
    Duplicate,
    NotFound,
}
