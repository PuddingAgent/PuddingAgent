using PuddingCode.Classification;
using PuddingCode.Tools;
using PuddingRuntime.Classification;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Classification;

/// <summary>
/// <see cref="ClassificationRuleCurator"/> 的离线单测（方案 v2 §14.12）。
/// <para>
/// 覆盖映射：§14.12.1 规则键规范化与禁通配（用例 01–03/16）、§14.12.3 幂等（13）、
/// §14.12.4 冲突 deny 胜 + 审计（17/18）、§14.12.5 尽窄 6 条各一例（04–10/15）、
/// §14.12.6 溯源字段（11）、§14.12.7 撤销与建议有效期（20/21）、单次类不落规则（14）、
/// store 异常 fail-closed 不冒泡（19）。全部用例零网络、注入固定时钟。
/// </para>
/// </summary>
[TestClass]
public sealed class ClassificationRuleCuratorTests
{
    private const string ClassifierId = "test-classifier";
    private const string TestWorkspace = "ws-test";
    private static readonly DateTimeOffset T0 = new(2026, 9, 20, 8, 0, 0, TimeSpan.Zero);

    // —— 01 §14.12.1：命令 subject 规范化（去首尾空白 / 折叠连续空白 / 分隔符统一） ——

    [TestMethod]
    public void NormalizeSubject_Command_TrimsCollapsesAndUnifiesSeparators()
    {
        Assert.AreEqual(
            "git status --short",
            ClassificationRuleCurator.NormalizeSubject("  git \t status  \t --short  ", null),
            "连续空白必须折叠为单空格，且去首尾空白。");

        Assert.AreEqual(
            "dir C:/repo/a.txt",
            ClassificationRuleCurator.NormalizeSubject("dir C:\\repo\\a.txt", null),
            "路径分隔符必须统一为 '/'。");

        // 规范化必须可复现（幂等键的前提）：同一逻辑命令重复规范化结果逐字节相等。
        Assert.AreEqual(
            ClassificationRuleCurator.NormalizeSubject("git   commit -m \"x\"", null),
            ClassificationRuleCurator.NormalizeSubject(" git commit -m \"x\" ", null));
    }

    // —— 02 §14.12.1：非命令工具走 arguments_hash（键排序、忽略空白 ⇒ 同一参数同一哈希） ——

    [TestMethod]
    public void NormalizeSubject_NonCommand_ProducesStableHash_IgnoringKeyOrderAndWhitespace()
    {
        var a = ClassificationRuleCurator.NormalizeSubject(null, "{\"b\": 2, \"a\": 1}");
        var b = ClassificationRuleCurator.NormalizeSubject(null, "{\n  \"a\" : 1,\n  \"b\":2\n}");

        Assert.IsTrue(a.StartsWith("args_sha256:", StringComparison.Ordinal), "非命令 subject 必须是 args_sha256: 前缀。");
        Assert.AreEqual("args_sha256:".Length + 64, a.Length, "SHA-256 十六进制应为 64 位小写。");
        Assert.AreEqual(a, b, "键序与空白差异不得改变 subject（规范化 JSON）。");

        var different = ClassificationRuleCurator.NormalizeSubject(null, "{\"a\": 2, \"b\": 1}");
        Assert.AreNotEqual(a, different, "不同参数值必须产生不同哈希。");
    }

    // —— 03 §14.12.1：RuleKey 五元组组装 ——

    [TestMethod]
    public void BuildKey_ComposesWorkspaceToolSubjectDirectoryShell()
    {
        var commandCtx = ToolContext(command: "git status", workingDirectory: "E:\\repo\\", shell: " pwsh ");
        var key = ClassificationRuleCurator.BuildKey(commandCtx);

        Assert.AreEqual(TestWorkspace, key.WorkspaceId);
        Assert.AreEqual("terminal_execute", key.ToolId);
        Assert.AreEqual("git status", key.Subject);
        Assert.AreEqual("E:/repo", key.WorkingDirectory);
        Assert.AreEqual("pwsh", key.Shell);

        var argsCtx = ToolContext(command: null, argumentsJson: "{\"path\":\"a.txt\"}");
        var argsKey = ClassificationRuleCurator.BuildKey(argsCtx);
        Assert.IsTrue(
            argsKey.Subject.StartsWith("args_sha256:", StringComparison.Ordinal),
            "非命令工具的键分量必须是参数哈希。");
    }

