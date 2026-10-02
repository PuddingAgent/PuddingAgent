---
title: 从 Source/PuddingRuntime/code_map.md 迁出的历史变更记录（迁出日 2026-10-02）
author: hyfree
date: 2026-10-02
last_reviewed: 2026-10-02
status: archived
description: 〔原文第 261–272 行：变更（2026-09-24，ADR-089 U4-5a）：search_grep 新增 backend 路由参数〕
categories: [docs, changelog]
tags: [puddingruntime, code, 迁出的变更记]
related_docs: []
related_files: [Source/PuddingRuntime/code_map.md, Docs/00_changelog/README.md, Source/PuddingRuntimeTests/Tools/SearchGrepToolTests.cs, Source/PuddingCodeIndex/code_map.md, Docs/12_features/Index-Retrieval-Known-Defects-2026-10-01.md]
slug: changelog-2026-10-02-puddingruntime-code-map迁出的变更记录
draft: false
---

# 从 Source/PuddingRuntime/code_map.md 迁出的历史变更记录（迁出日 2026-10-02）

> **为什么在这里**：`Source/PuddingRuntime/code_map.md` 只保留索引（关键概念 · 组件 · 关键文件 · 用途）。原先按轮次/日期堆叠在其中的变更、门禁与验收记录迁出到本文件。日志规则见 `Docs/00_changelog/README.md`。
>
> **内容来源**：迁出前 `Source/PuddingRuntime/code_map.md` 的原文，**逐字保留，未做删改**（节之间仅插入 `---` 分隔，不改变任何原文字）。原文件快照可 `git show <迁出前提交>:Source/PuddingRuntime/code_map.md`。
> **路径约定**：链接目标已改写为**相对本文件**的可点击路径（链接文字未变）；正文反引号内的路径仍保持原文的仓库根口径。
> **覆盖范围**：原文件第 261–335 行，共 6 节；日期 2026-09-24 ~ 2026-10-01。

**〔原文第 261–272 行：变更（2026-09-24，ADR-089 U4-5a）：`search_grep` 新增 `backend` 路由参数〕**

## 变更（2026-09-24，ADR-089 U4-5a）：`search_grep` 新增 `backend` 路由参数

| 项 | 事实 |
|---|---|
文件 | `Tools/BuiltIns/Search/SearchGrepTool.cs`（旧路径**一行未改**）+ `Tools/BuiltIns/Search/SearchGrepTool.cs` 的 `SearchGrepArgs.Backend` |
参数 | `backend`：`scan`（未传/缺省 = 既有托管扫描，行为逐字节不变）\| `index`（新：直接吃全文索引，不做托管扫描）\| 其他 ⇒ `contract_error` |
新方法 | `IndexBackendSearchAsync`（单次 `IFullTextSearchEngine.SearchAsync`，Lucene 查询解析器语义）+ `ReportIndexBackendTelemetry`（指标名 `search_backend_index`，`elapsed_ms` 作维度） |
契约 | 无索引/异常/超时/`case_sensitive=true` ⇒ **fail-closed** + 提示"省略 backend 走旧路径"；**不写失败账本**（避免把旧路径兜底短路）；索引快照指向已删除文件 ⇒ 跳过并计 `staleSkipped` |
观测 | 结果尾行：`backend=index` / `scope` / `engineMs` / `totalMs` / `engineMatches` / `engineTotalMatches` / `staleSkipped` / `truncated`；宿主侧 `agent_diagnostics(tool_stats, tool_name="search_grep")` 查调用数·成功率·平均耗时 |
测试 | `Source/PuddingRuntimeTests/Tools/SearchGrepToolTests.cs` 新增 6 用例：索引命中且**不回落到扫描**、未建索引时 fail-closed、未知 backend ⇒ contract_error、缺省 == `scan` 逐字节一致、`case_sensitive` 被拒、陈旧路径被跳过 |
门禁 | `dotnet test --filter FullyQualifiedName~SearchGrepToolTests` ⇒ **失败 0 / 通过 56 / 总计 56（5 s）**；变异（去掉 index 分支 `return` ⇒ 静默回落）⇒ **失败 4 / 通过 52（exit 1）**，复原 ⇒ **失败 0 / 通过 56（exit 0）** |
未做 | 新参数需 Core 重启后才在本机 `search_grep` 上可用（宿主仍跑旧程序集）；`index` 后端在活仓库上的召回/延迟未实测 |

