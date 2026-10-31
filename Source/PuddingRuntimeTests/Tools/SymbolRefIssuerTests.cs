using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCodeIndex.Contracts;
using PuddingRetrieval.Contracts;
using PuddingRetrieval;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// <see cref="SymbolRefIssuer"/> 的契约测试。
/// <para>
/// 这些用例的目标不是覆盖率，而是锁住三条要害：
/// ① hash 必须来自 manifest（索引时），不是磁盘；
/// ② 证据缺失时**不签发**，且不抛异常打断搜索；
/// ③ 相对/绝对两种命中形态都能命中，缺项目根时**不猜**。
/// </para>
/// </summary>
[TestClass]
public sealed class SymbolRefIssuerTests
{
    private const string ProjectRoot = @"D:\proj\alpha";
    private const string AbsoluteSourcePath = @"D:\proj\alpha\src\a.cs";

    private static CodeSourceEntry Entry(string path, string? contentHash, bool complete = true) =>
        new(
            path,
            contentHash is null
                ? null
                : new SourceFingerprint(DateTimeOffset.UnixEpoch, 12, contentHash),
            Array.Empty<AppliedFileVersion>(),
            complete);

    private static Dictionary<string, CodeSourceEntry> Manifest(params CodeSourceEntry[] entries)
    {
        var map = new Dictionary<string, CodeSourceEntry>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            map[entry.FilePath] = entry;
        }

        return map;
    }

    [TestMethod]
    public void Issue_WithManifestHash_ProducesParseableToken()
    {
        var issuer = SymbolRefIssuer.FromManifest(Manifest(Entry(AbsoluteSourcePath, "hash-1")), ProjectRoot);

        var issue = issuer.Issue(
            "proj-1",
            "roslyn-v1",
            new SymbolRefIssueRequest(AbsoluteSourcePath, "M:Ns.Type.Run", 2, 3));

        Assert.IsTrue(issue.Issued, issue.SkipReason);
        Assert.IsNull(issue.SkipReason);

        var parsed = SymbolRefV1.Parse(issue.Token!);
        Assert.AreEqual("proj-1", parsed.ProjectId);
        Assert.AreEqual("hash-1", parsed.ContentHash);
        Assert.AreEqual("roslyn-v1", parsed.ProviderGeneration);
        Assert.AreEqual(2, parsed.StartLine);
        Assert.AreEqual(3, parsed.EndLine);
    }

    /// <summary>
    /// 要害用例：manifest 里有记录但**磁盘上根本没有这个文件**，签发仍然成功。
    /// 若实现改成读磁盘取 hash，这条会红 —— 而生产上的表现是「索引落后于磁盘时签出假 Fresh ref」。
    /// </summary>
    [TestMethod]
    public void Issue_UsesManifestHashNotDisk()
    {
        const string GhostPath = @"D:\proj\alpha\src\ghost-does-not-exist.cs";
        Assert.IsFalse(File.Exists(GhostPath), "前提：该路径在磁盘上不存在，否则本用例失去意义。");

        var issuer = SymbolRefIssuer.FromManifest(Manifest(Entry(GhostPath, "hash-ghost")), ProjectRoot);

        var issue = issuer.Issue(
            "proj-1",
            "roslyn-v1",
            new SymbolRefIssueRequest(GhostPath, "T:Ns.Type", 1, 1));

        Assert.IsTrue(issue.Issued, issue.SkipReason);
        Assert.AreEqual("hash-ghost", SymbolRefV1.Parse(issue.Token!).ContentHash);
    }

    [TestMethod]
    public void Issue_RelativeHit_MatchesAbsoluteManifestEntry()
    {
        var issuer = SymbolRefIssuer.FromManifest(Manifest(Entry(AbsoluteSourcePath, "hash-1")), ProjectRoot);

        var issue = issuer.Issue(
            "proj-1",
            "roslyn-v1",
            new SymbolRefIssueRequest(@"src\a.cs", "M:Ns.Type.Run", 2, 3));

        Assert.IsTrue(issue.Issued, issue.SkipReason);
        Assert.AreEqual("hash-1", SymbolRefV1.Parse(issue.Token!).ContentHash);
    }

    /// <summary>
    /// 没有项目根就拼不出绝对形态 ⇒ 相对命中无法证明与 manifest 记录一致 ⇒ 必须拒绝签发。
    /// 这里锁的是「不猜」：猜错的后果是签出一条被判 Stale 的 ref，而日志里没有错误。
    /// </summary>
    [TestMethod]
    public void Issue_RelativeHit_WithoutProjectRoot_IsSkipped()
    {
        var issuer = SymbolRefIssuer.FromManifest(Manifest(Entry(AbsoluteSourcePath, "hash-1")), projectRoot: null);

        var issue = issuer.Issue(
            "proj-1",
            "roslyn-v1",
            new SymbolRefIssueRequest("src/a.cs", "M:Ns.Type.Run", 2, 3));

        Assert.IsFalse(issue.Issued);
        Assert.IsNull(issue.Token);
        StringAssert.Contains(issue.SkipReason, "manifest");
    }

    [TestMethod]
    public void Issue_MissingManifestEntry_IsSkipped()
    {
        var issuer = SymbolRefIssuer.FromManifest(Manifest(Entry(AbsoluteSourcePath, "hash-1")), ProjectRoot);

        var issue = issuer.Issue(
            "proj-1",
            "roslyn-v1",
            new SymbolRefIssueRequest(@"D:\proj\alpha\src\b.cs", "M:Ns.Type.Run", 2, 3));

        Assert.IsFalse(issue.Issued);
        Assert.IsNull(issue.Token);
        Assert.IsNotNull(issue.SkipReason);
    }

    [TestMethod]
    public void FromManifest_IgnoresEntryWithoutFingerprint()
    {
        var issuer = SymbolRefIssuer.FromManifest(Manifest(Entry(AbsoluteSourcePath, contentHash: null)), ProjectRoot);

        Assert.AreEqual(0, issuer.KnownPathCount);
        Assert.IsFalse(
            issuer.Issue("proj-1", "roslyn-v1", new SymbolRefIssueRequest(AbsoluteSourcePath, "T:Ns.Type", 1, 1)).Issued);
    }

    [TestMethod]
    public void FromManifest_IgnoresIncompleteEntry()
    {
        var issuer = SymbolRefIssuer.FromManifest(
            Manifest(Entry(AbsoluteSourcePath, "hash-1", complete: false)),
            ProjectRoot);

        Assert.AreEqual(0, issuer.KnownPathCount);
    }

    /// <summary>
    /// 非法行号必须**拒绝签发**而不是抛异常：签发发生在搜索路径上，
    /// 一条脏命中不该把整次搜索打挂（与解析端共用同一套不变量）。
    /// </summary>
    [TestMethod]
    public void Issue_InvalidLineRange_IsSkippedNotThrown()
    {
        var issuer = SymbolRefIssuer.FromManifest(Manifest(Entry(AbsoluteSourcePath, "hash-1")), ProjectRoot);

        var issue = issuer.Issue(
            "proj-1",
            "roslyn-v1",
            new SymbolRefIssueRequest(AbsoluteSourcePath, "M:Ns.Type.Run", 0, 3));

        Assert.IsFalse(issue.Issued);
        Assert.IsNull(issue.Token);
        StringAssert.Contains(issue.SkipReason, "start_line");
    }
}
