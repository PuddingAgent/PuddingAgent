## 2026-09-25 S5：宿主预建索引改走「协调器 + 暂存供给」（**默认关闭 ⇒ 零索引 I/O**）

**断点**：`IndexPrebuildService` 原为**直写** `_searchEngine.BuildIndexAsync` + 用**索引根** mtime 判新鲜 —— 绕开 A1 的跨进程租约与 A2a 的预算硬限 / staging / 原子切换；且多 scope 时根 mtime 是**错信号**（一个 scope 构建会让其它 scope 被误判“新鲜”）。

**交付（宿主 5 文件 + 3 测试文件）**：
- 新增 `Source/PuddingHost/Hosting/IFullTextIndexSupplyComposition.cs`：供给组合端口 + **惰性工厂**端口（不直接注入协调器，正是为了满足 R4「默认关闭时**连组合都不构造**」）。
- 新增 `Hosting/LuceneFullTextIndexSupplyCompositionFactory.cs`：生产装配（`FileSystemSupplyInventory` + `StagedFullTextIndexBuilder`（live 引擎 = 查询侧同一实例）+ `FileSupplyLease` + `FullTextIndexSupplyCoordinator`；配置流入；per-scope 新鲜度探针）。
- 改 `Services/IndexPrebuildService.cs`（256/45）：写路径换「提交 → 轮询 `GetStatusAsync` 至终态（超时上限默认 30 min；**超时不取消 job**，只停止观测并如实记 last state）」；**每 scope 最多提交一次**（无重试风暴）；`Rejected/Busy/Failed/RolledBack/Cancelled/超时/未知 job` 逐类如实上报（带 jobId）；ctor 去掉 `FullTextIndexOptions`，保留引擎**仅供只读 `HasIndex`**。
- 改 `Extensions/PuddingServiceCollectionExtensions.Runtime.cs`（23/0 纯追加：:172-186 注册工厂 + `IFullTextIndexRootedEngine` 接缝断言）、`Hosting/IndexPrebuildFreshness.cs`（9/5 **仅注释**：仪器口径由「索引根 mtime」改为 per-scope live 索引目录）、`Hosting/code_map.md`（22/0）。
- 测试（`Tests/PuddingHost.Tests/Hosting/`，xUnit）：`S5IndexSupplyHostWiringTests`（Fact=7，A1~A6 + 轮询超时）+ `S5FullTextIndexSupplyHostCompositionTests`（Fact=1，组合根级配置流入证明）+ `S5SupplyTestDoubles`；`IndexPrebuildServiceTests.cs` 按新构造适配（仍 3 条）。

**⚠️ 规格更正（父级任务书有错、实现是对的）**：任务书曾写「per-scope 新鲜度用 `SupplyIndexDirectoryLayout` 解析 `<IndexRoot>/<sha256(scopeKey)>`」——**实测两点不符**：① 该类型是 `internal`（宿主不可用）；② 它的 `Sha256Hex(scopeKey)` 用于 `.staging`/`.trash` 条目名与租约文件名，**不是 live 目录名**（live 目录名由 `LuceneSearchEngine.GetIndexDirectoryPath` 用**大写规范化全路径**算出）。照抄会得到永不存在的路径 ⇒ 每 scope 恒判「需重建」。实现改用组件文档指定的**单一真源** `IFullTextIndexRootedEngine.ResolveIndexDirectory(scope)`（public 契约，A2a 新增）。

**门禁（父级独立复跑）**：`dotnet build Source\PuddingHost -c Release` ⇒ **0 警告 / 0 错误**；`dotnet test Tests\PuddingHost.Tests -c Release` ⇒ **通过 154 / 失败 0 / 跳过 0**（基线 146 ⇒ **+8** = 7+1，逐数吻合）；`PuddingFullTextIndexTests` ⇒ **123（119 通过 / 4 跳过 / 0 失败）**；`PuddingFullTextIndex.Cli.Tests` ⇒ **41/41**；四者 exit 均 0。机械断言：`Source/PuddingHost` 内 `.BuildIndexAsync(` 调用点 **0**、`1_073_741_824` **恰 1 处**（配置类 :35）。
**变异取红 3 组**（原始输出 `temp/s5-evidence/M{1,2,3}-red.log` + `14-restored-green-for-mutations.log`）：M1 恢复直写 ⇒ `A2_...Live_Engine_Is_Never_Written_Directly` **红**（`Assert.Equal(0, engine.BuildCalls)`）；M2 拆掉 `Enabled` 门控 ⇒ `...Never_Touches_The_Engine` **红**（`Assert.Equal(0, factory.CreateCalls)`）；M3 新鲜度退回索引根 mtime ⇒ `A5_Per_Scope_Freshness...` **红**（陈旧 scope 被误判新鲜）；复原后三者转绿，`git hash-object` 与变异前**逐位相同**，代码内 `MUTATION` 残留 **0**（大小写敏感 + 含未跟踪；`.md` 与代码分开判定）。生产索引根全程只读（224 条目、无 `.staging`/`.trash`），构建/测试只用 `%TEMP%`。
**留白**：① 超时**不取消** job（组件继续跑完并释放租约），如需「超时即取消」属后续切片；② `BuildWaitTimeout` 默认 30 min 为经验值（Source scope ≈69 s / 仓库根 ≈115 s）；③ `StartupDelay`（10 s）与 `StopAsync` 不等待后台作业沿用 U4-7 语义未改；④ 生产索引根现存目录是 **8 位 hex 旧命名**，当前引擎产出 **64 位 hex** ⇒ 一旦启用供给会**全量重建**；⑤ **运行态未验证**：本刀未重启、未启用 `Enabled`，重启后必须实测「默认关闭 ⇒ 零索引 I/O」与「启用后 `search_grep backend=index` 命中」。报告：`temp/S5-REPORT.md`；重启验证清单：`temp/s5-restart-verification-checklist.md`。

---

## 2026-09-25 U4-7：全文索引「供给参数」配置化 + fail-closed 校验（**默认关闭，现网行为不变**）

**用户裁定（2026-09-25）**：「1GB 请使用配置文件确定参数，方便后期替换为 XXGB……用项目目录的统计 json 或 **Data 目录的配置文件**决定，而不是选择一个固定值。」⇒ 取 **Data 目录 `<DataRoot>/config/system.json`** 的 `FullTextIndex` 节。

**交付（宿主侧 4 文件 + 4 测试文件）**：
- `Source/PuddingHost/Hosting/FullTextIndexSupplyOptions.cs`：`Enabled`（**默认 false**）/ `Scopes` / `WorkspaceRoot`（相对项的显式绝对基准，**禁用进程 CWD**）/ `MaxIndexBytes`（**默认 `1_073_741_824` = 1 GiB = 2^30**，硬天花板 1 TiB）/ `MinRebuildInterval`（默认 12h）。**单一真源**：1 GiB 字面量生产代码只出现一次。
- `Source/PuddingHost/Hosting/FullTextIndexSupplyResolver.cs`：fail-closed **纯函数**校验（三态 scope 探针可注入替身 ⇒ 单测零文件系统访问）；关闭 ⇒ 空动作且**零 I/O**（探针调用数 0）；空 Scopes / 空串·不存在·非目录·重复项 / 相对无基准 / `MaxIndexBytes<=0` 或 >1 TiB / 负间隔 ⇒ **结构化拒绝**（参数名+值+原因枚举），accepted/rejected 分别列出。
- `Source/PuddingHost/Hosting/IndexPrebuildFreshness.cs` + `Services/IndexPrebuildService.cs`：预建服务改为**配置门控**（`StartAsync` 永不阻塞；默认配置下不建索引、零索引 I/O、不记 Error；校验不过 ⇒ 记 Error 且什么都不做）；**目标来自配置，不再是 `Directory.GetCurrentDirectory()`**（历史缺陷）。
- `Extensions/PuddingServiceCollectionExtensions.Runtime.cs`：`Configure<FullTextIndexSupplyOptions>(builder.Configuration.GetSection(...))`（**必须用 `builder.Configuration`**，`system.json` 只加在它上面）+ 把 `IndexPrebuildService` 从 `HOSTED-DISABLED` 改为常驻注册（默认关闭 ⇒ 等价 no-op）。

**门禁（父级自跑口径的原始输出）**：`dotnet test Tests/PuddingHost.Tests/PuddingHost.Tests.csproj -c Release` ⇒ **失败 0 / 通过 146 / 总计 146**（基线 126 + 新增 20；新增用例过滤跑 `FullTextIndex|IndexPrebuild` = 20/20）。
**变异取红 8 组**（红/复原绿原始输出在 `temp/u4-7-evidence/m{1..8}-red.txt` + `u4-7-test-green.txt` / `u4-7-test-full-green.txt`）：M1 打开默认关闭 ⇒ **4 红**（A1/A4/A6 + 无该节默认关闭）；M2 `MaxIndexBytes<=0`→`<0` ⇒ A5 红；M3 负间隔检查失效 ⇒ A5 红；M4 Scopes 为空检查失效 ⇒ **2 红**（A2 + 服务级「拒绝可见」）；M5/M6/M7/M8 分别打掉空串/不存在/重复/非目录判定 ⇒ A3 红。复原后四份源码 `git hash-object` 与变异前**逐位相同**。
**⚠️ 留白（R5）**：体积护栏的**执行**（达上限 ⇒ 告警/拒写/GC）**未实现**（ADR-089 §7.2 早已登记「留到后续切片」）；`MinRebuildInterval` 的「索引是否足够新」目前用**索引根 mtime** 粗粒度代理（per-scope 索引目录在组件内且 `internal`）⇒ 精确口径待组件暴露端口。报告：`temp/U4-7-REPORT.md`；ADR 追加节：`Docs/Features/ADR-089-索引服务与库管理-2026-09-24.md` §8。

---

## 2026-09-24 U4-5a：`search_grep` 新增 `backend` 路由参数（接全文索引后端，**旧路径零改动**）

**用户裁定（2026-09-24）**：「不要修改原来的旧的，而是 search_grep 新建一个参数…使用一个参数来路由到新的检索代码上，稳定之后，我们合并到旧的代码」，并要求可观测（“是否可以返回检索一次的时间”）。

**交付**（`Source/PuddingRuntime/Tools/BuiltIns/Search/SearchGrepTool.cs`）：
- 新增参数 `backend`：`scan`（未传/缺省 = 旧路径托管扫描，**逐字节不变**）| `index`（新：直接吃全文索引，不跑托管扫描）| 其他值 ⇒ `contract_error`。
- `index` 后端（新方法 `IndexBackendSearchAsync`）：单次 `IFullTextSearchEngine.SearchAsync`（Lucene 查询解析器语义，与评测探针 `LuceneFullTextProbe` 同源）；**不做托管全量扫描 ⇒ 2000 文件/64 MB/10 s 三重上限不适用**（这正是“假否定”的来源）；后置过滤复用既有 canonical 合同（`IsPathInExcludedDir` / `RetrievalGlobMatcher`）；索引快照指向已不存在文件时不输出并计 `staleSkipped`。
- **fail-closed**：无索引/引擎异常/超时/`case_sensitive=true` 均显式失败并提示“省略 backend 走旧路径”，**不静默回落**（静默回落会让索引坏掉也看不出来）。
- **不写失败账本**：否则一次 `index` 的 no_match 会把同 query 的旧路径调用短路，反而破坏兑底。
- **观测**：结果尾行含 `backend=index` / `engineMs`（引擎侧）/ `totalMs`（工具侧，含后置过滤）/ `engineMatches` / `engineTotalMatches` / `staleSkipped` / `truncated` / `scope`；telemetry 指标名 `search_backend_index`（与旧路径 `search_attempt` 分离，`elapsed_ms` 作维度）；宿主侧另有 `agent_diagnostics(tool_stats, tool_name="search_grep")` 可查调用数/成功率/平均耗时。

**门禁（S1~S3，未重启宿主）**：`PuddingRuntimeTests` 过滤 `SearchGrepToolTests` ⇒ **失败 0 / 通过 56 / 总计 56（5 s）**（新增 6 用例 + 既有 50 用例全绿；旧路径行为未变）。
**变异取红**：去掉 `index` 分支的 `return`（= 静默回落扫描）⇒ **失败 4 / 通过 52 / 总计 56（exit 1）**，红点正好是新路径的 4 个 `index` 用例；复原后 **失败 0 / 通过 56（exit 0）**。原始输出：`temp/test-out/u4-5a-mut-red.txt`、`u4-5a-mut-green-restored.txt`。
**未做（诚实留白）**：新参数要 Core 重启后才在本机 `search_grep` 上真正可用（宿主仍在跑旧程序集）；`index` 后端在活仓库上的真实召回/延迟未实测（评测面证据见 U4-6 根 scope 报告）；**“谁在生产里建全文索引”仍是未证实项**（`D:\data\fulltext-index` 有 9 个 SHA256 命名的索引目录，未逐个反查 scope）。

---

## 2026-09-24 U4-6 验收：根 scope 索引建成（1.9 分钟 / 4,432 文件）+ 用例覆盖度 78/80 → 80/80

**这是「增强检索跑起来」的第一个可见验收点**。U4-6（单次遍历 + 噪声目录剪枝）落地后，此前被判「不可行」的
**仓库根 scope 索引**真的建起来了（`Source/PuddingRetrievalEvalProbe` 的 `--mode index`，`BuildIndexAsync` 原路径）：

| 项 | 值 |
|---|---|
`filesIndexed` | **4,432**（剪枝前探针实测根 scope「可索引 28,178 文件 / 743 MB」⇒ 排除掉 84% 的产物/依赖目录）|
`totalBytes` | 81,081,257（81 MB）|
`engineMs` | **115,364 ms ≈ 1.9 分钟**（旧实现 77 × 195 s ≈ **4.2 h**，即「不可行」）|
`indexBytes` | 102,544,771（97.8 MB）|

**覆盖度**：同一索引面上跑 `seed-v1`（80 条）⇒ `cases=80 failed=0`。此前只能覆盖 78/80 —— 余下 2 条锚定仓储根
文件（`Agents.md` / `Agents-Hygiene.md`）的用例因「根 scope 索引不可行」而**结构上不可测**，现在**可测**了。

**指标（根 scope，全文面；报告已入库 `Source/PuddingRetrievalEval/eval/reports/u4-6-root-scope-2026-09-24.{md,json}`）**：
recall@1 0.3000 / @5 0.4375 / @10 0.4562；MRR 0.4217；noiseRate@10 **0.0000**；
冷 p50 12.077 / p95 49.095 ms；热 p50 12.305 / **p95 47.008 ms**（满足 ADR 建议的「本地中型仓库热态 p95 ≤ 50 ms」，判定权仍在用户）。

⚠️ **本条同时产生一个新发现（下一步的靶子）**：分层 recall@1 = **C# 0.6111 / TS 0.0682 / md 0.0227**，
而同一批 md 用例在 `Docs/` 子 scope 下 recall@1 = **0.2500** ⇒ **检索面变大后词法召回被稀释**。
这正是 U4-2（作用域 + 文件类型合同化，用户需求 R3/R4）与 U4-4（向量 chunk + RRF 融合）要解决的对象，
也是「在根 scope 上无过滤地做词法检索」不可取的量化证据。
⚠️ 仪器瑕疵（如实登记）：报告内 `## Reproduce` 段落打印的是 `temp/U4-0-probe/PuddingRetrievalEvalProbe/...`
路径，而活的探针工程已迁到 `Source/PuddingRetrievalEvalProbe/`；复现命令需按后者执行（待修）。

---

## 2026-09-24 U4-6：索引构建从「每个扩展名各遍历一次目录树」改为「单次遍历 + 噪声目录剪枝」

**为什么这是「让增强检索跑起来」的第一刀**：任何检索面都依赖索引，而索引自己建不出来就等于检索不存在。
`LuceneSearchEngine.BuildIndexInternalAsync` 把白名单里 **77 个扩展名**（`PlainTextExtensions` 76 + `ParsedExtensions`）
逐个当成 glob 交给 `Directory.EnumerateFiles(..., AllDirectories)` ⇒ **同一棵树被完整遍历 77 次**；而
`FullTextIndexOptions.IsExcludedPath` 只在枚举**之后**过滤 ⇒ 被排除目录仍被完整走一遍。探针据此判定「根 scope 索引不可行」
（77 × 195 s ≈ 4.2 h；可索引 28,178 文件 / 743 MB，其中 `.pudding` 20,926 + `.tmp-build` 1,335 + `.pnpm-store` 1,075 +
`.tmp-test-out` 240 占 84%）。后果：检索只能在 `Source/`、`Docs/` 等子目录建索引，仓库根上只能退化到 `search_grep` 的
托管扫描（2000 文件 / 64 MB / 10 s 三重上限 ⇒ **假否定**）。

**交付（S1~S3：只动组件与独立测试，未碰宿主）**：
- `LuceneSearchEngine.EnumerateFilesPruned`（新增）：显式栈 DFS **单次遍历**，遇到噪声目录（`ExcludedDirectoryNames`）
  **立即剪枝不再进入**；`DirectoryNotFoundException`/`UnauthorizedAccessException`/`IOException` 局部化到单目录，
  不再中断整次扫描；不跳过重解析点（与旧 `AllDirectories` 一致，登记为既有风险）。
- `LuceneSearchEngine.MatchesAnyPattern`（新增）：调用方 `filePatterns` 改由 `FileSystemName.MatchesSimpleExpression`
  在枚举后按文件名过滤，不再以 77 次 glob 枚举表达白名单（语义不变：`".cs"`/`"*.cs"` 一律按 `*.cs` 解释）。
- 扫描块：`foreach (var filePattern in filter)`（77 个 glob）→ **单次遍历**；扩展名白名单与调用方 glob 均在枚举后按文件过滤。
- 测试 `Source/PuddingFullTextIndexTests/BuildIndexWalkTests.cs`（**6 用例**）：单次遍历不重复计数、噪声目录不入索引面、
  显式 pattern 仍生效、空/超限文件跳过、排除文件名跳过、深层文件仍可被检索。

**门禁（父级自跑）**：`PuddingFullTextIndexTests` **失败 0 / 通过 56 / 已跳过 4 / 总计 60**（新增 6 用例）。
**行为等价性（同一天同一棵树同一缓存状态的配对实测，`Source/PuddingRuntime` scope）**：
新代码 **filesIndexed=347 / totalBytes=3,868,333 / engineMs=7,009**；`git stash` 复原旧代码 **347 / 3,868,333 / 8,550**
⇒ **文件集合与字节总量完全一致**。
**收益实测（`Source/` scope，`BuildIndexAsync` 原路径）**：**462,192 ms → 68,971 ms（≈6.7×）**。
⚠️ 同批 `filesIndexed=3404`（文档基线 3,514）：差额归因于**今日更早落地的排除清单扩容**（U4-4 D2/D4：33 项私有副本 →
派生自 `PathNoiseRules.DirectoryNames` 52 项），**不是本刀**（配对实测已证本刀集合中性）；此为归因、非逐文件核对，如实登记。
**变异取红**：移除本刀新增的调用方 glob 过滤 ⇒ **红**：`CallerPatterns_AreStillHonoured` 失败
（`失败 1 / 通过 5 / 总计 6`，`temp/test-out/u46-mut-red-nopattern.txt`）；复原后**绿**（exit 0）。
⚠️ **剪枝本身无语义可观测面** ⇒ 无可变异取红的单测（不用时间断言伪装）；证据是上述配对与计时。
**仪器提醒**：Lucene `indexBytes` **不作为等价性判据**（同一输入 4,374,903 vs 4,368,078）。
**留白**：`search_grep` 的三重硬上限（2000/64 MB/10 s ⇒ 假否定）未修（属 U4-5）；根 scope 尚未实际建过索引。

---

## 2026-09-24 M2-a：容量预算配置化（1GB 不再是硬编码常量）

**用户裁定（2026-09-24）**：容量上限必须由配置文件决定，便于后期改为 XXGB；默认值可由父级按推荐指定；建议用「项目目录 json」或「Data 目录配置文件」而非固定值。并明确指出「1GB」存在 **GB/GiB 歧义** ⇒ 必须固化为精确字节数。

**交付（组件内 3 新契约/服务文件 + 1 测试文件；严格 S1~S3，未碰宿主）**：
- `Contracts/CodeIndexLibraryBudget.cs`：`CodeIndexLibraryBudgetSource{Default,GlobalConfig,ProjectConfig}`｜`CodeIndexLibraryCapacityLevel{Ok,SoftExceeded,HardExceeded}`｜`CodeIndexLibraryBudget(MaxBytes, SoftRatio, Source, ConfigPath, Warnings)` + `SoftBytes`｜`CodeIndexLibraryBudgetDefaults.MaxLibraryBytes = 1L << 30`（**精确 1,073,741,824 B**，注释明示「改配置不要改常量」）｜`CodeIndexLibraryCapacityReport`。
- `Contracts/CodeIndexSizes.cs`：`CodeIndexSizes.Parse` 锁定单位语义 —— `B`/无后缀=字节；`KiB/MiB/GiB/TiB`=**二进制（IEC 80000-13）**；`KB/MB/GB/TB`=**十进制（SI）**；未知单位/非正数/越界一律失败。
- `Services/CodeIndexLibraryBudgetResolver.cs`：**路径注入**（组件不猜 DataRoot，边界显式），优先级 **项目文件 > 全局文件 > 内置默认**，**逐字段合并**；坏 JSON / 非法值 ⇒ **退回默认（fail-closed，绝不返回「无上限」）** 并告警；缺失文件 = 该来源缺席（不是错误）；两个 size 字段并存时以 `maxLibraryBytes`（精确字节）为准并告警；**使用十进制后缀时告警**，让 GB/GiB 歧义永不静默。
- `Services/CodeIndexLibraryCapacity.cs`：`MeasureDirectoryBytes`（**库目录总占用：含 db + wal + shm + 向量段 + 临时重建** —— 按授权取「最简单且不会漏计」口径）+ `Evaluate`（Ok / SoftExceeded / HardExceeded）。
- 测试 `PuddingCodeIndexTests/Services/CodeIndex/CodeIndexLibraryBudgetTests.cs`（**12 用例**）。

**实际配置文件已按裁定落地**：`D:\data\config\code-index.json`（全局，沿用 `llm.providers.json` 同目录同风格，含 `_doc` 自述单位与优先级）；项目级约定 `<projectRoot>/.pudding/code-index.json`（`.pudding` 已被索引器硬排除）。⚠️ **尚未接线**：宿主侧的路径发现与结果注入属 S5，**当前无任何代码读取该文件** ⇒ 它是「就绪待接线」，不是「已生效」。

**门禁（父级自跑）**：`PuddingCodeIndexTests` **失败 0 / 通过 133 / 总计 133**（基线 121，**+12**）；`PuddingHost.Tests` **126/126**（无回归）；两工程 `exit 0`。
**变异取红（两次，原始输出 `temp/test-out/m2a-mut-red-failopen.txt` / `m2a-mut-red-gb-binary.txt`）**：
- **#1** 坏 JSON 从 fail-closed 改成返回「无上限」⇒ **红**：`Invalid_Json_Falls_Back_To_The_Default_Instead_Of_Unlimited` 失败（应 `<1073741824>` 实 `9223372036854775807`），**失败 1 / 通过 11**；
- **#2** `"GB"` 改成二进制 ⇒ **红**：`Size_Suffixes_Keep_Si_And_Iec_Semantics_Apart` + `Decimal_Unit_Use_Is_Surfaced_Instead_Of_Silently_Reinterpreted` 两条失败（应 `1000000000` 实 `1073741824`），**失败 2 / 通过 10**；
- 复原后**全绿**；`MUTATION` 残留 grep **0 命中**。

**仪器教训（本轮新踩）**：`file_patch` 一次调用里的多个 operation **只能作用于同一个 `path`** —— 我误把「复原 A 文件 + 变异 B 文件」放进同一次调用，结果第 1 个操作静默未命中、第 2 个已生效，**差点把两次变异混在一起污染归因**。规范：**一次 `file_patch` 只处理一个文件**。

---

## 2026-09-24 M1-a：legacy scope 的覆盖态投影改穷举 + fail-safe（D1 根治第一刀）

**问题比 U3-G1 描述更广**：`CodeProjectStatus` 有 **6** 个成员，而 `CodeIndexScopeRegistry.MapStatus` 只显式处理 3 个，`_ => ScopeState.Covered` 把 **`Unknown` / `Registering` / `Removing` 三个不同生命周期状态全部静默折叠成「被父 scope 覆盖」**。`Covered` 的语义是「不归属、不服务、无需索引」⇒ 命中它的 scope 会被附着循环**永久跳过、永远无法自愈**（根 scope 就是这样消失的）；**`Removing`（删除中）被当成「被覆盖」尤其荒谬**。

**交付（组件内 1 文件 + 新建独立测试文件）**：
- `MapStatus` → 更名 **`ProjectLegacyScopeState`** 并**逐项穷举**：`Active`→`Active`｜`Registering`→**`Active`**（覆盖是**归属**事实；「欠一次运行」是运行态，不是覆盖态）｜`Unknown`→`Active`（未建立的状态不得冒充「被覆盖」）｜`Failed`→`Failed`｜`Removing`/`Removed`→`Removed`（删除中不得继续服务，且本就是合法清理目标）｜默认臂改为 **fail-safe：绝不报成 `Covered`**（`Covered` 是唯一「把 scope 藏起来」而非暴露它的投影）。
- 显式 `ScopeState` 仍优先（`p.ScopeState ?? …`）⇒ **不改写已声明的真实覆盖关系**。
- 新增独立测试 `Source/PuddingCodeIndexTests/Services/CodeIndex/CodeIndexScopeRegistryTests.cs`（**5 用例**，含 **2 条对照**：显式值优先、`Active`/`Failed` 不漂移）。

