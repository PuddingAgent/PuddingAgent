# PuddingBrowser.Automation — code_map

> 浏览器自动化**可靠性**组件（叶子）：统一控制权、页面授权、执行租约、证据化回执与重试裁定。
> 设计依据：[浏览器自动化可靠性与渐进阅读设计方案](../../Docs/12_features/浏览器自动化可靠性与渐进阅读设计方案-2026-10-08.md)（§3/§4，切片 **B0**）。
> 交付状态：**S1–S4 已完成；S5 已部分落地（2026-10-08）** —— 组件与其测试工程已登记
> `PuddingAgentNetwork.slnx`，`Pudding.DesktopService` 与 `PuddingHost` 已加显式引用（共用同一份
> 变更类后置条件）。**唯一事实源已在 Desktop 组合根装配**（`DesktopCapabilityChannelComposition`
> 创建 `BrowserAutomationAuthority` 并注入 `DesktopService`，接管/暂停收敛为投影）；仍属后续：
> `Admit`/`Revalidate` 与写租约接入准入路径、grant 签发（需要 Core 侧的可信调用方身份）、
> `IBrowserAutomationControl` 的 UI 侧驱动者。

## 1. 边界（编译期强制）

- **唯一允许的引用**：`Pudding.Contracts`（BCL-only 叶子，自身 `ProjectReference` = 0）。
- **禁止引用**：UI / 驱动（`PuddingBrowser.WebView2`、`PuddingBrowser.WinUI`、`PuddingDesktop*`）、
  映射层（`Pudding.DesktopService`、`Pudding.DesktopSurface.Browser`）、宿主（`PuddingHost`、`PuddingRuntime`、`PuddingAgent`）。
- `EnforceAutomationBoundary`（`PuddingBrowser.Automation.csproj`）在 `ResolveReferences` 前取红：
  临时加一条指向消费方的 `ProjectReference` ⇒ **构建立刻失败**（已实测）。
- `Source/PuddingBrowser.AutomationTests/ComponentBoundaryTests.cs` 另做三重运行时断言：
  探测器自检、进程未加载禁用程序集、`*.deps.json` 依赖闭包；并直接断言两个 csproj 的引用清单。

## 2. 文件与职责

