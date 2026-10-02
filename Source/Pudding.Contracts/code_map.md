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
| `Identity.cs` | `DesktopInstanceId`（按 DesktopHome/DataRoot 隔离的实例身份，**不是** PID）、`DesktopProcessInstanceId`（单次启动实例，用于识别 Core 重启）、`OperationId`（待完成表与幂等复用键）、`DesktopCorrelationId`（业务关联，不参与权限）、`ConnectionGeneration`（Core 在握手回执分配，每次重连递增；`None`=未握手）、`ContractText`（内部校验：标识符限可见 ASCII，展示文本允许 CJK 且超长取红） |
| `DesktopCallContext.cs` | 调用上下文：DesktopId + OperationId + **UTC** Deadline + 可选 CorrelationId；`IsExpiredAt` / `RemainingAt`。身份与权限不由模型填写字段决定；页面目标属于请求 DTO，禁止「当前激活 Tab」隐式定位 |
| `DesktopProtocolVersion.cs` | 协商版本区间常量（`Current = Minimum = 1`）与 `TryNegotiate`。线上字段/编号的真源是 proto，本类只存平台无关版本号，映射测试断言两者一致 |

## 能力目录与协商（`Capabilities.cs`）

| 成员 | 用途 |
|------|------|
| `DesktopCapability` | 位标志能力标识（WebView 导航/脚本/页面状态、Shell 通知/状态/对话框/Picker/剪贴板）。**这是协商身份，不是权限** |
| `DesktopCapabilityTraits` | 执行语义：`Mutating`（同目标串行）、`HasSideEffects`（取消/超时后可能已生效）、`RequiresTrustedContext`（普通网页不得调用）、`RequiresUserInteraction` |
| `DesktopCapabilities` | **能力目录唯一真源**：线名（`webview.navigate` 等）、版本、Kind、Traits；`DeclareFor` 生成握手声明；`TryGetByName` / `NameOf` / `Enumerate`。目录 ⊇ 当前已实现能力：未实现的能力**不声明**即不会被 Core 授予 |
| `DesktopCapabilityNegotiation.Validate` | 握手协商：Core 只能授予 Desktop 已声明且版本不高于声明的能力；越权授予 ⇒ 协议错误且**不做部分接受**（协商失败不产生任何可用能力） |

> 已接通 wire payload 的能力：`webview.navigate` / `webview.execute_javascript` / `webview.page_state` /
> `shell.notification`（2026-10-01 起 `page_state` 也有命令 payload：proto 的 `GetPageStateCommand`）。
> 其余条目是**预留身份**，在切片 E 逐能力补齐 payload 后才可能被声明。

## 能力请求/结果判别联合（`Desktop/`）

| 成员 | 用途 |
|------|------|
| `DesktopCapabilityRequest` | 判别联合（每个能力一个分支 + 工厂 `For*`）：导航/脚本/通知/页面状态/快照/定位/交互/等待/标签页/上下文清单/剪贴板/对话框/Picker/Shell 状态。`Target` 从分支取（无页面目标的为 `null`）；构造期保证「恰好一个分支非空」 |
| `DesktopCapabilityResponse` | 判别联合（每个能力一个类型化输出 + `Failure`）；构造期拒绝「成功 + 错误」歧义。存在理由：让执行器接缝与 UI 实现**都不依赖 proto**；新增能力在此追加分支与 `From*` 工厂 |

## 错误语义（`CapabilityErrors.cs`）

| 成员 | 用途 |
|------|------|
| `DesktopCapabilityErrorCode` | 领域错误码（计划 §5 的 12 个 + `InvalidRequest`/`InternalError`/`Disconnected`） |
| `DesktopCapabilityErrorCodes` | 线名 + 默认 `Retryable` / `MayHaveSideEffects` 真源；未知线名经 `ParseOrInternalError` 确定性 fail closed |
| `DesktopCapabilityError` | 码 + 脱敏消息（剔除控制字符、截断 512）+ 两个语义标志；`IsSafeToRetry` = 可重试且无副作用。**不承载**脚本正文/URL/剪贴板内容/页面数据 |

## 结果形状（`CapabilityResult.cs`）

`CapabilityResult<T>`：成功携带类型化输出，失败携带结构化领域错误。不用异常表达业务失败，因为跨进程失败需要区分「未执行 / 可能已产生副作用 / 可重试」。
`default(CapabilityResult<T>)` 是**未初始化**状态：`IsSuccess=false`、读 `Error`/`Value` 取红（抓「忘记赋值」）。

