---
title: BrowserAutomationAuthority 入口对账（17 个公开入口逐一核对）
author: hyfree
date: 2026-10-09
last_reviewed: 2026-10-09
description: "把权威状态机的**全部公开入口**逐条对账（读端口 5 个 + 控制端口 12 个）：每个入口写明语义要点、现有证据（用例文件）与判定（已核对并取红 / 仅正向 / 未验证），并单独列出仍缺的边界（如 RevokeManagementGrant 不释放页面租约、RevokeExecutionScope 对子代理授权的效果未验证）。配套门禁用例断言本表**恰好覆盖**两个接口的全部成员：新增入口必须补一行，否则变红。"
status: active
categories: [docs, reports]
tags: [browser, authorization, b1, audit, coverage]
related_docs: [Docs/00_changelog/2026Year/10/2026-10-08-授权撤销级联.md, Docs/00_changelog/2026Year/10/2026-10-08-授权不可变.md, Docs/12_features/浏览器自动化可靠性与渐进阅读设计方案-2026-10-08.md]
related_files: [Source/PuddingBrowser.Automation/BrowserAutomationAuthority.cs, Source/Pudding.Contracts/Desktop/BrowserControlContracts.cs, Source/PuddingBrowser.AutomationTests/ReadOnlyDelegationTests.cs]
slug: reports-authority-entry-audit
draft: false
---

# BrowserAutomationAuthority 入口对账

**范围**：`IBrowserAutomationAuthority`（读端口，Agent 侧）5 个成员 + `IBrowserAutomationControl`（控制端口，Shell/生命周期）12 个成员 = **17**。
**判定口径**：`已核对并取红` = 有变异取红或多种边界用例；`仅正向` = 只有一个正向用例，边界未覆盖；`未验证` = 无用例。

| 入口 | 端口 | 语义要点（不变量） | 现有证据 | 判定 |
|---|---|---|---|---|
| `Capture` | 读 | 只读投影；不因读取改变状态 | `BrowserControlAuthorityTests` | 已核对并取红 |
| `ActiveGrants` | 读 | 只暴露有效授权（过期/已撤销不出现） | `BrowserPageGrantTests` · `ReadOnlyDelegationTests` | 已核对并取红 |
| `ActiveManagementGrants` | 读 | 只暴露有效管理授权 | `BrowserControlAuthorityTests` | 仅正向 |
| `Admit` | 读 | 入队前准入：只读做完整校验、写额外签租约；拒绝时**驱动调用数为 0**；非可信身份/未握手/接管/暂停/绑定或世代不符/无授权/范围不足/`PageBusy` 一律拒 | `BrowserControlAuthorityTests`（16 条）· `ReadOnlyDelegationTests` | 已核对并取红 |
| `Revalidate` | 读 | 触碰页面前复检：租约身份、行动者、操作、目标、控制/授权世代、覆盖授权；任一不符即拒 | `BrowserControlAuthorityTests` · `ReadOnlyDelegationTests` | 已核对并取红 |
| `SetPaused` | 控制 | 推进控制世代、**释放在途租约**；恢复不复活旧队列 | `BrowserControlAuthorityTests` · `ReadOnlyDelegationTests` | 已核对并取红 |
| `SetUserTakeover` | 控制 | 推进控制世代、释放租约、**不撤销阅读授权** | `BrowserControlAuthorityTests` | 已核对并取红 |
| `Resume` | 控制 | 推进控制世代 ⇒ 接管前的队列与租约不复活 | `BrowserControlAuthorityTests` | 已核对并取红 |
| `Close` | 控制 | 推进控制+授权世代、清空租约与全部 grant | `BrowserControlAuthorityTests` · `BrowserLifecycleTests` | 已核对并取红 |
| `NotifyConnectionGeneration` | 控制 | 世代改变 ⇒ 旧世代许可/租约作废并清空授权 | `BrowserControlAuthorityTests` · `BrowserLifecycleTests` | 已核对并取红 |
| `ReleaseLease` | 控制 | 按租约 id 释放；非本租约 id 无副作用 | `BrowserControlAuthorityTests` · `ReadOnlyDelegationTests`（含"时间不释放、显式释放才释放"） | 已核对并取红 |
| `IssueGrant` | 控制 | 绑定必须 live；子代理须为父授权子集；**同 id 不得换内容**（不可变） | `BrowserPageGrantTests` · `ReadOnlyDelegationTests`（不可变取红） | 已核对并取红 |
| `IssueManagementGrant` | 控制 | 绑定必须 live；同 id 不得换内容 | `ReadOnlyDelegationTests`（不可变取红） | 已核对并取红 |
| `RevokeGrant` | 控制 | 推进授权世代、释放失去覆盖的租约、**级联清除派生授权**（子/孙） | `ReadOnlyDelegationTests`（级联取红等 3 条） | 已核对并取红 |
| `RevokeManagementGrant` | 控制 | 推进授权世代；**不**释放页面租约（有意） | `BrowserPageGrantTests` | 仅正向 |
| `RevokeExecutionScope` | 控制 | 去掉该页 Write/Manage、保留只读；推进世代、释放失去覆盖的租约 | `BrowserPageGrantTests` · `BrowserLifecycleTests` | 仅正向 |
| `RevokeAllGrants` | 控制 | 清空页面与管理授权；推进世代；释放租约 | `BrowserPageGrantTests` | 仅正向 |

## 仍缺的边界（诚实登记，未在本轮补）

1. **`RevokeManagementGrant`**：无用例证明"撤管理授权**不**影响在途页面写租约"这一**有意**语义（现在只有正向调用）。
2. **`RevokeExecutionScope`**：未验证"子代理授权是否随之收窄"（子集关系应仍成立）与"管理授权是否受影响"。
3. **`RevokeAllGrants`**：未验证清空后**派生授权**（子/孙）也一并消失（应成立，但无用例）。
4. **`ActiveManagementGrants`**：未验证过期管理授权不会出现在只读投影里。
5. **`Capture`**：仅验证投影可用，未验证"读取不改变任何状态"（幂等/无副作用）。
6. **并发**：权威是进程内 `lock` 保护，但**并发用例没有**（本会话所有验证都是单线程）。

## 配套门禁（防漂移）

`AuthorityEntryAuditTests`（AutomationTests）用反射取出两个接口的成员，规范化后与本表第一列**精确比对**：
新增/删除入口而没改本表 ⇒ 用例变红。这样"公开面"与"对账"不会各自漂移。

## 关联

- 本轮之前的四处授权修复：[撤销级联](../00_changelog/2026Year/10/2026-10-08-授权撤销级联.md) · [授权不可变](../00_changelog/2026Year/10/2026-10-08-授权不可变.md) · [行动者绑定](../00_changelog/2026Year/10/2026-10-08-页面授权行动者绑定.md) · [租约存活性判据](../00_changelog/2026Year/10/2026-10-08-租约存活性判据.md)
- 设计：[浏览器自动化可靠性与渐进阅读设计方案](../12_features/浏览器自动化可靠性与渐进阅读设计方案-2026-10-08.md)（§3.1/§3.2）
