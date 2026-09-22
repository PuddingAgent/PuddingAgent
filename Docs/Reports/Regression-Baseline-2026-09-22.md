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

**合计：12 条红，其中 1 条已修（见二）；余 11 条待各线分诊。**

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
- **未证但可疑**：两次运行均伴随 `[Startup] DB migration skipped — using pre-built database` 与
  `Failed executing DbCommand (…) ALTER TABLE room_messages ADD COLUMN conversation_id TEXT;`（旧口径 74 条 / 新口径 81 条）
  ⇒ 疑为**预置 DB 被运行中宿主占用，导致一次性列迁移失败 → 依赖该列的端点返回 500**，属环境/并发而非代码回归。
- **闭环方法**：在**独占 DB / 停止宿主**的口径下复跑该工程；并单独归因 `conversation_id` 列的迁移路径（`0742fc6` 2026-09-19「压缩 SQLite 一次性列迁移 —— 只保留最终 DDL」）。

## 五、未归因（3）

`Tests/PuddingAgent.IntegrationTests`（首次入基线）：失败 3 / 通过 15 / 总计 18

| 用例 | 位置 | 首个错误 |
|---|---|---|
| `Feishu.FeishuInboundImageTests.ImageEvent_DownloadsOnceAndMapsToCanonicalVisionArtifactMetadata` | `:55` | `PuddingCode.Core.VisionPipelineException: Image header could not be decoded.` |
| `Feishu.FeishuInboundPostTests.PostEvent_WithImages_MaterializesArtifactsInOrder` | `:128` | `KeyNotFoundException: 'visionArtifactIds'` |
| `Feishu.SendImageToolTests.ExecuteAsync_QueuesArtifactToCurrentTrustedFeishuRoute` | `:65` | 同 `Image header could not be decoded.` |

- **已证**：该工程最后改动 `b20e95c`(**2026-09-19**)；`git log --since=2026-09-21 -- Source/…/Vision …/Feishu …/Rooms` **为空**（09-21 起无提交触碰相关路径）；`git ls-files -- Tests/PuddingAgent.IntegrationTests` **图像资源零命中**，测试源码内也搜不到图像文件路径引用 ⇒ 图像应为内联构造，"header 无法解码"更像**解码依赖/环境**问题。
- **未证**：是否与原生图像解码依赖（如 SkiaSharp 类库）缺失或版本相关。

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