| 文件 | 职责 / 不变量 |
|------|----------------|
| `BrowserAutomationAuthority.cs` | 实现 `IBrowserAutomationAuthority` + `IBrowserAutomationControl`：唯一事实源。接管/暂停/恢复/关闭/连接世代改变推进**控制世代**；撤销授权、切目标、关闭、世代改变推进**授权世代**；接管**不**推进授权世代。`Admit` 在入队前校验并签发写租约，`Revalidate` 在触碰页面前复检租约世代 ⇒ 「验证通过 → 期间被接管」必然被拦下。同一页面同时只有一个写租约；授权被撤销时租约随之释放。 |
| `BrowserGrantIssuancePolicy.cs` | 子代理授权的**上界**判据：子代理 grant 的范围必须 ⊆ 父任务在同一页面/同一 frame 的 grant；父任务无授权 ⇒ 子代理不得凭空获得。失败时抛 `ArgumentException`（fail closed）。 |
| `BrowserReceiptJudge.cs` | 证据 → 回执。只按「执行阶段 + 声明的后置断言」判定，**不按动作名推断导航**；未执行/结果不明 ⇒ `not_requested`；需要断言但拿不到 ⇒ `unverified`；有未通过断言 ⇒ `failed`。同文件的重试裁定 `BrowserRetryPolicy`：`unknown` ⇒ 结果不明、`blocked`/`verified` ⇒ 不可重试、**真跑过（dispatched/completed）却没通过验证 ⇒ `dispatched_observe_only`（先观察、勿重做）**，其余按「未开始 + 可重试 + 无副作用」才 `safe_to_retry`；兜底分支回带回执自带的**最小修复提示**（无提示则 null，不编造） |
| `BrowserOperationLedger.cs` | operationId 账本：键 = 调用身份 + 连接世代 + operationId，另存请求摘要。在途项**永不因容量被淘汰**（容量耗尽拒新写请求）；终态 tombstone 保留到容量压力出现，因此过期重传返回 `receipt_expired` 而不是被当成新动作。默认 5 分钟 / 1,024 项。 |
| `BrowserMutationPostcondition.cs` | 变更类动作的**后置条件**（取代「所有 mutating 结果版本必须严格递增」）：没有活版本是真失败；观测到文档提交即满足任何期望；<b>未声明后置条件时版本不推进不算失败</b>（fill/click 开菜单/SPA 更新）；只有显式声明 `DocumentNavigation` 未满足才报 `outcome_unknown`。`ValidateTabs` 不拿「另一页的版本推进」当证据，改为校验 `close` 是否真的关掉、`activate`/`new` 是否回带活版本。 |
| `BrowserRefIdentity.cs` | 引用身份模型（设计 §5 / G4）：`BrowserRefScope`（context/page/frame 三元组，frame=null 即顶层）、`BrowserObservationRevisions`（**三个分开的事实**：文档世代 / DOM 修订 / 视口修订）、`BrowserRevisionPolicy`（只读**不推进任何**事实；文档提交推三者；DOM 变更只推 dom；滚动只推 viewport）、`BrowserRefInvalidation`（分类作废）、`BrowserRefDenial` + 线名、`BrowserRefToken`（宿主绑定节点键 + 最小刷新提示）、`BrowserRefDecision` |
| `BrowserRefRegistry.cs` | 宿主绑定的引用登记表：身份用**宿主侧**节点键（`data-pudding-ref` 之类页面可自写属性只能当提示）；校验同时看作用域与文档世代 ⇒ **跨页/跨 frame 即使版本号相同也拒绝**；撤销**分类**（整页 / 单 frame / 单引用），而普通 DOM 变更与滚动**不作废任何**引用；reference 值不可解析，**绝不按键名/角色自动重绑** |
| `BrowserReadingFormat.cs` | 阅读面收敛（设计 §6.1/§6.2、G5）：把驱动的无障碍树按模式（`outline` 默认＝可交互行＋标题/地标行，`interactive`＝仅带 `ref=` 的行，`full`＝全量不设预算，`text` 未实现）**只做投影不编造**；**输出预算（50 行 / 4096 UTF-8 字节）与内部扫描预算（10k 节点 / 64 层 / 200k 文本）分开**；截断如实区分 `incomplete_scan`（可能缺整片区域）与 `output_budget`（可用 `nextCursor` 续读）。`BrowserReadingFormatWire` 提供线名与 fail-closed 解析 |
| `BrowserNodeIdentity.cs` | **引用的宿主绑定身份与判定**（§1.1/§5 禁止自动重绑）：现有 ref 只绑「页面 token + 版本 + 序号」，SPA 原地重渲染（版本计数没动）时同一序号会指向**另一个节点**。这里补上宿主侧身份（CDP `backendNodeId`，编码为 `p…-v3-n7-b42`）与判定表：`Valid`（都一致）/`Replaced`（位置一致但**节点被换掉**，必须拒绝）/`StaleVersion`/`WrongPage`/`Unverifiable`（任一侧无身份：**不声称有效、也不凭空判死**）。判定是纯函数，驱动只负责采集事实；`IsSafeToProceed` 允许 Unverifiable 继续，但调用方必须当"未证明"而非"已证明相同" |
| `ArrivalScroll.cs` | **到达滚动的收敛策略**（§7/G8 的 `to_top`/`to_bottom`/`to_ref`）：原生滚轮没有原子"滚到底"，只能按步送并**必须能停**。三个停止条件：**到限**（位置+视口覆盖内容高度 / 到顶后 ≤0）、**位置不再变化**（停在边界或被容器挡住——继续送轮只会空转，且 G8 明确禁止把"没动"报告成滚成功）、**步数封顶**（`MaxSteps`，保护无限滚动页）；拿不到几何（内容/视口 ≤0）直接停并如实说明。`PlanToRef` 另按元素相对视口的上下边滚"刚好够到"（已可见即停、元素不跟着动即停） |
| `SubAgentDelegation.cs` | **子代理委派的授权信封与有界结论**（§9/G10）：`TryValidate` 逐条拒绝——缺目标/期望结果/页面 grant/正期限、**范围超出父授权**、预算非正或 >2 KiB、注入工具未覆盖必需工具、写入类任务缺必需工具；`BrowserDelegationConclusionShaper` 把子代理结果收窄成父代理可见结论（**按 UTF-8 字节**截断且不切断多字节字符、超限标记截断并指向 Artifact）；结论类型**结构上不含整页快照与内部对话**（比"记得别传"可靠） |
| `SubAgentDelegationLeases.cs` | **父子页面写租约账本**（§9/G10）：同页同一时刻只有一个写租约（父在写时委派必须先暂停/释放）、只读观察可并行、写入按页串行、`EndDelegation` 结束或超时**归还给父**、`RevokePage` 接管或撤权**令父子同时失效**且此后不再授予（`RegrantPage` 恢复）。只是策略账本（不做线程同步——那属宿主），因此每条转换都能确定性验证 |
| `FileUploadPlan.cs` | **文件上传的三步 CDP 请求规划**（§7）：页面脚本无权设置 `input.files`，只能让浏览器自己交付文件——`DOM.getDocument` → `DOM.querySelector`（用元素上的 `data-pudding-ref` 定位）→ `DOM.setFileInputFiles`。**选择器转义**（引用里的引号/反斜杠必须转义，否则选择器无效或选错元素）、文件清单在**触达浏览器之前**校验（空/相对路径/超量拒绝）、节点 id 读取显式判类型（`TryGetInt32` 在类型不对时会**抛异常**而不是返回 false） |
| `NativeInputSequence.cs` | **原生输入（CDP 键鼠）的调用序列规划**（§7 `inputMode=native`）：把"点击/悬停/滚动/填入/逐键输入/按键"翻译成 CDP 调用清单。要点是**状态不留残**——点击必定 移动→按下→抬起（成对）、组合键按 CDP 位掩码（Alt=1/Control=2/Meta=4/Shift=8）并在 keyDown 前按下、keyUp 后抬起、`ReleaseModifiers` 供**取消时清理**（一次被取消的 Ctrl+点击不能让页面以为 Ctrl 一直按着）；`fill` 用 `Input.insertText`（清空则 Ctrl+A→Delete），`type` 逐字符 keyDown/keyUp（页面才收得到 input），未知按键**拒绝而不是猜**。`PlanDrag` 必须**分步移动**（HTML5 拖放/画布依赖中间 mousemove）且**按下必然抬起**。本组件不引用 Abstractions，几何用自己的 `InputTarget` |
| `ScreenshotPlan.cs` | **截图的 CDP 参数语义与结果解码**（§6 视觉观察）：格式由 quality 决定（有 quality ⇒ jpeg，否则 png——quality 只对有损格式有意义，给 PNG 传是被拒的无效输入）；**整页必须有内容尺寸**（来自 `Page.getLayoutMetrics`）才做 clip + `captureBeyondViewport`，拿不到就拒绝而不假装整页；返回体缺 `data`/非法 base64 明确失败（不能解出 0 字节还当成功）；**字节预算** `CheckBudget`：超了**绝不截断图片**（半张图会让读者得出错误结论），如实报资源耗尽并给出降画质/改视口/提预算三条出路 |
| `BrowserScreenshotTransfer.cs` | **截图载荷的传输与校验（桌面一侧）**（设计 §8.1）：职责边界是"Desktop 返回**有界**媒体载荷/分块 + 元数据"，Core 再校验并写入既有 Vision Artifact；**不落库、不调模型、不假定 Core 能读本地临时文件**。硬约束来自设计文档：单张 **8 MiB**（`MaxImageBytes`）超限明确给原因、默认 `detail=low` 时最长边 **≤512px**（`LowDetailLongestEdge`，缩放没做却说做了会让调用方按错尺寸算坐标）、MIME 只认 png/jpeg、必须带 `observationId`（§8.2 坐标要用它）与**三代修订号**、viewport/scroll/裁剪变换**未上报就拒绝**（可空字段，绝不补 0 冒充事实）；`Split`/`TryAssemble` 保证跨进程分块**无缺口/无重复/总量相符**，否则明确失败而不返回"可能不完整"的载荷 |
| `BrowserScreenshotCoordinates.cs` | **截图坐标 → CSS viewport 的精确变换**（设计 §8.2）：坐标统一为 **CSS viewport 像素**，输入**必须携带截屏 observationId**；变换链为「图片像素 →去低清缩放→ 设备像素 →除设备缩放→ CSS 像素 →加裁剪原点→ 减滚动量」。三类拒绝是核心：**用别的 observation 的坐标**、**世代/滚动/缩放变了（旧坐标失效，要求重新观察）**、**换算后落在视口外或图片外**；几何信息不全时**算不出就拒**（绝不就近取点）。少乘或多减一项只会表现为"点偏了"而不报错，故每一步都有用例钉住 |
| `BrowserScreenshotPlanner.cs` | **截图能力的只读投影**（设计 §8.1/§8.2）：纯函数回答"这一刻能截什么、截多大、不行时为什么"。**先视口/区域，全页明确不支持**（全页不能冒充 viewport，滚动拼接要单独处理固定元素与动态内容）；截屏前验证 **page read grant / 当前文档 / readiness**（否则会返回导航前的画面）；区域必须落在 CSS 视口内（越界拒绝而不是裁一半）；`detail=low` 等比缩到最长边 ≤512px（不放大），high/original 保持设备像素；尺寸与上限全部来自常量（8 MiB / 512px），并声明将来必须上报的元数据要点 |
| `PageEventLog.cs` | **页面事件有界日志**（§8）：把「被自动处理的页面事件」有界地留下来（默认 8 条），**连续重复合并为计数**（一次页面里十个一样的权限询问，报十遍没有信息量、报 ×N 才有），`TotalCount` 如实反映总数（含被容量挤掉的）；空摘要不计入（不编造事实）。它经 `PageSnapshot.PageEvents` 回给调用方 |
| `PageEventPolicy.cs` | **页面周边事件的确定性处置**（§8）：与脚本对话框同一类问题——不处理时 WebView2 会弹 UI 或等人操作（权限询问／新窗口／下载另存为），自动化流程就停在那里且**往往没有明确报错**。三条安全侧默认：**权限一律拒绝**（自动化不该因为页面想要就悄悄拿到设备权限；要授权必须是显式产品决定，策略里不留白名单后门）、**弹窗抑制**并记录目标地址（不偷偷改成在当前页打开——那会丢页面状态）、**下载取消**（落盘是需要授权与去向的副作用）。摘要只留 `scheme://host`（不回显路径/查询串，避免把 token 带进日志），无名/无地址时不留空字段 |
| `ArticleCursor.cs` | **阅读分页的不透明游标**（§6.1）：`{kind}:{index}`（`md:3`/`json:5`/`lines:50`）。带**表示标识**的原因：两种正文表示的块成本不同 ⇒ 页边界不同，裸整数游标跨表示使用会**静默跳过**若干块（最难发现的数据丢失）；带标识后这种误用可被明确拒绝。解析 fail closed（未知 kind/非数字/负数/多余字段/空白变体一律拒绝，绝不回退成 0）。游标还带**页面/版本身份** `p{页面id短哈希}-v{版本}`：换页面或文档被改过后同一索引指向的是另一份内容（不报错、只是内容接不上），因此身份不符必须拒绝；页面标识只取短哈希，游标对调用方保持不透明 |
| `ArticlePaging.cs` | **块边界分页引擎**（两种正文表示共用）：分页接受一个**成本函数**，因此每种形状按**自己真实输出的字节数**分页（markdown 的块成本=渲染字节+空行 2 字节；compact_json=序列化字节+逗号 1 字节，且**包装也计入预算**）。三条不变式：只落块边界（不切块）、只要有块被选中就不超预算（除非单块本身就超，则整块给出并标注 `OverBudget`）、同输入同分页（确定性） |
| `ArticleCompactJson.cs` | **正文的 compact_json 表示**（§151 的 `format`）：与 markdown **共用同一份块解析与同一套分页决策**（两种表示的页边界一致，游标可互换续读），区别只在形状——给程序读的 `{blocks:[{k,t,d,h,r}],truncated,nextCursor}`。硬性要求：**截断后仍是合法 JSON**（先选块再一次性序列化，括号永远成对；边写边截会产出半截 JSON） |
| `ArticleMarkdown.cs` | **正文 markdown 组装**（设计 §6.1 的 `mode=text`）：驱动只输出**语义块**（一行一个 JSON 对象：h1-h6/p/li/pre/quote/a/table/hr），这里组装成 markdown。三条承诺（G6）：**不丢段不重复**（每块要么渲染要么计入 `SkippedBlocks`；分页只落**块边界**，不做块内切分）、**结构保真**（标题层级/列表缩进/表格行列/代码空白/链接目的地址）、**输出始终合法 markdown**（非法/未知输入不抛异常，未知块只要有文本就降级成段落）。单块超预算时整块给出并如实标注 `output_budget`（宁可超一点也不静默截断正文） |
| `BrowserInteractionReceipt.cs` | 交互回执**判定**（设计 §4.1/§4.2、G7）：把「调用方声明的期望 + 驱动回读的观测值」判成断言与完成度。① 不按动作名推断成功——只有声明了期望（显式 `expect_value`，或 select 的 `values`、check/uncheck 的 `checked` 这类调用方自给的目标值）才有断言，否则如实报 `dispatched`；② **表单值一律不回显**（value/selectedValues 只给 matched 与长度）；③ 期望存在但拿不到观测 ⇒ `unverified`（不是 failed）。`ExecutionFor(error)` 由错误目录推导执行阶段（未开始/已派发/结果不明），`IsGateDenial(error)` 把**接管/暂停/授权**三种拒绝判成 `blocked`（未开始、无副作用、不得自动重试）而不是普通 `failed` |