**门禁（父级自跑）**：`PuddingCodeIndexTests` **失败 0 / 通过 121 / 总计 121**（基线 116，**+5**）；`PuddingHost.Tests` **126/126**（无回归）；两工程 `exit 0`。
**变异取红（原始输出 `temp/test-out/m1a-mut-red.txt` / `-green.txt`）**：把整段投影回退为旧 `_ => Covered` ⇒ **恰好 3 条失败**（`Registering` 应 `Active` 实 `Covered`；`Unknown` 实 `Covered`；`Removing` 应 `Removed` 实 `Covered`），**2 条对照照常通过** ⇒ 测试**非空转、亦非互相绑定**；复原 ⇒ `MUT_green_EXIT=0`。

**附着后果（已核实，非推测）**：`Registering → Active` 会让根 scope 在**下次重启**被附着（`CodeIndexMaintenanceHostedService` 的 `State != Active ⇒ continue`）。父级用 `code_outline` 核实 `CodeIndexCalibrationService` **只有剪枝路径**（唯一变更方法 `RemoveBatchAsync`；结果字段全是 `AbsentFileCount`/`SweptFileCount`/`ProtectedFileCount`/`Truncated`，**无回填/重索引计数**）⇒ 附着**不会**触发仓库级全量索引，**不构成对 1GB 护栏的冲击**；收益是根 scope 自愈（重新进入校准与变更管线）。⚠️ **须在下次重启后实测确认**：触发一次真实 Core 重启并观察根 scope 是否进入校准/索引、库体积与 `-wal` 变化。
**零写**：本刀未改任何 DB 行；索引库未动。

---

## 2026-09-24 U3-G1 + 第 2 轮规划与审查：被中断的 scope 不再可被删除候选命中

**卡的是 D2+D3 数据风险**：取消分支不写任何状态 ⇒ 一次被取消的运行把该行**永久**留在 `Registering`；清理判据却把 `Status='Registering'` + `ScopeState IS NULL` + 超 24h 列为**可删候选** ⇒ 根 scope（携带 1,083 文件 / 34,848 符号 / 96,849 关系 / 143,587 引用行）**已命中**，仅靠 `AutomaticCleanupAllowed=false` 挡住 —— **一次人工清理即全删**。

**交付（R1 判据 + R2 留痕，宿主侧 2 文件 + 组件 1 文件）**：
- **R1** 删除判据摘掉 `'Registering'`：`Status IN ('Removed','Failed','Registering')` → `Status IN ('Removed','Failed')`，**宿主侧两处副本同步**（`StorageMaintenanceQueries.cs` 候选查询 + `StorageDerivedTargetHandlers.cs` 执行前重校验）；`'Covered'`/`'Removed'`/`'Failed'` 语义未动。
- **R2** 取消分支新增 `MarkInterruptedAsync`：**仅当该行确实处于 `Registering` 时才打标**（未认领即取消的运行不误标；`Active`/`Removed` scope 绝不被翻回 `Registering`），**保持 `Status='Registering'` 只写 `StatusMessage`**，**不新增枚举值、不改附着判据**；`try/catch` + `LogWarning` 不打断调度；显式 `CancellationToken.None`。

**门禁（父级独立复跑，不采信自述）**：`PuddingCodeIndexTests` **失败 0 / 通过 116 / 总计 116**（基线 114，**+2**）；`PuddingHost.Tests` **失败 0 / 通过 126 / 总计 126**（基线 124，**+2**）；两工程 `exit 0`。
**变异取红（三段原始输出 `temp/test-out/u3g1-mut-red.txt` / `-green.txt`）**：还原 `'Registering'` ⇒ **红**（正是 `Interrupted_Scope_Is_Not_A_Cleanup_Candidate` 失败：`Assert.Empty` 收到 `CodeIndexScopeCandidate{interrupted-scope, ArtifactRows=5}`）⇒ 测试非空转；复原 ⇒ **绿**。
**零写**：未改任何 DB 行；索引库 `code_index.db` mtime 仍 `2026-09-24 16:25:18 +08`、`-wal` 仍 `0 B`。

**第 2 轮规划与审查（codex gpt-5.6-sol，`exit_code=0`，543 行）**：6 处纠错（**「906,551,296 B 占 1GB ≈88%」作废** ⇒ 按 1GB 为 **90.66%**、按 1GiB 为 **84.43%**；「1GB 护栏」有 GB/GiB 歧义须固化为精确字节数）+ P0~P2 全部采纳（**最高价值 P0-1：D1/D2 根因是状态模型把 `Coverage`/`ServingState`/`RunState` 三个正交概念揉进一个字段**，`Registering` 绝不能映射为 `Covered`；P0-2 删除资格须 **tombstone 驱动**；P0-5 **scope/工程发现不得由查询触发承担正确性**）+ §E 8 条冲突裁定 + 修订路线 M0~M6。

**⚠️ 父级独立取证新发现（改写迁移方案）**：`CodeFiles` **distinct FilePath = 1,246 而 rows = 2,849** ⇒ **812 条路径跨 scope 重复**（样本同一文件在 4 个 scope 各存一份）；根 scope 1,083 行中 **706 与子 scope 重复**、**独立贡献上界仅 377 行**，且含**仓库外文件** `C:\Users\huany\.nuget\packages\microsoft.net.test.sdk\18.0.0\build\net8.0\Microsoft.NET.Test.Sdk.Program.cs` ⇒ **864.6 MiB 的主因是重复索引浪费而非内容量**；⇒ 采纳「**重扫构建新库，不做按 `ProjectId` 原样拆分**」；根 scope 处置由「E+A」改为「**E + 在新建项目库重建**」，**B 仍不推荐**。

> 结论与裁定固化：`Docs/Features/ADR-089-第2轮规划与审查-2026-09-24.md`（新）+ `ADR-089-U3F-根scope诊断-2026-09-24.md`（补遗 5 项修正）。
> ⚠️ **登记缺口（如实记账）**：`ADR-089-U3F-根scope诊断`、`ADR-089-U4-3b-向量规模化实测与裁决`、`ADR-089-U4-3c-就地int8扫描实测` 三刀已交付并推送（`59ed91fa` / `f4a86b29` 等），但**本变更日志此前未逐条登记**，待补。

---

## 2026-09-24 U3-E：校准重试指数退避 + 封顶（闭合 ADR-089「60s 轮询 **+ 退避**」）

**卡的是 ADR-089 明写却从未实现的那半句**：退避。一个根目录消失/不可读的 scope 会被置 `NeedsReconcile`，之后**每 60s 重试、永不退避**；U3-D 又把这条路径从「只被标位的 scope 才可能走到」扩到「**任何根消失的 scope 首次常规 sweep 后都会走到**」⇒ 一个坏 scope = **1440 次探测 + ≈2880 行 Error/天**。

**交付（生产仅 1 文件，153/10）**：新常量 `DefaultCalibrationBackoffMax = 30min`（`:90`，与 `DefaultPollInterval`/`DefaultCalibrationInterval`/`DefaultCalibrationPeriod` 并列，**不新增配置层**）；纯函数 `CalibrationBackoffInterval(long)`（`:1015`，`internal static`，**封顶在循环内以 `>` 夹取 + 循环出口双重保证 ⇒ 失败 10⁶ 次与 6 次同价、无溢出**）；`ScopeEntry` 纯新增 `ConsecutiveCalibrationFailures`（`:197`，与 `LastCalibrationAttemptAtUtc` 同 `_gate` 同位）；`IsCalibrationDue`（`:977`）**标位分支**拆为「从未尝试 ⇒ 立即（U3-C 逐字保留）」+「`now-lastAttempt >= CalibrationBackoffInterval(failures)`」，**else（15min 常规钟）一字未改**；`RegisterCalibrationFailure`（`:1035`）/ `ResetCalibrationBackoff`（`:1056`）；四条结局接线 = 异常 +1 档、被拒 +1 档、**截断复位**、**成功复位**（被拒/异常日志追加档位 + 下一次允许时刻）。

**阶梯（父级实读源码复核算术）**：`failures=1 → 60s ｜ 2 → 2m ｜ 3 → 4m ｜ 4 → 8m ｜ 5 → 16m ｜ ≥6 → 封顶 30m`；单次失败仍等 60s（U3-C 语义保留）。**尝试时刻** `0, 60, 180, 420, 900, 1860` 秒后每 1800s ⇒ **首日 1440 → 52（−96.4%）**、**首周 10080 → 340（−96.6%）**；Error 行 ≈2880 → ≈104/天。

**门禁（父级独立复跑）**：`PuddingCodeIndexTests` **114/114（失败 0，基线 107，+7）**；`slnx -c Release` **`BUILD_EXIT=0` + `error CS`/`error MSB` 行数 0 + 117 警告 / 0 错误**；`numstat Source/` = `153/10` + `22/2`（与自述逐条吻合）；三文件 `hash-object` = `422c166c…`/`8ccd7d57…`/`1823c28e…`（逐位相同）；`MUTATION` 残留 **0**；**`.csproj`/`.slnx`/Host/Runtime 零改动**；`Contracts/ICodeIndexer.cs` 有 `M` 但 numstat 零行（LF/CRLF artifact，未纳入）。**`ClearNeedsReconcile` 全组件仅 1 个调用点（`:940`）** ⇒ 「被标位的 scope 唯一出口是成功 sweep」成立。

**变异取红（原文）**：M1 去封顶 ⇒ **A2 红**（应 7 实 6；纯函数例应 00:30:00 实 00:32:00）；M2 系数改 1 ⇒ **A1 红**（*"attempt 3 must not happen before 00:02:00 have elapsed since attempt 2"*）；M3 成功后不复位 ⇒ **A3 红**（*"after a success the first retry is 60 s again"*）。复原全绿 114/114。

**必答三问**：① 阶梯/封顶如上；② **不叠加、不双扫**（判 due 二分支永不同时参与；反证：被标位 scope 跑完 31min 而 `CalibrationRunCount` 恰 = 6）；③ `LastCalibrationAtUtc` 被拒时**会盖章但不推迟常规钟**（标位时常规钟不被读取，解位唯一出口是成功 sweep）⇒ 常规钟实际始终锚在「上次成功」。

**裁决**：**接受 `Truncated → 复位`**（截断 = 根可读 + 真删了行，是进展而非失败；算失败会拖慢大仓收敛）—— 已登记为**可逆单点选择**。

**诚实留白**：① **根恢复检出延迟最坏 30min**（原 ≤60s）= 用检出延迟换 96% 探测量的**自觉取舍**；可用「变更管线再观测到该路径 / watcher 重附着」作复位信号压回 60s 量级，但**会改 U3-B3 管线语义**；② 档位**未暴露到 status/快照**（要暴露需单独下刀，会动 `Contracts/ICodeIndexMaintenance.cs`）；③ 探测量是「被拒次数」口径，未含「根可读但深层子目录不可读」；④ 未在真实 watcher / 真实 Roslyn 下端到端验证；⑤ `ListFilesAsync` 无分页仍缺。

**仪器教训（本刀新踩，已入规范）**：用 `... | Select-String ...` 过滤构建输出会得到**空结果**，而 `exit 0` 是**管道最后一条命令**的状态、**不代表构建成功** ⇒ 必须「输出收进变量 + 显式打印 `$LASTEXITCODE` + 统计 `error CS|error MSB` 行数 + 尾行做非 ASCII 掩码但保留数字」。

> 结论与关键数字固化：`Docs/Features/ADR-089-U3-E-校准重试退避实测-2026-09-24.md`；原始输出在 `temp/u3e-*.txt`（gitignore）。

## 2026-09-24 U3-D：按 scope 独立计时的常规校准周期（15 min）—— U3 唯一真功能缺口闭合

**卡的是 U3-C 留下的洞**：校准过去**只在 `NeedsReconcile` 时触发**，于是变更源长期静默的 scope（watcher 溢出 / 进程未运行期间的变更 / 附着失败后不再有事件）其陈旧索引行**永远清不掉**。

**交付**：每个 scope 一条**独立常规校准钟**（`DefaultCalibrationPeriod = 15 min`，`CodeIndexMaintenanceService.cs:78`），**不依赖 `NeedsReconcile`**；`CalibrateReconcileScopesAsync` → `CalibrateDueScopesAsync`（`:809`），到期判定抽为 `TryBeginCalibration`（`:897`，判 due + 盖章同在 `_gate` 内）+ 纯函数 `IsCalibrationDue`（`:914`，`:928` 即「锚 = 上次**完成**时刻，从未跑过则取挂载时刻」）。另新增 `ReconcileReasons.CalibrationTruncated`（`CodeIndexScopeState.cs` +6/−0）。`numstat`：`CodeIndexMaintenanceService.cs` **+118/−25** · `ICodeIndexMaintenance.cs` **1/1（仅 XML 注释）** · 组件 `code_map.md` +18/−4；测试新增 1 文件 398 行 9 用例 ⇒ **98 → 107**。

**必答四问（实测）**：① 实际间隔 = `15 min + 一次驱动步等待(≈200 ms) + 上次 sweep 耗时`，空载 **≈901.2 s**；被长批次挤占时**无硬上界**。② 静默 scope 从最后事件到陈旧行被清 **≈15 min 0.22 s**（仅对「根可读」成立）。③ 单 scope（350 文件）一次 sweep **19 ms、删除 2 条**，348 个在盘文件一行动不动；对照 **未到期的一步 = 0.03 ms** ⇒ 这就是「200 ms 步没变成扫盘」的直接量化。④ 每 scope **≈1.8 s CPU/天**；每 15 min 窗口占用 `N × 19 ms`（N=200 ⇒ 3.8 s = 0.42%）⇒ **判定可接受**；判据 `period ≥ N × t_sweep / 1%`。

**变异取红（原始日志在 `temp/test-out/`）**：M1（去掉时间门）⇒ **A2 红**（应为 0 实际 50）+ A3 红，复原 A2/A3 已通过；M2（到期不执行）⇒ **A3 红**；M3（全局共享钟）⇒ **仅 A3 红**（红灯指向精确）。并附 `SRC/DLL` 时间戳核对（DLL 晚于 SRC）防「跑变异 DLL」事故。

**门禁（父级独立复跑，非子代理自述）**：`PuddingCodeIndexTests` **107/107（失败 0）**；`PuddingAgentNetwork.slnx -c Release` **0 错误**；`MUTATION` 残留 **0**；**未改 Host / DI / csproj**（`git status` 对 Host/Runtime/Agent/CodeIntelligence/Tests = 0 条目）⇒ **零 Host / 零 DI 改动目标达成**（原因是组件**早已有** `TimeProvider? timeProvider = null` 构造参数）。

**⚠️ 终态是 `failed`，但交付物完整**（与 U4-1b 同型）：真因 `file_patch ... starting at old line 48 did not match`（工具失败 7 次）；**报告写在 17:09:09、死亡在 17:09:38**（报告先落盘，收尾再试一次 patch 才失败）。磁盘证据：改动齐全、组件 0 错误、测试 107/107、`MUTATION` 残留 0 ⇒ **不重跑，直接接手验收**。

**本刀对我任务书的两处校正（取证的价值）**：① 「驱动由 `CodeIndexMaintenanceHostedService` 以 200 ms 轮询」**不准** —— HostedService 不含循环/计时器（219 行只做 StartAsync + 挂载 + 有界 Stop），**200 ms 节拍在组件内部**（`RunLoop` + `Task.Delay(_pollInterval, _timeProvider)`）⇒ 本刀「零 Host 改动」是**结构上可能**而非运气；② 「校准被拒/被截断 ⇒ 保持置位」—— **被拒**成立，**截断**只在本就标位时成立（U3-C 的 `Truncated` 分支只告警 + `continue`）⇒ 这是我任务书里的一个**真缺口**，已补。

**诚实留白**：① **退避仍未实现** —— 常规路径下「根不可用」会置位并按既有 60 s 节流重试（坏 scope **1440 次探测 + Error 行/天**）；② `ListFilesAsync` 无分页（读侧无上限）；③ 根可读但深层子目录不可读仍识别不了；④ 未在真实 `FileSystemWatcher` / 真实 Roslyn 索引器下端到端验证（「变更源静默」用「不发布事件」模拟）；⑤ 豁免即延后（被豁免者最多再等 15 min）；⑥ 15 min 是组件**常量**（按 R4 不新增配置层）；⑦ 19 ms 不能外推到网络盘/超大库。

> 结论与关键数字固化：`Docs/Features/ADR-089-U3-D-常规校准周期实测-2026-09-24.md`；原始日志（18 个）在 `temp/test-out/u3d-*.txt`（gitignore）。

## 2026-09-24 U4-3c：就地 int8 扫描 + 有界 top-k（10,199 行 p95 45.152 → 14.068 ms）

**卡的是 U4-3b 实测出的「今天就不合格」**：shipped 扫描路径 p95 拐点仅 **5,284 行**，而三个真实 scope 的 P0 就是 6,599 / 7,551 / **10,199** 行 ⇒ Runtime 59.2 ms、Core 72.3 ms（扫描预算 20.207 ms）。慢的三处根因（实读 `InMemoryVectorIndex.cs:110-131`）：① 每行构造一个 `VectorSearchResult`（O(N) 分配）；② 全量 `Sort` 只为取 top-k；③ 量化行走 `Dequantize()` ⇒ **每行分配 `float[1024]`（4 KB）**。

**交付 1：叶子组件新增 `Source/PuddingVectorIndex/QuantizedInMemoryVectorIndex.cs`（282 行）** —— 持有 `IReadOnlyList<QuantizedVectorEntry>`，**codes 原样、不预先反量化**；未给既有 `InMemoryVectorIndex` 加开关（与该类型自身「separate type rather than a flag on it」的立场一致）。扫描核心：`var component = codes[i] * scale;`（**先在 float 里乘**）`dot += (double)component * query[i];`；范数在 `Add` 时按 `VectorMath.Norm` **同序**预计算一次 ⇒ 分数仍**逐位相同**。有界 top-k = 大小 `min(topK, Count)` 的**最大堆**，比较器逐字复刻 shipped（分数降序 → 同分 `CompareOrdinal(Id)`），出堆即 rank 逆序 ⇒ **不全量排序、不物化全量**。

**交付 2：测试（`PuddingVectorIndexTests` 51 → 57 例）** 与**探针**（`VectorScanBench.cs` 新增；`VectorStore.cs` **+74/−0** 增 `ReadInt8QuantizedEntries`；`Program.cs` **+4/−1** 增 `--mode scan`，既有 mode 行为不变）。

**核心数字**（`temp/U4-3c-logs/bench-scan.txt`，dim=1024 topK=20 n=100 热态，**仅扫描不含嵌入**）

| 行数 | shipped p95 | 就地 p95 | 比 |
|---|---|---|---|
| 475（纯真实行） | 2.329 | 0.624 | 3.73× |
| 4,096 | 18.773 | 5.761 | 3.26× |
| **10,199** | **45.152** | **14.068** | **3.21×** |
| 20,000 | 95.568 | 27.390 | 3.49× |
| 65,536 | 300.382 | 89.930 | 3.34× |

⇒ **必答问题：Core P0 规模（10,199 行）热态 p95 = 14.068 ms ≤ 20.207 ms 预算 ⇒ 达标（用掉 69.6%）**。单线程拐点 ≈ **14.7k 行**（两点**插值**，非实测点）；全仓 P0 45,677 行 ≫ 拐点 ⇒ **全仓仍需并行或 ANN**。

**分配量（R5，不靠自述）**：N=4,096、k=20 ⇒ shipped **296,464 B/query** vs 就地 **1,752 B/query（0.59%）**；**N 翻倍到 8,192 仍为 1,752 B**（「不随 N 增长」比「比值」更硬）。

**等价性**：5 规模 × 100 查询 × 3 路径，top-20 id 序列 **`mismatch = 0`**、每 rank **最大 |score 差| = 0（恰为 0，非 < ε）**。

**变异取红（三元组齐备 + 复原逐位相同；库 hash `4ab28449…`）**：M1（少乘 scale）⇒ A2 红 `maxDelta=28.84`；**M1B（写成 `(double)codes[i]*(double)scale`，即「第二种相似度定义」）⇒ A2 红 `maxDelta=2.90E-09`，而 id 序列当时并未改变 —— 只有「分数差恰为 0」这条断言抓得住它**（本刀最有价值的证据：把「一条相似度定义」从注释变成断言）；M2（去掉 Id 序数打破）⇒ A3 红；M3（改回物化全量 + 全量排序）⇒ A4 红（分配回到 296,400 B/query）。**仪器自身取红**：只变异生产代码 ⇒ 探针 `mismatch = 20/20`、`maxΔscore = 1264.83`，排除「比较器恒报 0」的假绿。

**门禁（父级独立复跑，非子代理自述）**：`PuddingAgentNetwork.slnx -c Release` **0 错误**（失败工程数 0）；`PuddingVectorIndexTests` **57/57**；`numstat` 与自述逐条吻合；`MUTATION` 残留 **0**；`PuddingVectorIndex.csproj` 两个引用元素 **0 个**。

**顺带量化的设计决策**：同一有界插入扫描只把范数改成扫描内联 ⇒ 10,199 行 p95 **14.227 → 23.377 ms（+64%）**；堆 vs 有界插入仅差 ~1% ⇒ 预计算范数是**性能必需，不是提前优化**。

**诚实留白**：① **未接入生产**，不主张任何端到端检索收益（`InMemoryVectorIndex` 仍是主链）；② 20.207 ms 是扫描预算，与嵌入 p95 29.793 相加得端到端 ≈43.9 ms 属**算术相加、未实测**；③ 10,199 是**真实规模**（Core P0 实测），但 ≥475 的向量是「真实行 + 扰动再量化」，真实 10,199 行嵌入需跑服务落盘；④ 范数预计算占 O(N) 个 double（10,199 行 82 KB、65,536 行 524 KB），**未计入 1 GB 磁盘体积预算**（口径不同）；⑤ p99 基于 n=100 nearest-rank（≈最大值）；⑥ **一次真实仪器事故**：首次「复原」用 `copy /y` 保留旧时间戳 ⇒ MSBuild 判定无需重编译、**测试跑的是变异 DLL**，靠「绿态输出与红态逐字节相同」才发现 ⇒ **DLL 时间戳核对已成为变异流程的固定步骤**。

> 结论与关键数字固化：`Docs/Features/ADR-089-U4-3c-就地int8扫描实测-2026-09-24.md`；原始日志（24 个）在 `temp/U4-3c-logs/`（gitignore）。

## 2026-09-24 U4-4：统一路径忽略合同（单一真源 + gitignore 语义 + 仓库根语料 −84.5%）

**卡的是用户第 4 条指令**：「忽略制成品、node_modules、读取 git 忽略文件的规则进行忽略」。审计证据显示**过去从未真正生效**且**规则分叉**：`IndexExcludePatterns` 内的 gitignore 解析**无任何外部调用点**（本工程内已证），其实现注释**自述跳过 `!` 取反**；同时全仓**七套互不相同**的排除表（`search_grep` 12 项 / 索引噪声目录 24 项 / `FullTextIndexOptions` 33 项 / `CodeIndexer.Cli` / `file_search` / `list_dir` / `project_map` 各自），且**第一大噪声源 `.pudding`（21,106 文件）在所有表里都缺席**（只有拼写相近的 `.pudding-code`）。

**交付 1：新叶子组件 `Source/PuddingPathFiltering/`（S1）** —— 10 个 `.cs` + csproj + code_map。`ProjectReference = 0`、`PackageReference = 0`（**gitignore 引擎自实现，不引 NuGet**：验收判据是**与真实 git 逐位一致**，引包同样需要这份 oracle，却额外引入供应链与离线还原面）。成员：`GitWildcard`（`*`/`**`/`?`/字符类/尾斜杠目录语义）、`IgnoreRule`、`IgnoreFileParser`、`IgnoreStack`（**父目录排除下的取反恢复**，这是旧实现跳过的那一半语义）、`IgnoreFileLoader`、`WorkspacePathFilter`、`PathNoiseRules`（**噪声目录单一真源**）、`PathText`。边界由 `ComponentBoundaryTests` **编译期+读盘**双重强制。

**交付 2：新测试工程 `Source/PuddingPathFilteringTests/`（S2/S3）** —— 17 文件、**84 用例**，含 `GitIgnoreOracleTests`（oracle）、`RepositoryRootReductionTests`（−84% 判据）、`ComponentBoundaryTests`（边界）、`IgnoreStackTests`（取反语义）。

**交付 3：消费侧收敛（19 个既有文件 + 5 个 csproj）** —— `IndexExcludePatterns.cs`（**+50/−159 大幅瘦身**）、`FullTextIndexOptions.cs`（+14/−49）、`SearchGrepTool`/`FileTools`/`ProjectMapTool`（默认排除目录改由 `PathNoiseRules.DirectoryNames` 派生）、`RoslynCSharpIndexer`/`PythonIndexer`/`TypeScriptIndexer`、`CodeIndexWatcher`、`DefaultCodeWorkspaceResolver`、`DefaultProjectRootDetector`、`GoalCheckInputIdentity`（**删掉 8 项私有副本**改用单一真源）、`CodeIndexer.Cli`；`PuddingCodeIndex`/`PuddingRuntime`/`PuddingPlatform`/`PuddingFullTextIndex`/`CodeIndexer.Cli` csproj 各增 1 条 `ProjectReference`。