    // —— 04 §14.12.5-1：shell 元字符（含换行回归：规范化会把换行折叠为空格，检查必须看原始命令） ——

    [TestMethod]
    public void CheckNarrowness_ShellMetacharacters_Hit_Item1()
    {
        AssertViolation(
            ToolContext(command: "Get-Content a.txt | Remove-Item -Force"),
            NarrownessViolation.ShellMetacharacter,
            "管道符必须命中第 1 条。");

        AssertViolation(
            ToolContext(command: "run.cmd a & run.cmd b"),
            NarrownessViolation.ShellMetacharacter,
            "& 串联必须命中第 1 条。");

        AssertViolation(
            ToolContext(command: "echo $env:HOME"),
            NarrownessViolation.ShellMetacharacter,
            "$ 变量引用必须命中第 1 条。");

        AssertViolation(
            ToolContext(command: "git status\nrm -rf /tmp/x"),
            NarrownessViolation.ShellMetacharacter,
            "换行必须命中第 1 条（不能因 subject 规范化折叠而漏检）。");
    }

    // —— 05 §14.12.5-2：通配/正则元字符 ——

    [TestMethod]
    public void CheckNarrowness_Wildcards_Hit_Item2()
    {
        AssertViolation(
            ToolContext(command: "del C:/temp/*.log"),
            NarrownessViolation.WildcardOrRegex,
            "'*' 通配必须命中第 2 条。");

        AssertViolation(
            ToolContext(command: "tool config set key ?value"),
            NarrownessViolation.WildcardOrRegex,
            "'?' 通配必须命中第 2 条。");

        AssertViolation(
            ToolContext(command: "grep [a-z]y file.txt"),
            NarrownessViolation.WildcardOrRegex,
            "正则字符类必须命中第 2 条。");
    }

    // —— 06 §14.12.5-3：裸解释器及其 -c/-e/-Command 形式（含负例：带脚本文件的解释器不命中） ——

    [TestMethod]
    public void CheckNarrowness_BareInterpreters_Hit_Item3_With_NonPrefix_Negative()
    {
        AssertViolation(ToolContext(command: "bash"), NarrownessViolation.BareInterpreter, "裸 bash 必须命中第 3 条。");
        AssertViolation(
            ToolContext(command: "python -c pass"),
            NarrownessViolation.BareInterpreter,
            "python -c 内联脚本必须命中第 3 条（用例刻意不含 shell 元字符，避免被第 1 条先命中）。");
        AssertViolation(
            ToolContext(command: "pwsh -Command Get-Process"),
            NarrownessViolation.BareInterpreter,
            "pwsh -Command 内联命令必须命中第 3 条。");
        AssertViolation(
            ToolContext(command: "C:/Windows/System32/WindowsPowerShell/v1.0/powershell.exe -Command gci"),
            NarrownessViolation.BareInterpreter,
            "带路径与 .exe 后缀的解释器同样命中第 3 条。");

        AssertPassed(
            ToolContext(command: "python script.py"),
            "解释器带具体脚本文件不属于规格列举的裸解释器/内联形式（不得自行放宽）。");
        AssertPassed(
            ToolContext(command: "bashful status"),
            "首 token 仅做整词比较，'bashful' 不是 bash（禁通配/前缀语义同样约束校验器本身）。");
    }

    // —— 07 §14.12.5-4：参数中不可静态求值的占位符（非命令工具 ArgumentsJson） ——

    [TestMethod]
    public void CheckNarrowness_NonStaticPlaceholders_Hit_Item4()
    {
        AssertViolation(
            ToolContext(command: null, argumentsJson: "{\"path\":\"${WORKSPACE}/a.txt\"}"),
            NarrownessViolation.NonStaticPlaceholder,
            "${...} 占位符必须命中第 4 条。");
        AssertViolation(
            ToolContext(command: null, argumentsJson: "{\"port\":\"%APP_PORT%\"}"),
            NarrownessViolation.NonStaticPlaceholder,
            "%VAR% 占位符必须命中第 4 条。");
        AssertViolation(
            ToolContext(command: null, argumentsJson: "{\"cmd\":\"$env:CONFIG\"}"),
            NarrownessViolation.NonStaticPlaceholder,
            "$env: 引用必须命中第 4 条。");
        AssertViolation(
            ToolContext(command: null, argumentsJson: "{\"tpl\":\"{{user}}\"}"),
            NarrownessViolation.NonStaticPlaceholder,
            "{{...}} 模板占位符必须命中第 4 条。");

        AssertPassed(
            ToolContext(command: null, argumentsJson: "{\"path\":\"E:/repo/a.txt\"}"),
            "完全静态的字面参数必须通过。");
    }