## 3. 契约落点（在 `Pudding.Contracts/Desktop/`，BCL-only）

| 文件 | 内容 |
|------|------|
| `BrowserControlContracts.cs` | 控制/授权世代、执行租约、`BrowserControlSnapshot`、`IBrowserAutomationAuthority` / `IBrowserAutomationControl` 两个窄端口、`BrowserAutomationOperation` + `BrowserOperationPolicy`（§3.2 表格的唯一真源）、`BrowserAutomationRequest` / `BrowserAuthorizationDecision` |
| `BrowserGrantContracts.cs` | `BrowserPageGrantScope`（read/write/manage）、`BrowserCallerIdentity`（含子代理父任务）、`BrowserPageGrantBinding`（实例+进程+连接世代）、`BrowserPageGrant`、`BrowserManagementGrant`（任务级管理授权）、`BrowserAuthorizationDenial` + 线名 |
| `BrowserActionReceiptContracts.cs` | 执行/验证/完成三组枚举与线名、`BrowserActionAssertion`（长度上限 + 敏感字段只留 length）、`BrowserObservationStamp`、`BrowserActionEffectSummary`、`BrowserActionReceipt`（completion/ok/safeToRetry 由入参派生，不可自相矛盾） |
| `BrowserRetryContracts.cs` | `BrowserRetryDisposition` + 线名、`BrowserRetryDecision`（纯数据；判定在组件内） |

