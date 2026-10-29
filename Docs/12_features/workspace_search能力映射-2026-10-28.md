---
title: workspace_search 能力映射与实现顺序
author: hyfree
date: 2026-10-28
last_reviewed: 2026-10-29
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

## 5. 已核实（2026-10-28 补读，取代原「未核实」）

- **`CodeRelationKind` 已含** `Unknown/Contains/Calls/References/Inherits/Implements/Overrides/Overloads/Uses`
  （`Source/PuddingCodeIndex/Contracts/CodeRelationContracts.cs`）
  ⇒ `References` 与 `Callers` 在图上**确实区分**；`Implementation` 可沿 `Inherits/Implements/Overrides` 实现。
  **⇒ §2.1 第 2 条要求的「降级声明」在 v1 不需要**（但仍要在返回里报 coverage，以防某项目没写过这些边）。
- **`CodeSymbolSearchRequest.Kind` 是单值 `CodeSymbolKind?`**；`CodeSymbolKind` 含
  `Namespace/Type/Struct/Class/Interface/Enum/Delegate/Method/Constructor/Property/Field/Event/…`
  ⇒ `Type` 需要 5 个 kind、`Member` 需要 6 个 kind，**而合同只接受单值**。
  **⇒ B1 必须把它扩展为 `Kinds` 集合**（可选、默认 null、不破坏现有调用），否则 `Type`/`Member`
  只能拆成 5–6 次查询，且去重跨次失效。
- `code_outline` / `code_summary` / `project_map` 的服务入口 —— **仍未定位**（不在 `CodeQueryService`），
  B4（`workspace_open`）开工前补。

### 5.1 B1 由此确定的两条实现约束

1. **不新写检索引擎**：`IRetrievalPort` 的实现只做「意图 → 现有服务」的路由 + 融合 + 覆盖声明。
2. **扩展必须向后兼容**：`CodeSymbolSearchRequest` 加 `Kinds` 时保留 `Kind`（单值），
   两者同时给出时以 `Kinds` 为准并在结果里回显实际使用的过滤面。
---

## 6. B4 `workspace_open` 的权威规格（2026-10-29 补）

### 6.1 规格在哪里（此前「开工前补」的答案）

| 内容 | 位置 |
|---|---|
| 当前有效技术合同 | `Docs/12_features/增强索引统一检索与代码地图设计方案-2026-10-18.md` §8.3（第 260 行起） |
| 历史入口（不作并行规范） | `Docs/90_archive/adr_089_legacy_20261018/Agent统一检索与渐进展开工具链设计-2026-09-13.md` |

### 6.2 规格要点（逐条摘自 §8.3）

- 请求 `ref` 或 `{project_id,path}` **二选一**
- `view = excerpt | outline | summary | relations | map | status`
- 可选 `lines` / `depth` / `cursor`，共用同一输出预算
- `map` 默认 `depth=2`、上限 5；节点默认 100 / 上限 500；边默认 200 / 上限 1000，且仍受 byte/token 上限约束
- `relations` 必须给 symbol ref 与 `relation=references|callers|callees|implementation|impact`，语义同 §8.2；**不按文件名猜符号**
- `ref` 绑定身份、源版本、scope、provider generation；**本身不授予权限**
- 源码变化后不得沿旧行号打开当前代码：能读历史快照则回显版本，否则 `stale_ref` + 重新定位建议
- `summary` / `outline` / `map` 的头部**必须携带能力与覆盖**；缺语义解析不得假装有完整引用图

### 6.3 现状核对（2026-10-29 实测）

- `IRetrievalPort` **只有 `RetrieveAsync` 一个成员** ⇒ `workspace_open` / `workspace_projects_load` /
  `workspace_index_status` 都**尚无冻结合同**。它们不是「S5 未完成」，而是**合同待冻结**：
  先冻结再实现，不先写工具再补契约。