---

**〔原文第 274–285 行：变更（2026-09-25，ADR-089 U4-5b）：索引后端假否定修复 —— 相对索引路径按 scope 解析〕**

## 变更（2026-09-25，ADR-089 U4-5b）：索引后端假否定修复 —— 相对索引路径按 scope 解析

| 项 | 事实 |
|---|---|
症状 | 仓库根上 `backend=index` 返回 `engineMatches=11 / engineTotalMatches=11`，却回 `(no matches)`：11 条命中路径全部 `File.Exists` 失败 ⇒ `staleSkipped=11` |
根因 1（写入侧） | `LuceneSearchEngine.GetIndexDirectoryPath` 把 scope 规范化成绝对路径再哈希（**索引目录定位本身正确**），但 `AddDocument` 把 `path` 按遍历原样写入 ⇒ 建索引时 scope 写成相对路径（探针 `--scope "."` 即如此）时，索引里存的就是**相对路径** |
根因 2（读取侧） | 工具侧 `Path.GetFullPath(match.FilePath)` 对非绝对路径按**进程 CWD**（Core 的 bin 目录）解析 ⇒ 真实命中被整体误判为"陈旧条目"而静默丢弃：引擎命中了、工具却答"没有"（假否定） |
实测证据 | 同一评分报告内两两对照：`--scope "."` 那次命中路径形如 `.\Source\PuddingRuntime\...`（537 条中绝对形式 **0** 条）；绝对 scope 那次形如 `E:\github\AgentNetworkPlan\PuddingAgent\Source\...` |
修复 | 相对索引路径以**本次查询的 scope** 为基准解析（`IsPathRooted ? GetFullPath : GetFullPath(Combine(scope, p))`）；尾行新增 `firstStaleRawPath` 暴露索引里的原始形态；**全部命中都陈旧**时不再回空洞 `(no matches)`，改为给出命中数/原因/下一步（空洞否定会诱发"换关键词重试"式打转） |
测试 | `Source/PuddingRuntimeTests/Tools/SearchGrepToolTests.cs` 新增 2 用例：相对索引路径按 scope（而非进程 CWD）解析、全部陈旧时不得回空洞 no matches |
门禁 | **失败 0 / 通过 58 / 总计 58**（基线 56，旧 56 条全绿无回归）；两处独立变异 ⇒ **失败 2 / 通过 0 / 总计 2（RC=1）**，变异版输出实测 `(no matches)\n(backend=index: ..., staleSkipped=1, firstStaleRawPath=...)`；复原 ⇒ 58/58（RC=0） |
遗留 | **写入侧仍未规范化** `path` ⇒"同一逻辑 scope 因书写方式不同而存不同形态"的根源仍在；本刀只保证读取侧对两种形态都正确。改动前须评估评测夹具（按相对路径比对）的语义影响 |

---

**〔原文第 287–301 行：变更（2026-09-25，ADR-089 U4-5c）：索引后端静默假否定修复 —— Lucene 语法查询 0 命中时降级为显式 OR 重试〕**

## 变更（2026-09-25，ADR-089 U4-5c）：索引后端静默假否定修复 —— Lucene 语法查询 0 命中时降级为显式 OR 重试

