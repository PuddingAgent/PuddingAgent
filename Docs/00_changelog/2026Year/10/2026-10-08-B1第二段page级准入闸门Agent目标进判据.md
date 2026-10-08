---
title: 2026-10-08 B1 第二段：page 级准入闸门——「Agent 目标」真正进入准入判据
author: hyfree
date: 2026-10-08
last_reviewed: 2026-10-08
status: archived
description: "把 IsAgentTarget 纳入准入：AgentAuthorized 上下文不再等于「里面所有页面已获授权」，同上下文非目标页被拒且驱动调用数为 0；同步两个注册表并新增 13 条门禁测试。"
categories: [docs, changelog]
tags: [browser, admission, security, desktop, s5]
related_docs: [Docs/09_audit/2026-10-08-浏览器控制权与准入闸门核验.md, Docs/12_features/浏览器自动化可靠性与渐进阅读设计方案-2026-10-08.md, Docs/00_changelog/2026Year/10/2026-10-08-B1首段删除mutating版本递增统一后置条件.md]
related_files: [Source/Pudding.DesktopService/DesktopService.cs, Source/Pudding.DesktopService/DesktopTargetRegistry.cs, Source/Pudding.DesktopService/DesktopCapabilityPolicy.cs, Source/Pudding.DesktopSurface.Browser/BrowserWorkspaceTargetBridge.cs, Source/Pudding.DesktopServiceTests/AgentTargetAdmissionTests.cs, Source/PuddingDesktop/Hosting/DesktopApplicationCoordinator.Capability.cs, code_map.md]
slug: changelog-b1-agent-target-admission-gate
draft: false
---

# 2026-10-08 B1 第二段：page 级准入闸门——「Agent 目标」真正进入准入判据

## 目标 / 背景

审计（2026-10-08）结论 #2 **已证实**：目标标记是 **page 级**，而准入用的是 **context 级** Trust
（`DesktopService.ValidateAdmission` 取 `pageState.Trust` 后查 `IsAllowedForTrust`）。二者不同层、
不同粒度 ⇒ 在 `AgentAuthorized` 上下文里，**非目标页面同样可通过准入**，UI 上的「Agent 目标」只是暗示。

设计方案 §3.2 的裁定是「`IsAgentTarget` 纳入准入，不能把整个 AgentAuthorized context 当作所有页面已获授权」。

## 改动

### 准入（page 级闸门）

- `Source/Pudding.DesktopService/DesktopService.cs` — `ValidateAdmission` 在可信级别检查之后、
  自动化状态检查之前新增 page 级闸门：`RequiresAgentTarget(capability) && !pageState.IsAgentTarget`
  ⇒ `unauthorized`，且**在触碰 UI 之前**返回（驱动调用数为 0）。
- `Source/Pudding.DesktopService/DesktopCapabilityPolicy.cs` — 新增 `RequiresAgentTarget(capability)`，
  判据**从信任表推导**（需要页面目标 ∧ 只对 AgentAuthorized 开放 ∧ 对 Untrusted/Workbench 都不开放），
  不引入第二份真源。

### 注册表（准入实际读的那一份）

- `Source/Pudding.DesktopService/DesktopTargetRegistry.cs` —
  `DesktopTargetState` 新增 `IsAgentTarget`；`RegisterPage` 新增可选 `isAgentTarget`；
  新增 `SetAgentTarget(contextId, pageId)`（同一时刻至多一个；`null` 撤销；
  **未登记或已关闭的 id 不标记**，fail closed）。
- `Source/Pudding.DesktopSurface.Browser/BrowserWorkspaceTargetBridge.cs` —
  `OnAgentTargetChanged` **同时**更新两个注册表：此前只写浏览器目标注册表，而准入读的是页面注册表，
  只更新一边就会出现「UI 说这是目标页、准入说不是」这种最难排查的漂移（本类存在的理由）。
  产品侧由 `Source/PuddingDesktop/Hosting/DesktopApplicationCoordinator.Capability.cs` 把
  `workspace.AgentTargetPageId` 同步进来（已在位，本次未改）。

### 测试与夹具

- `Source/Pudding.DesktopServiceTests/AgentTargetAdmissionTests.cs` — 新增 7 条：
  同上下文兄弟页被拒（审计 #2 的复现，且 `SnapshotCount == 0`）、目标页放行（控制组）、
  移动目标即移动闸门、清空目标后 fail closed、通用浏览能力（`page_state`）不受影响、
  无页面目标的能力不受影响、脚本注入必须落在目标页。
