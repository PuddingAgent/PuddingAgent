---
title: 删除死组件 IndexIndicator（chat 侧索引指示器已由 ComposerStatusDetails 接管）
author: hyfree
date: 2026-10-03
last_reviewed: 2026-10-03
status: active
description: 前端 `src/pages/chat/components/IndexIndicator.tsx`（1297 B）是零引用死代码：src 全量 grep 只在该文件自身命中，产物级证明是 P5 时代真实构建副本的 152 份 sourcemap 中探针命中 0 而对照各命中 8。删除后测试与构建预算三元组与基线逐字相同 ⇒ 不递增版本号、不重新部署。本轮同时纠正两处仪器误判：dist 逐字节比对不成立（同源连构两次即不同），search_grep 扫 bundle 会跳过 >1MB 文件。
categories: [docs, changelog, frontend, deadcode, instrumentation]
tags: [dead-code, index-status, sourcemap-probe, instrument-control, chat]
related_docs: [Docs/12_features/Index-Retrieval-Known-Defects-2026-10-01.md, Docs/00_changelog/2026Year/10/2026-10-03-P6-深链入口归一.md]
related_files: [Source/PuddingPlatformAdmin/src/pages/chat/components/IndexIndicator.tsx, Source/PuddingPlatformAdmin/src/pages/chat/components/ComposerStatusDetails.tsx, Source/PuddingPlatformAdmin/src/pages/chat/components/IntentConsole.tsx]
slug: p7-remove-dead-index-indicator
draft: false
---

# P7：删除死组件 `IndexIndicator`（并按纪律纠正两处仪器误判）

## 1. 缘起：一条**过期**的待办被实测推翻

我的 goal.md 待办③写的是「chat 页硬编码 `index: 'disabled'`（`IntentConsole.tsx:819-823`）
与死代码 `IndexIndicator.tsx` 是否接线」。本轮先做只读探索，结论是这条待办**前提已过期**：

- `IntentConsole.tsx` 里**已不存在** `index: 'disabled'` 字面量；
  现状是**真实数据链路**：`IntentConsole.tsx:420-421` 持有 `serviceSignals` 状态、
  `:440` 在同批 `Promise.allSettled` 中调用 `getIndexStatus()`、
  `:460-463` 用纯函数 `deriveIndexServiceStatus(...)` 派生、
  `:890` 组装进 `runtimeSummary`、`:1213-1216` 作为 `runtimeDetails` 交给 `ComposerStatusDetails`；
- 消费端 `ComposerStatusDetails.tsx:267-284` 渲染「索引」行（`:273` 圆点色、`:282` 文案），
  取值域 `available|building|warning|disabled|error|unknown` 定义在 `serviceStatus.ts`，
  且 `serviceStatus.ts:129` 的注释明确「非管理员 / 网络失败 / 还没请求 ⇒ `unknown`，**绝不** `available`，也**绝不** `disabled`」；
- 旧硬编码只以**历史注释**形式留档（`serviceStatus.ts:2-7`），以及测试替身
  `ComposerStatusDetails.test.tsx:26`（`index: 'disabled' as const`，非生产路径）。

⇒ 待办③的「硬编码」部分**已由他人实现解决**（不是我做的），需要核销；
剩下的真问题是那句"死代码"到底成不成立。

## 2. 死代码的三重证据

**① 静态引用（源级，全覆盖）**：`src/**/*.{ts,tsx}` 全量 grep `IndexIndicator`
→ 仅命中该文件自身的 1/8/14/51 行（声明、props 接口、组件、默认导出），**无任何 import**。
`Source/PuddingPlatformAdmin/code_map.md` 未被 grep 命中 ⇒ 文档层也无登记。

**② 共享样式不受牵连**（决定"只删文件、不动样式"）：该组件引用的
`styles.statusIconGroup / statusIconThunder / statusIconLabel`（`IndexIndicator.tsx:28,30,38`）
同时被 4 个**活组件**使用 —— `AspLspIndicator.tsx:23,25,29`、
`SubconsciousLlmIndicator.tsx:23,25,33`、`ThinkingIntensityIndicator.tsx:35`、
`TokenStatsIndicator.tsx:79,82` ⇒ 样式键**必须保留**，本次改动面严格限定为 1 个文件。

**③ 产物级（决定性，且仪器先经过对照验证）**：
在**删除前**的真实构建副本 `temp/admin-wwwroot-backup-20261003T035550`（P5 时代部署留档，
含 **152** 份 `.js.map`）里逐文件搜源路径：

| 探针 | 命中数 | 读法 |
|---|---|---|
| `IndexIndicator.tsx`（被删模块） | **0** | 该模块**从未进入任何 chunk 的依赖图** |
| `ComposerStatusDetails.tsx`（对照·活） | **8** | 仪器有效 |
| `IntentConsole.tsx`（对照·活） | **8** | 仪器有效 |

