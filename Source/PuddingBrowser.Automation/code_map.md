# PuddingBrowser.Automation

浏览器自动化的可靠性叶子组件：控制权与授权、引用身份、证据化回执、截图与渐进阅读。

> 源指纹: Source/PuddingBrowser.Automation/**=pending · 条目数: 31 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `BrowserAutomationAuthority.cs` | 控制权与准入的唯一事实源：签发与撤销授权，按页串行发放写租约 | `BrowserAutomationAuthority.Admit`、`Revalidate`、`IssueGrant` | `IBrowserAutomationAuthority`、`IBrowserAutomationControl` | 接管推进控制世代但不推进授权世代；`Admit` 签发与 `Revalidate` 复检必须都过 |
| `BrowserGrantIssuancePolicy.cs` | 子代理授权的上界判据：范围必须是父任务同页同 frame 授权的子集 | `BrowserGrantIssuancePolicy.EnsureSubAgentScopeIsSubset` | `BrowserPageGrantScope` | 父任务无授权时子代理不得凭空获得，失败抛异常而非降级 |
| `BrowserOperationLedger.cs` | 操作账本：按调用身份+连接世代+操作号去重，在途项不被容量淘汰 | `BrowserOperationLedger.Admit`、`TryComplete`、`TryGet` | `BrowserLedgerAdmission` | 容量耗尽拒新写请求；终态墓碑保留到容量压力出现，过期重传返回 `receipt_expired` |
| `BrowserMutationPostcondition.cs` | 变更类动作的后置条件判定（取代「结果版本必须递增」） | `BrowserMutationPostcondition.Validate`、`ValidateTabs` | `BrowserMutationExpectation` | 未声明后置条件时版本不推进不算失败；只有显式声明导航未满足才报 `outcome_unknown` |
| `BrowserInteractionReceipt.cs` | 交互回执构造：把声明的期望与回读观测拼成断言与完成度 | `BrowserInteractionReceipt.Build`、`ExecutionFor`、`BrowserInteractionEvidence` | `BrowserActionReceipt` | 表单值一律不回显（只给 matched 与长度）；有期望拿不到观测只能 unverified |
| `BrowserReceiptJudge.cs` | 证据到回执的判定，以及重试处置矩阵 | `BrowserReceiptJudge.Judge`、`BrowserRetryPolicy.Decide` | `BrowserActionReceipt` | 不按动作名推断导航；已派发却未通过验证只能先观察、不可重做 |
| `BrowserRefIdentity.cs` | 引用身份模型：作用域、三类修订事实与分类作废 | `BrowserRefScope`、`BrowserObservationRevisions`、`BrowserRevisionPolicy` | `BrowserRefToken` | 只读调用不得推进任何修订事实；跨页或跨 frame 即使版本号相同也必须拒绝 |
| `BrowserRefRegistry.cs` | 宿主绑定的引用登记与校验、按粒度的撤销 | `BrowserRefRegistry.Validate`、`ValidateForCoordinates`、`RevokeRef` | `BrowserRefIdentity` | 页面可自写属性只能当提示；普通 DOM 变更与滚动不作废引用 |
| `BrowserNodeIdentity.cs` | 引用节点的宿主身份判定（一致/被替换/版本陈旧/不可验证） | `BrowserNodeIdentity.Judge`、`IsSafeToProceed` | `BrowserRefRegistry` | Unverifiable 只能当「未证明」继续，不得当作同一节点 |
| `BrowserReadingFormat.cs` | 无障碍树的阅读投影：模式、双预算与两类截断 | `BrowserReadingFormat.Compose`、`BrowserReadingMode`、`BrowserReadingFormatWire` | `BrowserReadingRequest` | 只投影不编造；输出预算与内部扫描预算必须分开 |
| `ArticlePaging.cs` | 块边界分页引擎，按各表示真实字节成本决策 | `ArticlePaging.Page`、`PageResult` | `ArticleMarkdown`、`ArticleCompactJson` | 只落块边界；单块超预算时整块给出并标注超预算 |
| `ArticleMarkdown.cs` | 把语义块组装成 markdown 正文 | `ArticleMarkdown.Compose`、`ArticleBlock` | `ArticlePaging` | 不丢段不重复，未知块降级为段落且输出始终合法 |
| `ArticleCompactJson.cs` | 正文的 compact_json 表示，与 markdown 共用块解析与分页 | `ArticleCompactJson.Compose` | `ArticlePaging` | 截断后仍必须是合法 JSON：先选块再一次性序列化 |
| `ArticleCursor.cs` | 阅读分页的不透明游标，带表示标识与页面版本身份 | `ArticleCursor.Encode`、`TryParse`、`MatchesPage` | `ArticlePaging` | 未知 kind 或身份不符一律 fail closed，不得回退成 0 |
| `ArrivalScroll.cs` | 到达滚动须收敛：到限与位置不再变化、步数封顶三者取先 | `ArrivalScroll.Plan`、`PlanToRef`、`ArrivalTarget` | `NativeInputSequence` | 没动不得报告滚成功；拿不到几何直接停并如实说明 |
| `NativeInputSequence.cs` | 原生键鼠 CDP 调用序列规划，含修饰键清理与分步移动 | `NativeInputSequence.PlanClick`、`PlanType`、`ReleaseModifiers` | `ArrivalScroll` | 按下必抬起；非 ASCII 走 insertText，未知按键拒绝而非猜 |
| `FileUploadPlan.cs` | 文件上传的三步 CDP 请求规划 | `FileUploadPlan.BuildDocumentParameters`、`BuildSetFilesParameters` | `NativeInputSequence` | 文件清单须在触达浏览器前校验；选择器中的引号与反斜杠必须转义 |
| `ScreenshotPlan.cs` | 生成截图参数并做字节预算与质量阶梯重拍 | `ScreenshotPlan.BuildForObservation`、`CheckBudget`、`CaptureWithinBudgetAsync` | `BrowserScreenshotTransfer` | 超预算绝不截断图片；拿不到内容尺寸时拒绝整页而非假装 |
| `BrowserScreenshotPlanner.cs` | 截图能力的只读投影：能截什么与多大，不能时给出原因 | `BrowserScreenshotPlanner.TryPlan`、`BrowserScreenshotIntent` | `ScreenshotPlan` | 全页不支持，不得用全页冒充视口 |
| `BrowserScreenshotCoordinates.cs` | 截图坐标到 CSS viewport 的精确变换 | `BrowserScreenshotCoordinates.TryToCssViewport`、`BrowserCoordinateRejection` | `ScreenshotLayoutFacts` | 必须携带截屏 observationId；几何不全时算不出就拒，绝不就近取点 |
| `BrowserScreenshotTransfer.cs` | 截图载荷的跨进程校验与分块组装 | `BrowserScreenshotTransfer.TryValidate`、`Split`、`TryAssemble` | `ScreenshotImageHeader` | 单张上限 8 MiB；可空元数据未上报即拒绝，不得补 0 冒充事实 |
| `ScreenshotLayoutFacts.cs` | 页面几何事实：视口与滚动、内容尺寸与 zoom | `ScreenshotLayoutFactsReader.TryRead`、`ScreenshotLayoutFacts` | `BrowserScreenshotCoordinates` | 设备像素比由调用方采集；不得用 visual viewport scale 冒充 DPR |
| `ScreenshotImageHeader.cs` | 有界解析 PNG/JPEG 头中的真实格式与尺寸 | `ScreenshotImageHeader.Read` | `BrowserScreenshotTransfer` | 只信实际字节，不信请求声明 |
| `ScreenshotDomRevision.cs` | 页内 DOM 变更计数器，作为第三个失效键 | `ScreenshotDomRevision.TryRead`、`TryReadEvaluationResult` | `ScreenshotObservationRegistry` | 初值为 1（校验要求 >0）；读不到必须拒绝，不得当作没变过 |
| `ScreenshotObservationRegistry.cs` | 截图观察登记与按页面世代失效 | `IScreenshotObservationRegistry`、`InMemoryScreenshotObservationRegistry` | `BrowserScreenshotMetadata` | 任一世代不同即移除并失败；空 id 登记直接拒绝 |
| `PageEventPolicy.cs` | 页面周边事件的确定性处置：权限询问与新窗口、下载 | `PageEventPolicy.DecidePermission`、`DecideWindow`、`DecideDownload` | `PageEventLog` | 权限一律拒绝，弹窗只抑制不转移当前页，下载取消 |
| `PageEventLog.cs` | 页面事件的有界日志，连续重复合并为计数 | `PageEventLog.Add`、`Entry` | `PageEventPolicy` | 如实反映总数；空摘要不计入，不编造事实 |
| `ScriptDialogPolicy.cs` | 脚本对话框的确定作答策略 | `ScriptDialogPolicy.Decide`、`ShouldProvideText` | `PageEventPolicy` | 未处理的对话框会挂住流程且无报错，必须显式作答 |
| `SubAgentDelegation.cs` | 子代理委派的授权信封校验与有界结论收窄 | `BrowserDelegation.TryValidate`、`BrowserDelegationConclusionShaper.Shape` | `BrowserDelegationScope` | 结论按 UTF-8 字节截断且不切断多字节字符；结构上不含整页快照与内部对话 |
| `SubAgentDelegationLeases.cs` | 父子页面写租约账本：同页单写并允许只读并行，撤权时双侧失效 | `BrowserDelegationLeaseLedger.TryAcquireWrite`、`RevokePage`、`RegrantPage` | `BrowserLeaseRole` | 只是策略账本，不做线程同步（属宿主） |
| `PuddingBrowser.Automation.csproj` | 叶子组件工程，唯一引用为 `Pudding.Contracts` | — | `Pudding.Contracts` | `EnforceAutomationBoundary` 在任何指向消费方的引用上直接构建失败 |
