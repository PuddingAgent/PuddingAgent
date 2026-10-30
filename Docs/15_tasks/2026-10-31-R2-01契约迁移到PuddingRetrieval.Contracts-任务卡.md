---
title: R2-01 检索契约迁移到 PuddingRetrieval.Contracts · 任务卡
author: 鲸鲸
date: 2026-10-31
last_reviewed: 2026-10-31
status: proposed
---

# R2-01 契约迁移任务卡

> 施工计划 R2-00/R2-01：创建 `PuddingRetrieval.Contracts`（BCL，零依赖），并把跨 provider 的 DTO 与 `IRetrievalPort` 收敛进去。**Contract 不得传递引用 `CodeSymbolKind` / CodeIndex。**

## 1. 现状（现场实测，非估算）

```
Source/PuddingCodeIndex/Contracts/Retrieval/   30 个文件
其中依赖 CodeSymbolKind 的                       6 个文件
引用该命名空间的文件（含 Runtime / Eval / 测试）  ~40 个文件
```

`Contracts/Retrieval/` 文件清单：`ContainerSymbolGuidance` · `IRetrievalPort` · `LanguageCapability` · `RetrievalBudget` · `RetrievalClaim` · `RetrievalContractViolationException` · `RetrievalDiagnostics` · `RetrievalDistribution` · `RetrievalEmptyReason` · `RetrievalEvidence` · `RetrievalFilter` · `RetrievalHit` · `RetrievalHitComparer` · `RetrievalHitDeduplicator` · `RetrievalHitViews` · `RetrievalIntent` · `RetrievalIntentPolicy` · `RetrievalNextStep` · `RetrievalOverflow` · `RetrievalPathFacts` · `RetrievalRequest` · `RetrievalResult` · `RetrievalTaxonomy` · `ScopeIdSet` · `ScopeOverlap` · `SymbolAnchor` · `SymbolIdentity` · `SymbolRefV1`（另有 `DefaultLanguageCapabilityMatrix` 位于 Services 但命名空间同）。

## 2. 真正的阻塞点（本轮评估的核心结论）

**不能只搬文件。** 下列 6 个契约文件直接使用 `CodeSymbolKind`：

| 文件 | 用法 |
|---|---|
| `RetrievalHit.cs` / `RetrievalHitViews.cs` | 属性 `SymbolKind` |
| `RetrievalFilter.cs` | 过滤字段 + `ForSymbolKinds(...)` |
| `RetrievalIntentPolicy.cs` | 意图 → 允许的符号种类 |
| `RetrievalDistribution.cs` | `BySymbolKind` 计数 |
| `ContainerSymbolGuidance.cs` | 容器判定 + 文案 |

`CodeSymbolKind` 当前在 `PuddingCodeIndex.Contracts`。若把这些文件搬进 `PuddingRetrieval.Contracts` 而不动枚举，新组件就必须引用 `PuddingCodeIndex` —— **正好违反计划里那条依赖禁令**。

因此顺序必须倒过来：**先下沉枚举，再搬契约。**

## 3. 执行顺序（每步独立可验证、独立提交）

### 步骤 1：枚举下沉

- 把 `CodeSymbolKind`（以及被检索契约实际用到的 `CodeRelationKind`）从 `PuddingCodeIndex.Contracts` 迁到 `PuddingRetrieval.Contracts`，**成员与数值顺序逐字不变**。
- 在 `PuddingCodeIndex.Contracts` 保留 `global using` 或类型别名转发，避免一次性改动上百处引用。
- 验收：`PuddingCodeIndexTests` + `PuddingCodeIntelligenceTests` 全绿（数值未变，序列化/存储不受影响）；`PuddingRetrieval.Contracts` 仍零 `Pudding*` 引用。

### 步骤 2：契约文件迁移

- 30 个文件移入 `PuddingRetrieval.Contracts`，命名空间改为 `PuddingRetrieval.Contracts`。
- **只改命名空间与 using，不改任何公共成员名字/签名/默认值/校验语义。**
- 验收：`PuddingRetrieval.Contracts` 独立构建 0 警告 0 错误；依赖仅为 BCL；`ComponentBoundaryTests` 断言零 `Pudding*` 引用。

### 步骤 3：引用方改写

- ~40 个引用文件按工程分批改 `using`：`PuddingCodeIndex` → `PuddingRuntime` → `PuddingRetrievalEvalProbe*` → 各测试工程。
- 每批改完立即构建该工程，不攒到最后。
- 验收：`dotnet sln PuddingAgentNetwork.slnx list` 后逐工程构建通过；全仓测试与 R1 合并门禁基线一致（953 条 + PuddingGrep 40 条）。

### 步骤 4：边界与回归收口

- 断言 `PuddingCodeIndex` **不再**定义检索契约（除保留的转发）；
- 断言 `PuddingRetrieval.Contracts` 不含任何 `using PuddingCodeIndex...`；
- R0-01 的空结果 claim 行为、R1 各修复不许回退（合并门禁全绿）。

## 4. 禁改边界（可判定）

1. 不改任何公共成员的**名字、签名、默认值、异常类型**；
2. 不改 `RetrievalClaim` / `RetrievalResult` 的 fail-closed 校验语义；
3. 不顺手重构、不合并重复实现、不调整文件中成员的顺序（减少 diff 噪声）；
4. 不迁 `DefaultLanguageCapabilityMatrix` 到 Contracts（它是实现，留在组件内）；
5. 不在本任务内做 DI 接线或运行时替换（S5 另立）。

## 5. 变异与三态要求

