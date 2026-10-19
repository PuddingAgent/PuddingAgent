---
title: ADR-097 Agent 浏览器控制一期闭环与单一执行链
author: hyfree
date: 2026-10-18
last_reviewed: 2026-10-19
status: active
description: "以真实 Agent 在 PuddingDesktop 中完成可见浏览器任务作为一期交付目标；选择 Bridge 单一路由、独立于传输的 Desktop 执行器、单一会话控制权及分层验收。"
categories: [docs, architecture]
tags: [adr, browser, agent, desktop, execution]
related_docs: [Docs/12_features/Agent浏览器控制一期整体实施方案-2026-10-18.md, Docs/14_reports/Agent浏览器控制架构与实现审阅-2026-10-18.md, Docs/11_design/浏览器控制简化架构方案-2026-10-13.md, Docs/12_features/浏览器交付修订施工方案-2026-10-12.md, Docs/12_features/ADR-Desktop-Shell-WebUI-Separate-Core-2026-09-29.md, Docs/12_features/浏览器发现契约与配套部署修复方案-2026-10-18.md]
related_files: [Source/PuddingHost/BrowserBridge/BrowserBridgeServiceCollectionExtensions.cs, Source/Pudding.DesktopService/DesktopService.cs, Source/PuddingBrowser.Authorization/AgentTargetSessionAuthority.cs, Source/PuddingDesktop/MainWindow.xaml.cs]
slug: architecture-adr-097-agent-browser-control
draft: false
---

# ADR-097 Agent 浏览器控制一期闭环与单一执行链

## 1. 决策范围与优先级

**一期首先交付：用户向 PuddingAgent 下达浏览器任务，Agent 在 PuddingDesktop 的可见 WebView2 中完成操作，观察结果并如实答复。** 架构、组件、组合根、默认配置、工具发现、模型循环及验收共同构成这项交付。类型齐全、测试通过、清单可生成均不能单独代替它。

本 ADR 是浏览器一期的实施基线，取代 [10-12 修订方案](../12_features/浏览器交付修订施工方案-2026-10-12.md) 和 [10-13 简化方案](../11_design/浏览器控制简化架构方案-2026-10-13.md) 中与之冲突的目标架构和施工顺序。两份材料的历史证据保留。此处规定目标行为，不宣称实现已完成；代码现状与证据见[审阅报告](../14_reports/Agent浏览器控制架构与实现审阅-2026-10-18.md)。

继续遵守 [Shell / Web / Core ADR](../12_features/ADR-Desktop-Shell-WebUI-Separate-Core-2026-09-29.md)：Agent Loop、工具与业务在独立 Core，WinUI Shell 只承载窗口、WebView2 与进程监督；不装配进程内 Host，不载入 WPF UI。通用控制先于 Douyin Adapter。

## 2. 决策

### D1：一个可验收的产品主路径

一期基线是 **DesktopChild + 已启用 Browser Automation + 默认关闭的 capability channel + 单个主 Agent 会话**。内置 Agent 浏览器不要求预先人工选页：首条携带可信 Core 所有者的页面命令建立控制会话，同一所有者的后续请求可切换目标。普通导航、定位、点击、输入、滚动和观察连续执行，不再人工配置 grant 或为了浏览器开启第二通道。显式撤权、暂停和接管仍有效。

八个 `browser_*` 工具必须进入同一事实目录，注册、发现、提示词、测试和状态展示从该目录及运行时支持集生成。浏览器未就绪时返回具体恢复步骤；注册了工具不等于后端已就绪。通用 Console 的无浏览器模式维持明确不可用。

### D2：浏览器一期固定 Bridge，执行器与传输解耦

浏览器工具使用认证 WebSocket Bridge，Host 不再因 gRPC 会话出现或消失切换浏览器后端。gRPC 其他能力不在本 ADR 的删除范围；若以后承载浏览器，也只能适配到同一个执行器，不能恢复第二套 handler。

将 `DesktopService` 的浏览器执行部分从 `DesktopCapabilityChannelComposition` 的生命周期中解耦：浏览器工作区就绪即构造，Bridge 的导航、快照、交互、管理及截图全部调用它。`BrowserWorkspaceController` 保留用户 UI 的薄协调职责，Agent 命令不得绕过执行器直接操作页面。

