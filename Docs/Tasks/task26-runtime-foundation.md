# task26 - PuddingRuntime 基础宿主

最后更新：2026-03-15

## 任务目标

建立 `PuddingRuntime` 作为 Agent Runtime 宿主的最小可运行骨架，明确 Session 承载、Agent 执行、记忆处理、技能装配和受限执行的内部边界。

对应架构：
- [../07架构/03PuddingRuntime.md](../07架构/03PuddingRuntime.md)
- [../07架构/01总览与分层.md](../07架构/01总览与分层.md)

## 前置依赖

- 架构总览与 Runtime 分册已经稳定。
- `PuddingRuntime` 是 Agent Runtime 的结论已经确定。

## 可并行关系

- 可与 [task27-controller-routing-session.md](task27-controller-routing-session.md) 并行推进。
- 可与 [task29-agent-template-and-audit.md](task29-agent-template-and-audit.md) 的模板定义部分并行推进。
- 不应早于 [task27-controller-routing-session.md](task27-controller-routing-session.md) 完成集成联调。

## 顺序任务

1. 建立 `PuddingRuntime` 宿主入口
说明：独立进程、生命周期、基础 DI、配置加载、健康探针。
输出：最小可启动 Runtime host。

1A. 预留宿主适配接口
说明：在不阻塞独立宿主主链路的前提下，预留 `EmbeddedRuntimeHost` 或等价宿主适配抽象，使后续可把 Runtime 嵌入其他 C# 桌面软件。
输出：最小宿主适配接口。
前置依赖：任务 1。

2. 建立 `SessionRuntime` 最小模型
说明：承载 `ServiceSession`，维护基础会话热状态、元数据和状态上报。
输出：`SessionRuntimeRecord`、基础状态存取接口。
前置依赖：任务 1。

3. 建立单 Agent 执行视角
说明：不再引入独立 `AgentRuntime` 宿主概念，而是在 `PuddingRuntime` 内建立单 Agent 执行上下文，例如 `AgentExecutionContext`。
输出：Agent 实例创建、加载、销毁、状态查询接口。
前置依赖：任务 2。

4. 接入最小真实执行链路
说明：基于模板创建 AgentInstance，完成单轮真实 LLM 回复。
输出：`AgentExecutionService`、执行请求/结果对象。
前置依赖：任务 3。

5. 接入 `PuddingMemoryEngine`
说明：提供记忆召回、候选写回、边界校验和污染筛查的最小链路。
输出：`PuddingMemoryEngine`、`MemoryBoundaryService`、最小 recall/write candidate 流程。
前置依赖：任务 3。

6. 接入 `SkillRuntime` 与 `SandboxExecutor`
说明：装配低风险 Skill，并为高风险执行预留受限执行环境。
输出：最小技能装配与受限执行骨架。
前置依赖：任务 4。

7. 支持 sub_agent 承载
说明：由主 Agent 派生临时 sub_agent，支持创建、回收、结果回传与生命周期约束。
输出：`SubAgentExecutionContext` 或等价内部模型。
前置依赖：任务 4、任务 5。

## 验收标准

- Runtime 可以独立启动并上报健康状态。
- Runtime 能承载 `ServiceSession`。
- Runtime 能根据模板创建 AgentInstance 并返回真实回复。
- Runtime 能执行最小记忆召回和候选写回。
- Runtime 能承载由主 Agent 派生的临时 sub_agent。
