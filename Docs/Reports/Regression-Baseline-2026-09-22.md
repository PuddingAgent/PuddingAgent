# 全仓回归基线报告（2026-09-22）

> 建立者：`default.global_general-assistant.0e0`（dsh）｜HEAD：`a313337`｜时间：2026-09-22 13:16 (+08:00)
> 口径说明：本报告所有"红/绿"结论均**附口径声明**。凡标「未证」者**不得**被当作回归或环境结论引用。

## 一、覆盖情况

**全仓共 14 个测试工程**（`file_search` 枚举 + `search_grep 'Microsoft.NET.Test.Sdk|MSTest.Sdk|IsTestProject'` 交叉验证）。

| 工程 | 通过 | 失败 | 跳过 | 总计 | 判定 | 口径 |
|---|---|---|---|---|---|---|
| `Source/PuddingRuntimeTests` | 1833 | 0 | 6 | 1839 | 🟢 | 常规 |
| `Source/PuddingPlatformTests` | 1385 | 0 | 0 | 1385 | 🟢 | 常规 |
| `Source/PuddingCoreTests` | **1014** | **0** | 0 | 1014 | 🟢 | 常规（修复 `a313337` 后） |
| `Source/PuddingCodeIntelligenceTests` | 96 | 0 | 0 | 96 | 🟢 | 常规 |
| `Source/PuddingMemoryEngineTests` | 286 | **3** | 0 | 289 | 🔴 | 常规 |
| `Source/PuddingFullTextIndexTests` | 50 | 0 | 4 | 54 | 🟢 | 常规 |
| `Source/PuddingCodexServiceTests` | 4 | 0 | 0 | 4 | 🟢 | 常规 |
| `Source/PuddingWebApiTests` | 167 | **5** | 0 | 172 | 🔴 | ⚠️ `-p:BuildProjectReferences=false`（常规被文件锁阻断） |
| `Tests/PuddingHost.Tests` | 111 | 0 | 0 | 111 | 🟢 | 常规 |
| `Tests/HarnessAgent.Core.Tests` | 20 | 0 | 1 | 21 | 🟢 | 常规 |
| `Tests/PuddingAgent.IntegrationTests` | 15 | **3** | 0 | 18 | 🔴 | ⚠️ `-p:BuildProjectReferences=false` |
| `Tests/PuddingDesktop.Tests` | **247** | 0 | 0 | 247 | 🟢 | ⚠️ `-p:BuildProjectReferences=false`（`--no-build` 旧 dll 为 227/227，以 247 为准） |
| `Tests/PuddingBrowser.AgentTools.Tests` | 15 | 0 | 0 | 15 | 🟢 | 常规 |
| `Tests/PuddingBrowser.WebView2.Smoke` | — | — | — | — | ⬜ 非测试工程 | `OutputType=Exe`+`UseWPF`，无测试 SDK |

**合计：12 条红 —— 1 条已修（见二）；10 条已证为「测试契约滞后 / 既有幂等噪声」（即**非回归**）；1 条未证（非确定性）。**

> **最终结论（2026-09-22 13:50 收口）：本轮全仓基线发现的 12 条红中，「**无一条可凭证据判为产品回归**」。**
> 其中 10 条属「契约/夹具滞后」（测试文本早于守卫或契约引入），剩余 1 条仅能证「非确定性」；
> 唯一可行动的产品侧问题已另立缺陷卡：`c1a8d1da17014165ac3a17e6dc4cbc8d`（非法入参→500 而非 4xx）。

**无基线**：`Tests/PuddingBrowser.WebView2.Smoke` 不是测试工程，`dotnet test` 仅还原即退出 0；其冒烟能力只能人工运行 Exe。

**尚未跑**：`external/github.hyfree.GM/github.hyfree.GMTests`（确为测试工程，首轮清单遗漏）。

## 二、已修复（1）

**`a313337` — `SwarmOrchestratorTests.ProcessSwarmAsync_WithInvalidSwarmDirectory_HandlesError`**（PuddingCoreTests）

