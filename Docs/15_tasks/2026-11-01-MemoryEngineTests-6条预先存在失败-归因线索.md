# 2026-11-01 PuddingMemoryEngineTests 的 6 条预先存在失败（归因线索，未修）

## 已证实

1. **预先存在**：把当次的依赖上提改动 `git stash` 掉再跑同一组用例 ⇒ 同样 6 条红（对照实验，非推断）。
2. **测的是活代码**：`MemoryLibraryTests.cs` 引用 `PuddingRuntime.Services`，即 Runtime 的 `ContextPipeline`，
   不是 MemoryEngine 里的历史副本。
3. **该工程不在日常门禁里**：我的九工程基线（Runtime / CodeIndex / CodeIntelligence / FullText / Retrieval /
   Retrieval.Contracts / Domain.Contracts / Adapters / Grep）不含它，所以这片红一直没被看见。

## 六条失败与原始消息

| # | 用例 | 失败消息（节选） |
|---|---|---|
| 1 | `ContextPipeline_ShouldAssembleAll7Layers` | `'condition'`: `result.Layers.Any(l => l.LayerName == "环境信息")` 为 false |
| 2 | `EnvironmentLayer_ShouldBePresentInAssemblyResult` | `Assert.IsNotNull` 失败，`envLayer` 为 null（"Environment layer should exist in Layers"） |
| 3 | `ContextPipeline_ShouldTriggerGentleCompaction` | `System.InvalidOperationException: Sequence contains no matching element`（某处 `First()` 找不到层） |
| 4 | `Health_RejectsUsageFromBeforeCheckpoint_InMemoryAndAfterRestart (False)` | 断言失败（同族） |
| 5 | `Health_RejectsUsageFromBeforeCheckpoint_InMemoryAndAfterRestart (True)` | 断言失败（同族） |
| 6 | `ProviderUsage_DoesNotMislabelAnOverestimate_OrWeakenOutboundGuard` | 期望 `<1>`，实际 `<0.5887177835680338>`；表达式 `store.GetPromptCalibrationRatio("s", "deepseek-flash")` |

## 共同线索（假设，未证实）

1/2/3 是**同一条**：组装的 Layers 里**没有「环境信息」层**。三条分别从"存在性断言"、"取层为 null"、
"`First()` 抛无匹配"三个角度撞上同一事实。

4/5 与 6 是另一族（用量校验与校准比例），与「缺层」无关 —— 不要并成一件事。
6 的期望值 1.0 与实测 0.5887 说明**校准比例的默认/累计口径变过**，测试仍按旧口径写。

## 下一步（未做）

1. 先查 `PuddingRuntime/Services/ContextPipelineLayers.cs` 与 `ContextPipelineOrchestrator.cs` 里
   「环境信息」层的组装条件 —— 是**无条件组装**（则测试对、代码退化了）还是**有条件组装**（则测试陈旧）。
2. 结论二选一：补回环境层，或把测试改成按当前契约断言。
3. 无论哪种，都该把这个工程纳入门禁 —— 否则下一个 6 条红还会躺很久。

## 复现

```
dotnet test Source\PuddingMemoryEngineTests\PuddingMemoryEngineTests.csproj
```

## 查清了：1/2/3 是**层标签改名**后的陈旧断言（2026-11-01 04:20）

从 `ContextPipelineOrchestrator.cs` 逐条抽出 `RecordLayer(sb, content, snapshotLabel, layerName, …)`：

| `layerName`（进 `ContextLayerInfo` 与 `--- CONTEXT-LAYER: … ---` 标记） | `snapshotLabel`（进 `result.Layers[*].LayerName`） |
|---|---|
| `L0-STATIC` | 静态上下文 |
| `L0-ENVIRONMENT` | **运行环境不变量** |
| `L0-AGENTS-ROSTER` | 工作区 Agents |
| `L1-TOOLS` | **工具使用规则** |
| `L2-SKILLS` | **技能使用规则** |
| `L3-WORKSPACE-ENVIRONMENT` | 工作区环境 |
| `L2-INHERITED` | 继承上下文 |
| `L4-PINNED` | 重要记忆 |
| `L9-USER-PREFERENCES` | 用户偏好记忆 |

`result.Layers` 里的名字是**右列**（snapshotLabel）。测试断言的是左列时代的旧中文名：

| 测试断言 | 现状 |
|---|---|
| `"环境信息"` | 已改名为 **运行环境不变量** |
| `"动态工具"` | 已改名为 **工具使用规则** |
| `"动态技能"` | 已改名为 **技能使用规则** |
| `"静态上下文"` / `"用户偏好"` / `"当前消息"` / `"运行时指令"` | 仍存在，断言有效 |

⇒ 1/2/3 三条**不是代码退化**，是层标签重构后测试没跟着改。

