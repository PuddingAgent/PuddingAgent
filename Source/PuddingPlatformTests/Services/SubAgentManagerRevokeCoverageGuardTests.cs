using System.Runtime.CompilerServices;

namespace PuddingPlatformTests.Services;

/// <summary>
/// 回归守卫（R4）：**每一个"运行收尾"点都必须配对一次浏览器委派撤销**。
/// 背景（真实缺陷）：`ExecuteSyncAsync` 的两条收尾路径曾用 `TryRemove(..., out _)` 把 runId 丢掉
/// ⇒ 运行结束后**委派授权仍然有效**（授权泄漏）。该缺陷在 11/11 反例全绿、2962/2962 套件全绿时
/// **依然存在** —— 因为既有用例只覆盖了"申请/拒绝"，没有覆盖"每条收尾路径都撤销"。
/// 因此这里用**源码不变量**把它钉住：任何 `_runIdMap.TryRemove` 之后必须出现撤销调用。
/// 这条守卫是"把不可能悄悄漏接"的仓库惯例（同 canonical writer/reader 守卫）的延续。
/// </summary>
[TestClass]
public sealed class SubAgentManagerRevokeCoverageGuardTests
{
    private static string SourcePath([CallerFilePath] string callerPath = "") =>
        Path.Combine(
            Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(callerPath)!)!)!,
            "PuddingPlatform", "Services", "SubAgentManager.cs");

    [TestMethod]
    public void EveryTerminalRunRemovalAlsoRevokesTheBrowserDelegation()
    {
        var path = SourcePath();
        Assert.IsTrue(File.Exists(path), "cannot locate SubAgentManager.cs at " + path);

        var lines = File.ReadAllLines(path);
        const string removal = "_runIdMap.TryRemove";
        const string revoke = "RevokeBrowserDelegationAsync";

        var removals = 0;
        var unpaired = new List<int>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains(removal, StringComparison.Ordinal))
            {
                continue;
            }

            removals++;
            var window = string.Join('\n', lines.Skip(i).Take(6));
            if (!window.Contains(revoke, StringComparison.Ordinal))
            {
                unpaired.Add(i + 1);
            }
        }

        Assert.IsTrue(removals > 0, "the guard found no terminal removals; the anchor text changed");
        Assert.AreEqual(
            0,
            unpaired.Count,
            "these terminal removals do not revoke the browser delegation (authorization would leak): "
                + string.Join(", ", unpaired));
    }
}