- **根因＝测试夹具缺陷，产品代码无问题**：夹具构造函数只做 `Directory.CreateDirectory(_testRepoDir)`，**未把夹具仓初始化为 git 仓库**；而同文件兄弟用例 `ProcessSwarmAsync_FullWorkflow_CompletesSuccessfully` 一直有调 `InitializeTestGitRepoAsync()`（该类自带助手 `:551-566`：git init + config + initial commit）。本用例漏调 ⇒ `WorkerManager.RunGitAsync(["worktree","list"])`（工作目录＝`_testRepoDir`，非仓库）退出码≠0 ⇒ 抛 `InvalidOperationException`，被用例 `catch` 后触发 `Assert.Fail("Should not throw exception: ...")`（`:403`）。
- **修复**：Arrarse 段补 `await InitializeTestGitRepoAsync();`（与兄弟用例对齐）。**改动面：该测试文件 +2/−0 行**，产品代码零改动。
- **验证**：单测隔离 `--filter` → 失败 0/通过 1；全工程 → **失败 0 / 通过 1014 / 总计 1014**（1m08s，exit 0），修前为 1013 通过 + 1 失败。
- **归因证据**：该红在 `412feab`(2026-09-17) / `8e0ff89`(2026-02-12) 的旧代码上即存在，与 09-20 以来各批提交无关。
- **命名瑕疵（未擅改）**：用例名含 `WithInvalidSwarmDirectory`，但注释自承使用 "valid paths"、断言要求正常完成并产出 `SwarmCompletedEvent` ⇒ 名不副实；若要真正覆盖「无效 swarm 目录」场景应另立新用例。

## 三、根因已明、方案已备，**归属不明**（3）

`Source/PuddingMemoryEngineTests/MemoryLibraryTests.cs`

| 用例 | 位置 | 首个错误 |
|---|---|---|
| `ContextPipeline_ShouldAssembleAll7Layers` | `:671` | `Assert.IsTrue 失败：result.Layers.Any(l => l.LayerName == "环境信息")` |
| `ContextPipeline_ShouldTriggerGentleCompaction` | `:753` | `System.InvalidOperationException: Sequence contains no matching element` |
| `EnvironmentLayer_ShouldBePresentInAssemblyResult` | `:798` | `Assert.IsNotNull 失败：'envLayer'` |

- **根因＝契约冻结滞后**：测试断言**中文层名**（`静态上下文`/`环境信息`/`动态工具`/`动态技能`/`用户偏好`/`当前消息`/`运行时指令`，见 `:660-674`），而现行 pipeline 层名早已是 **ASCII id**（`L0-STATIC`/`L0-ENVIRONMENT`/`L1-TOOLS`/`L2-SKILLS`/`L9-INBOUND`；参见 `PuddingRuntimeTests/Services/TaskPlannerContextBuilderTests.cs:122` 断言 `L0-ENVIRONMENT`、`ContextAssemblyEventEmissionTests.cs:98` `LayerName = "L0-STATIC"`）。
- **已证非今日回归**：`git log -S "L0-ENVIRONMENT"` → `67af387`(**2026-08-18**)/`99860e2`(08-11)；`静态上下文` 最后出现于 `99860e2`(08-11)；`git log -S "环境信息" --since=2026-09-20 -- Source/` **为空**；`MemoryLibraryTests.cs` 最后改动 `717e1ce`(**2026-08-27**)。
- **修复建议**：改用具名 ASCII id，或引入「中文→id」映射常量，避免再次改名时二次失效。
- **为何未直接修**：该测试工程今日仍被 rsi/G4 线活跃改写（最新 `afac60a`），抢改存在合并冲突与责任归属问题 ⇒ 交该线 owner。
- 同类先例：`239c76b`（2026-09-21）「test(core): 修复 7 例既有红测试（契约冻结滞后）」。

## 四、已证**口径污染**、归因未闭环（5）

`Source/PuddingWebApiTests`（首次入基线）：失败 5 / 通过 167 / 总计 172