- 至少一处变异证明新测试/断言有效（例如：让 `PuddingRetrieval.Contracts` 临时引用 `PuddingCodeIndex`，`ComponentBoundaryTests` 必须红）。
- 每次提交附：构建输出、测试计数、变异红/还原绿的原始输出。

## 6. 风险与已知代价

| 风险 | 处理 |
|---|---|
| `CodeSymbolKind` 数值顺序被无意改动 → 持久化数据错位 | 步骤 1 逐成员对照，测试断言首尾数值 |
| 40 个引用点漏改 → 编译失败但可能被某工程跳过 | 用全解决方案构建，不只看主产物 |
| 大规模 diff 掩盖行为变化 | 每步只看「命名空间/using」差异，不允许出现逻辑行改动 |

## 7. 不在本卡范围

- `PuddingRetrieval.Adapters`（存储读取迁移）
- `PuddingGrep` 的运行时接入（已建好内核，S5 另立）
- R2-02 的物理项目/程序集身份

## 附录 A：影响面实测（2026-10-31 04:26，`TestScripts/migrate-retrieval-contracts.ps1` 干跑）

任务卡 §1 的估算数字已用脚本核实并精确化：

| 项 | 实测 |
|---|---|
| 契约文件（`Contracts/Retrieval/*.cs`） | **28** |
| 仓库内 cs 文件扫描量 | 3,522 |
| 引用旧命名空间的文件 | **68** |
| 其中声明旧命名空间（即契约文件本身） | 28 |
| **需要改写 using 的外部文件** | **40** |

按工程分布（引用文件数）：

| 工程 | 文件数 |
|---|---|
| `PuddingCodeIndex` | 32 |
| `PuddingCodeIndexTests` | 17 |
| `PuddingRuntime` | 7 |
| `PuddingRuntimeTests` | 7 |
| `PuddingRetrievalEvalProbe` | 3 |
| `PuddingRetrievalEvalProbeTests` | 2 |

任务卡 §1 写「30 个文件 / ~40 个引用」——**文件数应为 28**（`dir` 数了 30 是把两个非 `.cs` 条目也算进来），引用数与估算一致。

### ⚠️ 一处仪器失效（必须记）

脚本首版用 `Select-String -Path <file> -Pattern <ns> -SimpleMatch -Quiet` 统计引用文件，**返回 `files referencing old ns : 0`**。

若就此收工，会得出「没有任何外部引用、迁移毫无风险」的结论 —— 与实际 40 个引用相差 40 个文件。

改为 `[IO.File]::ReadAllText(path, UTF8).Contains(ns, Ordinal)` 后立刻得到 68（含 28 个自身）。**同一份数据、同一条命令意图，两种检索方式给出 0 与 68。** 这类「仪器坏了却报出平静结论」的情形，必须以阳性对照复核后才可信。

### 脚本的用法

```
powershell -NoProfile -ExecutionPolicy Bypass -File TestScripts\migrate-retrieval-contracts.ps1          # 干跑，只出报告
powershell -NoProfile -ExecutionPolicy Bypass -File TestScripts\migrate-retrieval-contracts.ps1 -Apply   # 实际移动 + 改命名空间
```

`-Apply` 之后仍需手工补：`PuddingCodeIndex.csproj` 增加对 `PuddingRetrieval.Contracts` 的引用、删除 CodeIndex 侧同名枚举、全仓构建修剩余编译错误。

## 附录 B：枚举影响面实测与一处架构判断修正（2026-10-31 04:36）

### 实测数字

扫描 `Source/**`（排除 bin/obj）中**使用** `CodeSymbolKind` 或 `CodeRelationKind` 的文件：

| 工程 | 文件数 |
|---|---|
| `PuddingCodeIndexTests` | 23 |
| `PuddingCodeIndex` | 13 |
| `PuddingCodeIntelligence` | 13 |
| `PuddingCodeIntelligenceTests` | 12 |
| `PuddingRuntimeTests` | 12 |
| `PuddingRuntime` | 3 |
| `PuddingRetrieval.Contracts` / `…ContractsTests` | 2 / 2 |
| `PuddingRetrievalEvalProbe` | 1 |
| **合计** | **81** |

对比附录 A：**引用检索契约的只有 40 个文件，而使用这两个枚举的有 81 个**。

### 这改变了什么

任务卡 §3 步骤 1 原计划「删掉 CodeIndex 侧枚举定义，让 CodeIndex 用契约组件的那份」。按上面的数字，这一步会牵动 **81 个文件**的命名空间解析 —— 远大于「搬 28 个契约文件」本身。

### 架构判断修正

`CodeSymbolKind` / `CodeRelationKind` **不是检索契约特有的概念**：它被 CodeIndex（存储）、CodeIntelligence（抽取）、Runtime（工具）、各处测试共同使用。把它塞进 `PuddingRetrieval.Contracts` 会让这个"检索契约"组件事实上变成**全仓基础类型库**，语义上并不准确。

更贴合依赖方向的做法：建一个更基础的共享契约组件（例如 `PuddingDomain.Contracts` 或 `PuddingCodeModel.Contracts`），只承载 `CodeSymbolKind` / `CodeRelationKind` 这类**跨层共享的值类型**；`PuddingCodeIndex` 与 `PuddingRetrieval.Contracts` 都依赖它，而不是互相依赖。

**在做出这个裁定前，不应执行 `-Apply`。** 迁移目标程序集一旦选错，81 个文件的 using 会白改一次。

### 需要裁定的问题

1. 枚举下沉目标：`PuddingRetrieval.Contracts`（现方案）还是新建更基础的共享契约组件（建议）？
2. 若采用新组件，`PuddingRetrieval.Contracts` 的定位收窄为「只放检索请求/结果/覆盖声明」，与计划 §2 的描述一致。
