---
title: Pudding.Contracts CodeMAP
author: hyfree
date: 2026-10-01
last_reviewed: 2026-10-02
status: active
description: "CapabilityResult<T>：成功携带类型化输出，失败携带结构化领域错误。不用异常表达业务失败，因为跨进程失败需要区分「未执行 / 可能已产生副作用 / 可重试」。 default(CapabilityResult<T>) 是未初始化状态：IsSuccess=false、读 Error/Value 取红（抓「忘记赋值」）。"
categories: [docs]
tags: [code, map]
related_docs: [Docs/12_features/Desktop-Contracts-Grpc-Capability-Plan-2026-10-01.md, Docs/00_changelog/2026Year/10/2026-10-02-Pudding.Contracts-code_map迁出的变更记录.md]
related_files: [Docs/00_changelog/2026Year/10/2026-10-02-Pudding.Contracts-code_map迁出的变更记录.md]
slug: code-map
draft: false
---

# Pudding.Contracts CodeMAP

> 平台与传输无关的**契约叶程序集**：接口 · 不可变 DTO · 能力标识 · 错误语义 · 审计形状
> 边界（[技术方案](../../Docs/12_features/Desktop-Contracts-Grpc-Capability-Plan-2026-10-01.md) §3）：**仅 BCL**。
> `ProjectReference = 0`、`PackageReference = 0`，由编译期 Target `EnforceContractsBoundary` 取红；
> UI / HTTP / gRPC / Protobuf / DI / 数据库 / 宿主引用一律属于传输适配器，不属于这里。
> 命名空间：`Pudding.Contracts`（原语与横切）/ `Pudding.Contracts.Desktop`（能力 DTO）/ `Pudding.Contracts.Audit`
> 测试：`Source/Pudding.ContractsTests`（只引用本组件；含 5 条边界/形状断言）
> 历史变更与门禁记录已迁至 [`Docs/00_changelog/2026Year/10/2026-10-02-Pudding.Contracts-code_map迁出的变更记录.md`](../../Docs/00_changelog/2026Year/10/2026-10-02-Pudding.Contracts-code_map迁出的变更记录.md)。本文件只保留索引，不再追加日志。

## 身份与调用上下文（`Pudding.Contracts`）

| 文件 | 用途 |
|------|------|
| `Identity.cs` | 身份/操作标识集合：实例·启动·操作·关联·世代；含 `ContractText` 校验（见下「关键语义」） |
| `DesktopCallContext.cs` | 调用上下文：DesktopId/OperationId/UTC Deadline/关联 + `IsExpiredAt`（见下「关键语义」） |
| `DesktopProtocolVersion.cs` | 协商版本区间常量（`Current = Minimum = 1`）与 `TryNegotiate` |

**关键语义**

**`Identity.cs`**：`DesktopInstanceId`（按 DesktopHome/DataRoot 隔离的实例身份，**不是** PID）、`DesktopProcessInstanceId`（单次启动实例，用于识别 Core 重启）、`OperationId`（待完成表与幂等复用键）、`DesktopCorrelationId`（业务关联，不参与权限）、`ConnectionGeneration`（Core 在握手回执分配，每次重连递增；`None`=未握手）、`ContractText`（内部校验：标识符限可见 ASCII，展示文本允许 CJK 且超长取红）
**`DesktopCallContext.cs`**：调用上下文：DesktopId + OperationId + **UTC** Deadline + 可选 CorrelationId；`IsExpiredAt` / `RemainingAt`。身份与权限不由模型填写字段决定；页面目标属于请求 DTO，禁止「当前激活 Tab」隐式定位 另含**类型化 caller**（`BrowserCaller`；`TryGetBrowserCaller` 对"缺身份 / User 冒充 / 无运行身份"分别给具体原因）
**`DesktopProtocolVersion.cs`**：协商版本区间常量（`Current = Minimum = 1`）与 `TryNegotiate`。线上字段/编号的真源是 proto，本类只存平台无关版本号，映射测试断言两者一致

## 能力目录与协商（`Capabilities.cs`）

