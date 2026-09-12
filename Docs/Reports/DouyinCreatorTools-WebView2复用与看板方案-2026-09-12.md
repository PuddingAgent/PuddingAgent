# douyin-creator-tools 调研与 WebView2 续建方案

日期：2026-09-12。性质：源码研究与任务设计，未实施产品代码、未运行抖音操作、未重新执行 Desktop/真实模型验收。现有权威边界沿用 ADR-066 与 68 实施规格；本文补充当前差距和实施顺序，不批准跳过 Phase 2A-3B/3C 准入。

## 结论

继续建设现有 WebView2 Browser，不新增 Playwright Sidecar 或兼容层。Pudding 已有 Context/Page、认证 Bridge、Snapshot/Locator/Interact/Wait 七项工具；真正前置是明确新构建上的真实 Agent 浏览器控制验收，以及抖音页面需要的通用能力差距核验。抖音领域逻辑仍属于 Core，上层 Adapter 只依赖 Browser Abstractions；WPF 只承担浏览器宿主与通用执行。

## 外部项目证据

本次通过网页与浅克隆核对 [wenyg/douyin-creator-tools](https://github.com/wenyg/douyin-creator-tools)，固定提交为 `e35dbe27548cff292cc2d709417467a3bd464ed1`。只读源码，没有安装依赖、执行其 Skill 或启动其浏览器。

| 来源（固定提交路径） | 已确认行为 | Pudding 应借鉴的部分 |
| --- | --- | --- |
| [douyin-browser.mjs](https://github.com/wenyg/douyin-creator-tools/blob/e35dbe27548cff292cc2d709417467a3bd464ed1/src/douyin-browser.mjs) | Playwright Chromium、独立持久 Profile、创作者中心评论页 | 账号绑定稳定 Context/UDF，用户在可见浏览器登录；不迁移 Chromium Profile |
| [works-panel.mjs](https://github.com/wenyg/douyin-creator-tools/blob/e35dbe27548cff292cc2d709417467a3bd464ed1/src/lib/works-panel.mjs)、[comment-snapshot.mjs](https://github.com/wenyg/douyin-creator-tools/blob/e35dbe27548cff292cc2d709417467a3bd464ed1/src/lib/comment-snapshot.mjs) | DOM 提取、作品面板滚动、评论及回复标记识别，大量 evaluate | 选择器和解析在 Douyin Adapter；虚拟列表必须有去重、终止条件和有界预算 |
| [interactive-comment-protocol.mjs](https://github.com/wenyg/douyin-creator-tools/blob/e35dbe27548cff292cc2d709417467a3bd464ed1/src/lib/interactive-comment-protocol.mjs) | comment_found/requestId 与 reply/skip/stop 决策关联 | 映射到 Pudding canonical 工具调用与结果，不另建 stdin/文件回调调度体系 |
| [browser-session-lock.mjs](https://github.com/wenyg/douyin-creator-tools/blob/e35dbe27548cff292cc2d709417467a3bd464ed1/src/lib/browser-session-lock.mjs) | Profile 原子独占和存活进程检测 | 一个账号一个操作队列，跨 Run 互斥并支持用户接管 |
| [reply-ledger.mjs](https://github.com/wenyg/douyin-creator-tools/blob/e35dbe27548cff292cc2d709417467a3bd464ed1/src/lib/reply-ledger.mjs)、[reply-flow.mjs](https://github.com/wenyg/douyin-creator-tools/blob/e35dbe27548cff292cc2d709417467a3bd464ed1/src/lib/reply-flow.mjs) | 发送前追加并 fsync 台账；结合数据库和页面标记去重；点击后无确认记 sent_unconfirmed，不自动重发 | 持久 ReplyIntent、写入边界和不确定结果对账，比替换浏览器驱动更重要 |

这是一条网页登录自动化路线，不是官方 OpenAPI SDK。仓库还包含文章/图文发布，但首批仅研究作品和评论链路；发布需要另行定义范围与上传验收。没有在当前树找到 LICENSE 文件或 package.json license 字段，实施按独立实现和行为参考处理，不直接搬入源码。

不能照搬的细节：台账键使用作品标题、用户名、评论文本，无法保证跨账号、同名作品或重复评论的唯一身份；部分回复匹配存在 username-only 或文本包含比较。Pudding 自动写入必须要求唯一目标：优先 accountId/workId/commentId；无稳定平台 ID 时保留页面证据、定位版本和明确的弱身份标记，歧义只能预览/人工确认。外部仓库测试不证明抖音当前页面可用，本次也没有实测登录和发评。

## 当前代码与差距

| 层 | 当前源码依据 | 判断 |
| --- | --- | --- |
| 通用接口与工具 | Source/PuddingBrowser.Abstractions；Source/PuddingBrowser.AgentTools | 已有七项工具和通用合同，可复用 |
| Core/Desktop 通信 | Source/PuddingHost/BrowserBridge/RemoteBrowserPage.cs；Source/PuddingDesktop/Browser/BrowserBridgeCommandDispatcher.cs | 已有远程执行和认证 Bridge，保留既有调用来源、暂停/接管和错误语义 |
| WebView2 | Source/PuddingBrowser.WebView2/WebView2BrowserRuntime.cs、WebView2BrowserPage.cs、WebView2DomClient.cs | 独立 UDF、导航、快照、定位、输入和容器滚动已存在；账号长期绑定与并发所有权仍需业务验收 |
| 能力缺口 | RemoteBrowserPage.cs 与 WebView2BrowserPage.cs | 任意 Evaluate、CDP、Screenshot 等仍 Unsupported；接口存在不等于已实现 |
| 页面兼容性 | WebView2DomClient.cs | click/fill/press 通过 DOM 方法和合成事件执行；需验证真实 contenteditable/框架输入响应，不能假定等价于 Playwright 输入 |
| 业务层 | 当前 Source 中未发现 Douyin 实现项目 | 作品/评论 DTO、选择器版本、账号映射、ReplyIntent 与对账仍待实施 |

76 验收报告记载确定性实现及 WebView2 TestSite 已验收，但真实 DeepSeek smoke pending；77/78/79 定义了工具选择、外部控制器和真实 Agent 控制闭环。本次没有找到可替代该门禁的新验收证据，故继续保留。不能将历史测试通过数报告为本轮通过。

最小能力策略：先验证七工具能否完成只读作品/评论路径。若 DOM 快照不能表达必要字段或分组，记录具体失败页面和需求，按 68 规格补通用、可限额的结构化提取或 BrowserScript 能力，经 Abstractions → Protocol → Remote → Dispatcher → WebView2 全链路接通；业务脚本仍在 Adapter。不要为跑原脚本一次性开放 Cookie/CDP/下载/上传全部能力。

## 三个工作包与验收

### DY-00：WebView2 真实 Agent 前置验收（P1）

所有者：Browser/Desktop 与进程外验收控制器。复核 Phase 2A-3C 源码/测试和当前构建，先交付 ready-for-external-deploy；进程外控制器部署明确版本，然后用户选定 Agent/DataRoot 上按 77 执行真实 DeepSeek 可见 smoke。保留 sessionId/runId/toolCallId/operationId、构建标识和脱敏 BrowserActivity，覆盖导航→快照→定位→输入→等待、stale ref、暂停/人工接管、断连和取消。生命周期结论仍由进程外控制器给出；缺测试配置时登记阻塞，不读复制 LLM Secret。既有环境卡 88414e5de53248789e0bfa11edae66b5 可作为连接故障线索，需复现后再判断是否仍阻塞。

### DY-01：抖音只读 Adapter 与通用能力差距收口（P1，依赖 DY-00）

所有者：Core Integration；通用缺口交 Browser 层。DY-00 通过后，先产出 Phase 2A-4 工作指令，再实现 PuddingIntegration.Douyin、账号→Context 映射、DouyinBrowserClient、版本化 LocatorProfile、Work/Comment DTO。配置文件保存非密钥配置，持久进度和业务事实存储于 Core；不修改用户 Chrome。

首个产品切片：可见登录→选自己作品→滚动读取评论/回复→结构化导出。验收覆盖同名作品、同名用户、重复文本、空列表、虚拟滚动去重、加载停滞、登录失效、DOM 变更、取消、重启后登录态复用。必须返回 Complete/Partial 与 cursor/stopReason，不将滚动到当前底部推断为全量完成。失败需有脱敏证据。此阶段不点击发送。

### DY-02：可靠回复与逐条 Agent 决策（P2，依赖 DY-01）

所有者：Core Integration/消息执行层。先完成只读→生成草稿/预览→单条明确目标回复，再扩展有界队列；复用 Pudding 任务、取消和 canonical 工具审计，不启动常驻 Node 服务或另造自动调度。

按 68 的 ReplyIntent 设计细化：Prepared → SendAttempted（先持久化）→ Confirmed / SentUnconfirmed。发送前复核账号、作品、评论身份及已回复证据，并按账号串行互斥；同一 intent 重放不再点击发送。点击后超时、断连或进程重启均先对账，无法确认保持 SentUnconfirmed，不自动重发；明确未发生副作用的失败才允许受控重试。Bridge 操作去重仅是传输保障，不能代替业务持久台账。

验收覆盖双击/重复调用、多 Run 并发、发送前后崩溃、页面回复标记迟到、数据库写入失败、歧义目标、用户接管与取消。成功必须有页面回复证据，不能仅凭 click 返回成功。真实写入 smoke 使用明确授权账号和指定评论，实际发送内容与范围在执行时确认；本轮研究不执行任何写入。

## 看板登记

本轮通过 External Task API v1 全量读取 default：119 条非归档任务及 2 条归档任务，按标题/描述检索未找到明确 Playwright、WebView2 或抖音前置原卡（存在一张 browser_not_available 环境卡）。因此补建上述三卡，保留原有历史状态，不推断原卡已完成或被删除。依赖先登记为描述和验收门禁，不声称当前 API 有结构化依赖调度。

已创建并经 GET 复读确认的任务（均为 Backlog，autoDispatchEnabled=false）：

| 工作包 | taskId | 依赖 |
| --- | --- | --- |
| DY-00 WebView2 真实 Agent 浏览器前置验收 | 4fa25304b48243f6bbe2e5d954594243 | 77/78/79 验收与用户选定测试配置 |
| DY-01 抖音只读 Adapter | 1473dd05d79c4e9987ee16adcdff4c95 | DY-00 |
| DY-02 抖音可靠回复 | 01f23c44076547c69ae592a29e02a0fd | DY-01 |

入口：http://localhost/admin/workspace/default/tasks 。创建和复读 JSON 回执保存在本机忽略目录 `.firecrawl/*-create-receipt.json` 与 `.firecrawl/*-verified.json`，不包含访问 Token。本轮没有派发、run-now 或改变既有卡状态。
