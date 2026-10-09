using Pudding.Contracts.Desktop;
using PuddingBrowser.Automation;

namespace PuddingBrowser.AutomationTests;

/// <summary>
/// 组件公开面的冻结快照（防「静默丢代码」）。
///
/// 为什么需要它：组件化规程的守恒等式抓的是「用例被悄悄移除」；这里抓的是同一类事故的另一半
/// —— 公开类型被改名/删除/误设为 internal 之后，测试数量可能不变而接入方（S5）编译不过。
/// 真实教训见规程 §4.2：搬迁后「我跑的那几套测试都绿」掩盖了别的工程编译不过。
/// </summary>
[TestClass]
public sealed class ComponentSurfaceTests
{
    private static readonly string[] ExpectedPublicTypes =
    [
        "ArticleBlock",
        "ArticleCompactJson",
        "ArticleCursor",
        "BudgetVerdict",
        "ContentSize",
        "Answer",
        "Kind",
        "DownloadVerdict",
        "Entry",
        "FileUploadPlan",
        "BrowserDelegation",
        "BrowserDelegationConclusion",
        "BrowserDelegationConclusionShaper",
        "BrowserDelegationEnvelope",
        "BrowserDelegationScope",
        "BrowserDelegationLeaseLedger",
        "BrowserLeaseOutcome",
        "BrowserLeaseRelease",
        "BrowserLeaseReleaseReason",
        "BrowserLeaseRole",
        "ArrivalScroll",
        "ArrivalTarget",
        "ArrivalStep",
        "PageEventLog",
        "PageEventPolicy",
        "PermissionVerdict",
        "ScriptDialogPolicy",
        "WindowVerdict",
        "ScreenshotPlan",
        "BrowserCoordinateRejection",
        "BrowserCoordinateTarget",
        "BrowserPageGenerations",
        "BrowserScreenshotIntent",
        "BrowserScreenshotPlan",
        "BrowserScreenshotPlanner",
        "BrowserScreenshotPlanning",
        "BrowserScreenshotScope",
        "BrowserScreenshotChunk",
        "BrowserScreenshotDetail",
        "BrowserScreenshotMetadata",
        "BrowserScreenshotRejection",
        "BrowserScreenshotCoordinates",
        "BrowserScreenshotTransfer",
        "ArticlePaging",
        "PageResult",
        "ArticleMarkdown",
        "BrowserActionEvidence",
        "BrowserNodeIdentity",
        "Identity",
        "Verdict",
        "CdpCall",
        "InputTarget",
        "KeyStroke",
        "NativeInputSequence",        "BrowserAutomationAuthority",
        "BrowserGrantIssuancePolicy",
        "BrowserInteractionEvidence",
        "BrowserInteractionReceipt",
        "BrowserLedgerAdmission",
        "BrowserLedgerAdmissionKind",
        "BrowserMutationExpectation",
        "BrowserMutationOutcome",
        "BrowserMutationPostcondition",
        "BrowserOperationLedger",
        "BrowserOperationLedgerEntry",
        "BrowserOperationLedgerOptions",
        "BrowserObservationRevisions",
        "BrowserReadingFormat",
        "BrowserReadingFormatWire",
        "BrowserReadingMode",
        "BrowserReadingRequest",
        "BrowserReadingResult",
        "BrowserReadingTruncation",
        "BrowserReceiptJudge",
        "BrowserRefDecision",
        "BrowserRefDenial",
        "BrowserRefDenialWire",
        "BrowserRefInvalidation",
        "BrowserRefRegistry",
        "BrowserRefScope",
        "BrowserRefToken",
        "BrowserRetryPolicy",
        "BrowserRevisionPolicy",
    ];