执行器表面的管理操作必须经 Abstractions 中的 `IBrowserWorkspaceLifecycle` 调用工作区所有者；不能直接在底层 runtime 创建、激活或关闭页面后就返回成功。工作区负责真实资源、页签、可见表面及生命周期登记同步；登记/展示失败须回收刚创建的资源并报告失败。每个页签保存自己的 contextId，关闭上下文只回收其所属页面。内容操作仍由统一执行器调用驱动，此端口不增加第二套导航/交互 handler。产品组合构造必须传入所有者，缺失在构造期拒绝。

连接选择在发起前固定；已派发或结果未知的操作不得跨传输重试。明确放弃本期“双后端同时实现同一浏览器语义”的目标。

### D3：一个授权会话、一个执行入口、一个页面所有者

以独立组件 `PuddingBrowser.Authorization` 为会话控制规则的目标归属；**先修正并独立验证该组件，再接宿主**。当前类型是候选实现，不能因其存在就认定可直接替换旧权威。

- `BrowserPageLifecycle` 持有页面登记、会话权威与回收入口；Shell 通过只含 Contracts/标量的门面调用。
- V1 会话目标来自明确登记为 AgentAuthorized 的内置浏览器上下文，必须由携带可信 owner 的页面命令或显式交页动作选择；可见活动页变化不能偷偷改目标。Workbench 与 Untrusted 上下文不能靠会话票据取得页面内容访问权。
- 会话有一个可信的 Core 会话所有者；身份由宿主注入，模型不能自报所有者或确认凭据。Agent 名称等诊断字段可以未知，**控制会话句柄与归属不能伪造或缺省放行**。
- 不再叠加每次调用的任务 grant、租约签发、binding gate 三套授权流程。权限决策只在 Desktop 权威发生；Core 负责工具策略、可信来源及会话生命周期。
- 切换时原子替换生产授权路径。旧 `BrowserAutomationAuthority` 的执行准入不能与新 authority 同时作为最终裁决者；Automation 中的回执、ref、输入规划等纯逻辑继续复用。

### D4：简化授权不能删除执行不变量

每次操作仍需不可变票据：控制会话、连接世代、目标版本、唯一 operationId、写世代与取消信号。入队前判定，触碰 WebView2 前复检，多步副作用在每步前复检，所有退出路径精确释放。

暂停与接管保留已授权阅读，阻止新写并取消旧写；恢复不能复活旧写票据。采用独立写世代或等效机制，不能只翻转一个布尔值。完成 A 操作的迟到回调不得释放 B 的占用。页面撤销、关闭、连接换代使旧会话失效；不自动恢复旧控制权。

Bridge 在线只证明传输活性，不授予任意页面的控制权。取消目标后的拒绝必须在默认 Bridge 路径也成立。已发生的浏览器副作用不能靠取消回滚。

状态快照与用户命令必须分开：初始化、页面关闭后的空目标及普通 PropertyChanged 只同步事实，不能转成用户撤权；相同的非空 UI 快照也不能反复覆盖会话当前目标。显式撤权才暂停后续隐式绑定；关页/断连通过各自生命周期端口作废旧票据。恢复控制必须是明确动作，空快照不能撤销或恢复用户决定。

### D5：观察驱动动作，结果报告实际执行事实

默认循环是“发现目标 → 导航/等待 → 快照 → 从快照定位 → 一个动作 → 新观察验证”。复用有版本的 ref；动作提交后不重查旧 Locator。读取不推进文档版本，导航后旧 ref 必须失效。

页面版本由持有真实文档的驱动签发。WebView2 初始化完成后已存在初始 `about:blank` 文档，即使没有传入 URL、尚未收到导航完成事件，也必须有正数版本；0 只表示没有已知的活文档。页面与发现描述须一致，首次导航继续递增。不能在工具或适配器中把 0 强行转换成 1，也不能删除新建页的后置版本校验。组件门禁必须覆盖“不传 URL 新建 → 空白页读取 → 首次导航 → 新观察 → 交互”，不能只测试带 InitialUrl 的新建。

工具回执使用原 operationId，区分未执行、已派发、已观察结果和结果未知。`inputBackend`、滚动结果、表单断言来自执行器的实际结果，不能从请求参数反推成功。`input_mode=native` 未支持时明确拒绝，不降为 DOM 后回报 native。

截图是一期能力，默认 Bridge 必须可用。图片字节进入 workspace 工件，再经 `LlmImagePart` 进入支持视觉的模型；必须在模型请求边界验证图片真正存在。文本模型明确报告视觉不可用，DOM 任务仍可完成，不偷偷换模型。截图成功不自动等于坐标输入已可用。

