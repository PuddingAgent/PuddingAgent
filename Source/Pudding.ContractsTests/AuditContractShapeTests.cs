using System.Reflection;
using Pudding.Contracts;
using Pudding.Contracts.Audit;

namespace Pudding.ContractsTests;

/// <summary>
/// 审计契约的<b>形状</b>就是「日志/诊断包不泄密」的机器可验判据：
/// 记录里不存在承载脚本正文、URL、剪贴板内容、Token 或页面数据的字段。
/// </summary>
public sealed class AuditContractShapeTests
{
    private static readonly string[] AllowedProperties =
    [
        "OperationId",
        "Generation",
        "Capability",
        "Outcome",
        "QueueDuration",
        "ExecutionDuration",
        "ErrorCode",
        "TraceId",
        "CorrelationId",
        "RecordedUtc",
    ];

    private static readonly string[] ForbiddenNameTokens =
    [
        "script",
        "url",
        "uri",
        "content",
        "body",
        "payload",
        "html",
        "cookie",
        "clipboard",
        "token",
        "secret",
        "text",
        "page",
        "target",
        "result",
    ];

    [Fact]
    public void AuditRecord_DeclaresExactlyTheAllowedDiagnosticProperties()
    {
        var names = typeof(DesktopCapabilityAuditRecord)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(AllowedProperties.OrderBy(n => n, StringComparer.Ordinal).ToArray(), names);
    }

    [Fact]
    public void AuditRecord_HasNoPayloadShapedMemberNames()
    {
        var offending = typeof(DesktopCapabilityAuditRecord)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Where(name => ForbiddenNameTokens.Any(
                token => name.Contains(token, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        Assert.Empty(offending);
    }

    [Fact]
    public void AuditRecord_MemberTypesStayInsideContractsAndBcl()
    {
        var offending = typeof(DesktopCapabilityAuditRecord)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p =>
            {
                var ns = p.PropertyType.Namespace ?? string.Empty;
                return !ns.StartsWith("System", StringComparison.Ordinal)
                    && !ns.StartsWith("Pudding.Contracts", StringComparison.Ordinal);
            })
            .Select(p => $"{p.Name}: {p.PropertyType.FullName}")
            .ToArray();

        Assert.Empty(offending);
    }

    [Fact]
    public void AuditRecord_RecordsOnlySemanticMetadata()
    {
        var record = new DesktopCapabilityAuditRecord
        {
            OperationId = new OperationId("op-1"),
            Generation = ConnectionGeneration.Require(2),
            Capability = "webview.navigate",
            Outcome = DesktopCapabilityOutcome.Failed,
            QueueDuration = TimeSpan.FromMilliseconds(5),
            ExecutionDuration = TimeSpan.FromMilliseconds(40),
            ErrorCode = DesktopCapabilityErrorCode.PageVersionMismatch,
            TraceId = "trace-1",
            CorrelationId = new DesktopCorrelationId("call-1"),
            RecordedUtc = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
        };

        Assert.Equal("webview.navigate", record.Capability);
        Assert.Equal(DesktopCapabilityErrorCode.PageVersionMismatch, record.ErrorCode);
        Assert.Contains("webview.navigate", record.ToString(), StringComparison.Ordinal);
        Assert.Contains("PageVersionMismatch", record.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void NullSink_AcceptsEveryRecordSilently()
    {
        var sink = NullDesktopCapabilityAuditSink.Instance;

        sink.Record(new DesktopCapabilityAuditRecord
        {
            OperationId = new OperationId("op-2"),
            Generation = ConnectionGeneration.None,
            Capability = "shell.notification",
            Outcome = DesktopCapabilityOutcome.Rejected,
        });

        Assert.Same(NullDesktopCapabilityAuditSink.Instance, sink);
    }
}