**刻意不改动**：`DesktopCapabilityErrorCode` 与 `DesktopCapabilityErrors` 的线缆错误表（`ErrorTaxonomyTests` 对其做快照）。
线缆可见的原因码属于**接入切片**，B0 只在新增契约里给结构化拒绝原因。

## 4. 与消费方的关系

- **已接线（S5，2026-10-08）**：`Pudding.DesktopService`（Navigate/Interact/Tabs 后置条件）与
  `PuddingHost`（Bridge 的 `InteractAsync`）都调用 `BrowserMutationPostcondition`，两条传输因此
  **同语义**；原 `Source/Pudding.DesktopService/DesktopMutationInvariants.cs` 已删除。
- **尚未接线**：三份互不相通的 takeover/paused 状态（Shell 只写最不权威的一份，见
  `Docs/09_audit/2026-10-08-浏览器控制权与准入闸门核验.md`）尚未收敛到本组件的唯一
  `IBrowserAutomationAuthority` 实例；`IBrowserAutomationControl` 也没有 UI 侧驱动者。
- 回执（`BrowserActionReceipt`）目前只在组件内被判定与测试，尚未进入
  `DesktopInteractionResult` 或工具输出 —— 那需要工具层新增 `expect` 参数，属后续切片。

## 5. 测试