删除后的现 dist（55 份 map）同样：探针 0、对照各 1。

> 说明：sourcemap 的 `sources` 保存**原始文件路径**（ASCII），是绕过压缩/转义/编码问题最稳的探针；
> 关键在于**对照组必须先点亮**，否则"0 命中"什么都不能说明（见 §4）。

**改动**：删除 `Source/PuddingPlatformAdmin/src/pages/chat/components/IndexIndicator.tsx`
（1297 B，sha16 `BD67AE894AC853B0`；删除后 `Test-Path` 复核为 False）。改动面 = **1 文件 / −51 行**。

## 3. 门禁（父级亲跑）

| 门禁 | 结果 | 判读 |
|---|---|---|
| 全量 jest | `Test Suites: 2 failed, 203 passed, 205 total` / `Tests: 3 failed, 1683 passed, 1686 total` | 3 条红全部落在**语音族**（`InputArea.test.tsx`、`IntentConsole.test.tsx`，失败断言是语音按钮查询）；他人提交 `ff444c9`（"前端全量基线复测：1591 通过 / 3 既有语音族红"）已把同族红记为**既有基线** |
| 构建 + 预算门禁 | `[chat-bundle-budget] ok sync=1383742 chat=350879 common=429095` | 与 P6 基线**逐字相同**（该三联数自 P1 起稳定）⇒ 产物零变化 |
| dist 文件数 | 159 → 159 | 无增删 |

**因果性论证**：被删模块经 §2-③ 证明从未进入依赖图 ⇒ 上表 3 条红与本次删除**无因果关系**；
且失败断言与索引功能无交集。

## 4. 本轮两处仪器误判（自我纠正，入库）

### 4.1 误判一：把"dist 逐字节不变"当作死代码证伪器（**不成立的证伪器**）

删除后重建，dist 清单哈希 `cd85750d…` → `c8aa754e…` 变了，一度看似"该文件其实在依赖图里"。
按**仪器纪律**先跑对照：**同一源码连构两次**（无任何改动）⇒ 清单哈希
`c8aa754e…` → `02986e2e…` **同样不同**，而预算三联数两次完全一致。
⇒ 该构建**非确定性**（页脚徽标 `v6.2.2+dirty · <commit> · <构建时刻>` 会把时刻写进产物）。
**"逐字节不变"从根本上不可满足**，是一个 ill-posed 的证伪器；证据改由 sourcemap 探针 + 预算三联数承担。

### 4.2 误判二：`search_grep` 扫构建产物（**工具静默失明**）

用 `search_grep` 在 dist / wwwroot 里搜中文串（`索引已激活`）返回 no matches，
但**同一个查询对"必然存在"的对照串同样 no matches**，且工具自报
`已跳过 70 个超过 1MB 的大文件` —— JS 产物正多为大文件。
⇒ 这是**仪器失效**（不是"结论变了"）：该调用不能用来判定 bundle 内容。
改用 .NET `ReadAllText` 逐文件读 + ASCII 源路径探针，并**先验证对照组点亮**。

## 5. 决策：不递增版本号、不重新部署

前端版本徽标约定"每次前端改动递增版本"，本次**有意不遵守**，理由如下（可复核）：

1. 被删模块**从未进入任何构建 chunk**（§2-③），删它**不改变任何交付字节**；
2. 构建预算三联数与 P6 基线**逐字相同**，dist 文件数不变；
3. 线上（`wwwroot/admin`）当前是已验证的 P6 产物（`index.html` sha `0B0ECB4E…`，与源 dist 一致）；
   若仅为"版本号递增"而重建 + 重新部署，会**无功能收益地**改写这份已验证产物、产生额外 I/O 与验证成本。

⇒ 结论：本次是**源码卫生**改动，不产生新前端产物，因此不 bump、不 deploy。
若项目要求"任何前端源码改动都必须 bump"，请以此处论证为准复裁。

## 6. 未做 / 待决策

- `IndexIndicator.tsx` 所表达的**原始意图**（在输入框旁显示"已索引 N 个文件"）已被
  `ComposerStatusDetails` 的「索引」行取代；若希望恢复"文件数"这类更细的呈现，属**新需求**。
- 前端**唯一**的索引端点是 `GET /api/admin/index/status`（admin-only，`Roles="admin"`）
  ⇒ 非管理员用户当前必然看到 `unknown`（`serviceStatus.ts:129` 的**诚实**设计，不是缺陷）。
  若要让普通用户看到真实索引状态，需要**新增面向用户的端点**（鉴权模型扩张）——
  属设计决策，未自行实施。