    // —— 08 §14.12.5-5：目标路径越出 workspace 根 ——

    [TestMethod]
    public void CheckNarrowness_PathOutsideWorkspace_Hit_Item5()
    {
        AssertViolation(
            ToolContext(command: null, argumentsJson: null, workingDirectory: "E:/repo", targets: ["E:/other/secret.txt"]),
            NarrownessViolation.PathOutsideWorkspace,
            "绝对路径越根必须命中第 5 条。");
        AssertViolation(
            ToolContext(command: null, argumentsJson: null, workingDirectory: "E:/repo", targets: ["../secrets.txt"]),
            NarrownessViolation.PathOutsideWorkspace,
            "相对 .. 越根必须命中第 5 条。");
        AssertViolation(
            ToolContext(command: null, argumentsJson: null, workingDirectory: "E:/repo", targets: ["sub/../../outside.txt"]),
            NarrownessViolation.PathOutsideWorkspace,
            ".. 回溯抵消后再越根必须命中第 5 条。");

        AssertPassed(
            ToolContext(command: null, argumentsJson: null, workingDirectory: "E:/Repo", targets: ["e:/repo/src/a.txt"]),
            "盘符/目录大小写差异（Windows 语义）不构成越根。");
        AssertPassed(
            ToolContext(command: null, argumentsJson: null, workingDirectory: "E:/repo", targets: ["E:/repo/build/b.txt"]),
            "根内路径必须通过。");
    }

    // —— 09 §14.12.5-6：不可逆且无备份证据 ——

    [TestMethod]
    public void CheckNarrowness_IrreversibleWithoutBackup_Hit_Item6()
    {
        AssertViolation(
            ToolContext(irreversible: true),
            NarrownessViolation.IrreversibleWithoutBackup,
            "IsIrreversibleOperation=true 且上下文无备份证据（S1 契约无 BackupTaken 字段）必须命中第 6 条。");
    }

    // —— 10 尽窄通过样例 + 条目顺序（首个命中优先） ——

    [TestMethod]
    public void CheckNarrowness_CleanCommand_Passes_And_FirstHitWins()
    {
        var check = ClassificationRuleCurator.CheckNarrowness(ToolContext(command: "git status --short"));
        Assert.IsTrue(check.Passed, "干净命令必须通过全部 6 条。");

        var combined = ClassificationRuleCurator.CheckNarrowness(ToolContext(command: "echo hi | grep *"));
        Assert.AreEqual(
            NarrownessViolation.ShellMetacharacter,
            combined.Violation,
            "同时命中多条时按规格编号返回第 1 条。");
    }

    // —— 11/12 §14.12.6：永久规则落库 + 溯源字段 ——