- **`code_outline` / `code_summary` / `project_map` 没有「服务层」**（2026-10-29 实测，解释此前的「仍未定位」）：
  三者都是 `PuddingRuntime\Tools\BuiltIns\CodeIntelligence\` 下的 `PuddingToolBase<T>` 工具类，
  逻辑直接写在 `ExecuteCoreAsync` 里（`CodeOutlineTool` 282 行：路径解析 → 存在性 → 扩展名 →
  `.cs` 走 Roslyn / 其他走 `IFileOutlinerRegistry`，全在方法体内）。
  **⇒ `workspace_open` 不能靠「组合现有服务」实现**：没有可注入的服务可组合。
  先决条件是**从工具类里抽出服务**（`ICodeOutlineService` / `ICodeSummaryService` / `IProjectMapService`），
  再把 `code_*` 工具退化成薄壳、`workspace_open` 复用同一服务。这属于 S1 抽取，
  不能反过来让 `workspace_open` 去调工具（依赖方向反了），也不能复制一份逻辑（必然漂移）。

### 6.4 一条搜索口径教训

本轮先在 `Docs/15_tasks` 搜 `workspace_open` 得 **0 命中**，差点据此写下「设计文档没提过」。
**正对照**（同目录搜 `workspace_search`）同样 0 命中；而全 `Docs/` 搜 `workspace_open` 命中 14 处 ——
规格实际在 `Docs/12_features/`。**⇒ 目录范围选错时，0 命中是范围产物，不是缺席证据。**
---

## 7. B4 前置清单（2026-10-29 06:05 补）

三个服务抽取的实际成本差别很大，别按「都是抽服务」一概而论：

| 目标 | 依赖 | 写特征测试的成本 |
|---|---|---|
| `ICodeOutlineService` | 无（只读文件 + Roslyn） | **低** —— 已交付，4 条特征测试即可 |
| `ICodeSummaryService` | `ICodeQueryService` + `ICodeIndexScopeResolver` | **中** —— 需要两个 fake |
| `IProjectMapService` | 同上 | **中** |

好消息：`ICodeQueryService` / `ICodeIndexScopeResolver` 的 fake **仓库里已有 6 处实现**，
可直接照抄形状，不必从零设计：

```text
Tools/CodeIndexStatusRegistryGateTests.cs      RecordingQueryService / FixedScopeResolver
Tools/CodeSymbolSearchMatchTargetTests.cs      RecordingQueryService
Tools/CodeSymbolSearchStalePathGateTests.cs    SearchQueryService
Tools/CodeSymbolSearchIndexFreshnessTests.cs   SearchQueryService
Tools/CodeCallersImpactFreshnessTests.cs       ICodeQueryService 实现
```

但它们是**各测试类内部的 private 嵌套类**，不能跨文件复用 —— 也就是说每个新测试文件仍要自带一份。
若将来出现第三处需要，应把这两个 fake 提到 `PuddingRuntimeTests/TestDoubles/` 共享，
**现在只有两处需求，先不提前抽象**。

### 7.1 冻结 `workspace_open` 合同的推荐切法

先冻结**能验证的部分**，其余显式未支持（而不是先冻结全套再慢慢实现）：

- `view=excerpt` —— 复用 `CodeOutlineTargetResolver` 的路径语义 + 文件读取
- `view=outline` —— 复用 `ICodeOutlineService`
- `view=summary` / `map` / `relations` / `status` —— 首版返回**显式未支持 + 替代方案**，
  等对应服务抽出来再逐个打开

理由：§8.3 要求 `summary`/`outline`/`map` 的头部必须携带能力与覆盖。能力声明这件事
**在「只有一个视图能跑」时最容易说真话**，等全都实现了反而容易含糊。
---

## 8. 外部验收结论（Codex gpt-5.6-sol，2026-10-29 08:10）

按「大节点整体验收交 Codex、日常改动派子代理」的分层规则做了一次独立验收。
Codex 的四条判断我逐条回磁盘核对，**全部成立**：

| 判断 | 核对方式 | 结果 |
|---|---|---|
| `CodeSummaryTool` 仍独占 lookup / 文件读取 / AST / 调用图 / 展示 | grep 工具文件：`SearchSymbolsAsync`(56) / `File.ReadAllTextAsync`(89) / `CSharpSyntaxTree`(90) / `GetCallersAsync`(104) / `BuildSummaryText`(114) | 成立 |
| `CodeOutlineOutcome` 暴露两个节点模型 | 第 34/35 行：`CSharpNodes` + `OutlinerNodes` | 成立 |
| 没有 excerpt 服务 | 在 `Services/Tools/*.cs` 搜 `Excerpt` → 0 命中 | 成立 |
| resolver 回退到进程 CWD 且不做工作区边界校验 | 第 45/48/49 行只有 `GetFullPath` | 成立 |

**Codex 的核心结论**：分层方向对，但（a）可复用能力（outline 解析 / 文档注释解析）放在
`PuddingRuntime/Services/Tools` 是耦合到工具宿主，长期应下沉；
（b）**当前形状不足以支撑 `workspace_open`**，提前冻结合同会迫使适配器或破坏性改动；
（c）118 行文档注释提取零覆盖属**高功能缺口**，应阻止「宣告抽取完成」。

### 8.1 已采取的动作（唯一能立刻做的）

补了 `ExistingFileWithXmlDocComment_EmitsDocumentationField`：造真实 `.cs` 文件（带 `///`），
断言输出 JSON 的 `documentation` 非 null 且含预期文本。5/5 通过；把工具里的
`documentation = docComment` 改成 `null` ⇒ 1 失败，证明断言是活的。

### 8.2 这条测试**没有**证明的事（必须写明）

把 reader 里的 `"///"` 前缀改成 `"//X"` 后测试**仍然全绿** —— 因为快路径返回 null 时
会回退到 Roslyn AST，两条路只要有一条能找到注释就过。
⇒ 该测试锁的是「**快路径 + 回退的合并行为**」，**不是**快路径本身。
快路径单独回归（例如它对 attribute / block comment 的判断变错但 AST 能兜住）仍不可见。
要真正覆盖，需要让测试能直接调用 `CodeSummaryDocCommentReader`（当前 `internal`，
需 `InternalsVisibleTo`），这属于下一步。
---

## 9. `IProjectMapService` 抽取施工单（2026-10-29 13:25）

特征测试已就位（`ProjectMapToolCharacterizationTests`，4 条，变异取红），可以直接动刀。
**这一步不要靠记忆做**，按下面的行号与边界执行。

### 9.1 搬什么

| 内容 | 当前行（`ProjectMapTool.cs`，309 行） |
|---|---|
| `BuildDirectoryNodeAsync` | 65–138 |
| `GetFileTopTypesAsync` | 139–173 |
| `ResolveRootPath` | 251–279 |
| `DirNode` / `FileEntry` 两个私有类 | 285–301 |
| 常量 `MaxFilesPerDir=50` / `MaxTopLevelTypes=3` | 25–26 |

### 9.2 留什么

| 内容 | 行 |
|---|---|
| `AppendTreeText`（树形文本） | 177–222 |
| `ToJsonNode`（JSON 投影） | 223–250 |
| `ExecuteCoreAsync` 的三段：挂 `---`、`Ok`/`Fail` | 29–61 |

### 9.3 唯一的硬点：节点类型必须重命名

`DirNode` / `FileEntry` 是**工具内的私有嵌套类**，搬走后两个格式化器（`AppendTreeText` / `ToJsonNode`）
编译不过。所以必须：

1. 在服务侧定义**公开** record：`ProjectMapNode` / `ProjectMapFileEntry`（字段逐个照抄，不改名不改顺序）；
2. 工具里的两个格式化器把类型引用改成新类型（纯重命名，**逻辑一行不动**）；
3. `ExecuteCoreAsync` 改成 `var outcome = await _mapService.BuildAsync(args.ProjectPath, context.WorkingDirectory, args.Depth, ct);`，
   失败直接 `Fail(outcome.Error!)`。

### 9.4 验收标准

- 4 条特征测试**断言一个字都不改**，仍全绿；
- 变异 `Math.Clamp(...)` → 不夹取，仍能取红 2 条；
- 工具里不再出现 `Directory.Exists` / `Directory.GetDirectories` / `CSharpSyntaxTree`（数据获取全在服务）。

### 9.5 为什么现在不做

当前会话上下文已经很长，而这一步要动 ~180 行并跨类重命名两个类型。
**在剩余上下文不足时做这种规模的重命名，出错概率高于收益** ——
留到新会话按本施工单机械执行，比在这里赌一把稳。
