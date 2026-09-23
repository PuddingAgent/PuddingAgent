# 任务书：找出 PuddingAgent **最值得修改的 5 个架构级问题**

## 0. 你的角色与硬约束
你是一名**架构评审者**，不是修 bug 的人。任务书要求输出**架构级**问题，不是零散缺陷。
- **严格只读**：不得修改/删除/移动任何文件；不得 `git add/commit/push`；不得构建或重启。
- **唯一允许的写入**：最终报告文件（见 §5）。
- 允许的工具行为：`file_read` / `file_search` / `search_grep` / `code_outline` / `code_symbol_search` / `code_explore` / `list_dir`。**不要**跑构建、测试、dotnet、npm。
- 每条结论必须给 **file:line 级证据**；无法现场查证的必须显式标注「推测」，**不得把推测写成结论**。
- 中文输出。

## 1. 对象与入口
- 仓库根：`E:\github\AgentNetworkPlan\PuddingAgent`（**唯一 git 仓库根**；其父目录 `AgentNetworkPlan` 不是仓库）。
- **先按顺序读这几份，再动手**（路径已经实测校正，照抄即可）：
  1. `Agents.md` —— **仓库根**（`E:\github\AgentNetworkPlan\PuddingAgent\Agents.md`）
  2. `code_map.md` —— **仓库根**（`E:\github\AgentNetworkPlan\PuddingAgent\code_map.md`）。
     ⚠️ 注意：**`Source/code_map.md` 不存在**（含我给你的旧资料写了这个错路径），不要照那个路径读。
  3. 各工程另有自己的索引：`Source/<Project>/code_map.md`（例如 `Source/PuddingPlatform/code_map.md`、`Source/PuddingRuntime/code_map.md`、`Source/PuddingHost/code_map.md`、`Source/PuddingCore/code_map.md`、`Source/PuddingPlatformAdmin/code_map.md`），按需要下钻阅读。
  4. `Docs/` 下的 ADR 与设计文档（尤其 `Docs/Features/`、ADR 编号连续的那些）。
- 解决方案结构（`.sln`）在 `Source/`：PuddingHost（组合根/控制器）、PuddingPlatform（平台服务/任务/存储/会话）、PuddingRuntime（Agent 执行/工具/审计）、PuddingCore（模型/契约）、PuddingCodeIntelligence、PuddingMemoryEngine、PuddingFullTextIndex、PuddingGateway、PuddingController、PuddingDesktop（WPF）、PuddingBrowser.*、PuddingPlatformAdmin（React 前端，`src/pages/*`）。
- 测试工程：`Source/PuddingPlatformTests`（≈1388 用例）、`Tests/PuddingHost.Tests`（≈112 用例）、`PuddingRuntimeTests`、`PuddingWebApiTests`。

## 2. 什么算「架构级问题」（判定口径）
优先找下面这些**结构性**问题，而不是"某个函数写错了"：
1. **单一事实源被破坏**：同一语义在多处各有一份实现/口径（前后端各算一次、缓存与库各一份、DTO 与投影各一份）。
2. **接缝没有被机器强制**：靠人记住的约定（注册/命名/顺序/契约），一旦漏掉只在运行时才炸，且**启动期静默**。
3. **失败模式设计缺失**：错误被吞、静默降级、失败无出口/无重试/无观测（"用户看不到、日志也没有"）。
4. **不可判定性**：状态/所有权/生命周期在多层之间模糊（谁权威、谁负责收敛、谁负责清理）。
5. **依赖方向或分层被破坏**：下层反向依赖上层、跨层直接 new、绕过契约。
6. **可观测性/取证能力不足**：出事后无法重建现场（日志保留、trace 断链、审计缺失）。
7. **契约漂移**：schema/DTO/枚举在跨层演化时没有同一处校验（"加了字段忘了填"这类静默 0/空值）。
8. **测试与真实失败模式不对齐**：测试手搓对象绕过生产组合根/容器，导致守护不到真实故障。