    [TestMethod]
    public async Task CurateAsync_AllowPermanent_CreatesRule_WithProvenanceFields()
    {
        var (curator, allowlist, audit, clock) = CreateCurator();

        var outcome = await curator.CurateAsync(
            Verdict(ClassificationOutcome.AllowPermanent, confidence: 0.97),
            ToolContext(),
            CancellationToken.None);

        Assert.IsTrue(outcome.Applied, "AllowPermanent 必须落规则。");
        Assert.IsFalse(outcome.Degraded, "干净输入不得降级。");
        Assert.IsFalse(outcome.ConflictDetected);
        Assert.IsNotNull(outcome.RuleId);

        var rules = await CuratorRulesAsync(allowlist);
        Assert.AreEqual(1, rules.Count, "首次策展恰好落一行。");
        var rule = rules[0];
        Assert.AreEqual(outcome.RuleId, rule.RuleId);
        Assert.AreEqual(TestWorkspace, rule.WorkspaceId);
        Assert.AreEqual("terminal_execute", rule.ToolId);
        Assert.AreEqual("git status", rule.Command, "命令类规则的 Command 必须是规范化 subject。");
        Assert.IsNull(rule.ArgumentsJson, "命令类规则不携带 ArgumentsJson。");
        Assert.AreEqual(ToolApprovalRuleEffect.Allow, rule.Effect);
        Assert.AreEqual(ToolApprovalAllowlistRuleStatus.Enabled, rule.Status);
        Assert.AreEqual(ToolApprovalAllowlistRuleSource.AuditAgent, rule.Source);
        Assert.AreEqual(ClassifierId, rule.SourceClassifierId, "§14.12.6：SourceClassifierId 必写。");
        Assert.AreEqual("test-model", rule.ClassifierModel, "§14.12.6：ClassifierModel 必写。");
        Assert.AreEqual(0.97, rule.OutcomeConfidence, "§14.12.6：OutcomeConfidence 必写。");
        Assert.AreEqual(T0, rule.FirstSeenAtUtc);
        Assert.AreEqual(T0, rule.LastSeenAtUtc);
        Assert.AreEqual(1L, rule.HitCount, "创建即一次命中。");
        Assert.AreEqual("E:/repo", rule.WorkingDirectory, "键分量 working_directory 必须落库。");
        Assert.AreEqual("pwsh", rule.Shell, "键分量 shell 必须落库。");
        Assert.AreEqual("agent-1", rule.ApprovedByAgentInstanceId);
        Assert.AreEqual("sess-1", rule.CreatedBySessionId);
        Assert.AreEqual("user-1", rule.ApprovedByUserId);
        Assert.IsNull(rule.ExpiresAtUtc, "置信度 0.97 ≥ 0.95，不建议有效期。");

        var events = await audit.ListAsync();
        Assert.AreEqual(1, events.Count);
        Assert.AreEqual(ToolApprovalAuditEventType.AllowlistRuleCreated, events[0].EventType);
        Assert.AreEqual(rule.RuleId, events[0].AllowlistRuleId);
    }

    [TestMethod]
    public async Task CurateAsync_DenyPermanent_CreatesDenyRule_WithDenylistAudit()
    {
        var (curator, allowlist, audit, _) = CreateCurator();

        var outcome = await curator.CurateAsync(
            Verdict(ClassificationOutcome.DenyPermanent, confidence: 0.98),
            ToolContext(command: "drop-database --name prod"),
            CancellationToken.None);

        Assert.IsTrue(outcome.Applied);
        var rules = await CuratorRulesAsync(allowlist);
        Assert.AreEqual(1, rules.Count);
        Assert.AreEqual(ToolApprovalRuleEffect.Deny, rules[0].Effect, "DenyPermanent 必须落 deny 规则。");

        var events = await audit.ListAsync();
        Assert.AreEqual(ToolApprovalAuditEventType.DenylistRuleCreated, events[0].EventType);
    }

    // —— 13 §14.12.3：幂等——同键同 Effect 更新既有行，不新增，HitCount 递增 ——

    [TestMethod]
    public async Task CurateAsync_SameKeySameEffect_UpdatesExistingRow_WithoutNewRow()
    {
        var (curator, allowlist, audit, clock) = CreateCurator();
        var ctx = ToolContext();

        var first = await curator.CurateAsync(Verdict(ClassificationOutcome.AllowPermanent, 0.9), ctx);
        clock.Advance(TimeSpan.FromMinutes(5));
        var second = await curator.CurateAsync(
            Verdict(ClassificationOutcome.AllowPermanent, 0.93, reason: "复核后的理由"), ctx);

        Assert.IsTrue(second.Applied);
        Assert.AreEqual(first.RuleId, second.RuleId, "同键同 Effect 必须复用同一行。");

        var rules = await CuratorRulesAsync(allowlist);
        Assert.AreEqual(1, rules.Count, "幂等更新不得新增行。");
        var rule = rules[0];
        Assert.AreEqual(2L, rule.HitCount, "每次同键策展 HitCount 必须累加。");
        Assert.AreEqual(T0, rule.FirstSeenAtUtc, "FirstSeenAtUtc 在更新时保持不变。");
        Assert.AreEqual(T0 + TimeSpan.FromMinutes(5), rule.LastSeenAtUtc, "LastSeenAtUtc 刷新为最新时间。");
        Assert.AreEqual(0.93, rule.OutcomeConfidence, "Confidence 刷新。");
        Assert.AreEqual("复核后的理由", rule.Reason, "Reason 刷新。");

        var events = await audit.ListAsync();
        Assert.AreEqual(1, events.Count(e => e.EventType == ToolApprovalAuditEventType.AllowlistRuleUpdated),
            "首策为 Created，本次更新恰好一条 Updated。");
    }

