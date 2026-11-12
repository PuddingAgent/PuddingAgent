using System.Buffers.Binary;
using Microsoft.Data.Sqlite;

using PuddingWeightMemory;

namespace PuddingWeightMemoryTests;

/// <summary>
/// 每个测试一个独立临时库的组件夹具。
/// <para>
/// 它提供三类能力：构造确定性向量与配置、按"章节/反馈"惯例生成不重复的事件与消息标识、
/// 以及**直接读库**（活动/候选快照里的权重、行数）。直接读库是有意的：只通过公开 API 断言
/// 无法区分"权重真的被训练成这个向量"和"打分恰好给出同样的数"，而金标准用例要求前者。
/// </para>
/// </summary>
internal sealed class WeightMemoryHarness : IAsyncDisposable
{
    private int _sequence;

    internal WeightMemoryHarness(
        (string Text, float[] Vector)[] vectors,
        Func<string, WeightMemoryOptions>? optionsFactory = null,
        IWriteAdmissionGate? gate = null,
        int dimension = 4)
    {
        DatabasePath = Path.Combine(
            Path.GetTempPath(),
            "pudding-weight-memory-tests",
            Guid.NewGuid().ToString("N"),
            "weights.db");

        Options = optionsFactory?.Invoke(DatabasePath) ?? CreateDefaultOptions(DatabasePath);
        Memory = new SqliteWeightMemory(Options, new FixedTextEncoder(dimension, vectors), gate);
    }

    /// <summary>临时库路径；需要第二个实例打开同一个库时用。</summary>
    internal string DatabasePath { get; }

    internal WeightMemoryOptions Options { get; }

    internal SqliteWeightMemory Memory { get; }

    internal MemoryScope Scope { get; } = new("ws-1", "user-1", "agent-1");

    /// <summary>默认阈值全开（0），让排序类用例看到完整列表而不是被门槛截断。</summary>
    internal static Func<string, WeightMemoryOptions> DefaultOptions => CreateDefaultOptions;

    /// <summary>按需覆盖阈值/超参的配置工厂；其余字段沿用测试缺省值。</summary>
    internal static Func<string, WeightMemoryOptions> OptionsFactory(
        float minimumTopScore = 0f,
        float minimumScoreGap = 0f,
        float skipGradientMargin = 0.1f,
        int trainingSteps = 4,
        Action<string>? faultHook = null) =>
        path => new WeightMemoryOptions
        {
            DatabasePath = path,
            EncoderId = "test/4d",
            MinimumTopScore = minimumTopScore,
            MinimumScoreGap = minimumScoreGap,
            SkipGradientMargin = skipGradientMargin,
            TrainingSteps = trainingSteps,
            FaultHook = faultHook,
        };

    internal SavedChapter NewChapter(
        string chapterId,
        string cueText,
        string? bookId = null,
        string? messageId = null)
    {
        var sequence = Interlocked.Increment(ref _sequence);
        return new SavedChapter(
            Scope,
            new TurnKey("conv-1", $"turn-{sequence}", messageId ?? $"msg-{sequence}"),
            $"evt-save-{chapterId}-{sequence}",
            bookId ?? "book-1",
            chapterId,
            cueText);
    }

    internal Feedback NewFeedback(
        string primaryChapterId,
        string cueText,
        IReadOnlyList<string>? acceptable = null,
        string? messageId = null,
        string? eventId = null)
    {
        var sequence = Interlocked.Increment(ref _sequence);
        return new Feedback(
            Scope,
            new TurnKey("conv-1", $"turn-{sequence}", messageId ?? $"msg-{sequence}"),
            eventId ?? $"evt-feedback-{sequence}",
            primaryChapterId,
            acceptable ?? Array.Empty<string>(),
            cueText);
    }

    internal Task RegisterAsync(string chapterId, string cueText, CancellationToken ct = default) =>
        Memory.RegisterAsync(NewChapter(chapterId, cueText), ct);

    internal Task<RecallResult> PredictAsync(
        string text,
        int limit = 10,
        string snapshotId = "",
        CancellationToken ct = default) =>
        Memory.PredictAsync(
            new RecallQuery(Scope, new TurnKey("conv-1", "turn-q", "msg-q"), snapshotId, text, Array.Empty<string>(), limit),
            ct);

