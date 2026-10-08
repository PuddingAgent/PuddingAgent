using Pudding.Contracts.Desktop;
using PuddingBrowser.Abstractions;

namespace PuddingDesktop.Tests.Browser;

/// <summary>
/// 滚动目标线名的**跨层一致**门禁。
///
/// 历史教训：同一个线名集合曾在 Core、驱动与 Desktop 宿主各写一份 switch，加/改目标时要同时改三处，
/// 漏一处就是"同一个目标在两条传输上语义不同"。现在运行侧（<see cref="ScrollTargetWire"/>，驱动与
/// Core 共用）与契约侧（<see cref="DesktopScrollTargetWire"/>，能力通道用）是**两份真源**，
/// 因此必须由测试钉住它们逐项一致；Desktop 宿主仍保留本地 switch（旧 WPF 归档不引用 Contracts），
/// 它的字面量同样在下面的断言范围内。
/// </summary>
public sealed class ScrollTargetVocabularyTests
{
    [Fact]
    public void RuntimeAndContractWireNames_AreIdentical()
    {
        var runtime = Enum.GetValues<ScrollTarget>().Select(ScrollTargetWire.NameOf).ToArray();
        var contract = Enum.GetValues<DesktopScrollTarget>().Select(DesktopScrollTargetWire.NameOf).ToArray();

        Assert.Equal(contract, runtime);

        // 五个目标都不能少（少一个就是"这个目标在某条传输上不可表达"）。
        Assert.Equal(["page", "element", "to_ref", "to_top", "to_bottom"], runtime);
    }

    [Fact]
    public void BothSidesParseTheSameVocabulary_AndRejectUnknownNames()
    {
        foreach (var name in new[] { "page", "element", "to_ref", "to_top", "to_bottom" })
        {
            var runtimeOk = ScrollTargetWire.TryParse(name, out var runtime);
            var contractOk = DesktopScrollTargetWire.TryParse(name, out var contract);

            Assert.True(runtimeOk && contractOk, $"'{name}' 必须两侧都能解析");
            Assert.Equal(DesktopScrollTargetWire.NameOf(contract), ScrollTargetWire.NameOf(runtime));
        }

        // fail closed：未知线名不猜测、不回退。
        Assert.False(ScrollTargetWire.TryParse("wheel", out _));
        Assert.False(DesktopScrollTargetWire.TryParse("wheel", out _));
        Assert.False(ScrollTargetWire.TryParse(null, out _));
        Assert.False(DesktopScrollTargetWire.TryParse(null, out _));

        // 未登记的枚举值必须响亮失败（而不是返回一个默认目标）。
        Assert.Throws<ArgumentOutOfRangeException>(() => ScrollTargetWire.NameOf((ScrollTarget)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => DesktopScrollTargetWire.NameOf((DesktopScrollTarget)99));
    }

    [Fact]
    public void HostSideLocalSwitch_UsesTheSameLiterals()
    {
        // Desktop 宿主的本地 switch 不引用 Contracts，因此这里**直接对源码断言**它的字面量集合：
        // 一旦有人只改了一侧，这条测试会立刻失败（而不是等到线上"滚错了区域"）。
        var path = Path.Combine(
            RepoRoot(), "Source", "PuddingDesktop.WpfArchive", "Browser", "BrowserWorkspaceController.cs");
        Assert.True(File.Exists(path), $"找不到宿主源码：{path}");
        var source = File.ReadAllText(path);

        foreach (var literal in new[] { "\"page\"", "\"element\"", "\"to_ref\"", "\"to_top\"", "\"to_bottom\"" })
        {
            Assert.Contains(literal, source, StringComparison.Ordinal);
        }
    }

    /// <summary>从测试程序集位置向上找仓库根（含 AGENTS.md 的目录）。</summary>
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("repo root (AGENTS.md) not found above the test output directory");
    }
}