    // —— 14 单次类（AllowOnce/DenyOnce）与 Unknown 不沉淀，且不是降级 ——

    [TestMethod]
    public async Task CurateAsync_SingleShotAndUnknownOutcomes_DoNotCurate()
    {
        var (curator, allowlist, audit, _) = CreateCurator();

        foreach (var outcome in new[]
                 {
                     ClassificationOutcome.AllowOnce,
                     ClassificationOutcome.DenyOnce,
                     ClassificationOutcome.Unknown,
                 })
        {
            var result = await curator.CurateAsync(Verdict(outcome, 0.99), ToolContext());
            Assert.IsFalse(result.Applied, $"{outcome} 不得落规则。");
            Assert.IsFalse(result.Degraded, "单次类不是降级（仅尽窄/异常才算降级）。");
        }

        Assert.AreEqual(0, (await CuratorRulesAsync(allowlist)).Count, "未沉淀任何规则。");
        Assert.AreEqual(0, (await audit.ListAsync()).Count, "未产生任何规则审计。");
    }

    // —— 15 §14.12.5：策展级降级——命中尽窄条目 ⇒ 绝不落规则、不落规则审计 ——

    [TestMethod]
    public async Task CurateAsync_NarrownessViolation_Degrades_WithoutWritingRuleOrAudit()
    {
        var (curator, allowlist, audit, _) = CreateCurator();

        var piped = await curator.CurateAsync(
            Verdict(ClassificationOutcome.AllowPermanent, 0.99),
            ToolContext(command: "Get-Content a.txt | Remove-Item -Force"));
        Assert.IsFalse(piped.Applied);
        Assert.IsTrue(piped.Degraded);
        Assert.IsTrue(
            piped.DegradeReason!.StartsWith("narrow_1_shell_metacharacter", StringComparison.Ordinal),
            $"降级原因必须指明具体条目，实际：{piped.DegradeReason}");

        var wildcard = await curator.CurateAsync(
            Verdict(ClassificationOutcome.DenyPermanent, 0.99),
            ToolContext(command: "del *.log"));
        Assert.IsTrue(wildcard.Degraded);
        Assert.IsTrue(wildcard.DegradeReason!.StartsWith("narrow_2_wildcard_or_regex", StringComparison.Ordinal));

        Assert.AreEqual(0, (await CuratorRulesAsync(allowlist)).Count, "降级绝不落规则。");
        Assert.AreEqual(0, (await audit.ListAsync()).Count, "降级不产生规则审计。");
    }

    // —— 16 §14.12.1 禁通配：含通配的输入不得命中（更不得更新）既有逐字节规则 ——

    [TestMethod]
    public async Task CurateAsync_WildcardVariant_NeverTouchesExistingByteEqualRule()
    {
        var (curator, allowlist, _, _) = CreateCurator();

        var created = await curator.CurateAsync(Verdict(ClassificationOutcome.AllowPermanent, 0.99), ToolContext());
        Assert.IsTrue(created.Applied);

        var exact = ClassificationRuleCurator.BuildKey(ToolContext());
        var wildcard = ClassificationRuleCurator.BuildKey(ToolContext(command: "git status *.log"));
        Assert.AreNotEqual(exact, wildcard, "通配变体的键必须不同（无前缀/通配语义）。");

        var result = await curator.CurateAsync(
            Verdict(ClassificationOutcome.AllowPermanent, 0.99),
            ToolContext(command: "git status *.log"));

        Assert.IsFalse(result.Applied, "通配输入必须被尽窄第 2 条拒绝。");
        Assert.IsTrue(result.Degraded);

        var rules = await CuratorRulesAsync(allowlist);
        Assert.AreEqual(1, rules.Count, "不得新增任何行。");
        Assert.AreEqual(1L, rules[0].HitCount, "既有逐字节规则不得被通配输入命中（HitCount 不变）。");
    }

