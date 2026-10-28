---
title: workspace_search 能力映射与实现顺序
author: hyfree
date: 2026-10-28
last_reviewed: 2026-10-28
status: active
description: "R6 施工前置调研：把 ADR-089 冻结的 12 个 RetrievalIntent 逐一映射到现有服务（CodeQueryService / search_grep / FileSearchTool 等），标出哪些可直接复用、哪些真缺失、哪些需融合层新写；并给出按可独立验收切分的实现顺序。"
categories: [docs, features]
tags: [adr-089, retrieval, workspace-search, r6, capability-mapping]
related_docs:
  - Docs/12_features/增强索引分阶段施工计划-2026-10-18.md
  - Docs/12_features/增强索引统一检索与代码地图设计方案-2026-10-18.md
  - Docs/07_architecture/103ADR-089Agent统一检索与渐进展开工具链ADR.md
related_files:
  - Source/PuddingCodeIndex/Contracts/Retrieval/IRetrievalPort.cs
  - Source/PuddingCodeIndex/Contracts/Retrieval/RetrievalIntent.cs
  - Source/PuddingCodeIntelligence/Services/CodeQueryService.cs
slug: features-workspace-search-capability-mapping-2026-10-28
draft: false
---

# workspace_search 能力映射与实现顺序

## 0. 结论先说

R1–R5 交付的是**合同与组件**；R6 缺的是**装配**。磁盘实测：

| 事实 | 证据 |
|---|---|
| 合同层极完整 | `Contracts/Retrieval/` 下 **25 个文件**（`RetrievalResult` 17 KB、`RetrievalDiagnostics` 13 KB、`RetrievalFilter` 12 KB） |
| **`IRetrievalPort` 实现数 = 0** | 全仓 `: IRetrievalPort` 只命中接口定义自身；接口注释自称「本刀只冻结签名，无实现」 |
| **`workspace_search` / `workspace_open` 源码 0 命中** | 全仓 `.cs` 搜索；只在 `.md` 文档里出现 |
| 底层能力已就绪 | `CodeQueryService` 6 个方法、`search_grep(backend=index)`、`FileSearchTool` |

⇒ **不需要重写检索引擎，需要写的是「融合层 + 两个工具」。**

## 1. 以枚举为准，不以设计文档为准

设计文档 §8.2 写的是 `auto|file|symbol|text|semantic|implementation|references|callers|callees|impact`（10 个），
但**冻结的枚举是 12 个**：

```
Auto=0, File=1, Namespace=2, Type=3, Member=4, Comment=5,
Implementation=6, References=7, Callers=8, Callees=9, Impact=10, Any=11
```

差异：`symbol` 被拆成 `Namespace/Type/Member`；`text` 对应 `Comment`；
`semantic` **不在枚举里**——它应由 `Auto` 内部作为一路召回处理，而不是独立 intent。

**实现以 `RetrievalIntent` 为准**（它是被契约测试钉住的冻结面）；设计文档的命名视为历史表述。

## 2. 12 个 intent 的能力映射

`CodeQueryService` 现有 6 个公开方法：`GetProjectIndexStatusAsync`、`SearchSymbolsAsync`、
`ExploreAsync`、`GetCallersAsync`、`GetCalleesAsync`、`GetImpactAsync`。

| intent | 可复用服务 | 状态 | 备注 |
|---|---|---|---|
| `Auto = 0` | 多路：`SearchSymbolsAsync` + `search_grep(index)` + `FileSearchTool`(+可选 semantic) | **需新写融合层** | 必须回显"实际选了哪路"，不能隐藏匹配语义 |
| `File = 1` | `FileSearchTool`（Everything 清单） | ✅ 直接复用 | 注意 `query` 是**规范化项目相对路径的 literal 子串**，通配符只放 include/exclude |
| `Namespace = 2` | `SearchSymbolsAsync`（按 symbol kind 过滤） | ✅ 复用 | 需确认 kind 过滤面是否已暴露 |
| `Type = 3` | 同上 | ✅ 复用 | |
| `Member = 4` | 同上 | ✅ 复用 | |
| `Comment = 5` | `search_grep(backend=index)`（文本层） | ⚠️ **需适配** | 文本检索非"语义"，需明确它不是 semantic；设计已注明 Markdown recall 极低 |
| `Implementation = 6` | `implements/overrides` 图边 | ❌ **待核实** | 需查 `CodeRelationRecord` 是否已含 `implements/overrides` kind；R1-04 曾补过这类边 |
| `References = 7` | 引用边（读/写/类型使用/方法组） | ❌ **待核实** | 与 `Callers` **不等价**（callers 只算调用）；R1-04 补过 `references` 边，需确认查询入口 |
| `Callers = 8` | `GetCallersAsync` | ✅ 直接复用 | |
| `Callees = 9` | `GetCalleesAsync` | ✅ 直接复用 | |
| `Impact = 10` | `GetImpactAsync` | ✅ 直接复用 | 需接 `depth` 与节点预算 |
| `Any = 11` | —— | ❌ **语义未定义** | 需裁定：是"不限制 kind"还是"回退到 Auto" |

