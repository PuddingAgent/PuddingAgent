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