| 用例 | 位置 | 首个错误 |
|---|---|---|
| `BootstrapApiControllerTests.Complete_CreatesAdminProviderAndDefaultModel` | `:50` | `应为 <OK>，实际为 <InternalServerError>` |
| `GoalApiContractTests.GoalCommands_Conflict_When_NonTerminal_Goal_Exists` | `:188` | `Assert.IsFalse 失败：'body.Success'` |
| `SessionApiControllerTests.CompactSession_Returns200_WithStringLevel` | `:347` | `应为 <OK>，实际为 <BadRequest>` |
| `SessionApiControllerTests.CompactSession_DoesNotStackCompactionPrefixInNewSessionTitle` | `:382` | `应为 <OK>，实际为 <BadRequest>` |
| `SessionEventsControllerTests.Compact_Passes_Runtime_Profile_To_Compaction_Service` | `:240` | `CollectionAssert.Contains 失败` |

- **已证（口径污染）**：常规 `dotnet test` **构建失败** ——
  `error MSB3027 / MSB3021：无法将 Source/PuddingRuntime/bin/Debug/net10.0/PuddingRuntime.dll 复制到 bin/…：文件被 "PuddingAgent (23764)" 锁定 [Source/PuddingAgent/PuddingAgent.csproj]`。
  根因链：`PuddingWebApiTests.csproj:21` `<ProjectReference Include="..\PuddingAgent\PuddingAgent.csproj" />`，而宿主进程 PID 23764 正锁定自身 bin。
  ⇒ 结论实际来自 `-p:BuildProjectReferences=false`（只重编译测试程序集），引用的是 **`Source/PuddingAgent/bin/Debug/net10.0/PuddingAgent.dll`（mtime 2026-09-22 12:26:52）**，早于 HEAD `a313337`(13:08:08) 约 41 分钟。
- **⚠️ 更正（2026-09-22 13:24 追加，推翻本报告初稿的「宿主占用」猜想）**：`Source/PuddingWebApiTests/CustomWebApplicationFactory.cs:30-38,110,119-120` 显示**每个测试实例都创建并使用独立的临时 data root**（`%TEMP%/pudding-webapi-tests/<guid>`，经 `PUDDING_DATA_ROOT` 注入，Dispose 时删除）⇒ **不存在与运行中宿主共用同一个 DB 的情形**，初稿「预置 DB 被宿主占用致迁移失败」的猜想**不成立**，特此更正。
### 5 条失败用例的逐条判定（2026-09-22 13:40 追加；均由只读归因取得，父级抽验 3 处关键断言）

| # | 用例 | 判定 | 依据（文件:行号） |
|---|---|---|---|
| 1 | `BootstrapApiControllerTests.Complete_CreatesAdminProviderAndDefaultModel` | **测试契约滞后（已证）**＋附产品健壮性缺口 | 测试发 provider 级 `protocol="openai"`（`BootstrapApiControllerTests.cs:35`），而契约只认 `chatModelProtocol`/`memoryModelProtocol`（`BootstrapApiController.cs:505,:507`；**父级抽验**）⇒ 读到 null ⇒ `:408` `throw InvalidOperationException("模型 'gpt-test' 必须选择 openai、responses 或 anthropic 协议。")` ⇒ 未处理 ⇒ 500 |
| 2 | `GoalApiContractTests.GoalCommands_Conflict_When_NonTerminal_Goal_Exists` | **未证（仅已证「非确定性」）** | 全套跑失败、隔离单跑通过（失败 4/通过 1，该用例不在失败清单）；日志无 `[GoalCommand]` category、取不到首个请求的响应体 ⇒ 不足以判产品缺陷，也不足以下环境结论 |
| 3 | `SessionApiControllerTests.CompactSession_Returns200_WithStringLevel` | **测试契约滞后（已证）** | 请求体无 `agentId`（`SessionApiControllerTests.cs:340-345`），产品空 `AgentId` 直接 400 `agent_id_required`（`SessionEventsController.cs:715`；**父级抽验命中**）；同端点兄弟用例传了 `agentId` 即得 200（`SessionEventsControllerTests.cs:201-205`）⇒ 机制性对照成立。溯源：守卫 `4b6a3d7`(2026-07-18) 晚于测试文本 `8e0ff89`(2026-02-12) |
| 4 | `SessionApiControllerTests.CompactSession_DoesNotStackCompactionPrefixInNewSessionTitle` | **测试契约滞后（已证）** | 与 #3 **同因同一代码行** |
| 5 | `SessionEventsControllerTests.Compact_Passes_Runtime_Profile_To_Compaction_Service` | **测试契约滞后（已证）** | 该测试自己在 `:181-182` 把 `IContextCompactionService` 换成捕获桩，真实发射点 `ContextCompactionService.cs:514-520` 根本不会执行；且更新的契约测试 `RequestCompactionHandlerTraceTests.cs:57,:99` **明文断言 `ContextCompactionStarted` 不得存在** |