**核心判据（实测，U4-0 `--mode count` 口径）**：仓库根可索引 **28,519 → 4,413（−84.5%）**、**743.6 MB → 80.7 MB**；`.pudding` / `.tmp-build` / `.pnpm-store` / `.tmp-test-out` 在顶层目录清单中**完全消失**，剩余为 `Source 3391 / Docs 684 / Tests 89 / …`；`visitedFiles = 4,902,722`、`excludedByOpts = 4,377,910`。

**oracle（D3 核心验收）**：两份语料共 **192 条路径**与真实 `git check-ignore` 逐条对照，每条**双重断言**——① 结论（ignore/keep）一致；② **「定案规则的原文」与 `git check-ignore -v` 打印的 pattern 逐字一致**（只比结论会让「恰好结论相同但原因不同」漏网）。要求**零不一致**（比任务书的 ≥95% 更严）。

**变异取红（两轮，各先红后绿）**：R1 打掉取反语义 ⇒ **失败 9 / 通过 71 / 总 80**；复原 **80/80**。R2 ⇒ **失败 4 / 通过 80 / 总 84**；复原 **84/84**。

**门禁（父级独立复跑，非子代理自述）**：`dotnet build PuddingAgentNetwork.slnx -c Release` = **exit 0 / 0 个错误**；`PuddingPathFilteringTests` **84/84**、`PuddingCodeIndexTests` **98/98**、`PuddingCodeIntelligenceTests` **93/93**、`Tests/PuddingHost.Tests` **124/124**。

**检索侧影响（同一 58 例标注集，Source scope）**：`noiseRate@10` **0.0290 → 0.0000**；`recall@1` **0.4224 持平**；`recall@5` **0.6638 → 0.6207（小幅下降）**、`precision@5` 0.1621 → 0.1517。**该对比不是受控 A/B**：两次运行之间语料本身已变（U4-1b/U4-2a/U4-3 各增文件），故下降**不能单独归因于忽略规则**。

**诚实留白**：① oracle 为**冻结快照**（`.gitignore` 文本 + `git check-ignore -v` 输出均落盘为夹具），测试期**不实时调用 git**；② `RepositoryRootReductionTests` 是**冻结基线的覆盖断言**，非实时重测（实时重测需扫 490 万文件、约 212 s，不适合进单测）；③ `Source/` 的召回小幅下降未定位到具体 case；④ 未做：`.gitignore` 的 `[a-z]` 字符类全谱、`.git/info/exclude`、global excludes、大小写敏感性平台差异。

> **本文档缺记的切片**：U4-2a（检索意图/结果/过滤合同）、U4-3（int8 量化 + 分层向量化）、U4-3b（向量规模化实测与裁决）的结论分别见 `Docs/Features/ADR-089-结构化地图检索设计-2026-09-24.md` 与 `Docs/Features/ADR-089-U4-3b-向量规模化实测与裁决-2026-09-24.md`；本条为 U4-4 补记，并登记该缺口。

## 2026-09-24 U4-1b：向量 / 全文 / 混合（RRF）同台对比（本地 Qwen3，零成本）

**卡的是用户第 5 条指令后半段**：“评估不同索引模型的质量（**纯向量、全文检索、混合检索**）的检索质量、索引速度、查找速度、索引文件体积差异”。**同一批块（引擎换、块不换）**：三种检索器都走 `--chunks outline --tiers p0p1p2 --filter both` ⇒ 同一份 **1,352 块**；目录 = `Source/PuddingCodeIndex`（37 个 `.cs` / 237,586 B），标注集 = `eval/sets/small-puddingcodeindex.json`（**28 条，未改动**）。

**交付 1：新叶子组件 `Source/PuddingVectorIndex/`（S1）** —— `ProjectReference = 0`、`PackageReference = 0`（**不引入向量数据库、不新增 NuGet**）。关键设计是**端口 `IEmbeddingProvider`**（`Describe()` / `EmbedAsync` / `EmbedBatchAsync` 保序）+ **值对象 `EmbeddingRoute`**（用户口径的 `"服务商/模型"`，按第一个斜杠切分 ⇒ modelId 可含斜杠）+ `EmbeddingModelInfo`（维度 / 最大上下文 / 是否本地 / **配置级**可用性）+ `VectorIndexEntry`（构造即**拷贝**向量）+ `VectorSearchResult`（含 `Score` 与 0 基 `Rank`）+ **`InMemoryVectorIndex`**（暴力余弦；分数降序、**同分按 id 序数**打破平局 ⇒ 两次检索逐位相同）+ `VectorIndexBuilder`（`BatchSize` 可控；**逐批校验数量/长度/非空**）。组件**不解析 route、不读资源池配置、不持 apiKey/baseUrl、不直连 HTTP**；**边界被编译期硬化**：csproj 里 `<Using Remove="System.Net.Http" />`（否则隐式 using 会把 HTTP 命名空间放进每个文件——这是实测发现的真实缺口）。

**交付 2：独立测试工程 `Source/PuddingVectorIndexTests/`（S2/S3）** —— `ProjectReference` 恰好 1 条，**39 用例**，含 S4 边界断言（检测器**自带阳性对照** + `deps.json` 依赖闭包 + **读盘校验组件 csproj 的 `ProjectReference`/`PackageReference` 必须为 0** + **源码不得出现 `HttpClient`/`http://`/`apiKey`/`baseUrl`**）。

**交付 3：探针扩展（适配器全在探针侧；默认行为不变）** —— `Source/PuddingRetrievalEvalProbe` 新增 `--retriever vector|fulltext|hybrid`（默认 `fulltext` ⇒ U4-1a 行为不变）、`--embedding-route`（**默认从 `llm.providers.json` 的 `embedding` 段解析，未硬编码**）、`--providers-config`、`--embedding-base-url`、`--embedding-dimensions`、`--embedding-batch`（默认 32）、`--rrf-k` / `--rrf-weight-fulltext` / `--rrf-weight-vector` / `--fusion-depth`。新增文件：`EmbeddingRouteResolver`（route → 端点/密钥/维度/价格，**未知/禁用/非 embedding 模型全部 fail-closed**）、`EmbeddingProviderAdapter`（OpenAI 兼容 `/v1/embeddings`；**调用次数/文本数/耗时/失败数/token 自计**）、`VectorStore`（`vectors.f32` 原始 float32 + `manifest.json`，供“索引体积”轴度量）、`VectorSearchProbe`（查询嵌入**故意不缓存**）、`RrfHybridProbe`（**文件级** RRF 融合）、`VectorRetrievalModes` + `VectorIndexRunReport`/`ProbeRunSidecar`（机器可读四轴）。**取舍理由**：生产实现 `OpenAiEmbeddingService` 在 `PuddingRuntime`，引用它会把整个 agent 运行时拖进探针 ⇒ 在探针侧写薄适配器（约 190 行，协议与批量语义对齐）。**未改** `PuddingCodeIndex` / `PuddingCodeIntelligence` / `PuddingFullTextIndex` / `PuddingIndexChunking` / `PuddingRetrievalEval` / `eval/sets/**` / Host / Runtime（`git diff --numstat` 对探针既有文件 = **184 插入 / 5 删除**，5 处删除分别是分支守卫、错误文案、探针构造、reproduce 串、一个访问修饰符；`.csproj` = **4 插入 / 0 删除**）。

**交付 4：四轴数字**（`--warmup 1 --measured 3`；28 例 `failed=0`、`repetitionStable=True`）

| 轴 | **F 全文**（Lucene outline） | **V 纯向量**（本地 Qwen3 + 暴力余弦） | **H 混合**（RRF k=60 wf=wv=1 depth=100） |
|---|---|---|---|
块数 | 1,352 | 1,352 | 1,352 |
**索引速度** | **2,060 ms** = 语料 367 + Lucene 1,693 | **41,624 ms** = 语料 347 + **embedding 41,187（43 调用 / 1,352 文本）** + 存储 36 | **41,253 ms** = 语料 426 + **embedding 39,276（43 调用）** + Lucene 1,471 + 存储 35 |
**索引体积** | **149,556 B**（0.143 MB，5 文件） | **5,918,577 B**（5.644 MB，2 文件；向量本体 5,537,792 = **39.6×**） | **6,068,133 B**（5.787 MB，7 文件 = **40.6×**） |
**热查找** p50/p95/p99（ms） | **3.545 / 6.311 / 8.088** | **41.236 / 56.813 / 61.826** | **48.827 / 64.484 / 67.661** |
`recall@1` | 0.7500 | **0.3571** | **0.7500** |
`recall@5` | 0.8214 | 0.8929 | **0.9286** |
`recall@10` | 0.9286 | **1.0000** | **1.0000** |
`MRR` | 0.7941 | 0.5810 | **0.8295** |
`precision@5 / @10` | 0.1643 / 0.0929 | 0.1786 / 0.1000 | **0.3000 / 0.1750** |
`noiseRate@10` | 0.0000 | 0.0000 | 0.0000 |

**交付 5：结论（三条必答）**

① **混合（RRF）优于任一单一策略**：vs 全文 `recall@5` 0.8214 → **0.9286（+13.1%）**、`MRR` 0.7941 → **0.8295（+4.5%）**、`precision@5` 0.1643 → **0.3000（+82.6%）**、`precision@10` 0.0929 → **0.1750（+88.4%）**、`recall@10` 0.9286 → **1.0000**、`recall@1` **0.7500 持平**；vs 向量 `recall@1` 0.3571 → **0.7500（+110%）**、`MRR` 0.5810 → **0.8295（+42.8%）**、`recall@5` 0.8929 → **0.9286**、其余并列。代价：索引 **20.0×**、体积 **40.6×**、热 p50 **13.8×**。机制可解释（case 0：全文第 1 + 向量第 6 ⇒ RRF 后回到第 1）。
② **向量只作为融合伙伴值得，单独用不值得**：单独 `recall@1` 只有全文的 **47.6%**（0.3571 vs 0.7500）、`MRR` **−26.8%**，但给融合带来 `recall@5` **+0.1071**、`MRR` **+0.0354**、`precision@5` **+0.1357**；体积 **4,378 B/块 vs 110.6 B/块（39.6×）**、热 p50 **11.6×**；**唯一完胜的轴是成本（本地 $0）** ⇒ 结论**依赖 route 是本地零成本，不可外推**。
③ **本地向量与全文的延迟差 = 热 p50 11.63×（+37.69 ms）**：3.545 → 41.236 ms；拆项后 **≈27.3 ms/次是查询嵌入（占 66%）**，其余 ≈13.9 ms 是 1352×1024 余弦扫描。**并更正一处已知输入**：任务书中的“本地单条 393 ms”是**冷/首调用**数字，本刀实测冷首调 370 ms、热态平均 **27.3 ms/次**，两者不可混用。

**消融（参数写进报告 + 三次对照）**：`k=10` vs `k=60` ⇒ 指标逐项相同且 top-5/top-10 窗口 **28/28 逐位相同**（k 在本语料规模不敏感）；`wVector=2.0` ⇒ 指标**整组塌回纯向量**（权重是敏感旋钮）；`fusionDepth=20` vs `100` ⇒ 指标逐项相同（**混合增益不是更深候选池造成的**，同时消除“单策略深度 20 / 混合深度 100”的实验不对称疑虑）。

**花费 = $0.000000**：全部 ≈650 次 HTTP 往返都指向 `http://127.0.0.1:1234/v1/embeddings`，**无任何远程调用**；route 价格 0/1M。**诚实提醒**：LM Studio 的 `usage.prompt_tokens` 恒为 0 ⇒ 本地路径拿不到真实 token 数，换远程 route 时必须按服务真实 token 计费（当前服务不提供该字段）。

**门禁（实测）**：新套件 **39/39 exit 0**；`PuddingIndexChunkingTests` **30/30**、`PuddingCodeIndexTests` **82/82**、`PuddingCodeIntelligenceTests` **93/93**、`PuddingRetrievalEvalTests` **78/78**、`Tests/PuddingHost.Tests` **124/124**（失败数未增加）；`dotnet build PuddingAgentNetwork.slnx -c Release` ⇒ **0 个错误 / 1588 个警告 / exit 0 / 失败工程数 0**（slnx 工程条目 44、构建输出行 45；两个新工程已登记进 slnx）。

**变异取红（两处，三份原始输出 + hash 三点值）**：M1 `Cosine` 去分母 + M2 构建器把“维度不符即抛”改成**静默截断** ⇒ **4 红 / 35 绿 / 39**（`Provider_Returning_Wrong_Vector_Length_Fails_Closed`、`Cosine_Of_Identical_Direction_Is_One_And_Opposite_Is_Minus_One`、`Cosine_Matches_Hand_Computed_Value`、`Cosine_Is_Scale_Invariant`）；复原后 `VectorMath.cs`（`b1f75cef2a81e11113e3998a4b7ef3f7837a2b6f`）与 `VectorIndexBuilder.cs`（`b36e2f940882b24b3f04ad3e8166256469449523`）blob hash **逐位相同**，复原后 **39/39 绿**，`MUTATION` 残留 **0**。附带发现：去掉分母是**单调变换**，`Search_Ranks_By_Cosine_Descending` 在 M1 下**仍绿** ⇒ **排序测试不能替代数值断言**。

**诚实留白**：① **小目录边界**（37 文件 / 1,352 块）——**不外推到全仓**（按 30.5 ms/块外推，全仓向量索引在时间上不可接受）；② 每个数字**只跑一次**（两次向量构建的 embedding 耗时差 **1,911 ms / 4.9%** **超过** Lucene 写入耗时 1,471 ms ⇒ “混合索引比纯向量快”**不构成证据**）；③ **标注集粒度太粗**（28 例、每例 1 个期望文件）⇒ 分不出 `wv=2` 与纯向量（top-10 窗口 25/28 不同）⇒ **指标相同 ≠ 排序相同**；④ 查询嵌入**故意不缓存**（缓存会藏起主项、缩小向量与全文的延迟差）；⑤ 暴力余弦**未引 ANN**，规模上界未测；⑥ 向量存储**未量化**（int8 理论 ~1,024 B/块，未实测）；⑦ **融合结果路径拼写不统一**（Lucene 绝对 / 向量相对）——指标不受影响，但用户可见列表不一致（探针侧呈现层小缺陷，未修）；⑧ 未测：并发/多进程、增量、跨语言分层、BFS/DFS、LLM 重排、远程 embedding 真实费用。

**本刀未提交**（约束：不 git add/commit/push）；完整证据 `temp/U4-1b-REPORT.md`，受版本控制结论 `Docs/Features/ADR-089-索引策略优先级-2026-09-24.md` §6。

## 2026-09-24 U4-1a：小目录索引策略实验台（plain 全文 vs outline 优先分块 + 过滤）

**卡的是用户第 1、2、5 条指令**：“避免把全部代码打包做索引 / 优先 code outline / 短 token 与关键字不入索引 / 先在小目录评估”。全仓实测（`c8fe4bd5`）已证明现状不可用（建索引 71.4 min、体积 838 MB、`recall@5` 从 0.66 掉到 0.41），本刀把“分层 + 过滤”验证到**可复现数字**。

**交付 1：新叶子组件 `Source/PuddingIndexChunking/`（S1）** —— `ProjectReference = 0`、`PackageReference = 0`。关键设计是**端口 `IOutlineSource`**：outline 由外部适配器喂入 ⇒ 组件**不引用** `PuddingCodeIntelligence`（语言 outliner 所在层）也**不引用** `PuddingRuntime`/`Host`/`Agent`/`PuddingCodeIndex`/`PuddingRetrievalEval`，可只用替身 outline 全量测。含：`ChunkKind{Outline=0,DocComment=1,CodeText=2}`（**枚举值即优先级** = C1 的 P0/P1/P2）与 `ChunkPriorities.BoostOf`（2.0/1.5/1.0，**优先级语义在组件，加权数值由检索侧消费**）、`IndexChunk`（构造即校验）、`ChunkingOptions`（三层独立开关 + 切段上限 + 是否把文档摘要并入 P0）、`ChunkFilterRules`（按语言最小 token 长度：C#/TS/Py=3、md/plain=2；按语言关键字停用词，**Markdown 为空集** ⇒ 语言差异可观察）、`ChunkFilter`（规则开关、大小写开关、阈值覆盖、`ApplyDetailed` 带**逐规则归因**，固定“先长度后关键字”）、`FileChunkAssembler`（**按符号切 P0 块**、绝不包含函数体；整行注释 → P1；剩余代码行 → P2）。两个不变量被测试钉死：**同层不重叠**、**声明行不复用**。

**交付 2：独立测试工程 `Source/PuddingIndexChunkingTests/`（S2/S3）** —— `ProjectReference` 恰好 1 条，**30 用例**，含 S4 边界断言（检测器自带**阳性对照** + `deps.json` 依赖闭包 + **读盘校验组件 csproj 的 `ProjectReference`/`PackageReference` 必须为 0**）。

**交付 3：受版本控制的标注集 `Source/PuddingRetrievalEval/eval/sets/small-puddingcodeindex.json`** —— **28 条**（symbol 20 + intent 8，均 C#）；期望命中**逐个独立核对**（文件存在 + 该文件确实声明该符号）⇒ `suspect = 0`（脚本 `temp/verify-u4-1a-annotations.ps1`；**核对发现并修掉了一个真错误**：最初把“文件名 `CodeSymbolContracts`”当成了符号）。

**交付 4：探针扩展 + 只增不改的索引入口** —— `Source/PuddingRetrievalEvalProbe` 新增 `--chunks plain|outline`（默认 `plain`，既有基线可复现）、`--tiers p0p1p2|p0p1|p0`、`--filter both|none|length|stopwords`、`--patterns`、`--index-json`；**`RoslynCSharpOutlineSource` = `IOutlineSource` 适配器**（经 `PuddingCodeIntelligence` 传递 Roslyn，**不新增 NuGet**，outline 语义对齐生产 `OutlineSyntaxVisitor`；该文件是本刀唯一新增的重依赖点，且它在组件**之外**）。`Source/PuddingFullTextIndex` **只增不改**：新增 `Contracts/IndexChunkDocument.cs` + `LuceneSearchEngine.BuildChunkIndexAsync`（按预分块文档全量建索引，boost 由调用方按优先级给定；**既有 `BuildIndexAsync` 一行未改** —— `git diff --numstat` = **139 insertions / 0 deletions**），写入沿用同一索引布局 ⇒ `SearchAsync`/`HasIndex`/`RemoveIndex` 原样复用。

**交付 5：四轴数字与结论**（scope = `Source/PuddingCodeIndex`：37 个 `.cs` / 237,586 B；每变体从**空索引根**开始；六变体 `repetitionStable=True`、失败用例 0；两策略语料同为 37 文件 / 237,586 B）：

| 轴 | **plain（现状）** | **outline（P0+P1+P2 + 双规则）** | **outline-p0p1（只 P0+P1）** |
|---|---|---|---|
索引文档数 | 引擎不报告（每非空行一条） | 1,352（Outline 475 / Doc 160 / Code 717） | 635（475 / 160 / 0）|
**索引体积** | **283,159 B** | **149,556 B（−47.2%）** | **89,769 B（−68.3%）** |
索引耗时（harness） | **1,987 ms** | 2,037 ms（语料 340 + 写入 ≈1,697） | 2,261 ms |
`recall@1/@5/@10` | 0.7500 / 0.8214 / 0.9286 | 0.7500 / 0.8214 / 0.9286 | 0.7500 / 0.8214 / 0.9286 |
`MRR` | 0.7885 | **0.7941** | **0.7941** |
`precision@5 / @10` | 0.1643 / 0.0929 | 0.1643 / 0.0929 | 0.1643 / 0.0929 |
`noiseRate@10` | 0.0000 | 0.0000 | 0.0000 |
热 `p50/p95/p99`（ms） | 3.702 / 5.984 / 8.510 | 3.871 / 6.130 / 7.751 | 4.454 / 7.286 / 9.650 |

**过滤规则各自贡献**（同 strategy，只切 `--filter`；基准 `none` = 173,627 B）：关键字规则单独 **−10.5%**（155,430 B）、长度规则单独 **−4.7%**（165,519 B）、两条同开 **−13.9%**（149,556 B）；质量上两条同开使 `recall@10` 0.8929 → **0.9286**，长度规则单独开使 `recall@5` 0.8214 → **0.8571**、`precision@5` 0.1643 → **0.1714**，四个变体 `recall@1` 均 0.7500。语料 21,533 token：长度规则删 2,172、关键字规则删 3,741。

**结论**：① **outline 用 −47.2% 的索引体积换来不差（且 `MRR` 略好）的质量**，代价是索引耗时 +2.5%、热 `p50` +4.6%（热 `p99` −8.9%）；② **只索引 P0+P1 最划算**（plain 的 31.7%、outline 的 60.0%），而七项质量指标与 outline 逐项相同 ⇒ 本语料上 P2 代码正文未贡献召回；③ 两条过滤规则都有效且都不伤质量。正式结论已追加到 `Docs/Features/ADR-089-索引策略优先级-2026-09-24.md` §5（含 §5.5 诚实留白）。

**门禁（实测）**：新测试工程 **30/30 exit 0**（0 警告除项目级 MSTEST0001）；`PuddingCodeIndexTests` **82/82**、`PuddingCodeIntelligenceTests` **93/93**、`PuddingRetrievalEvalTests` **78/78**、`Tests/PuddingHost.Tests` **124/124**（与既有基线逐项相同，**失败数未增加**）；`dotnet build PuddingAgentNetwork.slnx -c Release` ⇒ **0 个错误 / 1581 个警告 / exit 0 / 失败工程数 0**（两新工程已登记进 slnx，**+2 行 / 0 删除**）。组件边界：`ProjectReference=0`、`PackageReference=0`；组件源码对 7 个禁用程序集与 `Microsoft.CodeAnalysis.*` 的 **`using` 引用 0 处**，**阳性对照**：同一检索式在 `Source/PuddingRetrievalEvalProbe` 命中 12 处（含 `Microsoft.CodeAnalysis`）⇒ 零命中是真实否定。

**变异取红（三份原始输出 + hash 三点值）**：`IsStopWord` 恒 false ⇒ **5 红 / 25 绿 / 30**；`IsShortToken` 恒 false（阈值恒 0）⇒ **7 红 / 23 绿 / 30**；两次复原后 `Source/PuddingIndexChunking/ChunkFilter.cs` 的 blob hash **逐位相同**（`290ba4794ca83d3c2a2493d3105b8b55a2efe6dc`），复原后 **30/30 绿**，`MUTATION` 残留 **0**。原始日志：`temp/U4-1a-logs/mutation-A.txt`、`mutation-B.txt`、`restored-green.txt`。

**诚实留白**：① **未接入 Host/DI**（把分块+过滤固化进生产索引流程属 §3 的 **U4-3**）；② 只在**一个小目录（37 个 C# 文件 / 237 KB）**上、**各跑一次**（无方差；`repetitionStable` 只说明同一 run 内一致）；③ 冷样本是“本进程首次调用”，不是跨进程/OS 页缓存冷启动；④ “P2 无贡献”只在这批标注上成立（期望命中均为“定义符号的那个文件”，符号名已被 P0 覆盖），**不外推**；⑤ 未测：并发/多进程、增量路径、向量与混合（U4-1b）、BFS/DFS（C3）、统一忽略合同（U4-2）；⑥ `plain` 的文档数引擎不报告 ⇒ 留空不填估算值；⑦ 风险：`Source/PuddingCodeIndex/Contracts/ICodeIndexer.cs` 在本刀期间被**外部工作流并发修改**，结论绑定的是测量那一刻的目录内容（37 文件 / 237,586 B 已记录）。

**本刀未提交**（约束：不 git add/commit/push）；改动文件与逐条证据见 `temp/U4-1a-REPORT.md`。

## 2026-09-24 U4-0：检索评测设施（性能 + 准确率仪器 + 首份真实基线）

**为什么它必须最先做**：ADR-089 §4 要求"评估性能和准确率"，但**没有基线就无法证明后续每一步变好了**——U4-1（统一忽略）/ U4-2（作用域·类型）/ U4-4（向量）/ U4-5（并行）的收益全部由本刀的指标判定。⇒ 本刀只产出**测量仪器 + 基线**，**零检索行为改动**。

**交付 1：新叶子组件 `Source/PuddingRetrievalEval/`（S1）** —— `ProjectReference = 0`、`PackageReference = 0`（未新增 NuGet）。关键设计是**只依赖端口** `ISearchProbe`（query + scope → 命中列表 + 由探针自测的耗时）：评测因此成为叶子，换引擎不改评测。含标注集模型（kind∈symbol/intent/crossref、language∈CSharp/TypeScript/Markdown、`Unknown=0`）、指标（`recall@k` k=1/5/10、`MRR`、`precision@k`、**噪声率** = 命中落在噪声目录的比例 = U4-1 的收益度量）、最近秩百分位延迟统计（冷/热分离、p50/p95/p99、**只报原始数字不内置阈值**）、fail-closed 的 JSON 标注集加载器、Markdown+JSON 报告 writer。**噪声目录定义为现有三套规则的并集**（`SearchGrepTool.DefaultExcludeDirs` 12 ∪ `IndexExcludePatterns.NoiseDirNames` 28 ∪ `FullTextIndexOptions.ExcludedDirectoryNames` 33 = 46 个去重段名）——取并集是保守方向，避免仪器替 U4-1 预先裁决。