| 成员 | 用途 |
|------|------|
| `DesktopCapability` | 位标志能力标识（WebView 导航/脚本/页面状态、Shell 通知/状态/对话框/Picker/剪贴板）。**这是协商身份，不是权限** |
| `DesktopCapabilityTraits` | 执行语义标志：Mutating/HasSideEffects/RequiresTrustedContext/RequiresUserInteraction |
| `DesktopCapabilities` | **能力目录唯一真源**：线名/版本/Kind/Traits + `DeclareFor` 等（见下「关键语义」） |
| `DesktopCapabilityNegotiation.Validate` | 握手协商：Core 只能授予 Desktop 已声明且版本不高于声明的能力；越权授予 ⇒ 协议错误且**不做部分接受**（协商失败不产生任何可用能力） |

**关键语义**

**`DesktopCapabilityTraits`**：执行语义：`Mutating`（同目标串行）、`HasSideEffects`（取消/超时后可能已生效）、`RequiresTrustedContext`（普通网页不得调用）、`RequiresUserInteraction`
**`DesktopCapabilities`**：**能力目录唯一真源**：线名（`webview.navigate` 等）、版本、Kind、Traits；`DeclareFor` 生成握手声明；`TryGetByName` / `NameOf` / `Enumerate`。目录 ⊇ 当前已实现能力：未实现的能力**不声明**即不会被 Core 授予

> 已接通 wire payload 的能力：`webview.navigate` / `webview.execute_javascript` / `webview.page_state` /
> `shell.notification`（2026-10-01 起 `page_state` 也有命令 payload：proto 的 `GetPageStateCommand`）。
> 其余条目是**预留身份**，在切片 E 逐能力补齐 payload 后才可能被声明。

## 能力请求/结果判别联合（`Desktop/`）

| 成员 | 用途 |
|------|------|
| `DesktopCapabilityRequest` | 判别联合（每能力一分支 + 工厂 `For*`）：导航/脚本/通知…/Shell 状态（见下「关键语义」） |
| `DesktopCapabilityResponse` | 判别联合（每能力一类型化输出 + `Failure`）；构造期拒绝「成功+错误」歧义 |

**关键语义**

**`DesktopCapabilityRequest`**：判别联合（每个能力一个分支 + 工厂 `For*`）：导航/脚本/通知/页面状态/快照/定位/交互/等待/标签页/上下文清单/剪贴板/对话框/Picker/Shell 状态。`Target` 从分支取（无页面目标的为 `null`）；构造期保证「恰好一个分支非空」
**`DesktopCapabilityResponse`**：判别联合（每个能力一个类型化输出 + `Failure`）；构造期拒绝「成功 + 错误」歧义。存在理由：让执行器接缝与 UI 实现**都不依赖 proto**；新增能力在此追加分支与 `From*` 工厂

## 错误语义（`CapabilityErrors.cs`）

| 成员 | 用途 |
|------|------|
| `DesktopCapabilityErrorCode` | 领域错误码（计划 §5 的 12 个 + `InvalidRequest`/`InternalError`/`Disconnected`） |
| `DesktopCapabilityErrorCodes` | 线名 + 默认 `Retryable`/`MayHaveSideEffects` 真源；未知线名 fail closed |
| `DesktopCapabilityError` | 码 + 脱敏消息（剔控制字符·截断 512）+ 语义标志；含 `IsSafeToRetry` |

**关键语义**

**`DesktopCapabilityErrorCodes`**：线名 + 默认 `Retryable` / `MayHaveSideEffects` 真源；未知线名经 `ParseOrInternalError` 确定性 fail closed
**`DesktopCapabilityError`**：码 + 脱敏消息（剔除控制字符、截断 512）+ 两个语义标志；`IsSafeToRetry` = 可重试且无副作用。**不承载**脚本正文/URL/剪贴板内容/页面数据

## 结果形状（`CapabilityResult.cs`）