    // —— 17/18 §14.12.4：同键相反 Effect ⇒ 冲突信号 + RuleConflictDetected 审计（两侧 rule_id 与来源） ——

    [TestMethod]
    public async Task CurateAsync_ConflictingEffect_DenyWins_SignalAndAudit()
    {
        var (curator, allowlist, audit, _) = CreateCurator();
        var ctx = ToolContext();

        var allowOutcome = await curator.CurateAsync(Verdict(ClassificationOutcome.AllowPermanent, 0.99), ctx);
        var denyOutcome = await curator.CurateAsync(Verdict(ClassificationOutcome.DenyPermanent, 0.99), ctx);

        Assert.IsTrue(denyOutcome.Applied, "deny 规则照常落库（记录完整对峙双方）。");
        Assert.IsTrue(denyOutcome.ConflictDetected, "必须回报冲突信号。");
        Assert.AreEqual(allowOutcome.RuleId, denyOutcome.ConflictingRuleId);

        var rules = await CuratorRulesAsync(allowlist);
        Assert.AreEqual(2, rules.Count, "allow 与 deny 各一行。");

        var conflict = (await audit.ListAsync()).Single(e => e.EventType == ToolApprovalAuditEventType.RuleConflictDetected);
        Assert.AreEqual(allowOutcome.RuleId, conflict.AllowlistRuleId, "事件主体为既有（先落）规则。");
        StringAssert.Contains(conflict.Reason!, allowOutcome.RuleId, "审计须含既有规则 id。");
        StringAssert.Contains(conflict.Reason!, denyOutcome.RuleId, "审计须含新落规则 id。");
        StringAssert.Contains(conflict.Reason, "AuditAgent", "审计须含来源。");
        Assert.AreEqual(ToolApprovalRuleEffect.Deny, conflict.Effect, "冲突生效侧为 deny（§14.12.4）。");
    }

    [TestMethod]
    public async Task CurateAsync_ConflictingEffect_ReverseOrder_AlsoDetected()
    {
        var (curator, allowlist, audit, _) = CreateCurator();
        var ctx = ToolContext();

        var denyOutcome = await curator.CurateAsync(Verdict(ClassificationOutcome.DenyPermanent, 0.99), ctx);
        var allowOutcome = await curator.CurateAsync(Verdict(ClassificationOutcome.AllowPermanent, 0.99), ctx);

        Assert.IsTrue(allowOutcome.ConflictDetected, "后落的 allow 同样触发冲突。");
        Assert.AreEqual(denyOutcome.RuleId, allowOutcome.ConflictingRuleId);
        Assert.IsTrue(
            (await audit.ListAsync()).Any(e => e.EventType == ToolApprovalAuditEventType.RuleConflictDetected));
    }

    // —— 19 store 异常 ⇒ 不冒泡、fail-closed、绝不伪造成功 ——

    [TestMethod]
    public async Task CurateAsync_StoreThrowing_FailsClosed_WithoutBubbling()
    {
        var listThrowing = new ThrowingAllowlistStore(throwOnList: true);
        var curator1 = new ClassificationRuleCurator(listThrowing, new InMemoryToolApprovalAuditStore(), new FixedClock());
        var r1 = await curator1.CurateAsync(Verdict(ClassificationOutcome.AllowPermanent, 0.99), ToolContext());
        Assert.IsFalse(r1.Applied, "store 异常绝不伪造成功。");
        Assert.IsTrue(r1.Degraded);
        Assert.IsTrue(r1.DegradeReason!.StartsWith("store_error:", StringComparison.Ordinal));

        var saveThrowing = new ThrowingAllowlistStore(throwOnList: false);
        var curator2 = new ClassificationRuleCurator(saveThrowing, new InMemoryToolApprovalAuditStore(), new FixedClock());
        var r2 = await curator2.CurateAsync(Verdict(ClassificationOutcome.AllowPermanent, 0.99), ToolContext());
        Assert.IsFalse(r2.Applied);
        Assert.IsTrue(r2.Degraded);
        Assert.IsTrue(r2.DegradeReason!.StartsWith("store_error:", StringComparison.Ordinal));
    }

    // —— 20 §14.12.7：disable ⇒ Status=Disabled（不硬删除），按 Effect 落对应审计；幂等与不存在语义 ——