`Source/PuddingBrowser.AutomationTests/`（只引用本组件）：194 用例。

| 文件 | 覆盖 |
|------|------|
| `BrowserReadingFormatTests.cs` | 阅读面：默认 outline 只留可交互/结构行、interactive 只留带 ref 的行、full 不设预算、`output_budget` 与 `incomplete_scan` 的区分与游标续读、UTF-8 字节口径、单行超预算仍返回、非法预算取红、合成大树上默认模式收敛到 4 KB 内 |
| `BrowserInteractionReceiptTests.cs` | 交互回执（G7）：`expect_value` 匹配 ⇒ verified（不匹配 ⇒ failed）、**表单值零回显**（只给 matched 与长度）、无期望 ⇒ dispatched 不假装成功、有期望但拿不到观测 ⇒ unverified、select/check 用调用方自给的目标值判定、执行阶段由错误目录推导、未开始+可重试才 safeToRetry |
| `BrowserRefIdentityTests.cs` | 引用身份与三个事实（设计 §5 / G4）：跨页/跨 frame（含「两页都恰好 v7」）、同角色同名字的新节点**不让旧引用复活**、整页/单 frame/单引用分类作废、普通 DOM 变更与滚动**不作废任何**引用、宿主存活校验、视口变化只让坐标失效、只读调用**零推进** |
| `BrowserMutationPostconditionTests.cs` | 变更类后置条件：fill/click 不推进版本仍判成功、无活版本是真失败、显式声明导航未满足报 `outcome_unknown`、tab close 按「是否关掉」判、关最后一页无剩余版本仍成功 |
| `BrowserControlAuthorityTests.cs` | G1：接管/暂停拒绝新写步骤、只读仍可用、接管不撤销阅读授权、接管确认后复检失败、恢复不复活旧租约/世代推进、关闭与连接世代改变作废全部许可、租约按页串行与重传复用 |
| `BrowserPageGrantTests.cs` | G2：同 context 非目标页拒绝、read 不授权 write、TabClose 需 manage+read、撤销/切目标/跨实例/跨 frame 拒绝、子代理不得借用或扩大父授权、任务级管理授权 |
| `BrowserReceiptJudgeTests.cs` | G3：fill/select 同文档 verified、click 菜单不因版本未增假失败、断言失败是 failed、超时已提交是 dispatched+unverified、取消未派发可安全重试、断连 unknown |
| `BrowserRetryPolicyTests.cs` | 重试裁定矩阵与线名稳定性 |
| `BrowserOperationLedgerTests.cs` | 同 ID 重传驱动执行次数 ≤ 1、同 ID 不同请求拒绝、`receipt_expired`、容量耗尽拒新写且不淘汰在途、tombstone 回收、只读查询入口 |
| `BrowserOperationPolicyTests.cs` | §3.2 表格的机器可验快照 + 枚举覆盖完整性（新增操作必须显式分类） |
| `BrowserAssertionRedactionTests.cs` | 长度上限、敏感字段零回显、日志摘要不含值、线名稳定性 |
| `ComponentBoundaryTests.cs` · `ComponentSurfaceTests.cs` | S3/S4 边界断言（含探测器自检与取红）、公开面冻结、两窄端口成员冻结、逐类**用例守恒** |