`CapabilityResult<T>`：成功携带类型化输出，失败携带结构化领域错误。不用异常表达业务失败，因为跨进程失败需要区分「未执行 / 可能已产生副作用 / 可重试」。
`default(CapabilityResult<T>)` 是**未初始化**状态：`IsSuccess=false`、读 `Error`/`Value` 取红（抓「忘记赋值」）。

## 能力 DTO（`Desktop/`）

| 文件 | 用途 |
|------|------|
| `PageContracts.cs` | 页面目标/版本/就绪度/状态契约：`DesktopPageTarget` 等四型（见下「关键语义」） |
| `WebViewContracts.cs` | 导航/脚本契约：`NavigateDisposition`、`JavascriptRequest/Result` 等（见下「关键语义」） |
| `ShellContracts.cs` | Shell 通知契约：`DesktopNotificationPriority/Request/Result`（见下「关键语义」） |
| `ApiInterfaces.cs` | 桌面 API 接口：`IPuddingDesktopApi`/WebView/Shell（见下「关键语义」） |
| `Desktop/BrowserControlPlaneRequests.cs` | 控制面载荷：`BrowserBindingApplyRequest`/`RevokeRequest` 与 ack（见下「关键语义」） |
| `Desktop/BrowserTaskBinding.cs` | 任务绑定 `BrowserTaskBinding`：可信元数据 + 唯一 grantee 规则（见下「关键语义」） |
| `Desktop/BrowserOperationMapping.cs` | 能力 → 自动化操作的唯一映射（能力级 + request-aware 重载）（见下「关键语义」） |
| `Desktop/IBrowserDelegationLifecycle.cs` | 浏览器委派生命周期窄端口（Contracts 声明·Host 实现）（见下「关键语义」） |
| `Desktop/BrowserCallerRequirement.cs` | 浏览器操作对调用者身份的统一要求（三情形各给原因）（见下「关键语义」） |
| `Desktop/BrowserCallerKindWire.cs` | 调用者类别的线缆编码真源（`user`/`agent`/`sub_agent`）（见下「关键语义」） |
| `Desktop/BrowserCallerIdentityProjection.cs` | 可信调用身份的投影（唯一产生 `BrowserCallerIdentity` 处）（见下「关键语义」） |
| `Desktop/LocateContracts.cs` | 元素定位契约：定位种类/定位器/请求/元素引用/结果（见下「关键语义」） |
| `Desktop/LocateCursor.cs` | 分页游标 `BrowserLocateCursor`（对模型不透明）（见下「关键语义」） |
| `Desktop/ReadingRevision.cs` | 读取结果的修订标识（绑定页面版本+查询指纹+内容哈希）（见下「关键语义」） |
| `Desktop/BrowserTextDiff.cs` | 行级差异：削公共前后缀后只对中间段 diff（见下「关键语义」） |
| `Desktop/StableHash.cs` | 跨进程稳定短哈希（SHA-256 截断）（见下「关键语义」） |
| `CapabilityErrors.cs` | 错误码 ↔ 线名真源（快照由契约测试冻结）（见下「关键语义」） |
| `Desktop/OutputFieldSet.cs` | 输出字段白名单的通用实现（核心/可裁剪两类）（见下「关键语义」） |
| `Desktop/ElementFields.cs` | 元素输出的字段定义（locate）：核心 + 可裁剪（见下「关键语义」） |
| `Desktop/SnapshotFields.cs` | 快照输出的字段定义（核心事实 + 三种表示）（见下「关键语义」） |
| `Desktop/InteractionContracts.cs` | 交互契约：动作/输入模式/滚动目标/请求/结果（见下「关键语义」） |
| `Desktop/ScreenshotContracts.cs` | 截图契约：作用域/细节/请求/元数据/分块/结果（见下「关键语义」） |
| `CapabilityEndpoint.cs` | Core 发布的能力通道端点描述（无凭据字段）（见下「关键语义」） |

**关键语义**

