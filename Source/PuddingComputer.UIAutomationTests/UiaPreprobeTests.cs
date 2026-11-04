using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingComputer.UIAutomation;

namespace PuddingComputer.UIAutomationTests;

/// <summary>
/// 唤醒通道的最小契约：只在取根之前探一次，探测本身失败不阻断开主流程。
/// 真实唤醒效果属于 IO 行为，由实跑对照证明，不能靠单元测试声称已覆盖。
/// </summary>
[TestClass]
public sealed class UiaPreprobeTests
{
    [TestMethod]
    public void Probe_ZeroHandle_ReturnsZeroWithoutTouchingUiAutomation()
    {
        Assert.AreEqual(0, UiaPreprobe.Probe(IntPtr.Zero));
    }

    [TestMethod]
    public void Wake_InvokesProbeExactlyOnce()
    {
        var calls = 0;
        UiaPreprobe.Wake(new IntPtr(0x1234), _ => { calls++; return 3; });

        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public void Wake_SwallowsProbeFailure()
    {
        // 唤醒失败必须继续走主流程：能否操作由后续匹配与读回判定，不由此处决定。
        UiaPreprobe.Wake(new IntPtr(0x1234), _ => throw new InvalidOperationException("probe boom"));
    }
}