## 能力 DTO（`Desktop/`）

| 文件 | 用途 |
|------|------|
| `PageContracts.cs` | `DesktopPageTarget`（显式 ContextId+PageId，`Key` 用于按目标串行化/审计）、`DesktopPageVersion`（等价现有 Snapshot `PageVersion`；`Unknown`=0 表示不校验）、`DesktopPageReadiness`、`DesktopPageState` |
| `WebViewContracts.cs` | `NavigateDisposition`（Accepted/Completed，**都不代表 DOM 已就绪**）、`NavigateRequest/Result`、`JavascriptValueKind`、`JavascriptRequest`（脚本正文禁入审计；结果字节上限 1 B–4 MiB）、`JavascriptResult`（`JsonValue` 恒为裸 JSON 片段，避免二次编码） |
| `ShellContracts.cs` | `DesktopNotificationPriority`、`DesktopNotificationRequest`（标题 ≤128 / 正文 ≤1024）、`DesktopNotificationResult` |
| `ApiInterfaces.cs` | `IPuddingDesktopApi` → `IPuddingDesktopWebViewApi`（Navigate / ExecuteJavascript / GetPageState）+ `IPuddingDesktopShellApi`（ShowNotification）。全部带 `DesktopCallContext` 与 `CancellationToken` |
| `Desktop/LocateContracts.cs` | 元素定位契约：`DesktopLocatorKind`（10 策略 + 线名真源）、`DesktopLocator`、`BrowserLocateRequest`（**Ref 必须携带来源 PageVersion**，构造期强制）、`DesktopElementRef`（必须带有效版本）、`DesktopLocateResult`（命中 0 ≠ 被截断） |
| `CapabilityEndpoint.cs` | Core 发布的**能力通道端点描述**：形态（NamedPipe / LoopbackHttp2 / Tls）+ 地址 + 协议版本 + Core 实例 ID；`ToEndpointString()`/`TryParse` 严格解析。**没有任何凭据字段**（凭据由 Desktop 主机侧注入），地址形态受约束（明文只允许回环、TLS 只能 https、URI 不许带凭据/查询/片段） |

| `Desktop/BrowserCapabilitySurface.cs` | **窄端口** `IDesktopBrowserCapabilitySurface`：能力通道**能覆盖**的九个浏览器操作。刻意窄（`IBrowserPage` 约 35 个成员 ⇒ 宽接口整替会让二十多个成员只能在运行期抛 `NotSupported`）。约定：参数顺序与 `DesktopSession` 同名方法一致、结果一律 `CapabilityResult<T>` 不抛异常、两个实现必须给出**同形**结果 |

| `Desktop/DesktopCapabilityCallContextFactory.cs` | `IDesktopCapabilityCallContextFactory`：为一次能力调用产生 `DesktopCallContext`（实例 ID 来自活动会话/Bridge 连接、每次新 `OperationId`、期限来自配置）。工具侧只有业务参数，这三个字段不能由调用方猜。**不产生调用方身份** |

| `Desktop/ContextManagementContracts.cs` | 上下文**管理**（缺口 #1 的契约齿轮，**待接线**）：`BrowserContextCreateRequest`/`BrowserContextCloseRequest`/`DesktopContextClosed` + 窄端口 `IDesktopContextCapabilitySurface`（两个写操作）。单开端口而非并入上面那个：现有九个操作是页面作用域，这两个是上下文作用域 |



> 与方案初稿的差异（有意）：初稿示例写 `Task<NavigateResult>`；实际返回 `Task<CapabilityResult<NavigateResult>>`，
> 否则领域错误只能靠异常穿越流边界，丢「未执行/可能已生效/可重试」语义。

## 审计（`Audit/`）

| 成员 | 用途 |
|------|------|
| `DesktopCapabilityOutcome` | 终态分类：Succeeded / Failed / Rejected / Cancelled / DeadlineExceeded / Disconnected |
| `DesktopCapabilityAuditRecord` | TraceId、OperationId、Generation、能力线名、排队/执行时长、终态与错误码。**没有**脚本、URL、剪贴板、Token、页面数据字段 —— 「不泄密」由类型形状保证，`AuditContractShapeTests` 用反射断言 |
| `IDesktopCapabilityAuditSink` / `NullDesktopCapabilityAuditSink` | 审计出口；审计失败不得影响调用结果 |
