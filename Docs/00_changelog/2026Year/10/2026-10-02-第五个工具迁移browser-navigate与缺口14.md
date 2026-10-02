---
title: "第五个工具迁移：`browser_navigate` 改走窄端口；同轮补上它暴露的缺口 #14"
author: hyfree
date: 2026-10-02
last_reviewed: 2026-10-02
status: archived
description: "NavigateResult 不带标题，而 browser_navigate 的结果里 Page.Title 是必填字段 （迁移前取自运行时 page.Info）⇒ 若就此提交，Agent 会看到空标题——正是本系列一直在防的收窄。 因此本轮先补 #14 再收尾："
categories: [docs, changelog]
tags: [第五个工具迁, 与缺口]
related_docs: []
related_files: [Docs/12_features/Desktop-Surface-Browser-Mapping-2026-10-01.md]
slug: changelog-2026-10-02-第五个工具迁移browser-navigate与缺口14
draft: false
---

# 第五个工具迁移：`browser_navigate` 改走窄端口；同轮补上它暴露的缺口 #14

## 改动

| 文件 | 改动 |
|---|---|
| `BrowserNavigateTool.cs` | 依赖改为 `IDesktopBrowserCapabilitySurface` + `IDesktopCapabilityCallContextFactory`；动作名 → 契约动作；URL/超时校验与错误码**沿用迁移前**（校验失败仍带 context/page id）；导航**不强钉版本**（`Unknown` = "当前版本即可"，与迁移前一致）；结果按 `NavigateResult` 搬运（含 `Ok`/`StatusCode`/`ErrorText`） |
| `Tests/…/BrowserAgentToolsTests.cs` | 替身端口新增 `NavigateAsync`（委托给假页面）；2 处构造点更新 |

## 同轮补上缺口 #14（迁移暴露的真实收窄）

`NavigateResult` **不带标题**，而 `browser_navigate` 的结果里 `Page.Title` 是必填字段
（迁移前取自运行时 `page.Info`）⇒ 若就此提交，Agent 会看到**空标题**——正是本系列一直在防的收窄。
因此本轮**先补 #14 再收尾**：

| 层 | 改动 |
|---|---|
| 线缆 | `NavigateOutcome.title = 7` |
| 契约 | `NavigateResult(..., string? Title = null)` |
| Desktop→proto / proto→DTO | 写入 / 读出（`NullIfEmpty` 归一化） |
| Desktop 侧 / Bridge 侧 | 两条返回路径都回带 `browserPage.Info.Title` |
| 工具 | 用 `page.Title ?? string.Empty`（未知时回退空串，不编造） |
| 线缆快照 | `NavigateOutcome` 字段号断言补 `("title", 7)` |

## 验证

- 构建：`PuddingHost` / `Pudding.DesktopSurface.Browser` / `PuddingBrowser.AgentTools` 均 **0 错误**；
- 测试（七套件全绿，共 **582** 用例）：`Pudding.Rpc.ProtocolTests` **20/20**、
  `Pudding.ContractsTests` **96/96**、`Pudding.DesktopConnectionTests` **80/80**、
  `Pudding.DesktopSurface.BrowserTests` **54/54**、`Pudding.CapabilityBrokerTests` **90/90**、
  `PuddingHost.Tests` **227/227**、`PuddingBrowser.AgentTools.Tests` **15/15**；
- **未验证**：真实 Desktop 会话端到端（需启用能力通道 + 外部重启窗口）；
- 标题贯通由替身端口 + 工具测试间接覆盖，**没有专门断言**（如实记录）。

## 过程教训（工具层）

1. **迁移动过的三个工具都补了同一组 using**（`Pudding.Contracts` + `Pudding.Contracts.Desktop`）——
   这是"工具工程此前不引用契约"的必然结果；下次迁移可先一次性加上，省一次编译往返。
2. 我在工具里写了一处 `X ? string.Empty : string.Empty`（无意义的条件表达式），自查时清掉；
   ⇒ **批量脚本产出的表达式要再读一遍**，不能只看编译通过。
3. PowerShell 的 `.Split(<string>)` 会绑定到 `char[]` 重载（本轮得到 7919 段而不是 0/1 匹配），
   判断"出现次数"必须用 `[regex]::Matches(...).Count`。

## 迁移进度 5/7

已迁移：`locate`、`snapshot`、`wait_for`、`interact`、`navigate`。

| 剩余工具 | 缺口 |
|---|---|
| `browser_tabs` | **#6** 新建标签页 |
| `browser_context` | **#1** 上下文 `create`/`close` |

## 未完成 / 留白

- 缺口 #6 / #1 / #13；两个工具的迁移；
- 组合根「通道 / Bridge」开关；Tool Runtime 权限链；切片 F；外部重启窗口验收；索引登记。

## 关联

`548ce4b`（导航加宽 #3/#4/#5）、`4a24c7f`（第四个工具）、`ecfca05`、`f053909`；
`Docs/12_features/Desktop-Surface-Browser-Mapping-2026-10-01.md` §8.5.1。