    [TestMethod]
    public void Public_Surface_Matches_The_Frozen_List()
    {
        var exported = typeof(BrowserAutomationAuthority).Assembly
            .GetExportedTypes()
            .Select(type => type.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        // 差异直接写进消息：只报"数量不符"会让人反复数列表（这是我自己踩过的坑）。
        var added = exported.Except(ExpectedPublicTypes).ToArray();
        var removed = ExpectedPublicTypes.Except(exported).ToArray();

        Assert.IsTrue(
            added.Length == 0 && removed.Length == 0,
            "the public surface of PuddingBrowser.Automation changed; update the contract consumers (S5)"
            + " and this frozen list together"
            + $"; added=[{string.Join(", ", added)}] removed=[{string.Join(", ", removed)}]");
    }

    [TestMethod]
    public void Authority_Implements_Both_Narrow_Ports()
    {
        Assert.IsTrue(
            typeof(IBrowserAutomationAuthority).IsAssignableFrom(typeof(BrowserAutomationAuthority)),
            "the authority must serve the read/verify port used by dispatchers and mapping layers");
        Assert.IsTrue(
            typeof(IBrowserAutomationControl).IsAssignableFrom(typeof(BrowserAutomationAuthority)),
            "the authority must serve the control port used by the Shell and the lifecycle port");
    }

    [TestMethod]
    public void Read_Port_Exposes_No_Mutating_Members()
    {
        // 只读端口不得泄漏写入面：否则 Agent 侧就能绕过 UI 直接接管自己。
        var members = typeof(IBrowserAutomationAuthority).GetMethods().Select(method => method.Name).ToArray();

        CollectionAssert.AreEquivalent(
            new[] { "Admit", "Capture", "Revalidate", "get_ActiveGrants", "get_ActiveManagementGrants" },
            members,
            "the read/verify port must stay narrow");
    }

    [TestMethod]
    public void Control_Port_Exposes_The_Lifecycle_And_Grant_Operations()
    {
        var members = typeof(IBrowserAutomationControl).GetMethods().Select(method => method.Name).ToArray();

        CollectionAssert.AreEquivalent(
            new[]
            {
                "Close",
                "IssueGrant",
                "IssueManagementGrant",
                "NotifyConnectionGeneration",
                "ReleaseLease",
                "Resume",
                "RevokeAllGrants",
                "RevokeExecutionScope",
                "RevokeGrant",
                "RevokeManagementGrant",
                "SetPaused",
                "SetUserTakeover",
            },
            members,
            "the control port is the only place that may change control/authorization facts");
    }

    [TestMethod]
    public void Component_Assembly_Is_The_One_Under_Test()
    {
        Assert.AreEqual(
            "PuddingBrowser.Automation",
            typeof(BrowserAutomationAuthority).Assembly.GetName().Name);
    }

    /// <summary>
    /// 用例守恒：逐个测试类冻结 <c>[TestMethod]</c> 数量。
    ///
    /// 为什么不能只依赖「跑出来都绿」：`Compile Remove` 掉一个测试文件后，`dotnet test` 依然 exit 0，
    /// 只是通过数变少（规程 §4.1 实测：58 → 51 而 exit 仍是 0）。把每个类的用例数钉住，
    /// 静默丢用例就会变红。
    /// </summary>
    [TestMethod]
    public void Test_Method_Counts_Are_Conserved_Per_Class()
    {
        var expected = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["ArticleMarkdownFormatTests"] = 13,
            ["ArticleCompactJsonTests"] = 8,
            ["ArticleCursorTests"] = 7,
            ["ScreenshotPlanTests"] = 12,
            ["BrowserScreenshotCoordinateTests"] = 12,
            ["BrowserScreenshotPipelineTests"] = 5,
            ["BrowserScreenshotPlannerTests"] = 9,
            ["BrowserScreenshotTransferTests"] = 14,
            ["FileUploadPlanTests"] = 4,
            ["SubAgentDelegationTests"] = 10,
            ["SubAgentDelegationLeaseTests"] = 18,
            ["ReadOnlyDelegationTests"] = 21,
            ["AuthorityEntryAuditTests"] = 1,
            ["GrantExpiryWiringTests"] = 2,
            ["CallerIdentityGateTests"] = 2,
            ["ArrivalScrollTests"] = 11,
            ["SerpBudgetFixtureTests"] = 6,
            ["DriverEventHandlingTests"] = 4,
            ["PageEventLogTests"] = 6,
            ["PageEventPolicyTests"] = 7,
            ["ScriptDialogPolicyTests"] = 6,
            ["BrowserNodeIdentityTests"] = 10,
            ["NativeInputSequenceTests"] = 12,
            ["BrowserAssertionRedactionTests"] = 11,
            ["BrowserControlAuthorityTests"] = 16,
            ["BrowserInteractionReceiptTests"] = 24,
            ["BrowserMutationPostconditionTests"] = 12,
            ["BrowserOperationLedgerTests"] = 15,
            ["BrowserOperationPolicyTests"] = 10,
            ["BrowserPageGrantTests"] = 22,
            ["BrowserReceiptJudgeTests"] = 21,
            ["BrowserReadingFormatTests"] = 14,
            ["BrowserRefIdentityTests"] = 30,
            ["BrowserRetryPolicyTests"] = 8,
            ["ComponentBoundaryTests"] = 5,
            ["ComponentSurfaceTests"] = 6,
        };

        var actual = typeof(ComponentSurfaceTests).Assembly
            .GetTypes()
            .Where(type => type.GetCustomAttributes(typeof(TestClassAttribute), inherit: false).Length > 0)
            .ToDictionary(
                type => type.Name,
                type => type.GetMethods()
                    .Count(method => method.GetCustomAttributes(typeof(TestMethodAttribute), inherit: false).Length > 0),
                StringComparer.Ordinal);

        CollectionAssert.AreEquivalent(
            expected.Keys.ToArray(),
            actual.Keys.ToArray(),
            "a test class disappeared or was added; the component test surface must be explicit");

        foreach (var (className, expectedCount) in expected)
        {
            Assert.AreEqual(
                expectedCount,
                actual[className],
                $"'{className}' lost or gained test methods; a silently dropped case must not look like success");
        }

        Assert.AreEqual(expected.Values.Sum(), actual.Values.Sum());
    }
}
