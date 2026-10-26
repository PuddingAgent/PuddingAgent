using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopConnection;
using Proto = Pudding.Rpc.Protocol.V1;

namespace DesktopConnectionTests;

/// <summary>
/// 快照预算解码：表示集合的判据必须与契约一致。
///
/// 背景（实测 2026-10-26）：<c>browser_snapshot mode=text</c> 只要语义块，把 dom / ax / html
/// 三个开关全置 false、只置 <c>include_content_blocks</c>。而线缆上原先没有这个字段 ⇒
/// Desktop 解出的 <see cref="DesktopSnapshotOptions.HasContent"/> 恒为 false ⇒
/// 每一次正文快照都被判「没有请求任何表示」而拒绝。这两条用例锁住正负两侧：
/// 只有语义块**必须**被接受，一个表示都不请求**必须**被拒绝。
/// </summary>
public sealed class SnapshotBudgetDecodeTests
{
    private static readonly DesktopInstanceId DesktopId = new("desk-1");

    private static Proto.CoreFrame SnapshotFrame(Proto.SnapshotBudget budget) => new()
    {
        Command = new Proto.CapabilityCommand
        {
            Caller = new Proto.BrowserCallerIdentityMessage
            {
                Kind = "agent",
                CallerId = "agent-1",
                TaskId = "run-1",
            },
            OperationId = "op-snapshot",
            Generation = 1,
            Capability = "browser.snapshot",
            Deadline = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(
                DateTimeOffset.UtcNow.AddSeconds(30)),
            Snapshot = new Proto.SnapshotCommand
            {
                Target = new Proto.CommandTarget { ContextId = "ctx-1", PageId = "page-1" },
                ExpectedPageVersion = 3,
                Budget = budget,
            },
        },
    };

    [Fact]
    public void Decode_TextModeBudget_ThatOnlyRequestsContentBlocksIsAccepted()
    {
        var decoded = CoreFrameMapping.Decode(
            SnapshotFrame(new Proto.SnapshotBudget
            {
                IncludeDom = false,
                IncludeAccessibilityTree = false,
                IncludeHtml = false,
                IncludeContentBlocks = true,
                MaxNodes = 10000,
                MaxTextLength = 200000,
            }).Command,
            DesktopId);

        Assert.True(decoded.IsSuccess);
        var options = decoded.Value.Request.Snapshot!.Options;
        Assert.True(options.IncludeContentBlocks);
        Assert.False(options.IncludeDom);
        Assert.False(options.IncludeAccessibilityTree);
        Assert.False(options.IncludeHtml);
        // 判据本体（HasContent）必须在契约侧就已包含语义块，而不是靠解码补出来的巧合。
        Assert.True(options.HasContent);
    }

    [Fact]
    public void Decode_BudgetRequestingNoRepresentationAtAllIsRefused()
    {
        var decoded = CoreFrameMapping.Decode(
            SnapshotFrame(new Proto.SnapshotBudget
            {
                IncludeDom = false,
                IncludeAccessibilityTree = false,
                IncludeHtml = false,
                IncludeContentBlocks = false,
            }).Command,
            DesktopId);

        Assert.True(decoded.IsFailure);
    }
}