**交付 2：独立测试工程 `Source/PuddingRetrievalEvalTests/`（S2/S3）** —— `ProjectReference` **恰好 1 条**（只指向本组件），**78 用例**：指标正确性（"期望命中第 1 位 ⇒ MRR=1.0 / 全落空 ⇒ 各指标 0 / 全落 `node_modules` ⇒ 噪声率 1.0"）、边界（k>命中数、空用例集、重复期望、同文件多行命中、大小写/分隔符/相对-绝对路径）、加载器 fail-closed 反例、冷/热调用调度、报告契约（显式不设阈值、UTF-8 无 BOM）、S4 边界断言（进程内 + `deps.json` 依赖闭包 + **检测器自带阳性对照**）。

**交付 3：受版本控制的标注集 `Source/PuddingRetrievalEval/eval/sets/seed-v1.json`** —— 80 条 / **98** 个 (query, 期望文件) 对，语言分层 C# 36、TS-TSX 22、md 22（各 ≥15），kind 分层 symbol 36 / intent 26 / crossref 18。**禁止"用检索结果反过来当标注"**：98 个期望文件对全部由独立验证脚本 `temp/U4-0-probe/verify-set.ps1` 逐个核对（文件存在 + 该文件确实含查询字面量，或 intent 类的人工指定锚点 token）⇒ `caseCount=80 / pairCount=98 / missingFileCount=0 / unverifiedAnchorCount=0`（`temp/U4-0-probe/verify-set.txt`）。

**交付 4：首份真实检索面基线** —— 面 = **Lucene 全文索引**（`Source/PuddingFullTextIndex/Infrastructure/Search/LuceneSearchEngine.cs`），它正是 `search_grep` 的快速候选路径（`SearchGrepTool.cs:340` 持有 `IFullTextSearchEngine`）。适配器 `LuceneFullTextProbe` 放在 `temp/U4-0-probe/PuddingRetrievalEvalProbe`（**不进 slnx、不进版本控制**——它是适配器，不属于组件边界；数字落在 `Source/PuddingRetrievalEval/eval/reports/`）。实测（`--warmup 1 --measured 3`，`repetitionStable=True`）：

| scope | 用例 | recall@1 | recall@5 | recall@10 | MRR | precision@5 | precision@10 | noiseRate@10 |
|---|---|---|---|---|---|---|---|---|
`Source/`（C#+TS） | 58 | 0.4224 | 0.6638 | 0.6638 | 0.5768 | 0.1621 | 0.0810 | 0.0290
`Source/`（C#） | 36 | 0.6389 | 0.6944 | 0.6944 | 0.7292 | — | — | 0.0439
`Source/`（TS-TSX） | 22 | 0.0682 | 0.6136 | 0.6136 | 0.3273 | — | — | 0.0045
`Docs/`（md） | 20 | 0.2500 | 0.4750 | 0.5250 | 0.4238 | 0.1300 | 0.0700 | 0.0000

延迟（ms，冷/热分离，原始值）：`Source/` 冷 n=58 `p50=7.126 / p95=27.216 / p99=625.385`、热 n=232 `p50=7.116 / p95=26.548 / p99=31.313`；`Docs/` 冷 n=20 `p50=4.189 / p95=9.922 / p99=567.352`、热 n=80 `p50=4.175 / p95=7.698 / p99=12.896`。

**顺带测出的检索面事实（只测量、未改）**：① 该引擎索引 `Source/` 3,514 文件 / 52.2 MB 用 **462 s**，索引 `Docs/` 680 文件 / 11.3 MB 用 **13 s**；② 原因是 `BuildIndexAsync` **对每一种扩展名各遍历一次目录树**（77 个模式）——单次完整遍历 `Source/` 实测 6.6 s，与 462 s ÷ 77 ≈ 6.0 s 同量级（两种规模各一次实测印证该模型）；③ 因此**仓储根作为 scope 的索引构建不可行**：实测根 scope 可索引 **28,178 文件 / 743 MB**、单次遍历 **195 s** ⇒ 量级为小时级；④ 根 scope 的头部噪声源是 **`.pudding`（20,926 个可索引文件）与 `.tmp-build`（1,335）**，两者均**不在**任何现有排除表内 ⇒ 这是 U4-1 最直接的收益口径（原始输出 `temp/U4-0-probe/corpus-inventory*.txt`）。

**门禁（实测）**：`PuddingRetrievalEvalTests` **78/78 exit 0**；`PuddingCodeIndexTests` **82/82**、`PuddingCodeIntelligenceTests` **93/93**、`Tests/PuddingHost.Tests` **124/124**（与既有基线逐一相同，失败数**未增加**）；`dotnet build PuddingAgentNetwork.slnx -c Release` ⇒ **0 个错误 / 1580 个警告 / exit 0**（新工程已登记进 slnx：**+2 行 / 0 删除**）。新组件边界：`ProjectReference=0`、`PackageReference=0`；组件源码内对 6 个禁用程序集与 `Lucene.*` / `Microsoft.CodeAnalysis.*` / `Microsoft.Build.*` 的**代码引用 0 处**（仅 `NoiseDirectoryRules.cs:11-13` 注释里出现 3 处来源路径）；**阳性对照**：同名字符串在 `Source/PuddingRuntime` 命中 6 处 `using PuddingCodeIndex.* / PuddingFullTextIndex.*` ⇒ 搜索仪器本身有效，故组件的 0 命中是真实否定。

**变异取红（三份原始输出 + hash 三点值）**：`recall@k` 分母改错 ⇒ **8 红/70 绿/78**；`MRR` 位置改错 ⇒ **5 红/73 绿/78**；复原后 `Services/RetrievalMetrics.cs` blob hash **逐位相同**（`93edd683b5396ebeb03c9026a483290be656ede9`；变异期分别为 `e32d7770679eb54b801c9922285125c403020f0b`、`8f14c759ded4559eee6ab898b28142e5470a92a2`）且 **78/78 绿**，残留变异标记 **0**。另有 S4 边界变异：组件临时引用 `PuddingFullTextIndex` ⇒ **1 红/77 绿/78**（断言点名 `Lucene.Net, Lucene.Net.Analysis.Common, Lucene.Net.Queries, Lucene.Net.QueryParser, Lucene.Net.Sandbox, PuddingFullTextIndex`），撤销后 csproj hash 逐位相同（`19dd4f5946fb16468a9d7decf883a20227f67414`）且 78/78 绿。原始日志：`temp/test-out/u4-0-mutation-A-recall-denominator.log`、`u4-0-mutation-B-mrr-position.log`、`u4-0-restore-green.log`、`u4-0-a8-boundary-mutation.log`、`u4-0-a8-boundary-restored.log`；汇总 `temp/U4-0-probe/mutation-transcript.txt`。

**诚实留白**：① **S5 未接入**——不改 Host/DI、无宿主消费方、未注册进 DI 组合根；② 冷样本是"该查询在本进程内的第一次调用"，**不是**跨进程 / OS 页缓存冷启动；③ 根 scope 未能建索引 ⇒ 78/80 条用例有基线，**2 条**锚定在仓储根的 md 用例（`Agents.md`、`Agents-Hygiene.md`）当前**不可测**，而它们的可测性本身依赖 U4-1 把根 scope 规模压下来；④ 达标阈值（"毫秒级"落成具体 ms）**未内置**，属用户决策；⑤ 只接了一个面（Lucene）；代码索引面需 `ICodeIndexer`（Roslyn/MSBuild）产出语料、词法 grep 面的检索核心与工具壳尚未分离 ⇒ 两者的接入计划见 `temp/U4-0-REPORT.md` 的 BLOCKERS/RISKS。

**本刀未提交**（约束：不 git add/commit/push）；改动文件见 `temp/U4-0-REPORT.md` 的 CHANGES。

## 2026-09-24 U3-C：索引校准（mark-and-sweep）—— 陈旧行清除 + NeedsReconcile 归位（检索正确性收口）

**问题**：其它入口都是**变更驱动**的；`RoslynCSharpIndexer` 只遍历编译中现有语法树，scope 级/全量重索引**不做 sweep**（没有"本轮未见"这个概念）⇒ 历史遗留、整目录删除/改名、监听丢事件产生的陈旧行**永不被清除**，检索会持续返回已不存在文件的符号；`NeedsReconcile` 永不自动清除。

**交付（全在 `Source/PuddingCodeIndex/`，未改 Host/DI、未改检索查询链、未改 Enqueue 语义、未改索引算法、未新增 NuGet）**：
- 新增 `Services/CodeIndex/CodeIndexCalibrationService.cs`（`CodeIndexCalibrationRequest` / `CodeIndexCalibrationResult` / `CodeIndexCalibrationRejections`）：取 `ICodeIndexStore.ListFilesAsync` 的已索引路径集合，逐条判磁盘存在性，对"已消失"的调用 U3-B3 的 `RemoveFilesAsync`（事务性、幂等）；**只删索引行**；每事务 ≤ `DefaultSweepBatchSize`(256) 条、每轮 ≤ `DefaultMaxRemovalsPerRun`(4096) 条（超出则该轮 `Truncated=true` 并留给下一轮）；可取消（逐路径 + 逐批次，已提交批次不回滚）。
- **安全红线**：① 根目录缺失/不可读 ⇒ **拒绝 sweep**（`TryProbeRoot`：`Directory.Exists` + 强制一次枚举以暴露 `UnauthorizedAccessException`/`IOException`），记 `Error` + 保持/置位 `NeedsReconcile(calibration_root_unavailable)` + 本轮移除 0；② 只删索引行；③ **宽限判据**：驱动器把每个已施用批次触及的路径连时间戳记入 `RecentObservations`，校准对窗口内（`DefaultGraceWindow`=2min）的路径一律豁免（与 watcher/在途索引竞争；瞬时"不存在"≠历史遗留），豁免是延后不是丢弃；④ 幂等（第二次移除 0，且不再调用 store 的删除）。
- `CodeIndexMaintenanceService`：驱动步在"批次施用 + 无条件泵"之后追加校准步；`NeedsReconcile` 的 scope 走校准（仍保留既有 scope 级重索引升级：一个清陈旧行、一个补新增文件）；**成功 ⇒ `ClearNeedsReconcile()`**，被拒/被截断 ⇒ 保持；每 scope 至少间隔 `DefaultCalibrationInterval`(60s) 才重试；**变更源创建失败 ⇒ 立即标 `NeedsReconcile(watcher_error)`**（没有 watcher 就等于捕获不可信）。
- `ICodeIndexMaintenance`：`CodeIndexMaintenanceScopeStatus` 增 `SweptFileCount` / `CalibrationRunCount` / `RejectedCalibrationRunCount` / `LastCalibrationAtUtc`。`CodeIndexScopeState` 增 `ClearNeedsReconcile()`（只清 reconcile，不动 dirty/计数器）+ 新原因常量 `CalibrationRootUnavailable` / `CalibrationFailed`。
- 测试：新增 `Source/PuddingCodeIndexTests/Services/CodeIndex/CodeIndexCalibrationDriverTests.cs`（A1~A5 + 宽限窗口 + 每轮 ceiling）与 `CodeIndexCalibrationServiceTests.cs`（A6 幂等 + 拒绝守卫 + 宽限窗口 + 分批/上限 + 取消 + "只调用 ListFiles/RemoveFiles"），`CodeIndexCalibrationTestDoubles.cs`（记录型 store/logger 替身、失败 watcher 工厂），`CodeIndexScopeStateTests` +1；`MaintenanceHarness` 支持 scope 根覆盖 / 绝对路径播种 / 注入 logger / 注入 ceiling。

**门禁（实测）**：`PuddingCodeIndexTests` **82/82**（66→82，+16）、`PuddingCodeIntelligenceTests` **93/93**、`Tests/PuddingHost.Tests` **124/124**、`PuddingAgent -c Release` exit 0 且日志内 `error CS`=0；`PuddingCodeIndex.csproj` 的 `ProjectReference` 仍为 **0**，4 个禁用程序集名在组件内只命中 2 处**注释**（阳性对照：同名在 `PuddingCodeIntelligence` 命中 21 处 ⇒ 方向为消费方→组件）。

**改动前红（A1 基线）**：无事件 + 已删文件 ⇒ 步骤后 `SearchSymbolsAsync("LegacyClass")` 实际 **1** 条（`Assert.IsEmpty 失败。大小 0 的预期集合。实际： 1`），原始输出 `temp/u3c-A1-baseline-prefix-red.txt`。

**变异取红（三份原始输出）**：**A** 移除"根目录缺失即拒绝"守卫 ⇒ **2 红/80 绿/82**（A3：`status.SweptFileCount` 期望 0 实际 **1** ⇒ 索引被误删）；**B** 把 sweep 做成 no-op ⇒ **11 红/71 绿/82**（A1 硬判据红：`Assert.IsEmpty 失败。实际： 1`）；**复原**后 `git hash-object` 与变更前**逐位相同**（`CodeIndexCalibrationService.cs` 三点值：变更前 `08433a58741e39fa041d8938f60f1fb8f8840eee` / 变异 A 后 `1cdba0d6a4d0d7e6e98c915f243db734e25064ea` / 变异 B 后 `da1c3b7f8d597f8fb1509760371128a8108a2562` / 复原后 `08433a58…`）且复跑 **82/82 exit 0**，`MUTATION` 残留 0。原始日志：`temp/test-out/u3c-mutationA.txt` / `u3c-mutationB.txt` / `u3c-restore-green.txt`（81 用例态）与 `u3c-final-index-tests.txt`（最终树 82/82 exit 0）。

**§6 Lucene（只调查，未实现接线）**：内容文档的陈旧清除只在 `LuceneSearchEngine.BuildIndexInternalAsync` 的**增量**分支发生（`:325-352` 算集合、`:396-400` 消费），`stalePaths` 由引擎自己算（索引 `path` 词项 − 本次磁盘扫描），**无任何外部端口喂它**；唯一生产调用点是 `Source/PuddingPlatform/Services/RawSessionLogService.Fts.cs:74`。若接线，端口应定义在**能力所在侧**（`PuddingFullTextIndex/Contracts`），编排留在组合根（`PuddingHost` DI/HostedService）。详见施工计划 §U3 的 U3-C 块。

**诚实留白**：校准只在 scope **被标 `NeedsReconcile`** 时才触发（未实现 §U3-C 的"每 15min 常规 metadata 校准"周期）；`RootUnreadable` 分支只有代码路径、无实测（Windows 上稳定复现"存在但不可读"成本高）；只区分"路径缺失/根缺失/根不可读"，**不区分**"根存在但部分子目录不可读"；`Truncated` 之后靠下一次重试续跑而非同一轮循环；`PuddingFullTextIndex`（Lucene）仍未接线；未部署、未重启。

## 2026-09-24 U3-B3：删除/重命名真清除 + 按文件增量（检索正确性）

**第 0 步是取证，不是改码**。实测基线（改动前、可复现）：① 变更管线级 —— 文件已索引 → `Deleted` 批次 → 查询**仍返回该文件的符号**（`Assert.IsEmpty 失败。大小 0 的预期集合。实际：1`）；② 索引器级（真 Roslyn + 真 store）—— 文件从编译中消失后跑**全量重索引**，该文件仍残留 2 条符号行（`T:Probe.Alpha`、`M:Probe.Alpha.Value`）与对应的 `CodeFiles` 行 ⇒ **全量重索引不做 sweep**（源码证据：`RoslynCSharpIndexer.cs` 的 `IndexWorkspaceCoreAsync` 只遍历编译中的语法树，没有“本轮未见”清理），sweep/校准归 U3-C。

**交付**：① `ICodeIndexStore.RemoveFilesAsync`（**一个批次一个事务**：文件记录 + 其全部符号 + 其关系/引用；**幂等**，未知路径安全 no-op 并返回真正删除的文件数）；② 新端口 `ICodeIndexFileUpdater.IndexFileAsync`（**故意不放进 `ICodeIndexer`** —— 给全量端口加成员会破坏**每一个**实现者，实测直接弄坏了禁写路径 `Tests/PuddingHost.Tests/Hosting/CodeIndexMaintenanceHostCompositionTests.cs:290` 的 `CountingCodeIndexer`；`ICodeIndexer.cs` 已回退回 HEAD，blob hash 相等）；Roslyn / Python / TypeScript 三个索引器实现该能力，不能处理则返回 `Failed`；③ `CodeIndexMaintenanceService` **按文件施用**：`PathsToRemove` 真删、`PathsToReindex` 逐文件索引、重命名 = 旧清新增、消失的变更路径按删除处理；目录变更 / 索引器拒绝 / 无该能力 ⇒ **升级为 scope 级重索引**（不丢变更）；批次施用异常 ⇒ 标 `NeedsReconcile(batch_application_failed)` + 错误日志；状态新增 `RemovedFileCount` / `IncrementallyIndexedFileCount` / `ScopeEscalationCount`。

**检索正确性（唯一硬判据）**：文件已索引 → 被删 → 走变更管线 → **查询不再返回其符号**（`CodeIndexRemovalCorrectnessTests`，改动前红、现在绿）。

**门禁**：`PuddingCodeIndexTests` **66/66**（58 → 66：+5 管线施用 + 3 存储清除）、`PuddingCodeIntelligenceTests` **93/93**（89 → 93）、`PuddingAgent -c Release` **0 个错误 / 14 个警告（exit 0，日志内 `error CS` 真实命中 0）**；`Tests/PuddingHost.Tests` **123/124**：唯一失败项 `Enqueue_Is_Pumped_By_The_Host_Driver_And_Reaches_The_Indexer` 经实测是**既有时序竞态**（它等 `indexer.CallCount>=1`——在索引器调用**内部**被观测到——随后立即断言 `CommittedVersion>=1`，而该水位在 `CodeIndexScheduler.ProcessJobAsync` 的 `finally` 里、索引器返回**之后**才写；干净 HEAD 树（`git archive` + 补 `external/` 后）6 次运行 **2 红**，本工作树 9 次运行 3 红，失败率同级）。

**边界**：`PuddingCodeIndex` 的 `ProjectReference` 仍为空、无 Roslyn/MSBuild 引用（带阳性对照的反向检索：4 个禁用名仅命中 2 处**注释**）；**未改 Host/DI**（无新增依赖注册：维护服务从已注册的 `ICodeIndexer` 取 `ICodeIndexFileUpdater` 能力）；未新增 NuGet。

**变异取红（两组，各三份原始输出）**：A 让按文件清除变 no-op ⇒ **6 红 / 60 绿 / 66**（含硬判据用例）；B 只删文件记录不删符号 ⇒ **4 红 / 62 绿 / 66**（含硬判据用例）；复原后 `git hash-object` 与变更前**逐位相同**（`bccc3a3b0f0ae14867af8599c6ce85a28520a581`）且复跑 **66/66 exit 0**；`MUTATION` 残留 0。

**诚实留白**：`ReconcileRequired` 永不自动清除（U3-C）；目录删除/重命名下的**子树**陈旧条目不在本刀（U3-C manifest 校准）；`PuddingFullTextIndex`（Lucene）是与 code index **无数据交叠**的第二份索引（文件内容检索，自建 `stalePaths` 增量清理，`LuceneSearchEngine.BuildIndexAsync`），本刀**不接**；Roslyn `IndexFileAsync` 每次调用重新打开 MSBuild 工作区（未做复用），且**未做**真 `.csproj` 端到端实测；Python/TypeScript 的按文件实现沿用既有逐文件抽取脚本，未在测试环境实测。

## 2026-09-23 U3-B2a：索引维护接入宿主生命周期（P0「入队无人泵」收口）

U3-B1（`a378a9d8`）删掉 `CodeIndexScheduler` 的自驱动 worker 后，生产受理点 `code_index_register_project`（`Source/PuddingRuntime/Tools/BuiltIns/CodeIntelligence/CodeProjectManagementTools.cs`）的 `Enqueue` 就没有任何东西去泵 —— 入队即静默滞留。本刀让「入队 → 泵 → 索引器」在宿主运行时真正闭合。

**组件侧（逻辑留组件内）**：`CodeIndexMaintenanceService.ProcessDueBatchesAsync` 改为**完整驱动步** —— 除消费到期批次外，**每步都无条件泵一次** `ICodeIndexSchedulerDriver.ProcessPendingAsync`。为什么必须无条件：调度器队列的投喂方不止变更管线，注册工具是直接 `Enqueue` 的、背后没有批次；只在 `HandleBatchAsync` 里泵会让这类请求永久饿死（那就是 P0 本身，只是换了个位置）。`ICodeIndexMaintenance` 契约补齐 `EnsureScope(workspaceId, scopeId, rootPath)`（原先只在实现类上），使宿主可以**只通过端口**挂载 scope。

**装配侧**：`PuddingCodeIntelligence/DependencyInjection.cs` 新增三项注册 —— `ICodeIndexSchedulerDriver`（从 `ICodeIndexScheduler` 的**同一实例**派生：两个调度器＝同一索引两个写者；契约不满足时 fail-closed 抛出可诊断异常）、`ICodeIndexWatcherFactory`（显式传 `ILoggerFactory` 派生的 logger，非泛型 `ILogger` 未注册会静默为 null）、`ICodeIndexMaintenance` → `CodeIndexMaintenanceService`。`PuddingHost` 新增 `Hosting/CodeIndexMaintenanceHostedService`（**唯一生命周期驱动**，本身**没有任何循环/队列/调度器调用**）：`StartAsync` 非阻塞启动组件驱动，并把「已注册 scope」挂上变更源（附着在启动路径之外，失败只记日志，无 scope 时安全 no-op）；`StopAsync` 有界（外层 10s 上限 + 组件自身边界），不抛异常逃逸、不丢已入队请求。注册点：`PuddingServiceCollectionExtensions.Platform.cs`，紧邻 `AddPuddingCodeIntelligence()`。

**门禁（含一处顺带修复）**：`Tests/PuddingHost.Tests` **在本刀之前根本编译不过** —— `Storage/StorageMaintenanceServiceTests.cs:357` 与 `StorageManagementAdministrationTests.cs:530` 仍 `using PuddingCodeIntelligence.Contracts;`，而 `ICodeIndexScheduler` 已随组件拆分迁到 `PuddingCodeIndex.Contracts`（拆分片遗留回归，两个 1 行 using 即修，各 +1/−0）。修复后实测：`PuddingHost.Tests` **124/124**（其中 `Hosting` 命名空间 58/58、`PuddingApplicationHostCompositionTests` 2/2、新增 `CodeIndexMaintenanceHostCompositionTests` 3/3）；`PuddingCodeIndexTests` **58/58**；`PuddingCodeIntelligenceTests` **89/89**（两者与拆分后基线一致）；`PuddingAgent -c Release --no-incremental` **已成功生成 / 0 个错误 / 175 个警告**。新增 `Tests/PuddingHost.Tests/Hosting/CodeIndexMaintenanceHostCompositionTests.cs` 断言：驱动可解析＋泵端口与调度器同实例＋驱动已注册且持有同一实例＋无 scope 时附着安全 no-op＋有界停止；`Enqueue → 泵 → ICodeIndexer`（替身计数）闭合；已注册 scope 被挂上变更源。

**变异取红（两组，各三份原始输出）**：A 移除 `ICodeIndexMaintenance` 注册 ⇒ Build 期 `ValidateOnBuild` 失败（`Unable to resolve service for type 'PuddingCodeIndex.Contracts.ICodeIndexMaintenance' while attempting to activate 'PuddingHost.Hosting.CodeIndexMaintenanceHostedService'`）**3 红 / 0 绿**；B 移除驱动器 `AddHostedService` 注册 ⇒ `Assert.Single() Failure: The collection was empty` ＋ 2×`Sequence contains no elements` **3 红 / 0 绿**。复原后 `git hash-object` 与变更前**逐位相同**（`69fd5faf4d41b011c2b4dbb7f423a5e8e86bb71b`／`beaa5eefa33a9ef05092a505b74e01148ac3e8fa`），`MUTATION` 零残留。

**诚实留白**：scope 附着的 workspace id 取自 `<DataRoot>/workspaces/` 下的目录名（平台既有约定，见 `PuddingDataPaths.WorkspaceRoot` 与默认 agent manifest 的 `workspaceId: default`）—— 仓库内**没有**跨 workspace 列举 scope 的契约，故未新增此类 API；持久待办账本（U3-B2b）、按文件增量索引（U3-B3）、`PathsToRemove` 真删除语义、`NeedsReconcile` 自动清除均**不在本刀**。未部署、未提交（改动留在工作区由父代理验收）。

## 2026-09-23 组件化交付规程 S2/S3 首次兑现（PuddingCodeIndexTests）

新建 `Source/PuddingCodeIndexTests/`：索引组件的独立测试工程，**只引用** `PuddingCodeIndex`（`ProjectReference` 恰好 1 条）。从 `PuddingCodeIntelligenceTests` 迁入 `Services/CodeIndex/` 8 文件 + `Storage/SqliteCodeIndexStoreTests.cs`（**55 用例，零丢弃**：守恒等式 `144 = 89 + 55`），另加 3 条机器可验的边界断言（`ComponentBoundaryTests`：探测器自检 / 进程未加载 Roslyn·MSBuild·上层程序集 / `*.deps.json` 依赖闭包）。`PuddingCodeIndex` 的 `InternalsVisibleTo` 由 `PuddingCodeIntelligenceTests` 改为 `PuddingCodeIndexTests`（实测移除后上层测试仍全绿），**未对上层开放任何反向可见性**。门禁：新工程 58/58 exit 0、`PuddingCodeIntelligenceTests` 89/89 exit 0、`PuddingAgent -c Release` exit 0；变异取红 2 组（加 `PuddingCodeIntelligence` 引用 ⇒ 2 条边界断言红、分别列出 5/7 个禁用程序集；`Compile Remove` 一个测试文件 ⇒ 通过数 58→51 而 exit 仍 0），复原后 `git hash-object` 逐位相同（`5505b031…`）、`MUTATION` 大小写敏感 0 残留。见 `Source/PuddingCodeIndexTests/code_map.md`、`Docs/Conventions/组件化交付规程.md` §4/§4.1。