- `Source/Pudding.DesktopServiceTests/TargetAndPolicyTests.cs` — 新增 2 条策略断言
  （含「推导自信任表」的防漂移断言）与 4 条 `SetAgentTarget` 语义断言。
- `Source/Pudding.DesktopSurface.BrowserTests/BrowserWorkspaceTargetBridgeTests.cs` — 新增 2 条：
  目标标记镜像进页面注册表、关闭页面带走标记。
- `Source/Pudding.DesktopServiceTests/TestInfrastructure.cs` — `ServiceHarness` 把 `AgentPage`
  标记为 Agent 目标（按定义它就该是），否则既有 Agent 能力用例会全部被新闸门拒绝。

## 验证

| 套件 | 结果 |
|---|---|
| `Pudding.DesktopServiceTests` | **196 / 196**（原 183 ＋ 13） |
| `Pudding.DesktopSurface.BrowserTests` | **56 / 56**（原 54 ＋ 2） |
| `PuddingDesktop.Tests`（窗口/工作区接线） | **283 / 283** |
| `PuddingHost.Tests` | **251 / 251** |
| `PuddingBrowser.AutomationTests` / `Pudding.ContractsTests` / `PuddingBrowser.AgentTools.Tests` | 126 / 126 · 97 / 97 · 17 / 17 |

- `dotnet build Source\Pudding.DesktopService` 与 `Source\Pudding.DesktopSurface.Browser`：0 错误。
- 关键负例断言为「拒绝 + **驱动调用数 = 0**」，因此「闸门真的在触碰 UI 之前生效」是被钉住的，
  而不是仅凭错误码推断。

## ⚠️ 产品行为变更（需要产品级验证）

**未选定 Agent 目标页时，以下能力一律 `unauthorized`**：
`webview.execute_javascript`、`browser.snapshot`、`browser.locate`、`browser.interact`、
`browser.wait_for`、`browser.tabs`。

- 依据：设计 §3.2「**默认只授权用户选定的目标页**」，以及审计结论 #2 的修复建议。
- 影响面：`workspace.AgentTargetPageId` 只在用户显式
  `AssignAgentTargetAsync`（`BrowserWorkspaceController.cs:465`）后才有值 ⇒
  「开了页面就能被 Agent 驱动」的旧行为消失，必须先指定目标页。
- **刻意不含** `webview.navigate` 与 `webview.page_state`：它们对普通网页与工作台也开放，属通用浏览能力
  （`DesktopContextTrust.Untrusted` 的语义就是「只读状态与导航」）。把导航也收紧到目标页属于设计 §3.2 的
  写权限 / grant 切片，需要与「普通网页仍可导航」这条既有语义一起裁决。
- 需外部产品验证的两点：① 目标页未选定时 Agent 是否应给出**可操作的提示**（而非只看到 `unauthorized`）；
  ② 是否要在 Shell 侧引入「随任务授权目标页」的默认行为。两者都是产品决策，本提交不擅自决定。

## 未完成（B1 余项，仍留白）

- Shell 真接管：三份互不相通的 takeover/paused 状态尚未收敛到唯一 `IBrowserAutomationAuthority`
  （本次只做了准入闸门，未动接管路径）。
- 统一页面/通道生命周期端口（登记失败回收、关闭后同时清 UI/两个注册表/grant/ref/cursor/租约）。
- ref 身份与 `PageVersion`/`domRevision`/`viewportRevision` 三事实分离、`stale_element_reference`。
- 回执进输出面（`BrowserInteractRequest` 的 `expect` 参数与工具回执断言）。
- grant 模型与本次闸门的合流：`IBrowserAutomationAuthority` 的 page grant 已在组件内就绪，
  尚未接入准入（当前准入读注册表标记，尚未读 grant 记录）。

## 影响与回滚

- 无数据/配置影响；无线缆格式变更。
- 回滚：还原 `DesktopService.cs` / `DesktopCapabilityPolicy.cs` / `DesktopTargetRegistry.cs` /
  `BrowserWorkspaceTargetBridge.cs` 与 `TestInfrastructure.cs`，并删除新增的两个测试文件与日志。

## 关联

- 审计依据：[浏览器控制权与准入闸门核验](../../../09_audit/2026-10-08-浏览器控制权与准入闸门核验.md)（结论 #2、建议 3）
- 设计：[浏览器自动化可靠性与渐进阅读设计方案](../../../12_features/浏览器自动化可靠性与渐进阅读设计方案-2026-10-08.md)（§3.2、B1）
- 前序：[B1 首段：统一变更类后置条件](2026-10-08-B1首段删除mutating版本递增统一后置条件.md)