**⇒ 5 条中 4 条为「测试契约滞后」（均已证）、1 条未证（非确定性）；无一条可凭现有证据判为产品/真实缺陷。**

**⚠️ 附带发现：一处已证但未修的产品健壮性缺口**
#1 同时暴露：`BootstrapApiController` 把**客户端非法入参**（漏传 `chatModelProtocol`）映射为 **500** 而非 4xx（`BootstrapApiController.cs:401-409` throw → `:229-232` 回滚后 rethrow）。任何漏传协议的调用方都会看到 500。**本轮只归因、未修**，需产品侧决定修正方式。

- **✅ 已闭环（2026-09-22 13:40 追加）——「一次性列迁移反复失败」的性质已取证并定性**：
  - **形态**：80 条 ALTER = 2 次宿主启动 × 40 条（另 1 条 `INSERT INTO task_scheduler_scan_runs` 属另一宿主、不在本次范围）。引擎级复现（全新内存库 + sqlite3 3.49.1）得到 **`SQLITE_ERROR(code=1): duplicate column name: <col>`**（`conversation_id`/`reply_to_message_id`/`correlation_id`/`causation_id`/`metadata_json` 同形），并以「对不存在的列做 ALTER 会成功」的对照实验排除了文本歧义。
  - **成因**：同一 DDL 里的 `CREATE TABLE IF NOT EXISTS` 已含这些列（如 `MessageFabricSchemaBootstrapper.cs:20-35`），且启动前 `PuddingApplicationInitializer.cs:45` 已用 EF 模型 `EnsureCreatedAsync` 建表；WebApiTests 更把 PlatformDbContext 换成 `Data Source=:memory:`（`CustomWebApplicationFactory.cs:68-77`）⇒ 每条 ALTER **必然**撞 duplicate column。**ALTER 只对更老的存量库有意义**。
  - **是否被吞并**：四处同款分支 `ex.Message.Contains("duplicate column name") → continue` —— `MessageFabricSchemaBootstrapper.cs:164`（**父级抽验命中**）、`TaskPlanningSchemaBootstrapper.cs:163`（**命中**）、`GoalSchemaBootstrapper.cs:261`（**命中**）、`TodoSchemaBootstrapper.cs:86`（**命中**）；`WorkspaceTaskSchemaBootstrapper`（`pragma_table_info` 预检）与 `TokenUsageSchemaBootstrapper` 走预检、新库不发射。**不 throw、不中断启动**（`schema bootstrap failed` 计数 0；`Platform DB tables and schema upgrades ensured` 正常出现）。
  - **是否影响列存在性**：**不影响**（已证）—— EF 实体 `MessageDeliveryEntity.cs:55-56,:64-65`、模型快照 `PlatformDbContextModelSnapshot.cs:1058-1071`、既有验收测试 `MessageFabricSchemaBootstrapperTests.cs:114-115` 与 `SchemaBootstrapperFreshDatabaseColumnTests`（PRAGMA 列集合比对）共同锁定。
  - **⇒ 结论：设计内的幂等降级噪声，与 5 条失败无因果关系。** 副产物是它会把真实错误淹没在 80 条 ERR 里 ⇒ 建议降为 Debug 或一次性摘要（改进建议，未做）。
  - **存疑但未证（相邻噪声）**：`ClassCleanup … NullReferenceException … SqliteConnection.Close()`；`SQLite Error 5: 'database is locked'`（GoalContinuation 扫描）；进程级 `PUDDING_DATA_ROOT` 由每个 factory 覆盖（`CustomWebApplicationFactory.cs:26-31`）⇒ 跨类并行时存在**结构性互相干扰风险**（这可能正是 #2 非确定性的来源，但**未证明因果**）。