    [TestMethod]
    public async Task DisableRuleAsync_DisablesWithoutDeleting_AndAudits()
    {
        var (curator, allowlist, audit, clock) = CreateCurator();
        var ctx = ToolContext();

        var created = await curator.CurateAsync(Verdict(ClassificationOutcome.AllowPermanent, 0.99), ctx);
        clock.Advance(TimeSpan.FromMinutes(1));
        var disabled = await curator.DisableRuleAsync(created.RuleId!, "人工撤销");

        Assert.IsTrue(disabled.Applied);
        var rules = await CuratorRulesAsync(allowlist);
        Assert.AreEqual(1, rules.Count, "禁用不删除，保留完整审计链。");
        Assert.AreEqual(ToolApprovalAllowlistRuleStatus.Disabled, rules[0].Status);
        Assert.AreEqual(T0 + TimeSpan.FromMinutes(1), rules[0].DisabledAtUtc);

        var disableEvents = (await audit.ListAsync())
            .Where(e => e.EventType == ToolApprovalAuditEventType.AllowlistRuleDisabled).ToList();
        Assert.AreEqual(1, disableEvents.Count);
        Assert.AreEqual(created.RuleId, disableEvents[0].AllowlistRuleId);

        var again = await curator.DisableRuleAsync(created.RuleId!, "重复禁用");
        Assert.IsTrue(again.Applied, "重复禁用为幂等成功。");
        Assert.AreEqual(1, (await audit.ListAsync())
            .Count(e => e.EventType == ToolApprovalAuditEventType.AllowlistRuleDisabled), "幂等重复禁用不重复审计。");

        var missing = await curator.DisableRuleAsync("no-such-rule", "x");
        Assert.IsFalse(missing.Applied);
        Assert.AreEqual("rule_not_found", missing.DegradeReason, "规则不存在不得伪造成功。");
    }

    [TestMethod]
    public async Task DisableRuleAsync_DenyRule_UsesDenylistDisabledAudit()
    {
        var (curator, allowlist, audit, _) = CreateCurator();

        var created = await curator.CurateAsync(
            Verdict(ClassificationOutcome.DenyPermanent, 0.99),
            ToolContext(command: "format-volume x:"));
        await curator.DisableRuleAsync(created.RuleId!, "撤销");

        Assert.IsTrue((await audit.ListAsync())
            .Any(e => e.EventType == ToolApprovalAuditEventType.DenylistRuleDisabled));
        Assert.AreEqual(
            ToolApprovalAllowlistRuleStatus.Disabled,
            (await CuratorRulesAsync(allowlist))[0].Status);
    }

    // —— 21 §14.12.7：置信度 < 0.95（含缺失）⇒ 建议 30 天有效期；≥ 0.95 ⇒ 不设 ——

    [TestMethod]
    public async Task CurateAsync_LowConfidence_SuggestsThirtyDayExpiry()
    {
        var (curator, allowlist, _, clock) = CreateCurator();

        var low = await curator.CurateAsync(
            Verdict(ClassificationOutcome.AllowPermanent, 0.80),
            ToolContext(command: "npm test"));
        var lowRule = (await CuratorRulesAsync(allowlist)).Single(r => r.Command == "npm test");
        Assert.AreEqual(T0 + TimeSpan.FromDays(30), lowRule.ExpiresAtUtc, "置信度 < 0.95 建议 30 天有效期。");

        var missing = await curator.CurateAsync(
            Verdict(ClassificationOutcome.AllowPermanent),   // 无置信度信息
            ToolContext(command: "npm run lint"));
        var missingRule = (await CuratorRulesAsync(allowlist)).Single(r => r.Command == "npm run lint");
        Assert.IsNotNull(missingRule.ExpiresAtUtc, "置信度缺失按保守处理，同样建议有效期。");

        var high = await curator.CurateAsync(
            Verdict(ClassificationOutcome.AllowPermanent, 0.97),
            ToolContext(command: "npm run build"));
        var highRule = (await CuratorRulesAsync(allowlist)).Single(r => r.Command == "npm run build");
        Assert.IsNull(highRule.ExpiresAtUtc, "置信度 ≥ 0.95 不建议有效期。");
        Assert.IsNotNull(high);
    }

