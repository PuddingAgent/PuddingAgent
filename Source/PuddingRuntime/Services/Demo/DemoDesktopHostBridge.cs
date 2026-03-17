using PuddingCode.Platform;

namespace PuddingRuntime.Services.Demo;

/// <summary>
/// 演示桌面宿主桥接——模拟一个 C# 测试软件作为嵌入式 Runtime 节点。
/// 提供三类原生能力：查询软件状态、执行测试、读取测试结果。
/// 真实项目中替换为对接具体桌面软件 API 的实现。
/// </summary>
public sealed class DemoDesktopHostBridge : INativeHostBridge
{
    private readonly ILogger<DemoDesktopHostBridge> _logger;

    // 模拟软件内部状态
    private volatile string _appStatus = "Idle";
    private readonly List<string> _testLog = [];
    private int _testRunCount = 0;

    public DemoDesktopHostBridge(ILogger<DemoDesktopHostBridge> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<NativeCapabilityDescriptor> GetCapabilities() =>
    [
        new NativeCapabilityDescriptor
        {
            CapabilityId = "demo.query_status",
            Name = "查询软件状态",
            Description = "返回演示桌面软件当前运行状态（Idle / Running / Failed）。",
            Category = NativeCapabilityCategory.QueryState,
            RequiresApproval = false
        },
        new NativeCapabilityDescriptor
        {
            CapabilityId = "demo.run_test",
            Name = "执行测试",
            Description = "驱动演示软件执行指定测试用例。需要审批后方可执行。",
            Category = NativeCapabilityCategory.RunTest,
            RequiresApproval = true
        },
        new NativeCapabilityDescriptor
        {
            CapabilityId = "demo.read_result",
            Name = "读取测试结果",
            Description = "读取最近一次测试执行的结果日志。",
            Category = NativeCapabilityCategory.ReadResult,
            RequiresApproval = false
        }
    ];

    public Task<NativeCapabilityInvokeResult> InvokeAsync(
        NativeCapabilityInvokeRequest request,
        CancellationToken ct = default)
    {
        _logger.LogInformation("[DemoDesktopBridge] Invoking capability={Cap} session={Session}",
            request.CapabilityId, request.SessionId);

        var result = request.CapabilityId switch
        {
            "demo.query_status" => QueryStatus(),
            "demo.run_test" => RunTest(request.Parameters),
            "demo.read_result" => ReadResult(),
            _ => new NativeCapabilityInvokeResult
            {
                IsSuccess = false,
                ErrorMessage = $"Unknown capability: {request.CapabilityId}"
            }
        };

        return Task.FromResult(result);
    }

    // ── Private helpers ───────────────────────────────────────────────────

    private NativeCapabilityInvokeResult QueryStatus() =>
        new() { IsSuccess = true, Output = $"{{\"status\":\"{_appStatus}\",\"testRuns\":{_testRunCount}}}" };

    private NativeCapabilityInvokeResult RunTest(Dictionary<string, string>? parameters)
    {
        var testCase = parameters?.GetValueOrDefault("testCase") ?? "DefaultSuite";
        _appStatus = "Running";
        _testRunCount++;

        // 模拟测试执行
        var passed = _testRunCount % 3 != 0; // 每3次故意失败一次，模拟真实场景
        var summary = passed
            ? $"PASS  [{testCase}] 执行完毕，断言全部通过。Run#{_testRunCount}"
            : $"FAIL  [{testCase}] 断言失败：预期值 42，实际值 0。Run#{_testRunCount}";

        _testLog.Add($"{DateTimeOffset.UtcNow:HH:mm:ss} {summary}");
        if (_testLog.Count > 100) _testLog.RemoveAt(0);

        _appStatus = passed ? "Idle" : "Failed";
        return new NativeCapabilityInvokeResult { IsSuccess = true, Output = summary };
    }

    private NativeCapabilityInvokeResult ReadResult()
    {
        if (_testLog.Count == 0)
            return new NativeCapabilityInvokeResult { IsSuccess = true, Output = "暂无测试记录。" };

        var recent = string.Join("\n", _testLog.TakeLast(10));
        return new NativeCapabilityInvokeResult { IsSuccess = true, Output = recent };
    }
}