## 2026-09-21 code_map 索引补齐（PuddingTaskRecall.Cli）

按 `code-map-incremental-update` 技能做索引完整性审计：`Source/` 下 19 个生产项目均已有 L2 索引，唯一缺口是生产 CLI `Source/PuddingTaskRecall.Cli/`（有 `.csproj`、无 `code_map.md`，且未登记进顶层目录表）。已按模板补 `Source/PuddingTaskRecall.Cli/code_map.md`（入口 & 配置 / 核心功能 / 结果模型 / 测试）并在 L1 顶层目录表补链接。8 个 `*Tests`/Benchmarks 项目仍无索引（L1 无测试项目区，未擅自新增）。

## 2026-09-21 出站 HTTP User-Agent 统一标识

新增 `PuddingCode.Configuration.PuddingUserAgent`（`PuddingAgent/1.0`）作为出口 UA 单一事实来源：组合根 13 个命名 HttpClient 在各自 `AddHttpClient` 配置中逐点写入 UA（Connectors 6／Platform 6／Runtime 1）；`FlurlWebClient` 为全部搜索/抓取工具出站请求兜底（调用方显式 UA 优先）；`ControllerLlmProxyService` 三处裸 `new HttpClient()` 改经 `CreateHttpClient()`；`GitHubSearchTool` 硬编码改为引用常量。全局兜底方案（`ConfigureHttpClientDefaults` + `DelegatingHandler`）经 A/B 对照实测会使 `PuddingWebApiTests` 产生 48 项回归（失败 6→54，孤立运行亦失败），已回退为逐点注册并在组合根留注释警示。验证：`PuddingHost` 构建 0 error；`PuddingWebApiTests` 172 项 6 失败（均为既有已定性项）／166 通过；`PuddingRuntimeTests` 定向 31/31。提交 `a452eb76`。

## 2026-09-21 Desktop 空闲 CPU 与日志呈现

`IdleDetector` 将 ReArm 的调度续行与空闲状态日志标志分离，同一活动窗口只打印一次；IdleDetector/Heartbeat 18项回归通过，保留周期回调。

`WebView2PresentationGate` 在隐藏/最小化/卸载时解绑 SDK `PART_image.Source`，恢复同一图像，覆盖 Workbench/Agent Browser，不暂停浏览器执行。`RuntimeCenterView` 可见性门控 timer；`RuntimeCenterViewModel.RefreshTransient` 仅通知变化字段；`CoreProcessLogBuffer.GetTail` 缓存未变化文本；Shell/托盘忽略瞬时通知与重复提示。247 项测试通过，真实运行中心 CPU 2.014%→0.098%，设置页1.709%→采样0%；可见工作台仍有绘制成本。见[证据与部署记录](Docs/Reports/Desktop空闲CPU与日志展示修复-2026-09-21.md)。

## 2026-09-21 工具重复参数键故障隔离

`HarnessToolCompatibilityAdapter.GetArgumentValidationError` 按JSON对象递归检查重复属性，规范化保留歧义原文；`ToolInvocationService`/`PuddingToolExecutionService` 拒绝执行并返回 `tool_arguments_duplicate_key`，避免延迟JsonObject物化异常击穿Turn。`HarnessToolCompatibilityAdapterTests` 覆盖5种重复键、合法对象及失败后续行；连同分类器接线/健康/Jev单元回归60项通过。源码验证与Core重新加载分别记录。

部署补充：`1941a2e` 已通过 Desktop 第二次部署，Core PID42796/08:53:26 Ready，托管清单与实际前端匹配；Codex MCP恢复Available/7工具，蜜糖飞书通道因原Agent禁用保持告警，分类器未裁决时unknown。见[部署诊断与验收边界](Docs/Reports/Core重启与夜间代码部署诊断-2026-09-21.md)。

## 2026-09-21 夜间效率问题登记与优化合同

[第一性原理与代码级方案](Docs/Features/夜间效率问题登记与第一性原理优化方案-2026-09-21.md)：N01–N14覆盖测试门禁、RSI评测、执行身份、失败止损、价格对账、记忆/压缩、恢复片段、降噪余项、部署证据、信号来源、Goodput、最终请求与终端等待。优先补充现有任务，保留历史和执行状态；具体TaskId及写后核对见方案末尾登记表。登记不是实施或产品验收。

## 2026-09-21 夜间效率与RSI评估（只读取证）

[评估报告](Docs/Reports/PuddingAgent夜间效率与RSI评估-2026-09-21.md)与[聚合数据](Docs/Reports/PuddingAgent夜间效率与RSI评估-2026-09-21.metrics.json)：窗口09-20 22:00至09-21 08:09，2410条Gateway用量，DeepSeek缓存98.0698%、GLM93.8195%；DeepSeek静态计价为账单单价两倍，精确窗口按账单价重估¥15.93但非供应商结算。82提交有实质产出，心跳/私有goal.md不等于平台Goal；降噪summary已部署生效，后半夜classifier未加载。RSI优先补suite门禁、执行身份、同目标失败episode与恢复片段评测；本轮无产品/配置/运行数据修改。

## 2026-09-20 计划语义版本与重规划修订分离

`TaskPlanRunEntity.PlanVersion` 是固定编译语义；新增 `PlanRevision` 调度计数，`GoalSettlementStore.TryReplanBoundPlan` 仅增修订号，`GoalQueriesController`/熔断事件分别输出两者。`TaskPlanningSchemaBootstrapper` 幂等补列，`ExecutionCommandReader` 继续严格拒绝旧版/未知语义。`TestScripts/repair-task-plan-semantic-version.py` 是有编译绑定与连续事件证明的停机恢复工具，保留状态/成本/历史。源码 `08b3cbb` 已于21:21经Desktop部署，Core PID36380/Ready；原受影响任务第26轮真实工具调用通过。见[修复与验收记录](Docs/Reports/计划语义版本与重规划修订分离修复-2026-09-20.md)；完整 Task 侧合同重规划仍待实现。

## 2026-09-20 全量任务看板整理与规划实施分工（设计登记）

[施工方案](Docs/Features/任务规划与实施分工及存量看板施工方案-2026-09-20.md)与[逐卡台账](Docs/Reports/任务看板全量整理台账-2026-09-20.md)：全量盘点271条历史/活动记录，合并重复、归档测试卡、保留已完成历史，为有效卡补充代码入口和分层验收。Task侧拟新增ImplementationBrief校验与阶段路由，规划sol/困难决策astra/实施低成本路由均受授权和实际调用门禁。`GoalSettlementStore.TryReplanBoundPlan` 的 `PlanVersion++` 是版本循环直接落点；PlanVersion语义与PlanRevision修订必须分离。这里只登记方案，不代表新增机制已实现或部署。

## 2026-09-19 图片预处理与错误恢复

`VisionRequestPolicy`/`VisualInputRequestBudget`：默认 600 图，统一统计历史/附件/工具输出并分配尺寸和字节预算。`VisualRequestBodyBudget`：DeepSeek 最终 JSON 48 MiB 检查与有界重建。`IVisualArtifactPreprocessor`→`VisualArtifactResolverBridge`→`VisionArtifactStorageService.ResolveForRequestAsync`：保留原图的压缩/缩放缓存。`VisionTextContinuation`：纯文本续聊仅投影历史图片引用；Streaming/Buffered 共享恢复语义，视觉错误不触发 API 熔断。见[实施记录](Docs/Reports/图片请求官方限制与预处理恢复修复-2026-09-19.md)与 ADR-077 §3.2、ADR-088 补充。源码 `adc09ff` 已于 15:08 经 Desktop 受控部署重启，Core PID1608/Ready，托管产物哈希一致；真实模型图片 smoke 待验收。

## 2026-09-17 Goal模式简化设计（待实现）

[权威方案](Docs/Features/Goal目标驱动执行与分层验证闭环设计-2026-09-15.md)与[ADR-092第二版](Docs/07架构/106ADR-092目标驱动执行与分层验证闭环ADR.md)：Goal自有持久表、单一状态机/决策入口、回合与检查分离；删除Goal步骤推进/两级Verifier，Task可选适配并统一终态入口。Agent自身goal.md独立，不作Goal运行依赖。本轮仅设计，S1–S4代码落点、迁移和验收见方案。

## 2026-09-17 Goal多轮效率与缓存评估

只读账本核实G92-0 Goal仅以build/test合同完成，Task仍NeedsReview、G92-1 Blocked；15轮270次主调用52.43M输入，缓存98.8814%，业务目标未达成。当天DeepSeek96.7819%、GLM96.0982%；优先补目标合同、事件等待及认领恢复。见[评估报告](Docs/Reports/Goal多轮效率与缓存命中评估-2026-09-17.md)。

## 2026-09-17 DeepSeek缓存首批优化已部署

`ToolExposurePlanner`按首次发现的文件/编辑/代码/终端/Git只读能力稳定成组曝光；`ContextPipeline`偏好使用L9尾部完整快照，支持去重、清空及冷恢复。`UserPreferenceService`只读工作区专用偏好Book，读取失败不伪装空集。92项Runtime+1项宿主回归通过，Core已部署重启。见[实施与验收](Docs/Reports/DeepSeek缓存首批优化与部署-2026-09-17.md)；正式缓存99%验收未完成。

## 2026-09-17 隔夜缓存与参考实现诊断

2550次Gateway usage与归因唯一内容关联：54次工具稳定追加；后16次摘要复用99.29%，checkpoint后首轮18.40%。比较Harness动态快照/摘要重放与Reasonix稳定工具入口/版本上下文，给出Composition、网关、ToolExposurePlanner、压缩和后台的文件级方案。见[完整报告](Docs/Reports/隔夜缓存命中评估与Harness-Reasonix优化方案-2026-09-17.md)及ADR-084；当前仅诊断设计。

## 2026-09-17 GoalResume 宿主生命周期修复

`GoalResumeService`：单例 epoch 台账 + 每次调用 async scope 的 GoalRunStore/IGoalCommandService；产品 Runtime 组合根注册 GoalResumeTool。`GoalResumeServiceTests` 使用严格 DI 生命周期验证，`PuddingApplicationHostCompositionTests` 验证真实 DesktopChild 注册。见 [报告](Docs/Reports/GoalResume依赖生命周期导致Core崩溃修复-2026-09-17.md)。

## 2026-09-16 模型输出上限权威来源

`PuddingFileLlmConfigService` 从资源池模型读取 `MaxOutputTokens`；`AgentRuntimeProfileResolver`、`AgentLLMConfigResolver` 不再按 Agent/角色收紧。Agent 编辑、模板 DTO/manifest/profile 已移除 `maxReplyTokens`。DeepSeek 模板参数及本机验收见 [报告](Docs/Reports/模型输出上限归一与资源池核对-2026-09-16.md)。

## 2026-09-16 缓存命中诊断与修复方案

只读核对Gateway、归因、Composition及分层hash：47次工具变化为稳定追加，用户偏好重写system，四次摘要复用异常尚缺最终请求差异。修复顺序为任务能力包、偏好版本快照、C02最终请求证据、冷启动/后台输入治理。见[完整诊断与验收边界](Docs/Reports/缓存命中诊断与修复方案-2026-09-16.md)，ADR-084及长程自治设计已同步待实施项。

## 2026-09-16 Goal 状态格式与聊天页崩溃

`GoalCommandsController.ToDto` 统一 snake_case 状态；`GoalBanner` 对未知状态安全显示并限制动作。补齐七种 API 状态与额度耗尽/未知值 UI 回归。见[修复与运行验证](Docs/Reports/Goal状态格式与聊天页崩溃修复-2026-09-16.md)。

## 2026-09-16 Desktop/Core 同时退出与独立启动

新增 `TestScripts/start-pudding-desktop-independent.ps1`，使用既有 Explorer 窗口代理启动，核对 Desktop 父进程并拒绝单实例转发。07:28 退出与 Codex 更新重合，缺少新崩溃栈；已恢复并改正开发工具进程归属。见[证据与边界](Docs/Reports/Desktop与Core退出恢复及独立启动-2026-09-16.md)。

## 2026-09-15 Goal 检查器启动依赖修复

PuddingHost 将 DefaultTerminalCommandPolicy 同一 singleton 映射到 ITerminalCommandPolicy 与 ITerminalCommandAdmission，消除 GoalCheckRunner/GoalSettlementWorker 启动验证失败。产品组合根测试覆盖解析及准入拦截。见[运行中心熔断修复](Docs/Reports/Goal检查器依赖注册与Core启动修复-2026-09-15.md)。

## 2026-09-15 产品视觉上下文注册修复

PuddingHost 产品组合根显式注册共享 `FrozenVisionContextAccessor`，让 Agent Streaming 的逐次 MoveNext 快照绑定实际到达 DirectLlmClient；TurnExecutorAdapter 保留 Runtime 的 errorCode。见[故障与验证](Docs/Reports/产品视觉上下文注册修复-2026-09-15.md)。

## 2026-09-15 主代理缓存前缀修复

工具可见顺序在 SessionManager 中跨 dispatch 保留，并从既有 Composition 恢复；稳定规则留在 system，可变工具/技能目录在 User tail 按最新完整正文去重追加。保留权限、压缩及即时发现，65 项定向测试通过；线上 99% 待实际账本验收。见[诊断与修复](Docs/Reports/主代理缓存前缀修复-2026-09-15.md)。

## 2026-09-15 Image Reader 预处理

Image Reader 支持 metadata/read/prepare、detail、缩略图、原图多区域裁剪、旋转、灰度、降噪和编码；统一模型边界准备聊天/历史图片，保留原图。VisionHelper 配置与路由合同已移除。见[能力合同](Docs/Features/ImageReader原生阅读与预处理-2026-09-15.md)及[验证记录](Docs/Reports/ImageReader预处理实施记录-2026-09-15.md)。

## 2026-09-15 原生视觉流式上下文与 Image Reader

每次 LLM MoveNext 重新绑定冻结视觉快照，避免 SSE yield 后 AsyncLocal 丢失；Image Reader 只返回 typed 图片给调用模型，移除 helper 调用与设置入口。见[诊断与验证](Docs/Reports/原生视觉流式上下文与ImageReader修复-2026-09-15.md)。

## 2026-09-15 长消息卡片阅读优化

`ExpandableMessageContent` 为正文/静态过程提供按高度展开预览；`TurnContentStream` 就地收拢较早交错前缀，`ActivityGroup` 初次展示最近6项。保留canonical顺序与完整复制/TTS，手动行为组展开优先。阈值见ADR-079对应实施方案§15，验证与发布见[优化记录](Docs/Reports/长消息卡片阅读优化-2026-09-15.md)。

## 2026-09-20 心跳持久调度与低频登记

`AgentWakeQueue` 原子保存每实例 `state/heartbeat-wake.json`，重启恢复绝对到期时间；`HeartbeatOrchestrator` 全量登记/每分钟目录核对/发送前准入复核，忙碌短延期、成功后接续周期。`HeartbeatPreference.Enabled` 只控制心跳，sleep/agent_status 同步尊重。见[实施与验收](Docs/Reports/心跳持久调度与登记开销修复-2026-09-20.md)。52 项定向测试通过；已部署 Core PID32060/Ready，实机重启后绝对到期时间保持不变，自然心跳执行待到期验收。

## 2026-09-14 心跳失败状态与部署修复

见 [诊断与验证](Docs/Reports/心跳连续失败与状态投影修复-2026-09-14.md)：历史图片进入旧 Responses 文本路由导致 6 轮心跳连续失败；部署当前图片路由处理，ConversationMessageView.TurnOutcome 从 canonical 终态恢复无回复状态，MessageList/MessageRow 显示心跳失败原因；不改持久化结构。

## 2026-09-13 Desktop 启动恢复验证

见 [启动恢复记录](Docs/Reports/Desktop启动恢复验证-2026-09-13.md)：重新启动当前 Desktop 后 Core 约 30 秒进入 Ready，健康检查 HTTP 200；原 60 秒超时未复现，未修改产品代码，根因待失败周期诊断确认。

# PuddingAgent CodeMAP

## 2026-09-15 G92-1 下一行动（源码接线未完成）

见[定向检查与执行指令](Docs/Reports/Goal-G92-1运行链路下一步指令-2026-09-15.md)：4d5980f 已有 G92-0/1 合同与测试代码，尚未运行测试；下一步优先生产 capsule 事实填充、最终 Goal 结算消费 typed disposition、同条件多检查归并、真实 CheckRunner、完成提议与独立 Goal。六项源码提交不等于验收完成；外部指令与 canonical 回执单列。

## 2026-09-15 Goal 目标驱动与真实校验（ADR-092，Proposed）

见[施工方案](Docs/Features/Goal目标驱动执行与分层验证闭环设计-2026-09-15.md)、[ADR-092](Docs/07架构/106ADR-092目标驱动执行与分层验证闭环ADR.md)与[任务修订记录](Docs/Reports/Goal持续执行方案与任务修订-2026-09-15.md)。现状入口：GoalVerificationContracts、ConservativeGoalIterationVerifier、GoalSettlementStore/Worker、GoalContinuationWorker、TaskAgentCommandService、WorkUnitAwaitHandleEntity、AgentSleepTool。复用既有 outbox，补真实检查与逐项条件，修正 Turn 正常结束即单元完成、Task 先完成的循环依赖及恢复性 Goal Failed。四张既有任务重写范围；仅设计/派发，未实施或部署。

## 2026-09-15 ADR-091 A91-0 独立复审（needs_changes）

见[复审与下一轮指令](Docs/Reports/ADR-091-A91-0-Review-2026-09-15.md)：精确提交dd575cc定向131/131通过，943270d+6c34a24为123通过/16失败，另10项审计契约用例全部复现。已认可resolver解耦；待修隐式Check/Firewall的Dependency→Denied映射、生产fake开关、JSON语义、ReasonCode/wire传播、测试DI和真实deadline/cancel。A91-0已登记needs_changes并继续推进；未修改运行权限或部署Core，A91-1硬边界/原子许可仍待实施。

## 2026-09-15 自动审计后续设计（ADR-091，Proposed）

见[代码级设计](Docs/Features/自动审计与执行准入闭环设计-2026-09-15.md)、[ADR-091](Docs/07架构/105ADR-091自动审计与执行准入闭环ADR.md)和[阶段卡记录](Docs/Reports/自动审计方案与派发记录-2026-09-15.md)。现状入口：AgentFirewall、Tools/Approval/LlmToolApprovalReviewer、InMemoryToolApprovalService/AuthorizationService、PuddingToolRegistry.cs 内 PuddingToolExecutionService，以及 Web autoReviewClassifier/useAutoReviewClassifier/ChatMain。方案统一执行准入、增量行为审计与外部验证的修复循环；本轮仅文档和任务派发，未修改/部署这些产品代码。

## 2026-09-15 历史压缩状态显示修复（46679d7，已发布静态资源）

见[复查报告](Docs/Reports/压缩频繁复查与历史状态显示修复-2026-09-15.md)：新Core启动后的本轮观察无新compaction事件；useCompaction历史完成使用occurredAt绝对时间，未知不冒充刚刚，终态不被迟到started重新激活，reset清除旧toast。14项前端回归通过，隔离构建并发布148个静态文件；/admin/chat已返回umi.42511e7c.js且hash核对一致，Core PID24908未重启。A2后台无收益抑制仍执行中，不能将UI修复当作长期频次或99%验收。

## 2026-09-15 百万上下文压力压缩（572c394，已部署）

见[修复报告](Docs/Reports/百万上下文频繁压缩修复-2026-09-15.md)：ContextCompactionDefaults共享80%阈值；ContextCompactionService删除128K绝对cap、以CoverageManifest代际时间排除旧usage并优先当前请求；LlmOptions中的ContextUsageSnapshotStore区分实报与估算。141项定向回归通过，Core PID24908、只读context_health真实工具成功，UsedTokens与ProviderTotalTokens一致。按当前1M/384K配置约49.2万输入触发；A2无收益抑制、M01有界索引及长期/99%验收仍待完成。

## 2026-09-14 缓存99优化（部分修复，目标未达）

见 [实测报告](Docs/Reports/缓存99优化与实测-2026-09-14.md)：4680cc5新增HistoryPrefixReconciler及ChatMessage本地SourceContentHash，在ContextWindowManager核对热历史后保留原消息/追加canonical尾部；c0641c1让TurnId和MessageId共同排除当前入站（含空TurnId）；8edab4f移除AgentContextEnvelopeRenderer的JSON缩进。92项Runtime+1项Core回归通过，PID34120已加载。最终冷恢复97.5490%、热续行98.9814%、合计98.2766%；现场仍走既有richer_in_memory_history分支，新增对齐分支的线上命中未验收。C99 v11仍InProgress/C01 v7仍NeedsReview，下一步C02最终请求变化诊断与冷恢复协议差异，不能宣称99%完成。

## 2026-09-14 日志全文召回按需化（已部署）

见 [实施与产品验证](Docs/Reports/日志全文召回按需化与查询边界修复-2026-09-14.md)：fa309ba删除AgentLogRecallService、Runtime/Host注册和ContextPipeline私人日志回退层；95a0cd3由`RawSessionLogService.Fts.cs`负责显式日期/会话范围、短证据与诊断，`FullTextSearchScope`在Lucene TopK前过滤，`QuerySessionLogsTool`标记历史讨论未核实。Core PID26160真实两次调用通过，组装489ms；M01 v5/C03 v3记录部分完成，首轮摘要、通用Memory augmentation、分层快照和归档缺口仍待实施。历史记录与当前轮压缩保护保留。

## 2026-09-14 Memory快照与历史溯源定位纠偏

权威补充 `Docs/Features/Memory快照索引与历史溯源设计-2026-09-14.md` 与ADR-085：Memory是Agent主动维护的当前结论及项目/概念/场景多级索引，正文唯一存放在外部文件/目录或Book/Page；聊天与向量命中只是候选证据，历史按需查看前后文及后续修订。本节记录22:51设计基线，当时仍有自动私人日志召回、首轮整摘要和MemoryLibraryTool少量结果隐式探索；23:12已完成私人日志召回删除及显式FTS收敛，以上方实施记录为准，其余仍待完成。M02升P0/v2先实现最小快照写读，M01/v4再取消默认自动历史注入，C03/v2完善受约束向量/溯源；归档卡bfe2286/v2不再作为默认Memory前置。设计与四张看板已同步，未宣称产品完成。

## 2026-09-14 首轮上下文准备性能修复

`Docs/Reports/首轮上下文准备性能修复-2026-09-14.md`：AgentRunProjectionService 改用有界事件头查询（00bd0af）；ContextPipeline/ContextWindowManager 增加阶段计时（ddc25a2）；LuceneSearchEngine 使用稳定 SHA-256 目录，Host 从 PuddingDataPaths 注入索引路径，AgentLogRecall 每次增量刷新（8ddd0d3）。新 Core PID29400 跨进程复用索引，日志召回12.8秒→243毫秒、上下文13.7秒→683毫秒；相同两工具任务 canonical completed。用户同期恢复LM Studio，因此原87.6秒全量变化不能只归因代码。M01输入减负/99%/长程仍未验收；新增看板bfe2286047da49ec969c11717c46a81c修复归档身份与DataRoot。

## 2026-09-14 默认助手停滞与输入预算接管修复

`Docs/Reports/默认助手停滞修复与预算接管-2026-09-14.md`：Codex 直接修复 FileSearchTool 内置扫描的时间/条目预算与取消（4ce9d6f）、MessageRouter 类型化目标拒绝与 ConversationReplyProjectionWorker 逐项结算（8d2ad3b）、ExecutionUsageBudgetTracker / SubAgentInvocationService / LlmRequestBudgetGuard 的单请求容量和父子累计账本（990673e，PlanVersion=2）、ExecutionRunCoordinator 恢复前检查 pending cancel 并由 SqliteExecutionJournal 原子终态（a18702b）。204 项定向测试通过；新 Core PID25584，旧指令直接取消，真实两次 file_search 后 canonical succeeded。根目录仍可能明确部分覆盖；A2–A4、首轮上下文性能、task-bound 长程与99%缓存仍待验收。

## 2026-09-14 Flash 自修复代码任务书

`Docs/Reports/PuddingAgent自修复代码任务书-2026-09-14.md`：A1 优先分离 WorkUnit 单请求输入容量与累计输入账本，修正父子预算、委派 usage 汇总、零值轴和请求 guard，附 10 组反例及明确交付条件。后续 A2 压缩口径/无收益抑制、A3 无效等待、A4 新 fenced attempt 恢复；指出旧“失败后保留 assignment”建议与活跃执行槽不变量冲突。A1 已由 Codex 接管完成源码与部署验证，其他批次及新计划长程验收仍未完成；不要重复派发旧 A1 指令。

## 2026-09-14 最近 24 小时效率评估

`Docs/Reports/PuddingAgent最近24小时工作效率评估-2026-09-14.md` 与 `PuddingAgent效率指标-2026-09-14.json`：窗口 9/13 19:55–9/14 19:55，627 次模型调用、95.1818% 加权命中；17 次确认心跳 10 成功/7 失败，7 个子 Run 仅 3 completed。通过 canonical 工具回执核对 7 个自有提交（2 源码/5 文档），无新增 task.completed。重点为视觉故障持续失败、累计输入预算截断、审批/探针停滞与交接摘要占轮；仅评估，未改产品或看板。