## 五、已归因（3）—— 全部「测试契约滞后」（已证）

`Tests/PuddingAgent.IntegrationTests`（首次入基线）：失败 3 / 通过 15 / 总计 18

| 用例 | 位置 | 首个错误 |
|---|---|---|
| `Feishu.FeishuInboundImageTests.ImageEvent_DownloadsOnceAndMapsToCanonicalVisionArtifactMetadata` | `:55` | `PuddingCode.Core.VisionPipelineException: Image header could not be decoded.` |
| `Feishu.FeishuInboundPostTests.PostEvent_WithImages_MaterializesArtifactsInOrder` | `:128` | `KeyNotFoundException: 'visionArtifactIds'` |
| `Feishu.SendImageToolTests.ExecuteAsync_QueuesArtifactToCurrentTrustedFeishuRoute` | `:65` | 同 `Image header could not be decoded.` |

- **⚠️ 更正（2026-09-22 13:50 追加）**：本节初稿的路径核查用的是 `Source/PuddingRuntime/Services/{Vision,Feishu,Rooms}`，**而真正的守卫在 `Source/PuddingPlatform/Services/`** ⇒ 那次「09-21 起无提交」的核查**未覆盖守卫所在路径**，属核查盲区，特此更正；结论亦由此改写（见下）。
- **✅ 已归因：3 条全部为「测试契约滞后」（已证），0 条产品缺陷、0 条环境缺失**
  - **单一根因**：`74ae4e0`（**2026-09-15**）`feat(vision): preprocess images locally for native agent reading` 在保存路径植入 **ADR-077 强制解码守卫** —— `ImagePreprocessing.cs:28` `SKCodec.Create(stream) ?? throw Error(VisionErrorCodes.MediaInvalid, "Image header could not be decoded.")`（**全仓唯一抛点，父级抽验命中**），接入点 `VisionArtifactStorageService.cs:178`（`SaveCoreAsync`）。该提交**同步更新了平台侧测试**（`PuddingPlatformTests/Services/VisionArtifactStorageServiceTests.cs`）**但漏更集成侧**（`git show --name-only 74ae4e0` 无任何 IntegrationTests 文件 —— **父级抽验**）。
  - **夹具是伪造 PNG**（**父级抽验命中**）：`FeishuInboundImageTests.cs:142` 与 `FeishuInboundPostTests.cs:344` 均为 **8 字节** `[0x89,0x50,0x4E,0x47,0x0D,0x0A,0x1A,0x0A]`（只有签名、**无 IHDR chunk**）；`SendImageToolTests.cs:64` 更只有 **4 字节** `[0x89,0x50,0x4E,0x47]`。合法 PNG 至少需 8 字节签名 + IHDR ⇒ **必然不可解码**；测试期望的仍是旧契约「**声明 MIME 可信 + 字节原样存储**」（`FeishuInboundImageTests.cs:86` 所断言的 base64 恰为 `iVBORw0KGgo=`，即这 8 字节）。
  - **夹具早于守卫 1~1.7 个月**：`ff30d3d`(07-26) / `8b89cc6`(07-31) / `768f5a0`(08-13)；守卫后该目录仅被动过一次 `b20e95c`(09-19，依赖漏洞清理，**未碰这 3 个用例**，父级抽验) ⇒ **三例自 2026-09-15 起从未通过**（守卫前因「原样存储」语义本可通过）。
  - **#2 的 `KeyNotFoundException` 是同根因级联（已证）**：同一份伪 PNG ⇒ `FeishuInboundMessageMapper.cs:164-171` 逐图 `catch` 吞掉 `VisionPipelineException`（「**丢图不丢文**」是产品设计，同套件已通过的兄弟用例 `FeishuInboundPostTests.cs:202` 反向断言该键不存在），随后 `:178-180` 的 `if (artifactIds.Count > 0)` 守卫使 `visionArtifactIds` **从未写入** ⇒ 读取处 `:128` 抛 KeyNotFound。**同一根因在 #1 表现为异常外抛、在 #2 表现为缺键。**
  - **原生依赖不缺（已证，否证「环境缺失」）**：`Tests/PuddingAgent.IntegrationTests/bin/Debug/net10.0/runtimes/win-x64/native/libSkiaSharp.dll`（11,611,680 B，2026-02-06）与 managed `SkiaSharp.dll`（490,016 B）**均在**；且抛的是受管守卫的 `??` 空分支消息，而非 `DllNotFoundException`/`TypeInitializationException` ⇒ 原生库已成功加载并执行。
  - **两层检测缝（解释力，已证）**：下载层 `FeishuInboundMessageMapper.cs:336-346` **只嗅 magic bytes**（长度≥8 且前缀匹配即判 `image/png`）⇒ 8 字节 payload **在下载层被接受、在存储层被拒**。