**`PageContracts.cs`**：`DesktopPageTarget`（显式 ContextId+PageId，`Key` 用于按目标串行化/审计）、`DesktopPageVersion`（等价现有 Snapshot `PageVersion`；`Unknown`=0 表示不校验）、`DesktopPageReadiness`、`DesktopPageState`
**`WebViewContracts.cs`**：`NavigateDisposition`（Accepted/Completed，**都不代表 DOM 已就绪**）、`NavigateRequest/Result`、`JavascriptValueKind`、`JavascriptRequest`（脚本正文禁入审计；结果字节上限 1 B–4 MiB）、`JavascriptResult`（`JsonValue` 恒为裸 JSON 片段，避免二次编码）
**`ShellContracts.cs`**：`DesktopNotificationPriority`、`DesktopNotificationRequest`（标题 ≤128 / 正文 ≤1024）、`DesktopNotificationResult`
**`ApiInterfaces.cs`**：`IPuddingDesktopApi` → `IPuddingDesktopWebViewApi`（Navigate / ExecuteJavascript / GetPageState）+ `IPuddingDesktopShellApi`（ShowNotification）。全部带 `DesktopCallContext` 与 `CancellationToken`
**`Desktop/BrowserControlPlaneRequests.cs`**：**控制面载荷**（§4.1）：`BrowserBindingApplyRequest`（原样携带 `BrowserTaskBinding`）与 `BrowserBindingRevokeRequest`（只带任务+目标）；经 `DesktopCapabilityRequest.ForBindingApply/ForBindingRevoke` 构造。**ack**：`BrowserBindingAck`（成功带 `GrantId`+`Epoch`、拒绝带可区分原因），经 
`DesktopCapabilityResponse.FromBindingAck` 返回，并纳入"恰好一个载荷分支"不变量
**`Desktop/BrowserTaskBinding.cs`**：**任务绑定**（§4.1）：Core 可信元数据给出的"哪个任务被授权操作哪个页面"；grantee 必须过唯一规则（缺身份/冒充 User/无运行身份即拒）、范围非空、期限为正、来源非空。签发时仍受 Desktop 上限/目标已登记/世代存活/子代理子集约束
**`Desktop/BrowserOperationMapping.cs`**：**能力 → 自动化操作** 的唯一映射（准入前置件）：**能力级入口** + **request-aware 重载**（§5.3：`browser.tabs` 的 `New`/`Activate`/`Close` **分别**映射，未登记动作返回 `false` 由调用方明确拒绝），Shell 系"不适用"；由用例保证**目录里每个 WebView 能力都有映射**（防新能力静默绕过闸门）
**`Desktop/IBrowserDelegationLifecycle.cs`**：**浏览器委派生命周期窄端口**（§6.2）：Runtime/Platform **不得引用 Host/Desktop**，故端口声明在契约层（只含 Contracts 类型），实现留 Host。`RequestChildGrantAsync`（为**真实子 runId** 申请子集授权：父身份/子集/到期全校验，不匹配即明确拒绝）· `RevokeForRunAsync` · `RevokeForTaskAsync`（按**完整任务运行身份**撤销，不误撤同 Agent 其它 run）· 
`OnConnectionLostAsync`（新世代**不自动恢复**）
**`Desktop/BrowserCallerRequirement.cs`**：**浏览器操作对调用者身份的统一要求**（方案 §3.2）：缺身份 / 冒充 `User` / 无运行身份三种情形各给具体原因；**两条传输与 Desktop 准入共用的唯一一份规则**（`DesktopCallContext.TryGetBrowserCaller` 委托它）
**`Desktop/BrowserCallerKindWire.cs`**：调用者类别的**线缆编码真源**（方案 §3.2）：`user`/`agent`/`sub_agent`，`TryParse` 对未知/空/大小写不符一律失败（用**字符串**而非 proto enum，避免 proto3 默认值把"没填"当成某个类别）
**`Desktop/BrowserCallerIdentityProjection.cs`**：**可信调用身份的投影**（设计 §3.1/§3.2）：唯一允许产生 `BrowserCallerIdentity` 的地方，入参**只能是执行上下文的标量**（是否子代理/调用者/本次运行/父运行），**不接收任何工具参数**（⇒ 模型无法自报身份获得权限）。fail-closed：缺运行身份、缺调用者、**子代理缺父任务**（无法做子集校验）一律拒绝；身份值去空白后再比较（避免用空白变体伪造另一个行动者）
**`Desktop/LocateContracts.cs`**：元素定位契约：`DesktopLocatorKind`（10 策略 + 线名真源）、`DesktopLocator`（`IncludeHidden` 默认 false：只排除**隐藏**元素；`InViewportOnly` 默认 false：只留视口内 —— 两者**正交**，隐藏与屏外是两个事实）、`BrowserLocateRequest`（**Ref 必须携带来源 PageVersion**，构造期强制；可选 `Cursor` 分页）、`DesktopElementRef`（必须带有效版本；`Visible` 与 
`InViewport` 分开报告）、`DesktopLocateResult`（命中 0 ≠ 被截断；`NextCursor` 为 null 表示**已取完**）
**`Desktop/LocateCursor.cs`**：分页游标 `BrowserLocateCursor`：**对模型不透明**，绑定「页面版本 + 查询指纹 + 偏移」，线格式 `c1-{version}-{fp8}-{offset}`；严格解析（不猜测、不修补）；**`TryResolve` 是两条传输共用的分页判定**（过期 ⇒ `CursorExpired`、格式坏 ⇒ `InvalidRequest`，两者必须分开）；指纹用 SHA-256 截断（**不能用 `string.GetHashCode`**——按进程加盐，跨进程不稳定），只看影响匹配集合的输入（定位条件 + 
includeHidden/inViewportOnly），不含 limit/cursor 自身
**`Desktop/ReadingRevision.cs`**：读取结果的**修订标识**（§145）：线格式 `r1-{version}-{queryFp8}-{hash8}`，绑定「页面版本 + 查询指纹（模式/作用域/字段）+ 内容哈希」；`IsComparableTo` 明确"能不能谈变化"——不可比就 `refresh_required`，**绝不伪造 diff**。载体是调用方上次看到的内容，因此比较是本地纯计算，不需要服务端保存基线
**`Desktop/BrowserTextDiff.cs`**：**行级差异**（§145）：削公共前后缀后只对中间段做差异 —— 追加/删除/替换一块都能给出**精确**的新增/删除行，成本 O(n)，**永不发明没出现过的行**；不做最小编辑脚本（中间段的交错改动归成"替换块"，宁可少说）；超预算如实标注 `Truncated`（计数仍是真实的）。另有 `SplitLines`（空文本 = 0 行、统一换行）
**`Desktop/StableHash.cs`**：跨进程稳定短哈希（SHA-256 截断）：**不能用 `string.GetHashCode()`**（按进程加盐 ⇒ 重启后把"没变"误判成"变了"）。游标指纹与修订标识共用
**`CapabilityErrors.cs`**：错误码 ↔ 线名真源（快照由契约测试冻结）：`cursor_expired`、**`refresh_required`**（基线不可比 ⇒ 重新读取；都不可重试、无副作用；与"参数错" `invalid_request` 分开）
**`Desktop/OutputFieldSet.cs`**：输出字段白名单的**通用实现**（§6.2/§151：读取类工具都有 `fields`）：核心字段恒在、可裁剪字段才是作用面、线名与输出 JSON 键一一对应、未知线名**明确拒绝**、全空白等价于没给。各工具只提供"核心/可裁剪"两张清单，解析与错误语义只有一份
**`Desktop/ElementFields.cs`**：元素输出的字段定义（locate）：核心 `ref/tag/visible/enabled`（`ref` 是继续操作的唯一把手）；可裁剪 `role/name/text/inViewport/checked/value/selectedValues/boundingBox`（text/name/bbox 是 payload 大头）
**`Desktop/SnapshotFields.cs`**：快照输出的字段定义（§151）：核心 `mode/truncated/truncationReason/nextCursor`（这份结果能不能信）；可裁剪三种表示 `accessibilityTree/domText/html`（payload 绝对大头 ⇒ 设计要求"同次默认只返回一种表示"）+ 预算事实 `nodeCount/returnedLines/bytes`
**`Desktop/InteractionContracts.cs`**：交互契约：`DesktopInteractionAction`（10 动作 + 线名真源）、`DesktopInputMode`（`dom`/`native` + 线名、fail-closed 解析；**默认 dom**，请求 native 必须明确失败而不是静默降级，§7）、`DesktopScrollTarget`（`page/element/to_ref/to_top/to_bottom` + 线名；省略时按 locator/位移推导 `EffectiveScrollTarget`，元素类目标必须给定位、
`page` 不得带定位、位移类目标必须非零位移）、`DesktopScrollReport`（容器/前后位置/消耗位移/边界/内容变化；**内容没变不是失败**）、`BrowserInteractRequest`（**必须固定页面版本**、按动作校验参数、携带 `InputMode` 与 `ScrollTarget`）、`DesktopInteractionResult`（元素 + 可选滚动事实）
**`Desktop/ScreenshotContracts.cs`**：截图契约（§8.1）：`DesktopScreenshotScope`（viewport/region/full_page，**与实现同名同义**）、`DesktopScreenshotDetail`、`BrowserScreenshotRequest`（**钉版本**；scope 与参数一一对应）、`DesktopScreenshotMetadata`（**含 `ObservationId`**）/`Chunk`/`BrowserScreenshotResult`。
**`CapabilityEndpoint.cs`**：Core 发布的**能力通道端点描述**：形态（NamedPipe / LoopbackHttp2 / Tls）+ 地址 + 协议版本 + Core 实例 ID；`ToEndpointString()`/`TryParse` 严格解析。**没有任何凭据字段**（凭据由 Desktop 主机侧注入），地址形态受约束（明文只允许回环、TLS 只能 https、URI 不许带凭据/查询/片段）