## 2026-09-13 统一检索与渐进展开设计

U0 独立审阅：`Docs/Reports/ADR-089-U0审阅与返工意见-2026-09-13.md`。`bf6f91e/1f687d7/fe49137/ff79f3b` 落地 RetrievalContracts/RetrievalMatcher 与 SearchGrepTool 改造；Core 16、Runtime 32 项定向通过，补充复现暴露旧索引文本、正则超时丢失/整体预算、读取错误/取消误报 no_match、max_results 合并上限四项问题。U0 评价 needs_changes，S3 与 U1–U5 待交付；不能将已提交片段等同产品完成。

后台维护扩展（详细设计 §10）：U3 `8bddf9017b2d40049f8ea88823c2078a` 增补 FileSystemWatcher→有界 Channel→持久维护账本→后台单 writer，覆盖 in-flight 二次修改、增量提交、失效/删除、完整扫描后 sweep 与退休 generation GC；复用 CodeIndexScheduler/SqliteCodeIndexStore/LuceneSearchEngine，离线/读取异常不触发批量删除。总任务与 U5 同步扩展，仍仅设计。

看板已登记总任务 `b74e561e5299479e9afdd5123f26490d` 与 U0–U5 六张阶段卡（default；P1；自动派发关闭），U0 审阅时为 Ready、其余为 Backlog；完整 Task ID 与依赖映射见下述详细设计 §9，整体源码/部署/产品验收仍待完成。

`Docs/Features/Agent统一检索与渐进展开工具链设计-2026-09-13.md`、ADR-089（Proposed）：以 `workspace_search` 聚合 Everything 文件路径、代码符号和内容检索，`workspace_open` 统一读取/Outline/Summary/关系/Map/Status；复用 CodeQueryService、IFileOutlinerRegistry、FileChunkService、Lucene 与托管搜索。按用户范围仅借鉴 tgrep 思路，不引入程序/服务或新 trigram 引擎；U3 为现有查询计划与变更失效整合。施工入口为 FileSearchTool、SearchGrepTool、CodeQueryTools、CodeSummaryTool、SearchAttemptLedger、SmartWorkflow 与工具/模板权限投影。U0 部分源码已落地并待返工，统一入口及后台维护尚未交付，未完成性能/产品验收。

## 2026-09-13 自动压缩频次诊断

`Docs/Reports/上下文频繁压缩诊断-2026-09-13.md`：默认助手 9/12 至 9/13 07:41 共 40 次自动压缩流程、39 次写入；同日复查截至 09:44 的诊断日志累计 45 条 Auto、44 次写入（新增部分未重核 canonical）。raw cap 实际使用整请求估算，Provider 快照采用 max 并混淆实报标签。入口为 `ContextCompactionService.GetHealthAsync`、`ContextUsageSnapshotStore.RecordProviderUsage`、`ContextWindowManager.TryAutoCompactAsync`、`CompactionCoordinator`、Web `useCompaction`；本轮只有诊断/方案，未实施产品变更。

## 2026-09-12 VS Code Desktop 启动入口

`.vscode/launch.json`：Desktop普通/`--background`两种coreclr配置、DesktopHome与仓库环境选项；`.vscode/tasks.json`的`pudding-build-desktop`构建真实Desktop项目并使用标准`bin/Debug/net10.0-windows10.0.17763.0/`输出。DataRoot/Core路径/源码调试模式仍属于desktop.json，不是Desktop CLI参数；说明见 `Docs/07架构/92ADR-078Desktop调试模式源码启动与反向代理ADR.md` §4。

## 2026-09-12 MSBuild 非法历史输出路径修复

`Docs/Reports/MSBuild非法输出路径缓存修复-2026-09-12.md`：PuddingCodeIntelligence/PuddingFullTextIndex 的 `obj/Debug/net10.0/*.csproj.FileListAbsolute.txt` 残留150条含引号的历史输出路径，触发Visual Studio MSB3541。已备份并定点移除，两个项目经VS MSBuild实际构建通过；无需改业务代码或SDK targets。复发诊断与OutDir正确传参见 `How-Debuge.md`。

## 2026-09-12 原生视觉与截图优化入口

`Docs/Features/原生视觉与统一取图截图优化设计-2026-09-12.md`、ADR-088、`Docs/Reports/原生视觉优化看板修订-2026-09-12.md`：当前主力模型文件配置缺 `vision`；Reader 内 Responses/helper 分叉；Resolver 的 Data URI→Planner 解码上传；旧 384 图片估计；WebView2/RemoteBrowserPage ScreenshotAsync Unsupported。按 V5–V10 收敛能力/预算、Reader、Provider 流式传输，补齐 Web/Desktop 采集与共享主子代理图片轨迹。ADR-077 V0–V3 已有实现，本次仅新增设计/看板；V4 真实新构建验收待完成。

## 2026-09-12 子代理弹性与双向交互修订

`Docs/Features/子代理弹性预算与双向交互设计-2026-09-12.md`、ADR-087、`Docs/Reports/子代理弹性交互看板修订-2026-09-12.md`：600改为可选实例，任意合法正整数预算；系统管理生命周期；query_sub_agents共享快照、send_message主子双向/插嘴、ask_question持久等待120秒与StopRun；Web轨迹空白先修canonical事件/回放，检查器改善发现/状态/行为/操作。复用Message Fabric/AwaitHandle/Steering/Cancellation/SubAgent投影，不建平行生命周期系统；本轮仅设计/看板。


## 2026-09-12 下一阶段设计：缓存99、Memory长程自治与600轮纠偏

权威方案：`Docs/Features/PuddingAgent长程自治与缓存99优化设计-2026-09-12.md`；ADR-084/085/086；交付包与13张看板映射：`Docs/Reports/PuddingAgent-Next-Phase-2026-09-12/README.md`。重点入口为SubAgentManager/TaskExecutionPlanCompiler预算二次截断、ContextPipeline/AgentMemorySummaryContextBuilder首轮装配、SubconsciousRecallPipeline检索与后台miss、C01/C02最终请求、ToolInvocationService与legacy执行分支。预算纠偏（N00，commit f096bc5）已实施：子代理/WorkUnit 支持任意合法正整数轮次（8/32/600/1200/10000…），未填由系统 profile 决定，600 仅为 profile 默认示例；已删除 32/40/120 下压与强抬 600 双向补丁。缓存99/Memory/30日等其余新目标仍待实施/验收。


## 2026-09-12 抖音与 WebView2 续建入口

`Docs/Reports/DouyinCreatorTools-WebView2复用与看板方案-2026-09-12.md`：外部仓库 e35dbe2 源码调研及 DY-00/01/02 验收、只读适配、可靠回复拆分。现有实现入口为 `Source/PuddingBrowser.AgentTools`、`Source/PuddingHost/BrowserBridge/RemoteBrowserPage.cs`、`Source/PuddingDesktop/Browser/BrowserBridgeCommandDispatcher.cs`、`Source/PuddingBrowser.WebView2/WebView2DomClient.cs`。七工具已存在，Evaluate/CDP 等仍 Unsupported；本轮仅文档/看板更新，Douyin 业务尚待实施。

> 顶层快速索引 | 2026-09-12 | 29 项目 | .NET 10 / WPF / React / SQLite / WebView2

## 2026-09-12 GLM 进度复核与看板同步

后续运行审计入口：`Docs/Reports/PuddingAgent-Autonomy-Audit-2026-09-12/01-自主工作轨迹与自改进审计.md`及同目录`02-任务看板登记与实施顺序.md`。8心跳/12子Run/898请求；gateway与归因投影分开、token加权命中95.48%。主要源码定位：`MessageDeliveryDispatcher` recovery→`AgentInvocationDispatchFactory`的msg会话回退；`AgentExecutionService.Buffered`结构化文本工具路径与native路径审计不一致（F01 41工具仅2归档）；`TerminalProcessManager`输出/退出并发；`SubconsciousJobQueue`两表重复schedule_skip。方案为父级身份+canonical接续、HeartbeatOutcome/WorkUnit、统一工具审计、错误家族熔断与自修复/外部部署证据闭环。窗口外`c89920f`已提交C01-A、`b0cfa3a`已接Authorization入口；以下旧“WIP”是前轮时点，当前转为待独立验收与明确新构建验证。

当前入口：`Docs/Reports/PuddingAgent-GLM-Optimization-2026-09-11/06-实施进度复核与看板状态修订-2026-09-12.md`。`19ec137/9537f60/33c1489/15842ff` 已提交；原10探针转绿，前端28、Platform48、Runtime40定向通过。F01 hook恢复/失败/重试无真实消费者，S01-B invocation/attempt/provenance持久化和本地故障恢复待补；C01-A有未提交WIP待验收，C01-B/C02仍待完成。已修订7张描述、5张状态为NeedsReview；S01-A-R/T01-R源码accepted，不等于Completed或产品验收。C02保持Backlog，总卡InProgress；回执在同包`audit-evidence/2026-09-12/`。

## 2026-09-11 GLM 批次1独立审计

历史结论见 `Docs/Reports/PuddingAgent-GLM-Optimization-2026-09-11/04-批次1独立审计与下一步.md`；当时任务 ID/回执见同目录 `05-后续任务与看板回执.md`。9月11日既有91项通过、TypeScript/入口构建通过，新增10个边界探针失败；这些探针已在9月12日转绿，最新剩余工作以06为准。A01删除与编译层面通过，未做本批生产验收。

## 2026-09-11 GLM 优化批次1实现入口（原交付记录）

`Docs/Reports/PuddingAgent-GLM-Optimization-2026-09-11/` 的 15 个工作包中，批次1已有以下实现；完成状态以以上独立审计为准：

- **F01 明细水合**：新增 `Source/PuddingPlatformAdmin/src/pages/chat/runtime/detailHydrationScheduler.ts`（页面级 capacity=2 总并发、稳定请求 key、generation/owner token、AbortController、僵尸占位、401/404/transient 分类与有界退避）；`useTurnSurfaceStore` 改为订阅衔接，`registerVisibleTurn` 配对 `unregisterVisibleTurn`（引用计数），MessageRow→MessageList→ChatMain→ChatLayout→index 全链路透传 `onTurnInvisible`；MessageRow 视口观察不再首次相交即 disconnect。测试：`detailHydrationScheduler.test.ts` 7/7、`turnSurfaceStore.hydration.test.ts` 7/7（含 8 可见×20 重渲染并发≤2、A→B→A、迟到回调、离视口剪枝、401 停止、unmount）、`MessageRow.focus.test.tsx` 9/9。
- **S01-A usage 并发**：`TokenUsageRecorder.RecordCoreAsync` 改为 BEGIN IMMEDIATE 单写事务（明细幂等读 + 月度聚合读改写同事务，decimal 语义不变；同 source 异 payload 抛冲突，best-effort 路径吞掉），新增 `Services/UsageWriteConflict.cs` 唯一索引兜底；`LlmGatewayUsageRecorder` SaveChanges 捕获唯一冲突按幂等成功。顺手收尾了 dirty 树遗留的 `occurredAtUtc` 半成品重构（该文件此前无法编译）。测试：`TokenUsageRecorderConcurrencyTests` 5/5（50 并发对齐、重复 source 计一次、冲突拒绝、best-effort 跳过），usage 相关回归 39/39。
- **T01 记忆工具**：`SaveMemoryTool` important 分支身份改由 `ToolExecutionContext.AgentInstanceId` 派生（原 root 读取的 agent_instance_id 不可达），upsert 增加 preference 必 key / fact 必 content 前置校验（空 content 由旧“警告后照写”改为 fail-closed 零写入，`MemoryToolsTests` 对应用例同步更新）。测试：`SaveMemoryToolContractTests` + MemoryToolsTests 共 27/27。
- **A01 组合根**：删除 `Source/PuddingAgent/Services/` 两个旧服务注册副本（msbuild Compile 由 3 项减至 Program.cs 1 项，入口构建 0 错误）；`Docs/架构.md` 开头标注 Desktop + Core 当前形态、单进程 P2P 叙述移为历史背景；PuddingHost.Tests 组合守卫通过，Desktop 不引用 Host。

以上为首批历史记录。批次2+最新状态以同目录06为准：S01-B首片已提交、C01-A WIP待验收，其余未验收工作仍沿01/02设计推进。

## 项目定位

Pudding — Windows 桌面智能助手。ASP.NET Core 是 Desktop 子进程，Console 仅开发入口。详见 `Agents.md`。

## 架构文档

| 文档 | 主题 |
|------|------|
| `Docs/Reports/GLM前端首批交互审计与下一步-2026-09-11.md` | GLM 首批 Chat 交互独立审计；草稿、重复 Steering、排队目标、图片门禁、受理恢复、停止与队列回执共 7 个契约反例；needs_changes，附看板所有权与产品验收门禁 |
| `Docs/Reports/前端交互体验优化建议-2026-09-11.md` | 当前前端交互评估；发送/排队/停止一致性、可信反馈、阅读与草稿连续性、导航/交付物衔接和视觉规则；第一批（鼠标排队/补充当前任务/独立停止+服务端取消接线/失败保留草稿/操作回执/键盘可达）已于 2026-09-11 实施，第二批及以后仍为 Proposed |
| `Docs/Reports/PuddingAgent-GLM-Optimization-2026-09-11/01-代码审阅与优化设计.md` | 原始15包设计和hash基线；同目录06为最新进度复核与看板状态，03–05保留各阶段历史；先补F01/S01-B，验收C01-A后推进C01-B/C02 |
| `README.md` / `README_zh-CN.md` | 中英文产品与目标架构入口；Windows Desktop/Core 产品边界、Plugin/Function/Hook/Event/Projection 五类合同、Agent FSM、函数图编排、前端思想、现状缺口与路线 |
| `Docs/Features/工作区TODO与峰谷节能任务编排设计方案.md` | 工作区 TODO 台账、Agent 认领/拒绝/回报、durable 自动派发与定时消息、可信 idle、心跳 0、峰谷 WorkAdmissionFence，以及 Hook 触发的临时质询子代理、GoalRun 有界循环、manifest/Admin 模型路由、防无限循环熔断和公共 Plugin/Function/Event/Projection 映射 |
| `Docs/Features/Goal持久目标自主续行与自动压缩完整设计方案.md` | `/goal` 完整专项设计；统一 Web/Desktop/Connector 命令、持久 GoalRun、事件驱动 continuation、256 个外层 Goal Iteration、证据 Verifier、用户抢占、重启停用、自动压缩和 Task-bound Goal；明确不依赖 Heartbeat |
| `Docs/Features/TaskBoundGoal与Agent状态感知自动派发代码级施工计划.md` | 低峰自动执行施工图；2026-08-29 已由 Desktop 构建/加载新程序集并完成真实自动派发 smoke：同步后代 Token 预算传播、Task-bound Failed 释放和 stale Message target 淘汰已验证；warm cache 97.31%（DeepSeek 98.78%）仍低于 >99%，事件驱动 intent、Goal 成本/后代工具归因、AwaitHandle/checkpoint、动态模型反馈与 7 夜验收仍未完成 |
| `Docs/Features/Scheduler夜间有效调度与Execution生命周期闭环代码级实施方案.md` | 2026-08-31 夜间调度复盘的代码级收口方案；把有效调度冻结为 Intent→Decision/Outcome→Assignment/Reservation→TaskGoalBinding/GoalRun→ExecutionRun→Verifier/Task terminal，并细化 legacy false-busy 修复、事件 Intent 可靠结算、staged mode、scan-run 持久化、Blocked 恢复 UI、Runtime 预算与无人工 smoke 的实施顺序和验收门禁 |
| `Docs/deepseek-reference-architecture-master-plan-2026-08-14.md` | 本次会话的 deepseek-harness/pi 参考架构总蓝图；以“一切业务能力皆插件”为第一原则，覆盖 Model/Tool/Skill/Session/Agent Loop/Sandbox/Storage/Schedule/UI、统一运行事实、文件级改造矩阵、任务图与 T00-T16 施工步骤 |
| `Docs/07架构/67ADR-066*.md` | Browser 能力与 Douyin 分层决策 |
| `Docs/07架构/68*.md` | WebView2 自动化分阶段实施规格 |
| `Docs/07架构/69*.md` | Desktop 浏览器工作区/运行中心/存储 |
| `Docs/07架构/70–73*.md` | Phase 2A-1 Bridge 与双标签工作区（✅） |
| `Docs/07架构/74*.md` | Phase 2A-2 Remote Browser + Agent Tools（✅） |
| `Docs/07架构/75–76*.md` | Phase 2A-3 Snapshot/Locator/Interact/Wait（✅） |
| `Docs/07架构/77–79*.md` | Phase 2A-3B/C DeepSeek 验收与闭环 |
| `Docs/07架构/80ADR-069*.md` | MOA 子代理设计委员会编排核心；Phase 1–3 计划编译、纯状态机与运行时适配 |
| `Docs/07架构/81ADR-070*.md` | 通用 Agent 编排图；V2 组件/多模态端口、SQLite 事实、Graph/Run 发现、Revision/Layout 双 CAS、replay-to-live SSE，以及 React Flow 节点/端口/Edge/Graph Input 编辑器 |
| `Docs/07架构/82ADR-071*.md` | 通用 Agent 编排平台完整目标设计；JSON 图、Revision/Layout/Deployment/Run 事实边界、Agent/Tool/Graph 统一 Function、不可变图生成流程、有界循环、多模态、Agent 工具与 MOA 统一 |
| `Docs/07架构/83*.md` | 后端执行内核与 Control Plane 施工图；契约、SQLite、API、状态转换、Function Runtime/Invoker、Typed Hook Pipeline、Parent/Child Run、Outbox、Scheduler、Trigger 与权限 |
| `Docs/07架构/84*.md` | Admin 蓝图编辑器和组件系统施工图；Node/Edge/Input/Trigger、Revision/Deployment/Run、多模态 UX、Pudding 视觉语言、原因优先状态、Function Catalog、插件 Presentation 与系统构成检查器 |
| `Docs/07架构/85*.md` | 分期交付、测试、安全、性能、Desktop 部署、浏览器 smoke、恢复与验收证据图册 |
| `Docs/07架构/86ADR-072*.md` | 工作区 TODO 第一阶段任务领域 ADR；覆盖五列 Board、Task Failed/Reopen、Task Ledger、手工/Auto 派发、受限 Cron/Message Event、Agent Availability、Task executionWindow 与 provider/model 价格时段 Resolver；完整 Auto 受 Goal 前置约束，不新增 `work-policy.json` |
| `Docs/07架构/87ADR-073*.md` | 当前产品施工总表与冲突裁决基线；列出 30 项产品任务、17 项 T00–T16 平台底座任务及专项 Phase 去重映射，覆盖目标、优先级、工作量、难度、依赖、设计位置和里程碑 |
| `Docs/07架构/89ADR-074*.md` | Goal 专项架构决策；冻结外层 GoalRun/内层 Agent Loop 双层预算、256 accepted Iteration、durable outbox、证据验证、Task-bound Goal、Availability 与低峰派发；2026-08-26 G2/G3 和 Task-bound authoritative 源码链已落但默认关闭，真实低峰/完整 Verifier/Admin/进程外门禁未通过，ADR 仍为 Proposed |
| `Docs/07架构/90ADR-075*.md` / `Docs/Features/第三方任务看板AccessToken与外部API详细设计方案.md` | 第三方任务看板开发合同；冻结 hashed opaque Access Token、ASP.NET Core 独立 scheme + scope/workspace Policy、外部 API v1、ETag/幂等、追加式 TaskEvaluation 与 Admin Access Token 管理器；P1（Token 后端）+ P3（Admin UI）+ P2 基本功能已实现：`pdt_v1_` opaque Token 摘要存储、PuddingExternalAccessToken scheme、Admin 管理 API/UI、last-used 合并写、External Task API v1（list/get/create/patch/comments/evaluations/commands + ETag/428/412 + 简化幂等）共 65 项后端测试；SSE Watch/RateLimiter/OpenAPI 与 P4（部署收口）未实现，External API 默认关闭 |
| `Docs/07架构/96ADR-082*.md` / `Docs/Features/Pudding外部工作空间Agent消息API设计与使用说明.md` | External API v1 的 Workspace/Agent/消息扩展；新增 `workspaces.read`、`agents.read`、`messages.send`，安全目录投影、`canonical_turn` Message Fabric ingress、幂等 `202 + Location` 和 Token-owned execution receipt；明确 Delivery accepted 不等于 Agent terminal。External/Token 7/7 + Dispatcher 2/2 聚焦测试与 Desktop Loopback 真实模型 smoke 已通过；非 Loopback HTTPS、RateLimiter/OpenAPI/P4 运维收口待完成 |
| `Docs/07架构/97ADR-083*.md` / `Docs/Features/Agent系统预制模板完整快照与DeepSeek鲸鱼娘模板设计方案.md` | Agent 系统预制模板 v2 目标设计；目录包、完整 Creation Snapshot、版本/内容哈希/许可来源、显式导入升级与 drift 保护，Workspace 创建时选择模板即原子填充全部六组配置；重写通用助手并新增原创文本的 `deepseek-whalechan` 社区角色预制，既有 Agent 不被模板更新反向覆盖。当前仅设计完成，未实施或产品验收 |
| `Docs/07架构/91ADR-076*.md` / `Docs/Features/遥测调试数据自动过期与Web存储管理设计方案.md` | 遥测/Debug 存储治理设计 + 首轮实现（Phase 0–3 已落地：语义目录/快照估算/单 writer 协调器/语义 API/Web /storage 页面；Phase 4 生产验收待做）；上下文日聚合复用既有 `context_layer_daily_rollups`、retention 索引收编目录所有权、旧 /databases 端点与 Desktop 旧页面捆绑退役、appsettings Retention 节已迁移 system.json |
| `Docs/07架构/92ADR-077*.md` | 原生视觉基础：typed parts、Workspace Artifact、Files API、多轮恢复；V0–V3 已有实现，V4 真实新构建验收待做。后续 ADR-088 收敛 Reader/能力/图片预算/流式传输并补齐 Web/Desktop 截图，自动 helper 移除及显式通用子代理第二意见仍待实施 |
| `Docs/Features/Chat图片消息回放与前端旧Bundle缓存修复方案.md` | 2026-08-26 Chat 图片占位事故的可施工修复方案；冻结 Agent-first `contentParts` 透传、typed parts 优先兼容、localhost 旧 Service Worker 清理、入口/哈希资源缓存合同、build identity 和两段式产品验收；关联 P1 Task `ceba781342aa4353901654d1897092cb`，尚未实施 |
| `Docs/Features/子代理活动轨迹实时回放与运行检查器修复方案.md` | 2026-08-26 子代理检查器空时间线事故的证据化施工方案；活动 Run 继续零 archive 轮询，改由 Conversation SSE + active-subagent gap replay + 可对账状态水位恢复；修正有界工具详情导致的聚合少计、增加轨迹同步降级与 build identity 门禁；关联 P1 Task `791d062fa6ea44f18bfe5027a37696d0`，尚未实施 |
| `Docs/Reports/Core与DesktopCPU占用现场采样-2026-09-19.md` | CPU 只读采样：Core 均值 0.169%，Desktop 均值 2.727%；Desktop 热点线程映射 WPF 图形模块，Core 截图峰值未复现，待高占用窗口原生/托管联合采样 |
| `Docs/Reports/小型布局子代理耗时与进度失真诊断-2026-09-19.md` | 26 分 31 秒布局 Run 的只读诊断：83 次模型调用、6 次 Jest 启动、宽松委派预算；531 条 canonical 事件已落库但截图仍为启动/零指标；区分执行低效与活动投影缺失，未修改产品代码 |
| `Docs/07架构/tool-infrastructure-layering.md` | Tool 分层、强制委派合同、Smart 参数与结果合同 |
| `Docs/deepseek-harness-message-card-alignment-2026-08-14.md` | 对照 deepseek-harness 的消息、推理和工具调用 UI 目标架构；定义 TurnStatus、Reasoning/Tool/Delegation 行、toolCallId 投影、分期与验收矩阵 |
| `Docs/chat-ui-behavior-chain-quality-upgrade-2026-08-23.md` | 聊天前端「行为链 + 质感」升级：harness 质感纪律与 Hermes/业界 12 原则调研、四档灰阶 token、交错时间线（路径 A/B 统一 ViewModel）、五类 presentation renderer 设计与实施记录 |
| `Docs/Features/Agent消息交错内容流与最新行为组披露完整实施方案.md` | Flash 代码级施工合同：canonical sequence、TextBlock ⇄ ActivityGroup、会话级唯一最新披露 owner、完整 reasoning、工具详情懒加载、柔和收起/卸载、逐文件任务卡、测试命令和双阶段验收 |
| `Docs/07架构/93ADR-079Agent消息交错内容流与最新行为组披露ADR.md` | 冻结 AgentTurnCard 单一有序内容流与唯一正文源；当前最新 Agent 回合的最后行为组持续展开，最终正文不关闭，新行为/新回合转移 owner 并柔和收起旧组 |
| `Docs/07架构/94ADR-080任务看板分层读取子任务与命令化拖拽ADR.md` / `Docs/Features/任务看板状态机子任务渐进披露与高性能拖拽优化设计方案.md` | 任务看板下一阶段 Proposed 设计：Ready 证据化直达 Completed、单层独立状态子任务、普通 List/工具仅 id+title、Index/Card/Detail 三层投影、评论/备注分型、命令化拖拽、global-cursor Watch 修复与 10k 任务性能门禁；尚未实现或验收 |
| `Docs/07架构/95ADR-081AgentHarness兼容边界与工具协议适配ADR.md` / `Docs/Features/AgentHarness兼容与工具调用效率修复设计方案.md` | 模型后训练 Harness 适配；canonical 工具保持唯一，`rg/exec_command/write_stdin/apply_patch/pwsh` 在统一门禁前归一化，WSL 作为显式 Unix 通道，搜索 no-match 与真实失败分离，完整普通文本五段报告同轮收口；`BuiltInAgentTemplates` 单一权威，Low 投影保留读取/搜索/`search_tools`；动态定义在下一 LLM round 单调生效，连续 8 次 discovery-only 触发 `tool_discovery_stalled`；Token 归因使用 RuntimeExecutionIdentity；聚合报表和部署 smoke 待完成 |
| `Docs/Features/Chat独立插嘴按钮与当前Turn即时Steering设计方案.md` / `Docs/superpowers/specs/2026-06-06-runtime-steering-queue-design.md` | current-Turn Steering + 无人值守队列合同：普通消息立即进 canonical Turn；队列只含未认领 delivery/Turn，认领后由消息卡与轨迹接管；Agent/heartbeat delivery 受理为 canonical Turn；独立 `⚡` 复用 Steering admission。源码已实施，产品进程重启/smoke 待做 |
| `Docs/deepseek-harness-tool-system-alignment-2026-08-14.md` | 对照 deepseek-harness 的工具定义与执行协议；规划 canonical output、端到端 callId、结构化错误、管线、并发、spill、可回放 presentation 与 DeepSeek Code Mode |
| `Docs/deepseek-harness-pi-plugin-hook-event-architecture-2026-08-14.md` | 对照 deepseek-harness 与 pi 的统一目标架构与 2026-08-15 复评；定义 Plugin/Function/Hook/Event/Projection、Agent Transition+Effect FSM、Function Graph、Composition Snapshot、前端解释层、底座缺口与分期路线 |
| `Docs/Features/上下文Token效率缓存命中与分级压缩优化设计方案.md` | 7 日 Token 构成、工具结果重放、搜索失败和 ZIP 稀疏度基线；2026-08-28 登记 95.92% 事故基线、Harness 对齐的 warm-prefix checkpoint、prefix-v2 历史锚点、LLM purpose 分桶、审批控制面降耗，以及 DeepSeek 连续 7 日严格 >99% 验收 |
| `Docs/Features/服务商余额查询与多服务商计费适配器设计方案.md` | 聊天页主代理余额徽标 + 前后端双注册表计费抽象：后端 `ILlmBalanceProvider` 查询适配器（DeepSeek `/user/balance` 首个落地，`/v1` 剥离修复）+ 前端 `providerBilling.ts` 展示适配器；新服务商扩展步骤、5min 低频轮询/手动刷新、apiKey 不进日志约束 |
| `Docs/QA/QA-2026-08-03*.md` | Qwen 输入上限修复验收 |
| `Agents.md` | 仓库级开发约束 |
| `Directory.Build.props` | 全项目通用 .NET 构建属性；SDK 默认项统一排除项目内 `temp/**`、`tmp/**`，避免测试/构建输出递归自复制并触发 Windows 超长路径评估失败 |
| `dev-up.py` | 本地开发监督器；Codex MCP 子进程必须通过 5100 TCP readiness 后才启动 Backend，避免 MCP workspace reconciliation 启动竞态 |
| `How-Debuge.md` | 诊断路径 |