### 2.1 三个必须裁定的缺口

1. **`Any` 的语义**：设计文档没有定义。我倾向"不限制 kind，但仍走结构化检索"，
   与 `Auto` 的区别是 **Auto 会跨 provider 融合，Any 只在结构化 provider 内不筛 kind**。需你确认。
2. **`References` vs `Callers`**：前者含读/写/类型使用/方法组，后者只含调用。
   如果图里只有 calls 边，`References` 会退化成 `Callers`——**那就必须在返回里声明能力降级，不能静默等同**。
3. **`semantic` 的归属**：枚举里没有它。它要么作为 `Auto` 的一路召回，要么根本不进 v1。

## 3. 实现顺序（按可独立验收切）

| # | 交付 | 验收前提 | 依赖 |
|---|---|---|---|
| **B0** | 裁定 §2.1 三个缺口 | 你确认 | 无 |
| **B1** | `IRetrievalPort` 的 v1 实现：**仅结构化 + 文本 + 文件**，`Implementation/References` 返回 `capability_unsupported` | 单测：每个 intent 的 status/reason 正确；不支持的能力**不伪装成空命中** | B0 |
| **B2** | `RetrievalResult` 的 coverage 契约落地：逐 provider 报 generation/freshness/scanned | 单测：`total.relation=unknown` 时 `value=null`；`no_match` 只在覆盖完整时出现 | B1 |
| **B3** | `workspace_search` 工具（薄包装）：参数校验 + 错误码映射 | 参数越界/借用（如 `match` 用于非 text）返回 `invalid_arguments` | B2 |
| **B4** | `workspace_open` 工具：先 `excerpt/outline/summary` | 三种 view 能打开并携带能力声明 | B3 |
| **B5** | `workspace_projects_load/unload` | 未加载返回 `no_project_loaded`；越界 `scope_not_loaded` | B4 |
| **B6** | `relations/map/status` + cursor 续页 | cursor 绑定四元组，篡改返回 `cursor_expired` | B5 |
| **B7** | 旧入口降级为薄入口 | 见 §4 | B6 |

**B1 是分水岭**：它一旦通过，"零件已装配"就成立，后面都是加 intent 和加 view。

## 4. 旧入口怎么处置（需你裁定）

- **B 方案（我建议）**：`workspace_search` 上线后，`search_grep` / `code_*` **继续保留**，
  在工具描述里标注"建议改用 workspace_search"。**理由**：今天的 902 倍修复刚证明 `search_grep backend=index` 可用，
  直接抽掉风险大于收益。
- **C 方案**：上线即移出默认 catalog，只留 `workspace_search` + 一个 `search_grep` 兜底。

设计文档 §8.4 要求的是"**验收阶段**删除无调用者入口"，与 B 方案不冲突。

## 5. 未核实项（不得当成已知）

- `CodeRelationRecord` 是否已含 `implements/overrides/references` 三种 kind —— **未读**，仅从 R1-04 的提交标题推断。
- `SearchSymbolsAsync` 的 kind 过滤面是否足够区分 `Namespace/Type/Member` —— **未读签名**。
- `code_outline` / `code_summary` / `project_map` 各自的服务入口 —— **未定位**（不在 `CodeQueryService` 里）。

以上三项在 B1 开工前必须补读，不能按推断动手。