| 项 | 事实 |
|---|---|
症状 | 仓库根上 `backend=index` 对某些多词查询静默返回 `(no matches)`（`engineMatches=0 / engineTotalMatches=0`，**引擎 `Success=true`**），而**同一 query 走旧 scan 路径却有命中** ⇒ **索引后端独有**的假否定 |
根因（实测，非推理） | 引擎把 query 交给 Lucene 查询解析器，而 **`|` 在此 parser 配置下不等价于 OR**：同一批词 `BuildIndexAsync\|IndexScopeKeys` ⇒ 11 命中；再加一个 `\|ScopeKey` ⇒ **0 命中**；而 `BuildIndexAsync OR IndexScopeKeys OR ScopeKey` ⇒ **41 命中 / 42 total**。逐变量对照已排除 `regex=true`、`file_ext` 格式（`cs` 与 `.cs` 均命中）、`max_results`（单词 + `max_results=40` 仍 13 命中） |
判据 | 违反 ADR-089 §8 硬约束 6「空结果/超时/partial 一律不得作为最终答复返回」——空洞否定正是打转的燃料 |
修复 | 仅在「引擎 0 命中 **且** query 含 Lucene 语法字符」时，按非词字符拆词、以显式 `OR` 连接**重试一次**（已实测可用形态）；输出显式标注 `queryFallback="…"`；重试后仍 0 命中则给出非空洞的可行动说明（空格分隔 / 显式 OR·AND / 或省略 backend 走正则语义的旧路径）。**有命中的正常路径零行为变化**；旧 scan 路径一行未改 |
新成员 | `LuceneSyntaxChars`（语法字符集）· `FallbackTermSeparators`（拆词分隔符）· `TryBuildOrFallbackQuery`（返回 false = 无需/无法降级：不含语法字符，或拆不出 ≥2 词——单词查询的 0 命中就是真的 0） |
测试 | 新增 3 用例：① 取红主测——含语法字符且引擎 0 命中时必须重试并救回命中、断言恰好重试 **1** 次且降级形态 == `Alpha OR Beta OR Gamma`；② 守卫——纯词 0 命中**不得**重试；③ 守卫——有命中时**不得**多调一次引擎（正常路径零行为变化） |
门禁 | **失败 0 / 通过 61 / 总计 61**（基线 58，旧 58 条全绿无回归）；变异（`TryBuildOrFallbackQuery` 直接 `return false` = 禁用降级）⇒ **失败 1 / 通过 0 / 总计 1（RC=1）**，红在正确断言上（`SearchGrepToolTests.cs:1868`，输出回落 `(no matches)`）；复原 ⇒ 61/61（RC=0）；MUTATION 残留 **0** |
原始输出 | `temp/test-u4-5c-mut-red.txt`、`temp/test-u4-5c-green-restored.txt` |
未做 | 新逻辑需 Core 重启后在本机 `search_grep` 上生效；`|` 在该 parser 下的词法层解释**未定位到 Lucene 源码级结论**——本刀不依赖该解释，只按“`|` ≠ OR、显式 OR 可用”两条实测事实做降级

---

---

**〔原文第 303–313 行：变更（2026-09-25，ADR-089 U4-2a 接入面）：`code_symbol_search` 新增 `match_target`〕**

## 变更（2026-09-25，ADR-089 U4-2a 接入面）：`code_symbol_search` 新增 `match_target`

**改动**：`Tools/BuiltIns/CodeIntelligence/CodeQueryTools.cs` 的 `CodeSymbolSearchTool` / `CodeSymbolSearchArgs` —— 新增 `match_target` 参数（逗号分隔：`name` / `signature` / `container` / `all`），解析后写入 `CodeSymbolSearchRequest.MatchTarget`；未知取值 **fail-closed**（`Fail("Unknown match_target '…'")`）且**不触达** `ICodeQueryService`（不猜、不静默当全开）。同时修正 `query` 的 `[ToolParam]` 描述——原文写「matched against symbol names」，而实现实际匹配 Name/**Signature**/**Container** 三列。

**配套组件**：核心检索行为在 `PuddingCodeIndex`（契约 `CodeSymbolMatchTarget` + `SqliteCodeIndexStore` 逐列开关），详见 `Source/PuddingCodeIndex/code_map.md` 的 U4-2a 条目（含变异取红原始输出）。

**门禁**：新增 `CodeSymbolSearchMatchTargetTests` **4/4**（透传 / 缺省 All / 组合并集 / 未知值 fail-closed 且未触达服务）；`PuddingRuntimeTests` 全套 **1879 通过 / 0 失败 / 6 跳过 / 1885**。

**留白**：`match_target` 只影响**匹配域**，不改变默认行为（缺省仍 `all`）；默认是否收敛为 `name` 属召回/精度取舍，未裁定。

---

---

**〔原文第 315–325 行：变更（2026-09-25，ADR-089 U4-2c）：`file_extensions`「文件类型」过滤面〕**

## 变更（2026-09-25，ADR-089 U4-2c）：`file_extensions`「文件类型」过滤面

