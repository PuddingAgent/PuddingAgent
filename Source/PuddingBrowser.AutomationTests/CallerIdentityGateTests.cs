using System.Text.RegularExpressions;

namespace PuddingBrowser.AutomationTests;

/// <summary>
/// **禁止手写 `CallerId` 比较**（源码级门禁）。
///
/// 为什么需要它：本会话在**四处**各修过一次同一类缺陷 —— 授权查询（`FindPageGrant`）、
/// 租约存活性（`HasWriteCoverage`）、租约自身（`BrowserAutomationLease.Allows`）、以及它们的共同判据。
/// 根因都一样：`CallerId` 只是字符串，"谁的 id 长得一样"被当成了"同一个行动者"。
/// 判据现已收敛为 <c>BrowserCallerIdentity.IsSameActor</c>，但**编译器不会拦**新代码再手写一次字符串比较，
/// 所以这里用源码门禁兜住：判据只允许出现在它自己的定义处。
///
/// 扫描范围：本组件及其相邻契约/表面（`Pudding.Contracts/Desktop`、`PuddingBrowser.Automation`、
/// `Pudding.DesktopSurface.Browser`、`Pudding.DesktopService`）。其它子系统有各自的身份模型，不在此门禁范围。
/// </summary>
[TestClass]
public sealed class CallerIdentityGateTests
{
    /// <summary>允许出现 `CallerId` 比较的**唯一**文件（判据自身的定义处）。</summary>
    private const string IdentityHelperFile = "BrowserGrantContracts.cs";

    private static readonly string[] ScannedDirectories =
    [
        Path.Combine("Source", "Pudding.Contracts", "Desktop"),
        Path.Combine("Source", "PuddingBrowser.Automation"),
        Path.Combine("Source", "Pudding.DesktopSurface.Browser"),
        Path.Combine("Source", "Pudding.DesktopService"),
    ];

    [TestMethod]
    public void NoHandWrittenCallerIdComparisonOutsideTheIdentityHelper()
    {
        var offenders = new List<string>();
        var scanned = 0;

        foreach (var directory in ScannedDirectories)
        {
            var full = Path.Combine(RepoRoot(), directory);
            Assert.IsTrue(Directory.Exists(full), $"扫描目录不存在（门禁范围写错会静默放过）：{directory}");

            foreach (var file in Directory.EnumerateFiles(full, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                scanned++;
                if (string.Equals(Path.GetFileName(file), IdentityHelperFile, StringComparison.Ordinal))
                {
                    continue; // 判据自身就在这里定义
                }

                var lines = File.ReadAllLines(file);
                for (var index = 0; index < lines.Length; index++)
                {
                    if (Regex.IsMatch(lines[index], @"string\.Equals\([^)]*CallerId|CallerId\s*==|==\s*[A-Za-z_.]*CallerId\b"))
                    {
                        offenders.Add($"{Path.GetRelativePath(RepoRoot(), file)}:{index + 1}: {lines[index].Trim()}");
                    }
                }
            }
        }

        Assert.IsTrue(scanned > 20, $"扫描到的文件太少（{scanned}），门禁范围可能写错");
        Assert.IsEmpty(
            offenders,
            "禁止手写 CallerId 比较：`CallerId` 只是字符串，不同行动者可能撞上同一个 id。"
            + "请改用 BrowserCallerIdentity.IsSameActor（页面级授权/租约）或 IsSameTaskScope（任务级管理授权）：\n  - "
            + string.Join("\n  - ", offenders));
    }

    [TestMethod]
    public void TheActorJudgeIsActuallyUsedByTheLeaseAndTheAuthority()
    {
        // 门禁的另一半：判据必须**真的被用**。删掉使用处而改成字符串比较时，上面那条会红；
        // 但如果有人把使用处整段删掉（既不比较也不判定），就得靠这条。
        var lease = File.ReadAllText(Path.Combine(
            RepoRoot(), "Source", "Pudding.Contracts", "Desktop", "BrowserControlContracts.cs"));
        StringAssert.Contains(
            lease,
            "Holder.IsSameActor(request.Caller)",
            "租约的持有者判定必须用 IsSameActor（不得退回字符串比较，也不得省略持有者检查）");

        var authority = File.ReadAllText(Path.Combine(
            RepoRoot(), "Source", "PuddingBrowser.Automation", "BrowserAutomationAuthority.cs"));
        var usages = Regex.Matches(authority, @"\.IsSameActor\(").Count;
        Assert.IsTrue(
            usages >= 2,
            $"权威里应有两处使用同一判据（授权查询 + 租约存活性），实际 {usages} 处");
    }

    private static string RepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PuddingAgentNetwork.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("repository root not found");
    }
}
