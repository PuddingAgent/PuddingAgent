using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// 工具权限重分类回归测试（依据用户 2026-08-27 指示 + 2026-08-27/28 所有者裁定）。
/// 原则：「高风险」仅限可能直接损坏用户数据或泄露用户数据的工具；git 类纯本地/追加型
/// 操作不应触发审批门。机制链：ToolPermissionPolicyService.RequiresRuntimeAuthorization
/// = High || RequiresShell || RequiresFileWrite || Destructive；Low 且无敏感位 ⇒ AutoAllowed 免审。
/// </summary>
[TestClass]
public sealed class GitToolPermissionTests
{
    private static readonly ToolPermissionPolicyService s_policy = new();

    [TestMethod]
    public void All_Git_Tools_Except_Reset_Are_AutoAllowed()
    {
        // 20 个 git 工具中 19 个应为 AutoAllowed（免运行时授权），仅 git_reset 保持 High。
        IPuddingTool[] tools =
        [
            new GitAddTool(),
            new GitBlameTool(),
            new GitBranchCreateTool(),
            new GitBranchListTool(),
            new GitBranchSwitchTool(),
            new GitCheckoutTool(),
            new GitCloneTool(),
            new GitCommitTool(),
            new GitDiffTool(),
            new GitFetchTool(),
            new GitInitTool(),
            new GitLogTool(),
            new GitMergeTool(),
            new GitPullTool(),
            new GitPushTool(),
            new GitRemoteTool(),
            new GitStashTool(),
            new GitStatusTool(),
            new GitTagTool(),
        ];

        foreach (var tool in tools)
        {
            var descriptor = tool.Descriptor;
            var decision = s_policy.Classify(descriptor);

            Assert.AreEqual(ToolPermissionTier.AutoAllowed, decision.Tier, descriptor.ToolId);
            Assert.IsFalse(decision.RequiresRuntimeAuthorization, descriptor.ToolId);
            Assert.AreEqual(ToolPermissionLevel.Low, descriptor.PermissionLevel, descriptor.ToolId);
            Assert.IsFalse(descriptor.Safety.HasFlag(ToolSafetyFlags.Destructive), descriptor.ToolId);
            Assert.IsFalse(descriptor.Safety.HasFlag(ToolSafetyFlags.RequiresShell), descriptor.ToolId);
            Assert.IsFalse(descriptor.Safety.HasFlag(ToolSafetyFlags.RequiresFileWrite), descriptor.ToolId);
        }
    }

    [TestMethod]
    public void GitReset_Remains_High_Destructive_Requires_Runtime_Authorization()
    {
        // git_reset 是唯一可永久丢弃工作区未提交改动（--hard）的 git 工具，保持 High+Destructive。
        var descriptor = new GitResetTool().Descriptor;
        var decision = s_policy.Classify(descriptor);

        Assert.AreEqual(ToolPermissionLevel.High, descriptor.PermissionLevel);
        Assert.IsTrue(descriptor.Safety.HasFlag(ToolSafetyFlags.Destructive));
        Assert.IsTrue(decision.RequiresRuntimeAuthorization);
        Assert.AreEqual(ToolPermissionTier.RuntimeGranted, decision.Tier);
    }

    [TestMethod]
    public void GitClone_And_GitFetch_RequireNetwork_But_Do_Not_Trigger_Gate()
    {
        // RequiresNetwork 现行不参与门禁判定（仅标记），网络类工具风险靠实现级防护兜底。
        foreach (var tool in new IPuddingTool[] { new GitCloneTool(), new GitFetchTool(), new GitPullTool(), new GitPushTool() })
        {
            var descriptor = tool.Descriptor;
            Assert.IsTrue(descriptor.Safety.HasFlag(ToolSafetyFlags.RequiresNetwork), descriptor.ToolId);
            Assert.IsFalse(s_policy.RequiresRuntimeAuthorization(descriptor), descriptor.ToolId);
            Assert.AreEqual(ToolPermissionTier.AutoAllowed, s_policy.Classify(descriptor).Tier, descriptor.ToolId);
        }
    }

    [TestMethod]
    public void SmartTestTool_Is_AutoAllowed_After_Reclassification()
    {
        // 2026-08-28 裁定：smart_test 无文件写/删路径（仅委托 Tester 子代理跑测试+读码），
        // 由 High+None 降为 Low+ConcurrencySafe ⇒ AutoAllowed 免审直通。
        var tool = new SmartTestTool(
            new ServiceCollection().BuildServiceProvider(),
            NullLogger<SmartTestTool>.Instance);
        var descriptor = tool.Descriptor;
        var decision = s_policy.Classify(descriptor);

        Assert.AreEqual(ToolPermissionLevel.Low, descriptor.PermissionLevel);
        Assert.IsTrue(descriptor.Safety.HasFlag(ToolSafetyFlags.ConcurrencySafe));
        Assert.IsFalse(decision.RequiresRuntimeAuthorization);
        Assert.AreEqual(ToolPermissionTier.AutoAllowed, decision.Tier);
    }

    [TestMethod]
    public void High_Risk_Builtin_Tools_Remain_Gated()
    {
        // 直接损坏/执行任意命令路径的工具必须保持运行时授权门禁：
        // shell（任意命令）、terminal_start（启动任意进程）、file_write/file_patch（直接改文件）。
        IPuddingTool[] gated =
        [
            new HostShellTool(
                PuddingDataPaths.FromRoot(Path.Combine(Path.GetTempPath(), "pudding-git-perm-tests")),
                new AuditLogger(PuddingDataPaths.FromRoot(Path.Combine(Path.GetTempPath(), "pudding-git-perm-tests"))),
                NullLogger<HostShellTool>.Instance),
            new FileWriteTool(NullLogger<FileWriteTool>.Instance),
            new FilePatchTool(NullLogger<FilePatchTool>.Instance),
        ];

        foreach (var tool in gated)
            Assert.IsTrue(s_policy.RequiresRuntimeAuthorization(tool.Descriptor), tool.Descriptor.ToolId);
    }
}