## 6. 已知边界（诚实登记）

- 写操作的**排队**不在本组件：`Admit` 对同页第二个写操作返回 `page_busy`，队列由 Desktop 映射层在 B1 决定。
- 上下文级管理授权（`ContextCreate/ContextClose/TabNew`）由**任务级** `BrowserManagementGrant` 表达，
  因为这些操作发生时还没有可授权的页面；按 context 粒度的 grant 由 B1 的生命周期端口决定。
- 账本不保证跨进程 exactly-once（与设计一致）。
- `BrowserMutationPostcondition` 目前只在**没有**显式后置条件的情况下判定成功/失败；
  `BrowserInteractRequest` 还没有 `expect` 参数，因此 `DocumentNavigation` 目前只被 navigate 使用。
  工具层暴露 `expect` 属后续切片。
- **`BrowserRefRegistry` 尚未被驱动消费**（2026-10-08，诚实登记）：它是设计 §5 的契约与判据层。
  驱动侧**已**用同语义的**页面作用域 token** 关掉了「跨页/跨 frame 同版本号命中」这一条
  （ref 线格式 `p{页面token}-v{文档世代}-n{序号}`，见 `Source/PuddingBrowser.WebView2/` 的 code_map），
  但**尚未**消费本登记表，因此以下两点仍未闭合：
  ① **宿主绑定的节点身份**：节点标记仍是页面可自写的 `data-pudding-ref`；真正的宿主身份需要
  CDP `backendNodeId`，而 CDP 在本驱动里仍是 `NotSupportedException("CDP not in Phase 2A-1")`（属 B3）；
  ② **宿主存活校验**（节点是否仍 connected / 仍在同一文档）与三个事实的分别维护。
  接入方式：`WebView2BrowserPage` 持有登记表，在 snapshot/locate 返回 ref 时按自己的 scope 登记，
  交互前用 `Validate`/`ValidateForCoordinates` 复检；届时驱动内那份 token 校验可以退化为快速路径。