### 为什么没顺手改

断言是顺序执行的，只报第一个失败（`环境信息`）。把这一条改掉之后，`动态工具`/`动态技能` 大概率接着红。
改对需要同时确认「每个 label 在 `Layers` 里出现几次、顺序如何」，否则容易把断言改成「恒真」。

**正确做法**（下一轮）：按上表一次性替换三个旧名，然后确认这 3 条转绿、且断言仍能取红
（临时把某个 label 改回旧值必须红）。半改会留下「假绿」。

## 4/5/6 仍未定性

`Health_RejectsUsageFromBeforeCheckpoint` ×2 与 `ProviderUsage_*`（期望 1.0，实测 0.5887）
属于用量校验 / 校准比例一族，与「缺层」无关。尚未读代码，**没有证据说明是陈旧还是退化**。

## 一次被证伪的猜测（记录以免重犯）

`ContextPipeline_ShouldTriggerGentleCompaction` 里那句是：

```csharp
var recentLayer = result.Layers.First(l => l.LayerName == "近期历史");
```

按上一节的对照表，我猜它是「近期历史 → 历史上下文」的又一次改名，改成 `"历史上下文"` 后重跑：

**仍然是 `Sequence contains no matching element`** —— 场景里根本没有这个名字的层。

⇒ 猜测证伪，改动已 `git checkout` 回退。这条**不是**标签改名问题，可能的成因至少两种：

1. Gentle 压缩在该场景下根本没触发（历史没被摘要化 ⇒ 不产生历史层）；
2. 该层用了第三种名字（不在 `RecordLayer` 的字面量里，可能由更下层拼出）。

要定位得看 `AssembleAsync` 在 `maxTokens: 8000` + 30 条 200 字历史下的实际层输出——
打印一次 `result.Layers` 的 `LayerName` 列表即可判定，不需要猜。

## 当前状态

6 条中：**2 条已修好并取红验证**（层标签改名）；1 条已证伪猜测、待查实际层列表；3 条（Health ×2 / ProviderUsage）未读代码。

## 观测结果：那个场景里**没有任何历史层**（2026-11-01 05:25）

按上一节说的「先观测」，临时把断言换成 `Assert.Fail(层名:token 列表)` 跑了一次（跑完已 `git checkout` 回退）。
30 条 × 200 字历史 + `maxTokens: 8000` 的实际组装结果：

```
静态上下文:203 | 运行环境不变量:37 | 工作区 Agents:14 | 工具使用规则:393 |
技能使用规则:1009 | 工作区环境:20 | 用户偏好:15 | 重要记忆:22 |
运行时目录更新:81 | 运行时目录更新:84 | 运行时指令:73 | 当前消息:10
```

**12 个层里没有任何一个是历史相关的**（没有「历史上下文」「近期历史」「入站消息上下文」）。
`当前消息` 只有 10 tokens。

⇒ 测试的前提（"Gentle 压缩下远期历史摘要化为「近期历史」层"）在当前实现里**两种形式都不成立**。

## 但这还不能定性，下一步只差一次读取

两种解释都成立，必须区分：

1. **压缩把历史整个丢掉了**（丢历史 ≠ 摘要）—— 那就是真缺陷，值得修；
2. `CreateDefaultContextRequest(sessionHistory: …)` 没把历史真正接到 pipeline —— 那是测试构造问题。

区分方法：读那个 helper，确认 `sessionHistory` 是否被赋给请求对象的字段。
有 → 落到解释 1（真缺陷）；没有 → 落到解释 2（测试问题）。

本轮到此为止：不再靠猜，也不再改断言。该条已登记在 `TestScripts/staged-test-baseline.json`
的 `memoryEngineContextPipeline` 组里，门禁不会因它阻塞。

## 第二次证伪：`IsFirstMessage = false` 也不改变层清单（2026-11-01 05:48）

上一节说「只差一次读取」：`CreateDefaultContextRequest` 确实把 `SessionHistory` 赋给了请求
（`SessionHistory = sessionHistory ?? Array.Empty<…>()`），所以历史**是**传进去的。

于是第二假设：helper 里 `IsFirstMessage = true`，「首条消息」语义下不组装历史层。
把该用例改成 `with { IsFirstMessage = false }` 后重跑 —— **仍然 `First()` 无匹配**。

再把断言换成层名转储，`IsFirstMessage = false` 下的结果与原来**逐字相同**：

```
静态上下文:203 | 运行环境不变量:37 | 工作区 Agents:14 | 工具使用规则:393 |
技能使用规则:1009 | 工作区环境:20 | 用户偏好:15 | 重要记忆:22 |
运行时目录更新:81 | 运行时目录更新:84 | 运行时指令:73 | 当前消息:10
```