    // —— 测试辅助 ——

    private static (ClassificationRuleCurator, InMemoryToolApprovalAllowlistStore, InMemoryToolApprovalAuditStore, FixedClock)
        CreateCurator()
    {
        var allowlist = new InMemoryToolApprovalAllowlistStore();
        var audit = new InMemoryToolApprovalAuditStore();
        var clock = new FixedClock();
        return (new ClassificationRuleCurator(allowlist, audit, clock), allowlist, audit, clock);
    }

    private static ToolCallClassificationContext ToolContext(
        string? command = "git status",
        string? argumentsJson = null,
        string? workingDirectory = "E:/repo",
        string? shell = "pwsh",
        string toolId = "terminal_execute",
        bool irreversible = false,
        IReadOnlyList<string>? targets = null,
        string workspaceId = TestWorkspace)
        => new()
        {
            ToolId = toolId,
            CommandName = command,
            ArgumentsJson = argumentsJson,
            WorkingDirectory = workingDirectory,
            Shell = shell,
            WorkspaceId = workspaceId,
            SessionId = "sess-1",
            AgentInstanceId = "agent-1",
            UserId = "user-1",
            TargetResources = targets ?? [],
            IsIrreversibleOperation = irreversible,
        };

    private static ClassificationVerdict Verdict(
        ClassificationOutcome outcome,
        double? confidence = null,
        string reason = "测试裁决")
        => new()
        {
            Outcome = outcome,
            Reason = reason,
            ClassifierId = ClassifierId,
            ClassifierModel = "test-model",
            PerOutcomeConfidence = confidence is null
                ? null
                : new Dictionary<string, double>
                {
                    [outcome switch
                    {
                        ClassificationOutcome.AllowPermanent => "allow_permanent",
                        ClassificationOutcome.DenyPermanent => "deny_permanent",
                        ClassificationOutcome.AllowOnce => "allow_once",
                        ClassificationOutcome.DenyOnce => "deny_once",
                        _ => "unknown",
                    }] = confidence.Value,
                },
        };

    private static async Task<IReadOnlyList<ToolApprovalAllowlistRule>> CuratorRulesAsync(
        InMemoryToolApprovalAllowlistStore store)
        => (await store.ListAsync()).Where(r => r.SourceClassifierId == ClassifierId).ToList();

    private static void AssertViolation(ToolCallClassificationContext ctx, NarrownessViolation expected, string message)
    {
        var check = ClassificationRuleCurator.CheckNarrowness(ctx);
        Assert.AreEqual(expected, check.Violation, message);
        Assert.IsFalse(check.Passed, message);
        Assert.IsNotNull(check.Detail, "命中项必须携带具体证据。");
    }

    private static void AssertPassed(ToolCallClassificationContext ctx, string message)
    {
        var check = ClassificationRuleCurator.CheckNarrowness(ctx);
        Assert.IsTrue(check.Passed, $"{message}（实际：{check.Violation} {check.Detail}）");
    }

    /// <summary>固定时钟：时间只经注入的 TimeProvider 获取（§14.12.9 无静态可变状态）。</summary>
    private sealed class FixedClock : TimeProvider
    {
        public DateTimeOffset Now { get; private set; } = T0;

        public void Advance(TimeSpan delta) => Now += delta;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>按需抛异常的 allowlist store 存根（验证 fail-closed 契约）。</summary>
    private sealed class ThrowingAllowlistStore : IToolApprovalAllowlistStore
    {
        private readonly bool _throwOnList;

        public ThrowingAllowlistStore(bool throwOnList) => _throwOnList = throwOnList;

        public Task SaveAsync(ToolApprovalAllowlistRule rule, CancellationToken ct = default)
            => _throwOnList ? Task.CompletedTask : throw new InvalidOperationException("save-boom");

        public Task<ToolApprovalAllowlistRule?> GetAsync(string ruleId, CancellationToken ct = default)
            => throw new InvalidOperationException("get-boom");

        public Task<IReadOnlyList<ToolApprovalAllowlistRule>> ListAsync(CancellationToken ct = default)
            => _throwOnList
                ? throw new InvalidOperationException("list-boom")
                : Task.FromResult<IReadOnlyList<ToolApprovalAllowlistRule>>([]);
    }
}