## 顶层目录

| 项目 | 说明 | 详细索引 |
|------|------|----------|
| `Source/PuddingAgent/` | 🔑 入口 (Program.cs · Console/DesktopChild 薄壳) | [code_map](Source/PuddingAgent/code_map.md) |
| `Source/PuddingRuntime/` | 🔑 Agent Loop · LLM · 工具 · 上下文管线；压缩与冷水合共享 canonical ChatMessages 增量同步门禁 | [code_map](Source/PuddingRuntime/code_map.md) |
| `Source/PuddingDesktop/` | 🔑 WPF Launcher · 固定端口 Core 子进程 · 回环鉴权控制面（Core/前端制品加载、重启、诊断）· Core 点火事务部署/程序集哈希验收 · Browser 工作区 · 调试模式与运行中心 · 客户端精灵源素材 | [code_map](Source/PuddingDesktop/code_map.md) |
| `Source/PuddingHost/` | 🔑 组合根 · 全网卡 HTTP/本机控制地址 · Browser Bridge · 飞书连接器 | [code_map](Source/PuddingHost/code_map.md) |
| `Source/PuddingCore/` | 🔑 抽象与契约 · 接口 · 模型 | [code_map](Source/PuddingCore/code_map.md) |
| `Source/PuddingPlatform/` | 🔑 Session · API（含认证/当前用户投影）· EF Core · 消息网关 | [code_map](Source/PuddingPlatform/code_map.md) |
| `Source/PuddingMemoryEngine/` | 🔑 Library/Book/Chapter · FTS5 · 潜意识 | [code_map](Source/PuddingMemoryEngine/code_map.md) |
| `Source/PuddingGateway/` | LLM 网关适配 | [code_map](Source/PuddingGateway/code_map.md) |
| `Source/PuddingController/` | 代理控制层 | [code_map](Source/PuddingController/code_map.md) |
| `Source/PuddingCodexService/` | Codex MCP Sidecar | [code_map](Source/PuddingCodexService/code_map.md) |
| `Source/PuddingBrowser.AgentTools/` | 七项 Browser Agent Tools | [code_map](Source/PuddingBrowser.AgentTools/code_map.md) |
| `Source/PuddingBrowser.Abstractions/` | Browser 契约 | [code_map](Source/PuddingBrowser.Abstractions/code_map.md) |
| `Source/PuddingBrowser.WebView2/` | WebView2 Driver | [code_map](Source/PuddingBrowser.WebView2/code_map.md) |
| `Source/PuddingBrowser.Protocol/` | Bridge 线协议（8 .cs） | [code_map](Source/PuddingBrowser.Protocol/code_map.md) |
| `Source/PuddingCodeIntelligence/` | 代码索引/分析 | [code_map](Source/PuddingCodeIntelligence/code_map.md) |
| `Source/PuddingCodeIndexer.Cli/` | 代码索引 CLI | [code_map](Source/PuddingCodeIndexer.Cli/code_map.md) |
| `Source/PuddingFullTextIndex/` | 全文索引引擎 | [code_map](Source/PuddingFullTextIndex/code_map.md) |
| `Source/PuddingGit.Tools/` | Git 20 工具（实现在 Runtime） | [code_map](Source/PuddingGit.Tools/code_map.md) |
| `Source/PuddingPlatformAdmin/` | React 管理前端 · Chat 虚拟视口/渐进消息/状态缓存 · Agent 编排布局编辑器 · 管理壳异步隔离 · 主代理服务商余额徽标（DeepSeek 首个，多服务商计费展示适配器） · 已移除 Phaser/2D Studio · 生产 dist 经 PuddingHostContent.props 部署到 Core `wwwroot/admin`（dev 输出分流 dist-dev，防 MSBuild 增量清理破坏部署，见 How-Debuge §6.12） | [code_map](Source/PuddingPlatformAdmin/code_map.md) |
| `Source/PuddingTaskRecall.Cli/` | 历史脏数据一次性诊断/修复 CLI（默认 dry-run；`--apply` 才写库，写前备份 + 单事务回滚） | [code_map](Source/PuddingTaskRecall.Cli/code_map.md) |

## 调用链路

```
Agent Loop → search_tools → Browser Tools (PuddingBrowser.AgentTools)
  → dispatch 冻结授权 catalog/schema；search_tools 结果只在下一 LLM round 单调增加 visible definitions
  → 连续 8 次 discovery-only（查询换词也同族）→ tool_discovery_stalled，禁止高缓存命中零 Goodput 空转
  → IBrowserRuntime → RemoteBrowserRuntime (Host/BrowserBridge/)
    → WebSocket → DesktopBrowserBridgeClient (Desktop/Browser/)
      → WebView2 (PuddingBrowser.WebView2)

Agent Loop → LlmInvocationService → DirectLlmClient
  → model.protocol=openai → OpenAiLlmGateway (/chat/completions)
  → model.protocol=responses → ResponsesLlmGateway (/responses；DeepSeek reasoning_text + incomplete/length 终态兼容)
  → model.protocol=anthropic → AnthropicMessagesLlmGateway (/messages)
  → Provider 不保存协议；同一 Provider 的模型可分别选择三种协议
  → Provider usage → ILlmGatewayUsageRecorder → llm_gateway_usage_events
    → StatsApiController（月度/趋势本地计费口径）
    → TokenUsageDailyAggregateService / ContextLayerDailyRollupService
      （闭日 UTC 聚合缓存 llm_usage_daily_aggregates / context_layer_daily_rollups +
        stats_daily_cache_days 完成标记；当天实时计算，Rebuild 后按月失效）
    → TokenUsageEvents 继续只承担会话/角色/上下文归因

TaskAutoDispatchWorker（5min bounded authoritative；MaxStartsPerScan=2）
  → AgentAvailabilityProjectionStore（每轮先刷新全部 Agent；即使候选为 0 也输出 idle/busy/unknown）
  → BacklogRefinementEvaluator/Store（显式 autoDispatchEnabled；结构化准入后 CAS Backlog→Ready）
  → TaskAutoDispatchEvaluator（Ready/Deferred；结构化类型/能力/模型路由 + Availability/Window）
  → ProviderModelExecutionWindowResolver（llm.providers.json 版本化价格窗口；未知 fail closed）
  → TaskExecutionPlanCompiler（结构化 Task → 版本化 WorkUnit DAG + budget/scope/dependency SHA-256）
  → TaskExecutionTracker（active Binding；Task→Plan/WorkUnit→Assignment→Reservation→Goal→Iteration→Execution→Outbox 五态跟踪）
  → GoalContinuationWorker / ConversationAcceptanceStore（canonical plan/node/fingerprint + reservation 二次围栏；首个 WorkUnit 原子 Running）
  → ExecutionCommandReader / ExecutionRunCoordinator（执行前重读 Command→Goal→Binding→Plan/Node；Agent 与 WorkUnit rounds/tools/duration 取更严格值）
  → 每 Agent 每轮最多一个、全局最多两个；任何 promotion/start/repair 都必须经唯一 CAS/fencing 写入者

Admin ChatMain 余额徽标 → useProviderBalance (5min 轮询/手动刷新)
  → GET /api/llm/providers/{id}/balance → LlmProviderApiController.GetBalance
    → LlmProviderFileService.GetBalanceAsync（解析 apiKey：ApiKey/${ENV}/{{vault}}/ApiKeyRef）
      → ILlmBalanceProvider 注册表 CanHandle 分发（DeepSeek: {baseUrl 去 /v1}/user/balance）
      → 未注册适配器 → IsAvailable=false「暂不支持」DTO（前端隐藏/显示 —）

ContextPipeline → Tool layer mandatory delegation policy
  → 首次工具调用前必须判定 Direct / Delegated
  → 复杂任务前三次工具调用内必须进入匹配 smart_* 或 spawn_sub_agent
  → SmartWorkflowToolBase 将历史 question/what/query 仅在执行边界归一为 task
  → smart_explore 统一替代已退役的 smart_search / smart_query_session_log

Terminal 长命令能耗协议（2026-08-22）
  → terminal_wait 阻塞语义：等到任务退出或输出超过预览上限才返回，wait_seconds 0-600 默认 60
  → 工具描述/NextAction/ToolLoopInstruction/Smart 提示词统一引导"一次阻塞等待"，禁止 1-2 秒式轮询
  → 动机：旧"出现新输出即返回"语义在全库产生 6,040 个纯轮询轮 ≈ 8.26 亿 tokens（16.3%）

上下文注入冗余治理（2026-08-22，指令层曾占每次调用 67%）
  → 工具描述单语化：41 文件去除英文复述（-13K 字符）；使用教学入 skill 文档，schema 只留必要说明
  → search_tools 装载收紧：默认 3/上限 8（原 8/20），阻止长会话工具集棘轮到 50+（主会话曾 34.6K schema tokens/轮）
  → L1-TOOLS 索引补延迟工具名清单（仅 id 无 schema），Agent 不再盲搜 search_tools
  → L2-SKILLS 索引行压缩：skillId + 首句摘要(≤100字) + tags≤4/keywords≤6，去掉 Name/版本/path；
    主 Agent 57 技能的索引从 29K 字符/轮显著缩减，全文仍由 agent_skill 渐进加载
  → 前缀稳定性结论：分层排序已正确（稳→动）；L9-INBOUND/L6-AGENT-LOG-RECALL 变化属尾部动态层，
    缓存损伤被限制在其自身与 <1K 尾巴，无需整改

缓存命中率冲刺（2026-08-25，基线 8/22-24 DeepSeek 96.783% → 目标 >99%，设计方案 §1.2）
  → 有界冷启动重组：ContextCompactionOptions.MaxHydrationTokenBudget(49152,0=禁用) 钳制重水合
    预算（DB/JSONL 双路径）；摘要链优先占预算，JSONL 胜出时补拼（原会静默丢摘要）
  → 滚动摘要链：压缩候选纳入旧代 compact_summary，新摘要统一标记 CompactedBy；
    CompactionCoverageFilter 改全部 manifest 并集（修多代 JSONL 复活缺口）
  → 转录连续性：每次压缩和 memory DB 冷水合前从 platform ChatMessages 按稳定 MessageId + durable platform Id
    高水位 after-Id 升序分页（256 条）镜像当前 session 到 memory Messages，不全扫会话/既有 ID；同步失败时水合 fail-closed，
    当前 turn/message 从历史水合排除，pre-projection live 历史不被 DB 覆盖；压缩候选包含当前围栏 Turn 或最后一个未围栏 user 时，
    在摘要和 DB 写入前以 current_turn_in_compaction_scope fail-closed；自动压缩后只合并完整 hash 围栏的当前 live Turn
    （禁止 active.Count==0 门禁）；summary-only（含超大旧摘要）/无可压缩原文直接 no-op，
    result/diagnostics compacted count 均为 0；补读排除 CompactedBy!=null 原文（阻止失忆/套娃）
  → 绝对窗口 proactive 压缩：MaxActiveRawTokenBudget(131072) 与 0.65 比例 OR 触发
    （大窗口模型旧阈值 16 万 token 才压缩）；Streaming 路径补齐轮内软压缩
  → 前缀字节稳定：ToolLoopInstruction 可见集清单（原全注册表）；ContextAssemblyService
    首组装透传 LoadedToolIds/Capability（灭 turn1→turn2 必变）；L0-AGENTS-ROSTER session 冻结
  → 归因卫生：vision-helper:/subconscious: sessionId 命名空间；image_reader 委派稳定 system 前缀
    + (artifact,prompt) 观察缓存；ConversationProjector usage 指纹查重（灭双计 NULL 桶）；
    session_rehydrated 显式归因；会话默认驻留 1h→4h
  → 验收：TestScripts/deepseek-cache-e2e.py 保持任务/模型/工具不变执行双轮真实 DeepSeek 探针，TestScripts/deepseek-cache-hitrate.py 生成日报；连续 7 完整自然日 >99%（§15.3）

自动权限审查（唯一任务 e187a8bbd2d640bb87b96fd3cf548966；ce63f8c0 已合并）
  → ToolApprovalCommandFirewall：引号/括号感知解析 PowerShell/POSIX pipeline、正则 pipe、2>&1、变量赋值；
    已知只读/构建/测试命令秒放，危险命令秒拒，绝对输出/调用运算符/子表达式等未知形态继续 LLM 审批；
    provider usage 以 purpose=approval 独立计费归因
  → feature/auto-approval-v2 的 e716829 已实现 Gate1 静态分级 / Gate2 事实自检 / Gate3 单次 LLM，62/62 测试通过，但尚未合入/部署且 AgentFirewall 仍传 Evidence=null
  → 参数级风险：save_memory get=L0、upsert/set_important=L1、delete=L2；风险事实由 descriptor+实际参数+系统证据派生，Agent 不能用 may_damage_or_delete_data=false 降级
  → 用户审批降为最后手段：L0/L1 无感放行，StaticDeny 不可覆盖，Challenge 只反馈 Agent，仅 HumanRequired 弹一次审批；相同 args/evidence 重复拒绝触发 approval_loop_detected
  → 配置由程序默认 + <DataRoot>/config/system.json ToolReview 覆盖，review profile 走 llm_resource_pool；分阶段部署/启用/下线旧 audit-agent 与默认工单入口
  → 权威设计：Docs/superpowers/specs/2026-06-03-auto-tool-approval-design.md

工具模型倾向适配（2026-08-22，实测子代理调用链驱动）
  → shell 输出去 ANSI：pwsh 注入 $PSStyle.OutputRendering='PlainText' + NO_COLOR=1 + 输出侧正则剥离兜底
  → 探查命令返回值教育：Get-ChildItem/Select-String/Get-Content 等成功输出尾附专用工具提示（94.8% shell 曾是探查类）
  → Codex 补丁格式自动转译：UnifiedDiffParser 识别 *** Begin Patch 并转 unified diff（内容匹配定位，行号占位安全）
  → file_read 护栏窗口 120→400 行（小文件与大文件双路径），减少同文件翻页重读（实测同文件重读 8 次）

ContextPipeline → stable system prefix + volatile User tail
  → 当前消息、日期、召回与 inbound context 不再插入 system prompt
  → AgentExecutionService 用 `[CURRENT USER TURN input_sha256=…]` 围住本轮文本与 typed ContentParts；若预算裁剪/投影后围栏缺失，Buffered/Streaming 在 provider 调用前 fail-closed
  → AgentExecutionService → ToolResultContextPolicy（模型历史最多 8 KiB；原始完整结果写入工作区 `.pudding/context-tool-results`，不做模型输入脱敏）
  → search_tools 已发现 schema 在 live session 内保持加载，避免跨 dispatch 重复收缩/扩张

用户 Turn → TurnExecutorAdapter → AgentExecutionAdmissionCoordinator（foreground）
  → 抢占同 workspace/agent 的 Message Fabric 后台执行（含 subagent_result）
  → MessageDeliveryDispatcher 取消旧执行并把 exact delivery 立即 defer 回队列
  → foreground demand 存续期间 recovery/idle drain 不领取后台 delivery
  → MessageFabricStore 依据 wake event deliveryId 精确 claim，避免旧队首抢在用户事件前执行

Agent `send_message` → MessageDeliveryPolicy → Message Fabric 反风暴消费
  → 默认 `inform/report_result` 为 `notify`：不创建 Turn、不调用模型；`ask/request_review/delegate` 才为 `execute`
  → `agent_reply` 永远被动，ConversationReplyProjectionWorker 最多投影一次，切断 A→B→A 自动回声
  → MessageDeliveryDispatcher 按 workspace/Agent 跨 room 原子领取最多 20 条 notify，ConversationNotificationStore 逐条原子写 ChatMessage + message.created 后 ACK
  → 合并 claim 不合并内容/因果链/UI 卡片；execute 仍一条 delivery 对应一个 canonical Turn
  → `message_deliveries.handling_mode` + bootstrap/migration 回填历史普通 inform/report_result/agent_reply

P1-2 召回同源去重（压缩摘要/原文/recall 片段 ≤1 次注入）
  → SessionChunkIndexer（写侧）回查 Messages 补齐 CanonicalContentHash/ContextGeneration 冗余列
  → MemoryLibrary 第 5 路 LEFT JOIN Messages 取 hash/generation/CompactedBy，默认过滤 covered chunk
  → RecalledMemory/SearchHit 透传 SourceMessageId + CanonicalContentHash
  → SubconsciousRecallPipeline 注入前经 CompactionCoverageFilter 过滤 covered + 同轮 hash 去重
  → ContextPipeline assembler 兜底去重（双保险）

P1-3 Reasoning 紧凑归档（v2 sidecar + ThinkingJson 不回流）
  → ReasoningCompactCodec（PuddingCore）：{v:2,text,chunks:[{o,t}],hash} UTF-8 字节偏移 + delta 时间戳 + SHA-256，旧格式兼容、hash fail-open
  → MessageDeliveryDispatcher 写侧：thinking 帧累积 → ReasoningCompactCodec.Encode 落 v2（T2）
  → MessageApiController / AgentConversationProjectionService 读侧：codec 双格式解码（T3）
  → JSONL/Compaction 路径断言：ThinkingJson 不进模型 prompt / compact 输入（T5）
  → E2E：写侧 v2 → 读侧解码 → UI DTO 逐字节还原 + hash 校验（T6）

Plugin configuration → Plugin Resolver → PluginActivation
  → capability registry（Tool/LLM/Prompt/Context/Connector/Job/Presentation）
  → Typed Hook（Guard/Transform/Around，同步有界干预）
  → state commit + transactional outbox → durable DomainEventLog
  → per-consumer checkpoint/retry/dead-letter → UI projection / Heartbeat / Subconscious / Self-learning
  → Session/Run/Turn/LLM/Tool/SubAgent/Message/Compaction/Heartbeat/Job/Learning 使用统一状态机与提交后事件

spawn_sub_agent → SubAgentInvocationService → SubAgentManager
  → model 参数必须是 providerId/modelId 完整路由；裸 modelId 多 provider 注册时 FileLlmResolver 报
    "exists under multiple providers"（2026-08-24 起 list_llm_providers 内置工具输出实时路由表与
    ambiguous_model_ids 歧义清单，不含 apiKey/baseUrl，已入 CoreToolIds 常驻可见；应急快照
    memory/llm-providers-cheatsheet.md 转兜底）
    → `runtime.execution.json` 提供系统 profile（默认 600/2400/24h，非强制统一值）
  → 内部契约 SubAgentSpawnRequest 已含 `int? MaxRounds` 等请求级预算字段（N00 已实施）
  → 父代理工具 schema 面向可选 `max_rounds` 的暴露随 ADR-087 后续批次（SA-MSG/SA-ASK）落地
  → AgentExecutionService 在启动、剩余 80%/50% 与预算耗尽时注入预算通知
  → 正常轮次/时间耗尽后提供 20 轮、最多 30 分钟的收尾宽限，终态为可续跑 `budget_exhausted`
  → `resume_sub_agent_id` 复用 SubSessionId/上下文、创建新 runId 并重置系统计数器
  → run archive 固化实际预算与 `subagent.budget.notice`
  → 子代理轮内 warm-prefix checkpoint（2026-08-28）：估算达 0.65×有效输入上限时以原样
    system/tools/history + 固定尾部指令生成摘要，只有有效且缩小的 checkpoint 才原子替换旧区间；
    失败保留完整 history、每 dispatch 最多尝试一次；写 subagent.context.compacted 事件，
    LlmRequestBudgetGuard 硬悬崖保留为最后防线
  → FileSubAgentRunStore 归档并发协议（ADR-060 §3.11）：读写同一 per-run gate、读方 FileShare.ReadWrite、
    JSONL 追加 sharing violation 退避重试；重试耗尽丢弃事件写 archive-degraded.json 降级，不杀死运行
  → FirewallContext.WorkingDirectory 从 ToolExecutionContext 冻结；防火墙 WorkspaceGate、审批目标解析
    与文件工具统一委派执行根（worktree），不回退进程级静态 workspace root
  → ContextPipeline 以 ConfigurationAgentInstanceId 读取持久 Skill/人格/记忆，缺失 Skill 索引不写盘
  → SubAgentTransientDirectoryGcService 只隔离终态/孤儿的精确空脚手架，运行归档与有状态目录不进入 GC

Runtime 跨层服务 → Core contracts → Platform implementations
  → SubAgentTool → ISubAgentPool → SubAgentPool
  → AgentDiagnosticsTool → ITokenUsageEventRepository → TokenUsageEventRepository
  → FileReadTool/FilePatchTool → Runtime-owned FileChunkService

PuddingHost 产品组合根 → Runtime tool assembly scan
  → 每个自动发现的 IPuddingTool 都参与 ValidateOnBuild
  → 新工具的构造依赖必须同步注册到 PuddingHost 的 Runtime 扩展
  → AgentExecutionAdmissionCoordinator 必须在 Runtime 与 PuddingHost 两个组合根都注册为 Singleton，供前台 Turn 与 MessageDeliveryDispatcher 共享准入状态
  → PuddingApplicationHostCompositionTests 用 DesktopChild 入口防止“构建成功、Core 启动即退出”

Desktop → Core Ready 契约（2026-08-28 增加冷升级启动租约）
  → Core 初始化期间每 5s 发 PUDDING_DESKTOP_STARTING（协议/PID/单调序号）；Desktop 以 startupTimeoutSeconds 作为静默超时，合法租约允许 10 倍且最高 10 分钟的有界冷升级窗口
  → Core 在全部 hosted service StartAsync 返回后才发 PUDDING_DESKTOP_READY；租约不能替代 Ready、PID 校验或 /health/ready
  → ConnectorHostLifecycleService 本地注册保持同步，StartAllAsync 后台执行（ApplicationStopping 绑定）
  → FeishuWebSocket 端点发现/WS 握手各 15s 上限；飞书不可达只 Faulted 单个连接器，不阻塞 Ready

当前视觉链路（2026-09-12复核：ADR-077 V0–V3 已有实现；ADR-088收敛待实施）：typed image content part（`ContentPart{type=image, artifactId, detail}`）
  → ConversationAcceptanceStore 同事务写 `ChatMessages.ContentPartsJson`（v1 信封，Content 为文本拼接投影）
  → ExecutionRunCoordinator 读 canonical parts + 冻结 AgentExecutionSnapshot（CapabilityTags/Protocol/VisionPolicy；VisionHelperRoute 已随 ADR-077 V2 去外挂化移除）
  → 主模型带 vision：ChatMessage.ContentParts 原生进入请求；文本模型只收 `artifact://` 占位并显式调用 image_reader
  → 已删除 VisualArtifactObservationService 自动预观察旁路（服务+注册+旧测试）
  → LlmVisualInputPlanner fail-closed；已有inline/Files两种路径，旧产品策略单图2MB转Files、inline聚合40MiB、默认8张和384估计由V5/V7纠偏，不作为当前各Provider通用限制
  → Responses：user `input_image`（detail original→high）；`function_call_output.output` 支持 [input_text, input_image] 数组
  → ChatCompletions/Anthropic 遇图片工具结果抛 vision_tool_output_not_supported
  → Image Reader（image_reader）：path 唯一必填（http(s) URL / 宿主绝对路径 / artifact://），Low 权限 ReadOnly|RequiresNetwork（2026-08-28 裁定：纯只读无写/删路径，免审直通）
    → 只有 native 一条路径（ADR-077 V2 去外挂化，2026-09）：ToolExecutionResult.ToolContentParts 图片部件回交调用模型，零辅助 LLM invocation；无 delegate/helper 回退
    → 调用模型无视觉能力 ⇒ fail closed `vision_model_capability_mismatch`（工具输出明示 No helper model is used）；非 responses 协议 ⇒ `vision_tool_output_not_supported`；工具 schema 不提供 mode 参数
  → image_reader source resolver：URL 有界下载（每跳 SSRF/DNS 重校验、禁内网）、本地只读、内容哈希稳定 vision-* Artifact
  → DB 水合经 MessageEntity.AttachmentsJson 恢复图片 part；Snapshot 工厂冻结能力，单一判定来源
  → V3 Files已实现上传、持久remote ref与过期恢复；V4当前真实模型smoke与进程外验收待做。V7进一步收敛流式读取、多Provider传输、全请求预算和引用生命周期