## 3. 已知事项（**请勿重复报告**，除非你发现我之前判断错了并给出证据）
以下是 2026-09-22/23 已定位或已修复的，**不要当新发现**：
1. **DI 注册模式缺陷（已修）**：`AddHostedService<T>()` 只注册 `IHostedService` 不注册具体类型 `T`，导致注入具体类型的控制器**整个控制器所有端点 500**且**启动期无报错**。已在 `PuddingServiceCollectionExtensions.Platform.cs` 改为两步注册，并新增走生产组合根的守护测试 `Tests/PuddingHost.Tests/Hosting/ControllerConstructorDependencyResolutionTests.cs`。**若你认为这条修复仍不彻底（例如该模式在全仓还有其它变体、或该测试覆盖面不足），可以报告，但必须给出新证据。**
2. **存储管理页口径缺陷（已立卡）**：页面标题「Pudding 数据约 9.8 GB」实为「DB 主文件+WAL」；实际 `D:\data` 33.7 GB，其中 `backups` 21.3 GB **在页面上没有对应数据类**；环形图仅 1 扇区渲染 100%、趋势图单点退化、分类报表空。相关：`StorageDataClassCatalog.cs`、`StorageInventorySampler.cs`、`StorageInventorySnapshotStore.cs`（`HistoryPointInterval = 1 hour`）。
3. **聊天「正文渲染不完整 / 晚一条」**：已加 `client/canonicalMerge.ts` + 两个启发式短路门改证据判定；另加 `terminalBodyAlignment`，但真实数据实测「投影是权威全文的真前缀且更短」命中 **0/99** ⇒ **该修复在真实数据上是 fail-closed 空操作**，真因未定位。设计文档 `Docs/Features/Chat前端架构设计方案-2026-09-22.md` §12 有更正块。**completion hydration 尚未实现**。
4. **system 日志被异常删除**：`D:\data\logs\system` 当天仅剩最新 1 个文件，08-22~09-22 整段缺失（非归档）；已立诊断卡。
5. **未接线代码**：`client/syncEngine.ts::createAgentChatSyncEngine` 只被自身与测试引用，页面未接线（已立清理卡）。
6. **`.pudding/` 是运行期必需**：`PuddingRuntime\Services\AgentExecution\ToolResultContextPolicy.cs:16` 的 spill 目录，**不可删**。
7. **前端 bundle 预算很紧**：chat chunk 约 497 KB，硬上限 507904 B，余量约 10 KB。
8. **任务看板**：`Backlog` 积压 184 张卡；`TaskPageDto` 刚加 `totalCount`（忽略 cursor）。看板列映射在 `Services/Tasks/TaskWireMaps.cs`。
9. `AddControllersAsServices()` 未启用 ⇒ `ValidateOnBuild` **覆盖不到控制器构造函数**（这是第 1 条缺陷能静默存在的机制之一）。

## 4. 输出要求（每一条都要有）
对 5 个问题各给：
- **标题**（一句话说清是什么结构性缺陷）
- **证据**：至少 2 条 `file:line`（含原文片段）
- **为什么是架构级**（对照 §2 的 1~8 编号）
- **实际影响**：会造成什么可观测的故障/返工/无法取证（如果已有真实事件，引用它）
- **建议方向**：改法要点 + 是否需要 ADR；**不要写完整实现代码**
- **代价与风险**：改动面、回归风险、是否需要迁移/重启
- **优先级**：P0/P1/P2 + 一句话理由
- **可判定的验收**：怎样才算修好（能写成测试或可测量的判据）

**排序要求**：5 条按「值得修的程度」从高到低排。**我关心的不是数量，而是权重** —— 请明确说明你为什么把某条排第 1，以及你**排除了哪些**看起来像架构问题但你认为不值得修的（列出 ≥3 条被排除项 + 排除理由）。被排除项是本次交付的重要组成部分。

## 5. 交付物
1. 报告文件：`E:\github\AgentNetworkPlan\PuddingAgent\temp\arch-review-gpt-20260923.md`
   - 含 §4 的全部 5 条 + 「被排除项」章节 + 「我没能验证的事项」章节（诚实列出）。
2. 最终消息只返回：
   `SUMMARY:`（≤6 行，含排序后的 5 个标题）
   `REPORT_PATH:`（绝对路径）
   `TOP1_REASON:`（为什么它排第一，≤3 行）
   `EXCLUDED:`（你排除的项，≤5 行）
   `UNCERTAIN:`（未验证事项，≤5 行）

## 6. 工具使用协议（先读，能避免你被工具语法坑掉）
- **`search_grep` 的 `query` 默认按正则解析**。查询中含 `(` `)` `[` `]` `{` `}` `.` `*` `+` `?` `|` `$` `^` `\` 时**必须转义**（如查 `catch (Exception` 要写成 `catch \(Exception`），或改用不含元字符的**短纯文本片段**（如直接查 `Exception`）。
  ⇒ **优先用短纯文本，不要写复杂正则。**
- `search_grep` 有硬上限（最多枚举 2000 文件 / 扫描 64MB / 单次 10 秒）。返回带 `coverage: partial` 或 `搜索超时` 表示**未覆盖全仓** ⇒ 必须缩小 `directory` / `file_ext` 再查，**不得据此下「不存在」的结论**。
- 查「某符号在哪定义 / 被谁引用」优先用索引工具 `code_symbol_search` / `code_explore`（毫秒级、不受枚举上限影响），比 grep 稳得多。
- 大文件用 `file_read` 的分页参数（`head_lines`，或 `offset_lines` + `limit_lines`），**不要**用 `full_file=true`。
- **单个工具报错不许终止整个任务**：换一种更简单的查法继续；把失败的那次尝试记在「我没能验证的事项」里。

## 7. 完成标准
- 报告文件存在且含 5 条 + 被排除项 + 未验证事项。
- 每条问题都有 ≥2 处 file:line 证据（未验证的已标注）。
- 除报告文件外，未发生任何写入/修改。