| `Desktop/BrowserCapabilitySurface.cs` | **窄端口** `IDesktopBrowserCapabilitySurface`：能力通道**能覆盖**的九个浏览器操作。刻意窄（`IBrowserPage` 约 35 个成员 ⇒ 宽接口整替会让二十多个成员只能在运行期抛 `NotSupported`）。约定：参数顺序与 `DesktopSession` 同名方法一致、结果一律 `CapabilityResult<T>` 不抛异常、两个实现必须给出**同形**结果 |

| `Desktop/DesktopCapabilityCallContextFactory.cs` | `IDesktopCapabilityCallContextFactory`：为一次能力调用产生 `DesktopCallContext`（实例 ID 来自活动会话/Bridge 连接、每次新 `OperationId`、期限来自配置）。工具侧只有业务参数，这三个字段不能由调用方猜。**不产生调用方身份** |

| `Desktop/ContextManagementContracts.cs` | 上下文**管理**（缺口 #1 的契约齿轮，**待接线**）：`BrowserContextCreateRequest`/`BrowserContextCloseRequest`/`DesktopContextClosed` + 窄端口 `IDesktopContextCapabilitySurface`（两个写操作）。单开端口而非并入上面那个：现有九个操作是页面作用域，这两个是上下文作用域 |



> 与方案初稿的差异（有意）：初稿示例写 `Task<NavigateResult>`；实际返回 `Task<CapabilityResult<NavigateResult>>`，
> 否则领域错误只能靠异常穿越流边界，丢「未执行/可能已生效/可重试」语义。

## 审计（`Audit/`）

| 成员 | 用途 |
|------|------|
| `DesktopCapabilityOutcome` | 终态分类：Succeeded / Failed / Rejected / Cancelled / DeadlineExceeded / Disconnected |
| `DesktopCapabilityAuditRecord` | 审计记录：TraceId/OperationId/Generation/线名/时长/终态/错误码（见下「关键语义」） |
| `IDesktopCapabilityAuditSink` / `NullDesktopCapabilityAuditSink` | 审计出口；审计失败不得影响调用结果 |

**关键语义**

**`DesktopCapabilityAuditRecord`**：TraceId、OperationId、Generation、能力线名、排队/执行时长、终态与错误码。**没有**脚本、URL、剪贴板、Token、页面数据字段 —— 「不泄密」由类型形状保证，`AuditContractShapeTests` 用反射断言