Desktop Storage → CoreStorageManagementClient
  → GET/POST /api/admin/storage/databases（Admin JWT 或 Loopback ControlToken）
  → StorageMaintenanceService
    → 平台库页面/行/重复索引 + 代码索引作用域明细
    → PreviewId（10 分钟）→ 白名单批量删除 → checkpoint/VACUUM → 重扫
    → session_event_log / conversation_events / ChatMessages / memory 永不进入清理目标

PuddingHost → RetentionPruningService（platform.db 自动保留调度壳，ADR-076 收编）
  → 策略读 <DataRoot>/config/system.json storageManagement（StorageRetentionPolicyService，CAS + fail closed）
  → 执行全部委托 StorageMaintenanceCoordinator（唯一在线维护 writer：双优先级队列 + DataRoot maintenance.lock）
    → StorageCleanupExecutor 小批执行器（100 行/批、250ms 让步、busy 退避、rowid cursor 续行）
    → conversation_events 证据先 RetentionArchiveWriter 归档再删；在线 VACUUM 已全线移除
  → 遥测/上下文原始行自动清理默认关闭（聚合未实现 fail-safe）；Debug 字段/运行活动/日志默认开启
  → 旧 /api/admin/storage/databases 三端点保留双通道鉴权，Execute 内部经协调器，Desktop 旧页面无感

ADR-076 存储管理（Core + Web /storage，2026-08-24 首轮实现 Phase 0–3）
  → StorageDataClassCatalog 语义目录（9 类型 + evidence.conversation-events，物理白名单 + 保护清单）
  → StorageInventorySampler 有界采样（50–100ms slice、LIMIT 300 样本、索引探测 min/max、目录分片）
    → StorageInventorySnapshotStore 原子合并快照 + history.jsonl（每小时一点、90 天趋势）
    → POST inventory/refresh 立即 202、重复请求合并；GET overview 只读缓存
  → StorageMaintenanceJobStore durable 作业（maintenance/storage/jobs/<id>/job.json + events.jsonl，90 天轮转）
  → StorageAdminController 语义 API（overview/data-classes/refresh/history/policy(CAS)/preview/job/confirm/cancel，Admin JWT）
  → Admin /storage 页面：总览（文件+可复用页+分类估算）、SVG 占比圆环+趋势堆叠图、分类报表、
    可清理选择器（Evidence 只读展示）、受保护区、策略 Drawer、Preview 确认 Modal、作业列表（轮询+取消+确认）
  → 消费循环修复：writer Complete 后 WaitToReadAsync 同步 false 自旋会挂死宿主 StopAsync（测试抓出）

DesignRequest + ExpertGroupDefinition → DesignCouncilPlanCompiler
  → 上下文审计 → 调研 → 独立提案 → 交叉批判 → 主席综合 → 独立终审
  → 输出 Draft + RequiresExplicitActivation
  ├→ 当前 MOA 运行：DesignCouncilRunStateMachine → ISubAgentOrchestrationRunStore
  │  → DesignCouncilRuntimeService（精确 provider/model，无 fallback）
  │  → ISubAgentInvocationService（复用 sub-session/run archive/deadline）
  └→ 通用化迁移：DesignCouncilOrchestrationGraphAdapter
     → pudding.agent-orchestration/v2（component/trigger + typed multimodal port + control/data edge）
     → AgentOrchestrationGraphCompiler（组件冻结、端口/schema/route/reference/DAG 校验，不执行）
     → SqliteAgentOrchestrationStore
       （revision CAS → run/node projection → atomic claim/fence → append-only event replay）
     → AgentOrchestrationApiController
       （graph/run discovery + catalog/revision/run/events → AgentOrchestrationEventFollower → replay-to-live SSE）
     → AgentOrchestrationLayoutApiController
       （GraphLayout read + Admin CAS write；不可变 Revision/Node 先只读校验，与 executable revision/run facts 隔离）
     → AgentOrchestrationManagementApiController
       （Admin Graph create + Head-CAS delete；任意 Run 历史都会阻止删除）
     → AgentOrchestrationHttpHookApiController
       （Admin debug POST + 显式 immutable revision；从不解析 Graph Head）
       → AgentOrchestrationHttpHookService
         （sourceEventId 幂等 + payload binding → durable Run Inputs → Create/Activate）
     → AgentOrchestrationRunCommandApiController
       （Admin 顶部“运行” + 显式 immutable revision + typed inputs → ManualRunService → Create/Activate）
     → AgentOrchestrationWorkerService
       （SubAgent → SubAgent → image-generate → image-preview；按端口 outputs_json 传递文本/Artifact、lease 续租、后继 Ready/Skipped 与 Run 终态同事务推进）
     → Admin /orchestration
       （紧凑 Graph/Run 控制条 + 顶部运行 → 全宽画布 → 悬浮工作台 → SubAgent 模型/模板/角色设置与文本输出 → 图片生成/展示组件自有预览 → Revision/Layout CAS）

Chat 插嘴模式（当前 Turn steering）
  → useMessageInteractionQueue：busy 时 Enter 仍立即提交 canonical Turn API，受理后由 chat_execution_commands + ChatExecutionWorker 持久排队；不创建 React local_pending
  → Composer 独立 ⚡ 设计增量：active Turn + 非空纯文本时直达同一 Steering admission，不写 pendingSendQueue、不创建普通 delivery/第二 Turn；202 后 compare-and-clear，409/失败保留草稿且不自动排队
  → 第一批交互一致性落地（2026-09-11，Docs/Reports/前端交互体验优化建议-2026-09-11.md §0）：
    鼠标发送按钮不再挪用作停止——运行中有草稿=「加入队列」（与 Enter 同链）、⚡菜单=「补充给当前任务」（与 Ctrl/Cmd+Enter 同链）、
    独立「停止当前执行」按钮=本地 abort + requestActiveTurnCancel（新增 cancelConversationTurn 封装，接线既有 ADR-059
    POST .../turns/{turnId}/cancel；已结束/未受理按竞态静默）；useMessageSend 失败恢复草稿（restoreDraft 端口，空输入框才回填）、
    busy 提交 202 受理后「已加入队列」回执；状态胶囊/余额徽标补键盘激活
  → MessageQueueProjectionService：默认只读投影 queued/retrying deliveries + pending commands；claimed/running 只在诊断查询出现
  → MessageQueueDropdown：内容宽度胶囊摘要；详情向上悬浮限高，明确“认领后转入会话轨迹”
  → POST /api/v1/conversations/{conversationId}/turns/{turnId}/steering + X-Workspace-Id
  → ConversationTurnsController → CreateSteeringHandler（Running + Workspace/Agent 围栏）
  → SessionSteeringService → session_steering_messages durable queue（不可变 target_turn_id；source_queue_item_id 重试幂等；旧库由 bootstrapper 原地升级）
  → AgentExecutionService：每次 LLM 前只 drain 当前 Turn；最终回复后的 late safe boundary 命中则继续同一个 Turn
  → steering.injected + agent.steering.inject 形成消费证据；不取消正在运行的工具/模型请求
  → 独立按钮方案：Docs/Features/Chat独立插嘴按钮与当前Turn即时Steering设计方案.md（Proposed；P1 Task ed88185f1d3b4e16a70e9b9ea0f0e040）

Chat first paint → AgentConversationProjectionService
  → 过滤 transport duplicate 占位、按 pudding-message envelope message_id 折叠历史重复入站
  → system/heartbeat envelope 正文投影为 context.text，不把协议 JSON 显示在聊天气泡
  → 最近 20 条可见消息 + active run 最近 64 条过程明细/全量摘要
  → TurnSurfaceStore（2026-08-24 行为链重构）：canonical turnId + 别名归并
    （messageId/runId/commandClientId），完成 turn 经 per-message 明细接口懒水合
    （text/thinking/tool/delegation 同一事件流，eventId 幂等去重），
    终态/刷新后轨迹从投影重建；AgentConversationProjectionService 明细端点
    补 text 事件 + delegation 三重排除修复（canonical 事件名/kind 映射/run 过滤）
  → 验收二轮修复（2026-08-24）：委派节点按 subAgentId upsert（重复 spawn 不再
    留下永久 running）；DTO 透传 canonical sequence/turnId/runId + activeRun 快照
    补正文事件（运行态即可交错文本段）；懒水合有界化（MessageRow 通过消息滚动
    容器 IntersectionObserver 在 600px 预取区注册可见 turn、并发窗口 ≤2 且槽位
    完成后持续排空可见队列；组件挂载不再批量水合全部历史）；正常流保留真实行高，
    删除会在动态 Agent 行上积累过期 remembered size 的 content-visibility 占位；
    虚拟化按消息内容 + 已水合 canonical 行为链 render weight 即时开启；
    AgentTurnCard 大卡片外壳（暖色表面/1px 边界/14px 圆角，终态不重挂载）；
    工具行 aria-label 带状态 + 成功终态卡「已完成」标记
  → canonical event → ExecutionFlowProjectionIndex（2026-08-27）
    （eventId 先去重；同帧按 Turn 合并；只重投影 dirty Turn；快照结构共享；
      终态释放原始事件/eventId；session switch 硬 reset；collector 保留 typed payload）
  → MessageList → messageProjection（保持已组装消息顺序，未匹配 active run 留在当前流末端）→ MessageViewportRuntime（虚拟化、锚点、贴底）
  → ChatMessageStyleProvider（消息树共享一次聚合样式注册）
  → MessageRow（稳定块直接渲染 + 语义 memo；接收本 Turn 具体 Projection，其他 Turn revision 不失效；不再经过单条 MessageStream 兼容重建）
  → AgentMessageBubble → TurnContentStream（AgentTurnCard 内容块流，2026-08-25）
     （per-turn canonical 投影 nodes 按 sequence 形成 TextBlock ⇄ ActivityGroup 交错流；
       正文永久可见且只渲染一次，answerMarkdown 不再以字符串关系切回第二正文气泡；
       最新尾部组始终展开、历史组折叠并卸载成员 DOM，组内长详情默认单行折叠；
       单 Turn 默认只挂载最新 40 个内容块、单 ActivityGroup 最新 24 个行为节点，
       较早轨迹按 40/24 项渐进揭示，避免单个长 Turn 绕过消息级虚拟化撑爆 DOM；
       路径 A processItems adapter 只作无 canonical 正文节点时的旧记录回退；
       ProcessSummaryItem 必须透传服务端 sequence，缺失即 fail closed，不用下标伪造；
       已封闭 TextBlock/ActivityGroup 语义 memo，append 只更新尾段/状态变化组；
       TurnOutputChunker 非 delta 事件先 flush 正文/思考缓冲，轮次边界进 canonical sequence；
       终态 reply 分叉以服务端为准不再拼接（重复输出修复）；
       流式中同回复单卡：activeRun↔本地 turn 合并移除「本地正文为空」门槛
       （commandClientId 已是同一发送的强约束），hasProjectedUserTurn 增加
       turnId 锚点、合并保留本地 clientMessageId（2026-08-24，生成中多卡修复）；
       ReasoningDisclosureRow 多段 + 段时长 chip、ToolCallRow 耗时/exit 折叠行、
       TurnStatsLine 终态计量、PresentationRegistry 五类 renderer：terminal/diff/read/search/web）
  → 主消息运行监视区（首 Token 前也保留主代理“查看过程”：当前阶段 + 推理摘要 + 工具操作 + 有界子代理委派状态；不展开子代理内部过程）
  → subAgentReducer（事件/快照统一投影；状态接口携带 canonical runId 并可重建漏收 created/started 的运行；budget_exhausted 终态单调；原样展示有界的实际 reasoning_preview）
  → SubAgentActivityDock（实时 reducer + 终态 run 归档一次性回放；活动 run 零归档轮询=ADR-060 §3.11；归档降级时展示 archive-degraded 提示；刷新后按 canonical runId 恢复子代理任务/推理/工具/轮次/耗时/输出；Agent-first 路由回退 mainSessionId 保证图标可见）
  → 展开过程摘要时才构建 rounds / trace chips
  → MessageItem 先渲染纯文本，异步加载 Markdown/KaTeX 增强块
  → 任务看板、Checkpoint、历史搜索、开发面板、子代理检查器、右键菜单、
    会话诊断 Drawer 与摄像头输入仅在首次使用时加载；工具栏 hover/focus 可预取
  → 余额、Goal、会话推断等辅助请求在首帧后的 idle window 启动，
    不阻塞消息投影、当前 Agent 与实时事件；旧 WebView2 以短 timer 有界退化
  → 常驻埋点使用 perfEventRuntime；完整诊断模块仅在 perf/debug 模式加载
  → npm run build 强制 Chat bundle budget：同步脚本 ≤1536 KiB、Chat 路由 ≤480 KiB，
    并阻断任务看板/Checkpoint/ContextMenu 回流首载共同块
```

Task auto execution settlement (2026-08-29 hardening)
  → GoalSettlementStore: terminal completeness scans the full Turn window; EvidenceRefs retain the newest bounded 128 events
  → settlement atomically projects RunId/elapsed/LLM rounds/tool calls/input-output tokens into GoalIteration + Goal totals
    (root Turn canonical usage + recursive descendant TokenUsageEvents inside the exact Turn window)
  → blocked Task-bound attempt atomically releases Binding/Assignment/Reservation, keeps Task Blocked/NeedsReview and terminals the attempt Goal as Failed audit history
  → dispatch atomically retires legacy detached Blocked Task Goals for the same Agent/conversation (including a prior Task) before creating a fresh fenced Goal, preventing UX_goal_runs_active from masquerading as task_goal_lost_race
  → five-minute tracker/repair heals historical blocked_binding_still_active ownership before the next dispatch
  → legacy delivered-without-execution assignments are fail-closed to Blocked and released after the stall threshold
  → scan order is tracking/repair → availability rebuild → candidate evaluation → dispatch in the same interval
  → task dispatch outbox revalidates Task/Assignment before send and again at atomic bind; stale/terminal conflicts dead-letter, transient send/bind failures stop at MaxAttempts, shutdown cancellation stays lease-recoverable
  → WorkUnit remaining input/output/cost budgets propagate through tool and sub-agent boundaries; synchronous delegated usage returns to the parent ledger
  → AgentOutputTruncationPolicy: one action-forcing recovery for length/incomplete, then explicit failure
  → ConversationProjector: direct usage dedup uses SQL-stable fingerprint candidates + bounded in-memory time fence

Task scheduler + Goal user control plane (2026-08-31 source-ready)
  → Admin Task board exposes explicit auto-dispatch opt-in and structured task routing facts
  → SchedulerDrawer consumes server status/policy/actions/evaluate: pause/resume, immediate scan/repair, revision-CAS hot policy
  → TaskAutoDispatchScanRunner is the shared periodic/manual reconciliation order; dynamic policy reaches worker, event bridge, coordinator and starter
  → GoalBanner covers start/pause/resume/stop/new without deleting durable Goal history
  → Goal continuation transport keeps new Unicode readable while escaping envelope delimiters; Admin projections decode legacy JSON escapes only for server-marked goal_continuation messages
  → authoritative design: Docs/Features/任务调度器与Goal用户控制面设计.md; external Desktop/Core deploy and product smoke remain separate gates

Task scheduler effective-dispatch closure (2026-09-01 proposed)
  → effective dispatch requires Intent → durable task-scoped Decision/Outcome → fenced Assignment/Reservation → TaskGoalBinding/GoalRun → canonical ExecutionRun → verified Task settlement
  → legacy execution claims must join execution_runs terminal state; terminal/orphaned claims release ownership through Serializable deterministic repair instead of remaining Healthy forever
  → event Coordinator completes each Intent only after its triggering Task has a durable stable outcome; single/bounded modes share the same Bridge/Coordinator/Starter gates
  → durable scan-run summaries preserve empty/failed scans across restart; Blocked recovery UI uses diagnostics + preview + per-task ETag commands
  → code-level implementation and task slicing: Docs/Features/Scheduler夜间有效调度与Execution生命周期闭环代码级实施方案.md

## 测试项目

| 项目 | 覆盖 |
|------|------|
| `Tests/PuddingCoreTests/` | 工具契约、LLM 网关、MessageFabric |
| `Tests/PuddingRuntimeTests/` | Agent Loop、上下文管线、语音/图片 |
| `Tests/PuddingPlatformTests/` | 渠道配置、Artifact 存储、图片生成 |
| `Tests/PuddingMemoryEngineTests/` | Library/Book/Chapter、FTS5、Skill 去重 |
| `Tests/PuddingMemoryEngineBenchmarks/` | BenchmarkDotNet |
| `Tests/PuddingCodeIntelligenceTests/` | 代码索引 |
| `Source/PuddingCodeIndexTests/` | **索引组件（`PuddingCodeIndex`）独立测试工程**：变更管线/调度/维护/存储 + 边界断言（58 用例） |
| `Tests/PuddingCodexServiceTests/` | Codex MCP Service |
| `Tests/PuddingFullTextIndexTests/` | 全文索引 |
| `Tests/PuddingWebApiTests/` | Web API |
| `Tests/PuddingDesktop.Tests/` | Desktop 进程/配置、Browser Controller/Client、Debug 调试模式（路由/反向代理集成/SSE/WS 中继/前端监督器/源码构建器/前端构建部署） |
| `Tests/PuddingHost.Tests/` | Bridge Endpoint/Remote proxy（56/56 ✅） |
| `Tests/PuddingBrowser.AgentTools.Tests/` | 七项 Agent Tools（10/10 ✅） |

## 2026-09-05 效率与代码审计入口

持续恢复入口：`Docs/Reports/PuddingAgent持续优化执行台账-2026-09-05.md`（既有 30 分钟 heartbeat）。第四轮：`StorageInventorySampler` 的两个索引端点/2000-entry 惰性目录预算；`TokenUsageRecorder` 每当前层只读上一条 hash，`PlatformDbContext` / `TokenUsageSchemaBootstrapper` 同步覆盖索引。定向19/19、扩展96/96；第四轮已部署，同任务56.898s/预热13.253s。`Docs/Reports/PuddingAgent第四轮有界查询与流空档修复-2026-09-05.md` 区分模型流与本地记账、GC heap与Private；后续Private仍回升，不能宣布整体内存优化通过。

第三轮部署与产品证据：`Docs/Reports/PuddingAgent第三轮部署与产品验收-2026-09-05.md`。Desktop 主管不变，新 Core/前端已部署，hash/Ready/单次只读功能通过；161.749 秒流空档、预热内存与夜间吞吐仍待收口。`TestScripts/invoke-pudding-desktop-deployment.ps1` 提供停机备份/预构建部署/全量前端核验；`test-pudding-deployment-gates.ps1` 覆盖历史 PID 停机判定（7/7）；`measure-pudding-process-baseline.ps1` 记录进程树 CPU/Private/WS，前后负载不一致不得当作 A/B 收益。

第二轮前端与发布：`Docs/Reports/PuddingAgent第二轮前端修复与发布验证-2026-09-05.md`、`Docs/Reports/pudding-agent-round2-build-2026-09-05.json`。`PuddingAdminShell/EntityCard/PageHeader/StatusBadge/Toolbar` 使用 createStyles；`PerfTab` 使用实际诊断合同；SSE reconnectCount 状态穿透 ChatLayout，移除 ChatMain 500ms 轮询；`PuddingHostContent.props` 的 `PuddingAdminDistPath` 与前端 `PUDDING_ADMIN_OUTPUT_PATH` 支持隔离打包。源码检查和发布核验通过，未部署。

首轮实现与验证：`Docs/Reports/PuddingAgent首轮修复与验证-2026-09-05.md`。新增 `Source/PuddingPlatform/Services/Scheduling/LegacyTaskExecutionProbe.cs`，由 Tracker/Repair 与完成结算共用精确 Command→latest Run 解析；`ExecutionRunCoordinatorMonitorTests.cs` 覆盖监视异常取消；`TestScripts/test_deepseek_cache_hitrate.py` 覆盖完整北京时间日→UTC 边界。产品部署与性能对照仍待验收。

`Docs/Reports/PuddingAgent效率与代码审计-2026-09-05.md`：TaskExecutionTracker legacy claim → canonical terminal、FilePatchTool schema/缺字段语义、useSessionEventConnection 鉴权重试、SubAgentConversationProjectionWorker/FileSubAgentRunStore 增量回放边界，以及 ExecutionRunCoordinator monitor fault。报告附七日指标与验证结果；这些是诊断发现，尚未标记为修复或产品验收完成。

## 2026-09-18 FastRouter 本机资源池配置

`D:\data\config\llm.providers.json` 新增 `fastrouter` 下的 `gpt-6` / `gpt-6-astra`，Responses 协议，1050000 上下文 / 128000 输出。仅运行时配置变更，待 Core 重启加载；映射和验证边界见 [配置记录](Docs/Reports/FastRouter资源池配置-2026-09-18.md)。

## 2026-09-18 FastRouter 兼容性实测

当前 Key 分组的 GPT-6 两个 ID 返回 model_not_found；gpt-5.6-sol 的 Responses 流式工具调用与工具回传成功，Chat Completions 亦成功。入口为 ResponsesLlmGateway 及其内嵌 ResponsesStreamParser；详见[诊断证据与调用路径](Docs/Reports/FastRouter资源池配置-2026-09-18.md)。未修改产品或替换运行时模型。

## 2026-09-18 FastRouter GPT-6 Astra 复测通过

服务商更新配置后，`gpt-6-astra` 已通过 Responses SSE 工具调用及结果回传，`gpt-6` 仍404；当前应选择完整ID gpt-6-astra。原有资源池协议不变，未重启或执行产品内会话验收。见[复测记录](Docs/Reports/FastRouter资源池配置-2026-09-18.md)。

## 2026-09-18 FastRouter 最终模型清单

按用户要求，本机 fastrouter 资源池仅保留已实测成功的 gpt-6-astra，删除不可用的 gpt-6。备份与校验见[配置记录](Docs/Reports/FastRouter资源池配置-2026-09-18.md)。

## 2026-09-18 FastRouter 新增 GPT-5.6 Sol

本机 fastrouter 资源池现含 gpt-6-astra、gpt-5.6-sol，均使用responses；Sol按官方文档登记1050000上下文/128000输出。备份、参数来源和加载边界见[配置记录](Docs/Reports/FastRouter资源池配置-2026-09-18.md)。

## 2026-09-19 历史图片8张上限诊断

VisionRequestPolicy默认8、VisionCapabilityContract上限钳制、PuddingFileConfigLoader加载拒绝及VisualInputRequestBudget跨消息累计共同导致第9份图片失败。13项现有合同测试通过，未修复/部署；历史理由、精确Turn及纠偏方向见[诊断](Docs/Reports/历史图片累计触发8图上限诊断-2026-09-19.md)。

### 2026-09-20 压缩活性与界面重设计
- 权威设计：`Docs/Features/上下文压缩运行状态与界面设计.md`。
- `ContextCompactionService.GetActiveCompaction` / `SessionEventsController.GetCompactionStatus`：精确 ID + 开始时间的轻量活性快照；started 移到摘要输入准入后；取消补写终态。
- `useCompaction`：按 ID 投影，10 秒确认、30 秒动画许可、重放不切会话；`CompactionCard` 独立状态区；`ContextUsageRing` 总窗口占比、来源和采样时间；`IntentConsole` 压缩结束刷新并防旧响应覆盖。
- 部署验收：实现 `aab7f5c`，2026-09-20 17:25 Core PID 23280 Ready，264 文件 manifest 匹配；`umi.83c11143.js` HTTP 哈希匹配。111 项定向回归通过，真实默认助手页面无伪压缩动画；自然压缩执行未被人工触发。
