using System.Text.RegularExpressions;
using PuddingBrowser.Automation;

namespace PuddingBrowser.AutomationTests;

/// <summary>
/// **过期父授权不得成为子代理授权的来源** —— 这条不变式目前由两处配合保证，
/// 而它们**会互相掩盖**（round-115 实测：单独变异任一处，行为用例仍然全绿）：
///
/// <list type="number">
///   <item>`IssueGrant` 先回收过期记录（顺带解决字典增长）；</item>
///   <item>子集判定只接收**回收后仍然有效**的授权（对"顺序被改动"的防御）。</item>
/// </list>
///
/// 行为用例无法区分二者（两条路径的结论相同），所以这里把**接线本身**钉住：
/// 回收必须发生在子集判定**之前**，且子集判定不得直接吃原始字典 `_grants.Values`。
/// </summary>
[TestClass]
public sealed class GrantExpiryWiringTests
{
    [TestMethod]
    public void TheParentScopeCheckRunsAfterReclaimingAndNeverSeesTheRawDictionary()
    {
        var source = AuthoritySource();

        var issueGrant = MethodBody(source, "public BrowserPageGrantEpoch IssueGrant(BrowserPageGrant grant)");
        Assert.IsTrue(issueGrant.Length > 0, "找不到 IssueGrant 方法体");

        var reclaimAt = issueGrant.IndexOf("ReclaimExpiredGrantsUnsafe()", StringComparison.Ordinal);
        var subsetAt = issueGrant.IndexOf("EnsureSubAgentScopeIsSubset(", StringComparison.Ordinal);

        Assert.IsTrue(reclaimAt >= 0, "IssueGrant 必须先回收过期记录（否则过期父授权仍可作来源）");
        Assert.IsTrue(subsetAt >= 0, "IssueGrant 必须做子代理子集判定");
        Assert.IsTrue(
            reclaimAt < subsetAt,
            "回收必须发生在子集判定**之前**：反过来的话，判定会看到尚未回收的过期父授权");

        // 子集判定只能吃"仍然有效"的那一份，不得直接传原始字典。
        var subsetCall = issueGrant[subsetAt..];
        Assert.IsFalse(
            Regex.IsMatch(subsetCall, @"EnsureSubAgentScopeIsSubset\(grant,\s*_grants\.Values", RegexOptions.None),
            "子集判定不得直接接收 _grants.Values（那会放过已过期的父授权）");
    }

    [TestMethod]
    public void ReclaimingReallyRemovesExpiredEntries()
    {
        var source = AuthoritySource();
        var reclaimBody = MethodBody(source, "private IReadOnlyList<BrowserPageGrant> ReclaimExpiredGrantsUnsafe()");
        Assert.IsTrue(reclaimBody.Length > 0, "找不到回收方法");

        // 必须真的按过期判定删除（不是只做只读过滤）。
        StringAssert.Contains(reclaimBody, "IsExpiredAt");
        StringAssert.Contains(reclaimBody, "_grants.Remove(");
        StringAssert.Contains(reclaimBody, "_managementGrants.Remove(");
    }

    private static string AuthoritySource() => File.ReadAllText(Path.Combine(
        RepoRoot(), "Source", "PuddingBrowser.Automation", "BrowserAutomationAuthority.cs"));

    /// <summary>取一个方法的源码片段（从签名到下一个同缩进的成员声明；够用即可）。</summary>
    private static string MethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }

        // 用大括号配平截取方法体，避免依赖下一段的写法。
        var braceStart = source.IndexOf('{', start);
        if (braceStart < 0)
        {
            return string.Empty;
        }

        var depth = 0;
        for (var index = braceStart; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
            }
            else if (source[index] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source[start..(index + 1)];
                }
            }
        }

        return source[start..];
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