- **明礁未证（不主张任何产品缺陷）**：①「真实飞书入站图片会被该守卫误拒」——**未证**（真实图含完整 IHDR/IDAT，静态分析不支持该假设）；② 两层 MIME 检测不一致是否构成**生产影响**——**未证**（仅证它是测试失效的缝）；③ #2 的「同一异常被吞」未由**运行时日志直读**（mapper 注入 `NullLogger`），属代码级演绎。
- **修复（属写操作，需另行授权，未执行）**：把 3 处夹具换成**合法最小 PNG**，并同步 `FeishuInboundImageTests.cs:86` 与 `FeishuInboundPostTests.cs:128` 的期望值。
- **附带观察（非当前影响，仅风险提示）**：同样的「伪 PNG」造法还出现在 `Tests/PuddingHost.Tests/Platform/UserAvatarApiControllerTests.cs:93`、`Tests/HarnessAgent.Core.Tests/Feishu/FeishuClientReplyTests.cs:134,207,362` —— 这些用例**当前通过**（不经保存路径），但一旦路由到解码守卫即会失效。

## 六、⚠️ 文件锁造成的口径污染（影响所有人）

| 持有者 | 锁定目录 | 受害工程 |
|---|---|---|
| `PuddingAgent (PID 23764)` | `Source/PuddingAgent/bin/Debug/net10.0/*` | `PuddingWebApiTests`、`PuddingAgent.IntegrationTests` |
| `PuddingDesktop (PID 42912)` | `Source/PuddingDesktop/bin/Debug/net10.0-windows10.0.17763.0/*` | `PuddingDesktop.Tests` |

⇒ 这三个工程**无法常规全量构建**。**严格 HEAD 口径必须在停止宿主后复跑**（本次操作未重启，遵守只读约束）。
今后凡涉及这三工程的绿/红结论，**必须附带口径声明**（是否为 `BuildProjectReferences=false`、引用 dll 的时间戳）。

## 七、平台限制（协作通道）

`send_message` 向 `room:default` / `@all` **广播被拒**：
`广播目标暂不支持：@all / room:* 没有单点 ExternalConversationId，当前仅支持 1:1 私聊回信`。
⇒ 本工作区消息通道**仅支持 1:1**，无法一次性触达全员；故本报告同时以仓库内文档形式落档，便于各线 owner 发现。
另：本批 rsi/G4 相关提交**不带 `[agent:*]` 前缀**，git 元数据无法定位具体 Agent 归属。

## 八、判「既有红 vs 今日回归」的可复用三步法（本次用到）

1. `git log -n N -- <失败测试文件>` → 测试文件最后改动时间；
2. `git log -S "<相关字符串/宏>" -- Source/` → 该符号的引入/消失时间；
3. **限定时间窗复跑**：`git log -S "<token>" --since=<今日或指定窗> -- Source/` → **为空即可断言非该窗口引入**。
全程**无需 checkout / stash**，可在共享工作区**只读**完成，不干扰并发写入者。

---
_本报告由回归基线任务产出；如需更新，请勿直接改写结论，按新证据追加「已证/未证」标注。_
