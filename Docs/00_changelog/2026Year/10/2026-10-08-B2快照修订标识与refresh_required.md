---
title: 2026-10-08 B2 快照修订标识：能回答「变了吗」，且基线不可比就说 refresh_required
author: hyfree
date: 2026-10-08
last_reviewed: 2026-10-08
description: "按设计 §145 为 browser_snapshot 增加 revision 与 since_revision：修订标识绑定「页面版本 + 查询指纹 + 内容哈希」，同一版本同一查询下用内容哈希如实回答内容是否变化；版本或查询不可比时返回 refresh_required（新增错误码），基线缺失时绝不伪造 diff。顺带把游标指纹里的短哈希抽成跨进程稳定的 StableHash。"
status: archived
categories: [docs, changelog]
tags: [browser, snapshot, revision, diff, b2]
related_docs: [Docs/12_features/浏览器自动化可靠性与渐进阅读设计方案-2026-10-08.md, Docs/00_changelog/2026Year/10/2026-10-08-B2快照scope只读子树.md]
related_files: [Source/Pudding.Contracts/Desktop/ReadingRevision.cs, Source/Pudding.Contracts/Desktop/StableHash.cs, Source/PuddingBrowser.AgentTools/BrowserSnapshotTool.cs]
slug: changelog-b2-snapshot-revision-and-refresh-required
draft: false
---

# 2026-10-08 B2 快照修订标识：能回答「变了吗」，且基线不可比就说 refresh_required

## 目标 / 背景

设计 §145：「diff 是只读观察优化，**绑定 baseline 和同一文档**；基线缺失返回 `refresh_required`，
**不伪造变化**。」§151 也把 `sinceRevision` 列为快照参数。

要让"渐进阅读"成立，调用方必须能问一句"**自我上次读到之后，这块内容变了吗**"，
并且在**问不了**的时候被告知"重新读"，而不是拿到一个编出来的答案。

## 改动

| 层 | 文件 | 改动 |
|---|---|---|
| 契约 | `Desktop/StableHash.cs`（新增） | 跨进程稳定短哈希（SHA-256 截断）：**不能用 `string.GetHashCode()`**（按进程加盐 ⇒ 重启后把"没变"误判成"变了"）。游标指纹与修订标识共用 |
| 契约 | `Desktop/ReadingRevision.cs`（新增） | `BrowserReadingRevision`：线格式 `r1-{version}-{queryFp8}-{hash8}`，绑定「页面版本 + 查询指纹（模式/作用域/字段）+ 内容哈希」；`IsComparableTo` 明确"能不能谈变化" |
| 契约 | `CapabilityErrors.cs` | 新错误码 **`refresh_required`**（不可重试、无副作用），错误码冻结快照同步更新 |
| 工具 | `BrowserSnapshotTool` | 输出 `revision`；`since_revision` 参数与三类判定：格式坏 ⇒ `browser_invalid_arguments`；版本/查询不可比或页面无活版本 ⇒ **`refresh_required`**；可比 ⇒ `changed` = 内容哈希是否一致（**证据**，不是猜测）。没给 `since_revision` 时 `changed` 为 `null`（不比较就不声称"没变"） |

## 验证

| 套件 | 结果 |
|---|---|
| `Pudding.ContractsTests` | **143 / 143**（原 127 ＋ 16） |
| `PuddingBrowser.AgentTools.Tests` | **54 / 54**（原 53 ＋ 1） |
| `PuddingHost.Tests` · `PuddingDesktop.Tests` · `DesktopSurface.BrowserTests` · `DesktopServiceTests` · `AutomationTests` | 258/258 · 317/317 · 63/63 · 220/220 · 194/194 |

新增门禁：

- 契约层：修订标识往返、**10 种畸形输入**全部拒绝、可比性要求同版本同查询、内容哈希跨进程稳定、
  查询指纹区分模式/作用域/字段、`refresh_required` 进入冻结快照（不可重试、无副作用）；
- 工具层：首次读取给 `revision` 且 `changed` 为 **null**；同内容 ⇒ `changed=false`（证据）；
  **内容真的变了 ⇒ `changed=true`**；换 mode（查询指纹不同）⇒ `refresh_required`；
  来自别的版本的修订 ⇒ `refresh_required`；格式坏 ⇒ `browser_invalid_arguments`
  （三类必须分开，否则调用方会把"参数写错"当成"页面变了"）。

**取红实测**：把可比分支改成永远返回 `changed=false`（"看不出变化"）⇒
`Snapshot_RevisionAnswersWhetherContentChanged_AndRefusesToCompareIncomparableBaselines`
**恰好变红**；复原后 54/54 复绿。

## 已知边界（诚实登记）

- **没有行级 diff**：本轮提供的是**内容是否变化**（内容哈希的证据）与"可比/不可比"的明确区分。
  真正的行级差异需要保存基线正文（现在不保存，因此不可比时给 `refresh_required` 而不是编造 diff），
  属后续切片（设计里的 extraction snapshot revision 存储）。
- **修订标识由工具侧计算**（基于它刚读到的文本），不是由宿主抽取管线签发：因此它绑定的是"调用方
  看到的内容"，而不是"宿主内部某次抽取的版本"。对"变了吗"这个用途足够且无需服务端状态，
  但与设计设想的宿主侧 revision 存储还不是一回事。
- **`changed=true` 只说明内容不同**，不说明"多了哪些行"；大页面上"变了"与"值得重读"仍需调用方判断。
- 真机行为未验证（本环境跑不了 WebView2）；本轮不涉及驱动改动。

## 影响与回滚

- **行为变化（Agent 可见）**：`browser_snapshot` 新增 `revision` 输出与 `since_revision` 参数；
  不传时行为**完全不变**（多一个 `revision` 字段与 `changed: null`）。不可比时不再给"看起来像答案"的
  结果，而是明确 `refresh_required`。
- 错误码表新增一项（旧消费方按 `ParseOrInternalError` 折叠为 `internal_error`，fail closed）。
- 回滚：还原上表 4 个生产文件与 3 个测试文件及 code_map。

## 关联

- 设计：[浏览器自动化可靠性与渐进阅读设计方案](../../../12_features/浏览器自动化可靠性与渐进阅读设计方案-2026-10-08.md)（§145、§151）
- 前序：[B2 定位 cursor 分页与游标过期](2026-10-08-B2定位cursor分页与游标过期.md)（同一种"绑定版本+指纹"的思路）、
  [B2 快照 scope 只读子树](2026-10-08-B2快照scope只读子树.md)
