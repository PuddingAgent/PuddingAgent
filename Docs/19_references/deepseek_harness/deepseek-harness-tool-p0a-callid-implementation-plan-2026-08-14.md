# T05 P0-A callId 身份闭环 · 实施方案（2026-08-14）

> 出方案：蜜糖 ｜ 施工：默认助手（用户指定分工）
> 依据：`deepseek-harness-tool-system-alignment-2026-08-14.md` B:70-94（差距）/ B:227-254（callId 契约）；`deepseek-reference-architecture-master-plan-2026-08-14.md` T00/T05 卡（A:1622-1636）；digest `temp/master-plan-tool-alignment-digest-20260814.md`

## 1. 目标与范围

**目标**：
1. callId 不可变、全链路透传（修执行层身份断裂）
2. SSE 帧携带 toolCallId（前端可按 id 配对，替代 P1-1 按序同名过渡策略）
3. T00 最小子集：ToolCallId 值对象 + 事件信封最小增强

**范围**：PuddingCore（值对象/合同）、PuddingRuntime（ToolExecution* 族）、PuddingPlatform（SSE 投影/事件发射）。

**非目标**：ToolExecutionResult string 形态（P0-B）；全局信封重构（T00 全量）；前端配对升级（另任务）；禁碰区（§5）。

## 2. 现状断点（施工先复读确认）

- 工具调用请求与结果各生成新 Guid，身份断裂（B:87-93）——call 与 result 无法稳定关联
- SSE tool_call/tool_result 帧缺 toolCallId——前端 ToolCallRow 只能按序+同名配对（并发同名工具可能错配）
- ID 裸 string/Guid 流转，无值对象（T00 缺失）

## 3. 实施步骤（原子顺序）

1. **值对象**：PuddingCore 新建 `readonly record struct ToolCallId`（string 或 Guid 基底二选一，RISKS 注明选型理由）+ `NewToolCallId()` 工厂 + `Parse/TryParse` + `ToString`；新代码禁裸 string 流转，旧代码渐进迁移不强制全量。
2. **透传**：定位身份断裂点（B:87-93），callId 在调用创建处生成**一次**，经 ToolExecution* 全链路（请求→执行→结果→日志）不可变透传；结果侧移除 NewGuid 覆盖。
3. **SSE 帧**：tool_call/tool_result 帧携带 `toolCallId`（camelCase；旧字段若存在保留兼容）。
4. **信封最小增强**：tool 相关事件（tool_call/tool_result/approval.requested 等）payload 补 `toolCallId` 可选字段；不重构全局信封（留 T00 全量，RISKS 记录）。
5. **测试**：值对象单测（生成/解析/相等）；透传集成测试（call→result callId 一致）；SSE 帧合同测试（帧含 toolCallId）。
6. **兼容**：旧客户端忽略新字段不破坏；历史数据无 toolCallId 投影为 null（前端回落按序配对）。

## 4. 验收标准

- PuddingCore/Runtime/Platform 三项目 build 0 错误
- 新增测试绿 + 工具相关既有测试无回归
- 合同核验：SSE 帧样本含 toolCallId；call 与 result callId 一致（集成测试断言）
- 零前端文件改动；禁碰区零改动

## 5. 边界与纪律

- **禁碰区**：`ContextCompaction*`/`CompactionCoordinator`/`AgentDiagnosticsTool`/`SessionEventsController` compaction 区（6a8 compaction ②在途）；`MessageDeliveryDispatcher`/`MessageFabric*`（队列已闭环）；全部前端文件。
- commit 用 hunk 分离（用户并发开发面仍在）；只推自己 commit；commit 前互知会。
- **原型参考**：蜜糖侧子代理 `sub-3ed1a0e4` 将交付工作区原型（未 commit，仅可行性验证）。施工方可评审后复用（推荐，复用部分以施工方名义 commit）或独立实现；若复用前先与蜜糖核对原型验收状态。

## 6. 后续衔接

- P0-A 收口 → 前端 ToolCallRow 按 toolCallId 配对升级（蜜糖侧前端任务）
- P0-B（Schema+canonical output）/P0-C（结构化错误+14 步管线）等 6a8 compaction ②③ 收口后接，动手前先对齐契约文件归属
