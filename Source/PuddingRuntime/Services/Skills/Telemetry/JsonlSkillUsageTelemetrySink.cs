using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace PuddingRuntime.Services.Skills.Telemetry;

/// <summary>
/// JSONL 文件落点：按 UTC 日期分片，追加写 skill-usage-YYYYMMDD.jsonl，
/// 每行一个独立 JSON 对象（禁止数组包裹，方便逐行消费与按天归档）。
///
/// 为什么 fail-open：目录不可写、序列化异常等一切故障都只 LogWarning 后返回，
/// 绝不抛出 —— 遥测是旁路信号，故障不得影响对话。写入用 UTF-8 无 BOM，
/// 逐条加锁避免并发句柄在 Windows 上触发 sharing violation。
/// </summary>
public sealed class JsonlSkillUsageTelemetrySink : ISkillUsageTelemetrySink
{
    // 枚举序列化为 camelCase 字符串（如 readFailed），让 JSONL 对人可读、对打分器稳定
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _directory;
    private readonly ILogger<JsonlSkillUsageTelemetrySink> _logger;
    private readonly Func<DateTimeOffset> _utcNow;

    // 同进程内串行化追加写，避免并发 append 交错或 sharing violation
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public JsonlSkillUsageTelemetrySink(
        string directory,
        ILogger<JsonlSkillUsageTelemetrySink> logger,
        Func<DateTimeOffset>? utcNow = null)
    {
        _directory = directory;
        _logger = logger;
        _utcNow = utcNow ?? DefaultUtcNow;
    }

    public async ValueTask RecordAsync(SkillUsageRecord record, CancellationToken ct = default)
    {
        try
        {
            var line = JsonSerializer.Serialize(record, JsonOptions);
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                Directory.CreateDirectory(_directory);
                var path = Path.Combine(_directory, GetFileName(_utcNow()));
                await File.AppendAllTextAsync(path, line + "\n", Utf8NoBom, ct).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch (Exception ex)
        {
            // fail-open：任何 IO/序列化异常都不上抛，遥测故障只留痕
            _logger.LogWarning(ex,
                "[SkillUsageTelemetry] Failed to append skill usage record skill={SkillId}",
                record.SkillId);
        }
    }

    /// <summary>按 UTC 日期生成分片文件名。时间源可注入，测试据此直接断言分文件规则。</summary>
    private static string GetFileName(DateTimeOffset occurredAtUtc) =>
        $"skill-usage-{occurredAtUtc:yyyyMMdd}.jsonl";

    private static DateTimeOffset DefaultUtcNow() => DateTimeOffset.UtcNow;
}