**改动**：`Tools/BuiltIns/CodeIntelligence/CodeQueryTools.cs` 的 `CodeSymbolSearchArgs` 新增 `file_extensions`（逗号/分号分隔，前导点可选、大小写不敏感，如 `"cs"` 或 `".cs,.ts"`），经 `ParseFileExtensions` 切分去空白后写入 `CodeSymbolSearchRequest.FileExtensions`；**省略或全空白 ⇒ `null`**（不过滤，与历史行为逐字一致）。归一化（补前导点、大小写）刻意留给存储层，工具层只做切分。

**配套组件**：过滤的 SQL 实现与归一化均在 `PuddingCodeIndex`，详见 `Source/PuddingCodeIndex/code_map.md` 的 U4-2c 条目（含变异取红原始输出）。

**门禁**：`CodeSymbolSearchMatchTargetTests` **6/6**（新增「透传为切分后的列表」与「缺省为 null」两条）；`PuddingRuntimeTests` 全套 **1881 通过 / 0 失败 / 6 跳过 / 1887**。

**留白**：本改动需宿主重启才在运行中的 `code_symbol_search` 上生效。 |

---

---

**〔原文第 327–335 行：变更（2026-10-01，D1 的一半）：`code_index_status` 对未登记项目 fail-closed〕**

## 变更（2026-10-01，D1 的一半）：`code_index_status` 对未登记项目 fail-closed

**改动**：`Tools/BuiltIns/CodeIntelligence/CodeQueryTools.cs` —— `code_index_status` 在返回索引状态**之前**先用**注册表**核对 `project_id`。判定为**单点定义** `CodeQueryToolHelper.IsRegistered`（真源 = `ICodeProjectRegistry.ListProjectsAsync`，与 `code_index_list_projects` **同一 API、同一 workspace 口径**、逐项 `Ordinal` 比对 ⇒「列表工具查不到」⇔「未登记」，两处口径不可能各自漂移）。未登记 ⇒ 显式返回 `status="not_registered"`（单点常量 `CodeQueryToolHelper.NotRegisteredStatus`），`message` 指向 `code_index_list_projects`，且 `started_at_utc`/`completed_at_utc` 恒为 `null`（不泄露陈旧完成时间）；**不抛异常、不是 500**。`ICodeProjectRegistry` 未在 DI 注册 ⇒ 与同目录 `CodeProjectManagementTools` 用**同一句** Fail 文案（不静默降级成「当它已登记」）。**已登记项目的返回体逐字不变**（回归线由冻结原文断言钉住）。`code_symbol_search` 一字未改（D2 是独立切片）。

**为什么**：D1 实测——已从注册表移除的 `scope-6526fb344e33` 仍被 `code_index_status` 报成 `Completed`（查询视图只读索引存储、从不查注册表）。缺陷登记见 `Docs/12_features/Index-Retrieval-Known-Defects-2026-10-01.md`（F1）。

**门禁**：新增 `PuddingRuntimeTests/Tools/CodeIndexStatusRegistryGateTests.cs` **4 用例**（未登记⇒`not_registered` 且**零次**读索引视图 / 已登记⇒冻结原文逐字一致 / 注册表缺席⇒既有 Fail 文案 / 自动探测出的 project_id 同样过门槛）；`dotnet build PuddingRuntime.csproj -c Debug -t:Rebuild` **0 error**；`dotnet test --filter CodeIndexStatusRegistryGateTests` **4/4（RC=0）**。变异（`IsRegistered` 无条件 `return true`）⇒ **2 失败 / 2 通过**，两条均为 `应为: "not_registered" 但却是: "Completed"`（取红）；复原后源码 SHA-256 与变异前**逐位相同**，产物 `PuddingRuntime.dll` SHA-256 由 `C1BE0B1F…` 回到 `5D275F47…`（同一 Clean 构建确定性复现，证明真的重编译），`MUTATION-D1` 残留 **0**。

**留白**：① 需宿主重启才在运行中的 `code_index_status` 上生效；② D1 的另一半（`code_symbol_search` 对已注销/死路径项目照常作答）属独立切片；③ 本片未触碰注册表写路径，D4 的清理阻塞不变。