⇒ 这个 fixture 构造出的 pipeline **在任何情况下都不组装历史层**。两次假设均被证伪，改动都已回退。

## 下一步（唯一剩下的方向）

问题不在请求参数，而在 **`CreateContextPipeline()` 的依赖装配**：很可能是历史/摘要所需的服务
（如 `IAgentMemorySummaryContextBuilder` 之类）在该 helper 里为 null 或被 mock 掉，
于是历史层被**静默跳过**。

判定方法：读 `CreateContextPipeline()` 的构造实参，看历史相关依赖是否为空；
然后与 `PuddingRuntimeTests` 里同类 pipeline 的构造对比（那边有历史层用例，装配应当是完整的）。

**不要**再靠改断言或改请求参数 —— 两次都证明无效。

## 找到根因：fixture 构造 pipeline 时 `orchestrator: null`（2026-11-01 06:10）

读了 `CreateContextPipeline()` 的构造实参：

```csharp
new ContextPipeline(
    mockMemory.Object, skillRuntime, skillRegistry, systemPromptBuilder,
    memCache, contextAssemblyStore, NullLogger<ContextPipeline>.Instance,
    envProvider: envProvider.Object,
    libraryConvenience: mockLibrary.Object,
    recallService: null,        // ← 也是 null
    orchestrator: null,         // ← 编排器没有注入
    templateProvider: mockTemplateProvider.Object,
    workspaceProfileProvider: mockWorkspaceProfile.Object,
    personaFileProvider: null);
```

`ContextPipelineOrchestrator` 正是所有 `RecordLayer(...)`（含历史层）的所在地。fixture 传 `null`，
pipeline 走的是**没有编排器的旧路径** —— 那条路径本来就不组装历史层。

⇒ **一次解释全部三次观测**：层标签是旧名、无历史层、`IsFirstMessage` 无影响。

## 定性结论（本条）

**不是生产缺陷，是测试 fixture 少接线。** 生产 DI 会注入 orchestrator；这些用例断言的是
「没有编排器时的降级路径」，与现行契约对不上。

## 处理选项（未执行，需选择）

| 选项 | 代价 | 说明 |
|---|---|---|
| A. fixture 注入真实 orchestrator | 高 | 需要补齐 orchestrator 的整条依赖链；这些用例才真正覆盖现行组装 |
| B. 认定该文件为遗留双写、按新契约重写或退役 | 中 | `PuddingRuntimeTests` 已有覆盖编排器路径的同类用例（`ContextPipelineSkillLayerTests` 等） |
| C. 仅在本文件标注「测的是无编排器降级路径」 | 低 | 不修行为，但让后来人不再把它当契约 |

我倾向 **B**：同一套语义不该在两个测试工程里各写一遍，且 Runtime 侧那份是接线完整的。

## 收口：6 条已全部有结论（2026-11-01 07:40）

| # | 用例 | 结论 | 证据 |
|---|---|---|---|
| 1 | `ContextPipeline_ShouldAssembleAll7Layers` | **已修**（层标签改名） | 改后绿；变异（标签改回旧名）红；还原 SHA 一致 |
| 2 | `EnvironmentLayer_ShouldBePresentInAssemblyResult` | **已修**（同上） | 同上，与 #1 同时变红 |
| 3 | `ContextPipeline_ShouldTriggerGentleCompaction` | **已定性，未改代码** | fixture 传 `orchestrator: null` ⇒ 走无编排器旧路径，不组装历史层；已在 fixture 上注释 + 基线登记 |
| 4 | `Health_RejectsUsageFromBeforeCheckpoint (False)` | **已修**（用量档位不符闸门口径） | 实测 EffectiveWindow=934,976 / GateRatio=used/effective / Trigger 0.8 / Blocking 0.92；原 600,000 只到 0.6417 |
| 5 | `Health_RejectsUsageFromBeforeCheckpoint (True)` | **已修**（同上） | 同上 |
| 6 | `ProviderUsage_...OrWeakenOutboundGuard` | **已修**（断言方向写反）+ 用例改名 | 设计允许系数 < 1.0（下限 0.5，安全网 `RecordProviderInputLimitFailure`）；改后绿，变异（下限改 1.0）红 |

## 总数

```
PuddingMemoryEngineTests  301 条
  改动前  295 通过 / 6 失败
  现在    300 通过 / 1 失败   ← 唯一剩余为 #3（fixture 缺 orchestrator，已注释与登记）
```

## 复用的判据（写给下一次）

1. **先观测再改**：tmp 把断言换成 `Assert.Fail(对象)` dump 出来，比读十遍代码快；
2. **改完必须取红**：把实现改回去，断言必须变红，否则就是恒真；
3. **修好立刻从基线移除**：`TestScripts/staged-test-baseline.json` 只能减，加了不删会变成垃圾桶。