截图采集与模型细节级别须端到端一致：`LlmImagePart(artifactId, detail)` 的第二参是细节级别，MIME 来自工件内容，不能占用 detail。浏览器 `low` 为等比例最长边不超过 512 像素的预览，`high/original` 保留设备像素；文字识别与细小商品差异应显式选择 original 或区域截图。网关保留低清选择，canonical original/high/auto 映射为供应商 high。根据 [DeepSeek Detail Level](https://api-docs.deepseek.com/zh-cn/guides/vision/)，服务端 low 为 512×512 处理，high 等价 original，auto 当前也等价 original；本地预览策略不冒充服务端像素变换，也不把 JPEG quality 当作 detail。浏览器工具当前只提供 low/high/original，不新增含糊的 auto 采集策略。Responses 的工具图片可进入 function_call_output；Chat Completions 不支持的工具图片路径继续明确失败，不借 user 消息隐式绕过。

真实采集参数由纯组件生成：PNG/JPEG 格式、JPEG quality、区域和低清缩放必须实际传入 CDP。`window.devicePixelRatio` 与 visual viewport scale 分别采集，不能互相代用；CDP clip 按 [Page 协议](https://chromedevtools.github.io/devtools-protocol/tot/Page/#method-captureScreenshot) 使用文档坐标及页面 zoom。回执尺寸从编码后的图片头读取并与计划核对，格式、尺寸不符或捕获期间页面版本改变即失败，不登记 observation。观察元数据的裁剪原点为文档 CSS 坐标；图片坐标还原为视口 CSS 坐标后，点击、移动和拖拽两端均须把转换结果真正交给驱动。无参数 CDP 命令将 nullable null、JSON null、Undefined 统一序列化为 `{}`。

截图需要已布局且可见的浏览器表面。WinUI 必须检查祖先工具区/工作台是否折叠，WPF 检查有效可见性；不可采集时明确提示打开工具区并选择页签，不等待一个不会到来的合成帧，也不为了后台操作强抢用户焦点。CDP 等待受命令取消令牌约束，即使 COM 调用未返回，调用者也能结束等待；底层只读截图迟到结果不得发布为新 observation。

Bridge 收包与命令执行分离：每代连接一个有界串行命令队列，保持命令顺序；接收循环继续处理心跳、取消和断连。取消同时覆盖排队和已执行中的命令，满队列明确报告未派发，连接结束回收队列。不能以长截图/等待为理由暂停连接存活检测。

### D6：保留现有技术栈，借鉴开源的职责边界

不为一期引入 Node REPL、Playwright sidecar 或新的浏览器进程。借鉴的是资源归属、工具就绪、动作观察与结果回流，而非复制框架。

| 参考 | 采纳的原则 | Pudding 落点 |
|---|---|---|
| [DeepSeek Harness browser-use](https://github.com/deepseek-ai/deepseek-harness/blob/687f282e8625c6b8d11ff58211a90524be2e4317/docs/subsystems/browser-use.md) | 后端注册与任务循环分工、资源归属于活会话 | Host 就绪与发现；Desktop 会话/页面生命周期 |
| [ZCode control-browser](https://github.com/zai-org/ZCode/blob/aac4755666d09fdcd70272fcf063c077a639015f/apps/zcode-cli/packages/browser-use-plugin/skills/control-browser/SKILL.md) | 按实际支持能力操作、快照驱动定位、动作后验证 | 八工具契约及浏览器操作指引 |
| [Codex MCP handler](https://github.com/openai/codex/blob/8c076942cb70fba8e5c4732c23f1faabc4aed484/codex-rs/core/src/tools/handlers/mcp.rs) | 统一工具描述、调用上下文、取消与结果回流 | 复用 Pudding 工具执行链，不另建浏览器 Agent Loop |

以上是本项目的设计取舍。Codex 的公开 MCP 实现不是其桌面内嵌浏览器的完整源码证明；ZCode 的 JS 调用方式也不是 Pudding 必须实现的接口。研究范围见[报告](../14_reports/Agent浏览器控制架构与实现审阅-2026-10-18.md)。

### D7：按用户能力递进验收

先通过独立组件门禁，再接入完整主链；接入后的第一条目标测试必须是主 Agent 的真实任务。子代理、多会话并行控制、截图坐标、复杂上传下载与站点适配排在主链之后，分别声明支持范围，不能用这些切片的数量替代主链进度。

一期开工后的产品验收要求：

1. 外部控制器启动明确的新构建，记录 Desktop/Core/前端构建标识及配置。
2. 用户选定测试 Agent/DataRoot；真实模型仅凭自然语言任务，自行发现并调用浏览器工具，完成 TestSite 上可观察的表单/导航/阅读任务。
3. 覆盖选择/换页/取消目标、接管与只读、旧 ref、断连及未知结果不重复执行；截图与视觉按模型能力分别验证。
4. 进程内证据可声明 `in-product-functional-complete`；启动、重启、崩溃恢复、退出回收仍由外部控制器声明 `external-lifecycle-verified`。两者均满足才可称一期产品交付。

### D8：命令结果合同与发现面不可被内部能力形状替代

Bridge 每条命令有唯一的参数、结果及无值语义，由 Protocol 定义。内部能力可返回全集，但传输适配器必须按请求精确投影：context.getInfo 是单对象，context.list 是列表；goto 与 back/forward/reload/stop 按各自消费者合同返回；close 的成功可以无值。有值消费者严格校验 required 字段，不能以可选化、双形状解析或猜第一项掩盖错配。

发现只读取有界元数据，不通过逐页内容/控制准入来补全；无目标时能发现，页面内容和副作用仍需合法会话。原始 wire、内部 Contracts 与 Agent 摘要是三个不同边界，不允许拿摘要证明原始协议字段是否存在。结果解码失败不能撤销已发生的副作用，也不能据此自动重试。

必需门禁是实际生产者 → wire 编解码 → 实际消费者的连通测试；两侧各自构造期望 DTO 的单测不能替代它。具体 19 条命令矩阵、映射位置与发现端口见[契约修复方案](../12_features/浏览器发现契约与配套部署修复方案-2026-10-18.md)。

### D9：按配套产物交付，协议就绪与页面可控分开

本次合同修复整体发布协议修订 3，两端严格匹配，不提供旧新载荷兼容分支；修订号只隔离不支持的混配，正确性仍由 D8 门禁证明。Hello/Ack 应携带协议修订与可诊断构建身份；正式发布清单绑定 Desktop、Core 及共享程序集的预期哈希。实际 Core 路径及进程加载模块身份必须与清单核对，不能以 DLL 长度、时间戳或“都重启了”代替。

部署采用完整配套包，禁止只替换 Protocol.dll；显式 Core 路径配置必须在切换前核验。传输连接、合同可用、发现成功、控制会话成立分别呈现，握手成功不是 Agent 可控。协议不匹配时停止浏览器工具派发并提供修复入口，Shell 仍可启动和进入运行中心。产品外部部署及在飞任务处理遵守两段式验收边界。

握手的外层 envelope 和内部 Hello/Ack 都必须检查版本；门禁分别注入两处版本错误。外层不兼容的端无法读取新版 Ack，因此使用不依赖应用协议版本的 WebSocket policy close，附有界错误码与 expected/received；Core 日志记录连接标识及拒绝阶段，不记录原始帧或认证凭据。Desktop 必须保留首次终止原因，不能把接收错误、取消或拒绝统称为超时；关闭操作有独立短时限。

配套清单对每个目标文件分别绑定预期哈希。不同目标框架或构建路径可能使同源共享程序集的二进制哈希不同，不能拿两侧 DLL 字节完全一致替代协议修订、源身份及各自清单验证。`bootstrap_reboot(desktop-build)` 的当前执行单位是 Core 项目，不构成 Shell + Core 配套更新；跨 Bridge 合同变更必须通过进程外的完整包部署。

## 3. 后果与替代方案

复用 WebView2 驱动、八工具及既有协议/回执资产，投入集中在组合根、语义透传与真实 Agent 验证。代价是需要迁移 Bridge handler 并重新验证旧测试假设；不能把旧测试全绿当成新会话模型已正确。

不采纳“只补文档和外部验收”：它不能修复默认截图依赖及执行路径分叉。不采纳“直接整体删除旧授权文件”：须先核对非浏览器调用方、修复候选会话组件并通过边界与正反向测试。不采纳“桥在线就全部放行”：连接状态不能证明目标归属。

实施顺序、动作支持矩阵与证据合同见[一期整体实施方案](../12_features/Agent浏览器控制一期整体实施方案-2026-10-18.md)。