    /// <summary>读某个快照里某章节的落盘权重（<paramref name="snapshot"/> 取 <c>active</c> 或 <c>candidate</c>）。</summary>
    internal async Task<float[]> ReadWeightAsync(string snapshot, string chapterId)
    {
        var snapshotId = await ReadSnapshotIdAsync(snapshot).ConfigureAwait(false);
        await using var connection = await OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Weight FROM snapshot_weight WHERE SnapshotId = $snapshot AND ChapterId = $chapter LIMIT 1;";
        command.Parameters.AddWithValue("$snapshot", snapshotId);
        command.Parameters.AddWithValue("$chapter", chapterId);
        var blob = await command.ExecuteScalarAsync().ConfigureAwait(false) as byte[]
            ?? throw new InvalidOperationException($"快照 {snapshotId} 里没有章节 {chapterId} 的权重。");

        return Decode(blob);
    }

    /// <summary>读某个快照里的章节权重行数（用于证明"没有产生第二条记录"）。</summary>
    internal async Task<long> CountWeightsAsync(string snapshot, string chapterId)
    {
        var snapshotId = await ReadSnapshotIdAsync(snapshot).ConfigureAwait(false);
        await using var connection = await OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM snapshot_weight WHERE SnapshotId = $snapshot AND ChapterId = $chapter;";
        command.Parameters.AddWithValue("$snapshot", snapshotId);
        command.Parameters.AddWithValue("$chapter", chapterId);
        return Convert.ToInt64(await command.ExecuteScalarAsync().ConfigureAwait(false));
    }

    /// <summary>章节表里的行数（一次登记只能有一行，无论事件重投多少次）。</summary>
    internal async Task<long> CountChapterRowsAsync(string chapterId)
    {
        await using var connection = await OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM chapter WHERE ChapterId = $chapter;";
        command.Parameters.AddWithValue("$chapter", chapterId);
        return Convert.ToInt64(await command.ExecuteScalarAsync().ConfigureAwait(false));
    }

    /// <summary>章节版本号：每次新的登记事件前进一格，是“这条地址被改写了几次”的唯一证据。</summary>
    internal async Task<long> ReadRevisionAsync(string chapterId)
    {
        await using var connection = await OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Revision FROM chapter WHERE ChapterId = $chapter LIMIT 1;";
        command.Parameters.AddWithValue("$chapter", chapterId);
        return Convert.ToInt64(await command.ExecuteScalarAsync().ConfigureAwait(false));
    }

    public async ValueTask DisposeAsync()
    {
        await Memory.DisposeAsync().ConfigureAwait(false);

        var directory = Path.GetDirectoryName(DatabasePath);
        if (string.IsNullOrEmpty(directory))
            return;

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响判定：测试已断言完，残留文件留给系统临时目录清理。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static WeightMemoryOptions CreateDefaultOptions(string path) => new()
    {
        DatabasePath = path,
        EncoderId = "test/4d",
        MinimumTopScore = 0f,
        MinimumScoreGap = 0f,
    };

    private async Task<string> ReadSnapshotIdAsync(string snapshot)
    {
        if (snapshot is not ("active" or "candidate"))
            throw new ArgumentOutOfRangeException(nameof(snapshot), snapshot, "只支持 active / candidate。");

        await using var connection = await OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = snapshot == "active"
            ? "SELECT ActiveSnapshotId FROM scope LIMIT 1;"
            : "SELECT CandidateSnapshotId FROM scope LIMIT 1;";
        return await command.ExecuteScalarAsync().ConfigureAwait(false) as string
            ?? throw new InvalidOperationException("分区尚未建立，快照号为空。");
    }

    private async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection($"Data Source={DatabasePath};Mode=ReadOnly");
        await connection.OpenAsync().ConfigureAwait(false);
        return connection;
    }

    /// <summary>按小端序还原 <c>VectorMath.ToBytes</c> 写下的 BLOB。</summary>
    private static float[] Decode(byte[] bytes)
    {
        var vector = new float[bytes.Length / sizeof(float)];
        for (var index = 0; index < vector.Length; index++)
            vector[index] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(index * sizeof(float)));

        return vector;
    }
}
