---
title: PA-02：Agent 与 SubAgent 工厂、工作控制和池租约
author: hyfree
date: 2026-11-05
last_reviewed: 2026-11-05
status: proposed
description: "提供存储无关、宿主绑定身份的代理实例与工作 API，明确创建、借用、委派、取消和池租约所有权。"
categories: [docs, features]
tags: [agents, delegation, sdk, lifecycle]
related_docs: [Docs/12_features/Agent组件化任务书-总则与依赖-2026-11-05.md, Docs/12_features/Agent单轮执行API-P2-契约与接入说明-2026-11-05.md]
related_files: [Source/Pudding.Api.Contracts/AgentWork.cs, Source/PuddingCore/Abstractions/ISubAgentPool.cs, Source/PuddingPlatform/Services/SubAgentPool.cs, Source/PuddingPlatform/Services/AgentWork/CanonicalAgentWorkBackend.cs]
slug: features-agent-taskbook-02-agents-20261105
draft: false
---

# PA-02：Agent 与 SubAgent 工厂、工作控制和池租约

遵守[总则](Agent组件化任务书-总则与依赖-2026-11-05.md)。02A 的持久工作控制/父子身份依赖 01；02B 的工厂、会话和池可以随后交付，不阻塞只读模型验收切片。

## 1 目标与边界

上层拿到已受权 client 后可列举/创建 Agent、取得实例句柄、提交工作、等待或取消指定工作、委派子任务；不接触模板 JSON、数据库、模型密钥或 Runtime DI。构造 client 不启动 Core、不加载模型、不隐式付费。使用显式异步工厂，不能用 `new Agent()` 隐式寻找全局宿主。

当前 `PuddingClient` 已有目录 Get 和配置入口；`AgentWorkClient` 只支持 Submit/Get。`ISubAgentPool` 已有 Create/Get/Execute/Sleep/Destroy/List，但没有公共原子租用合同。复用它的实际行为与 canonical Work 后端，不能再造第二套执行队列。既有单轮 `Completed` 不改成 Goal 验收通过。

## 2 主代理代码范围

| 所在层 | 文件与动作 |
|---|---|
| Public DTO，拟新增 | `Source/Pudding.Api.Contracts/AgentDirectory.cs`、`AgentWorkControl.cs`、`AgentDelegation.cs`；创建/目录、观察/取消、委派/租约分别定义 |
| SDK，已有与拟新增 | `Source/Pudding.Api.Sdk/PuddingClient.cs`、`AgentWorkClient.cs`；拟增 `AgentFactoryClient.cs`、`AgentDelegationClient.cs`，只依赖公共 DTO |
| 独立组件，拟新增 | `Source/PuddingAgents/PuddingAgents.csproj`、`AgentDirectoryService.cs`、`AgentFactoryService.cs`、`AgentOwnershipPolicy.cs`；配套 `PuddingAgents.Contracts` 的 BCL 后端端口 |
| 02A 适配 | `Source/PuddingPlatform/Services/AgentWork/CanonicalAgentWorkBackend.cs`、`Services/Conversation/RequestTurnCancellationHandler.cs`、`Services/AgentChat/ExecutionRunCoordinator.cs` |
| 02B 适配 | `Source/PuddingPlatform/Services/WorkspaceAgentFileService.cs`、`WorkspaceAgentQueryServiceAdapter.cs`、`SubAgentPool.cs`、`SubAgentManager.cs`；`Source/PuddingRuntime/Services/SubAgentInvocationService.cs` |
| 独立验证，拟新增 | `Source/PuddingAgentsTests/PuddingAgentsTests.csproj`、`AgentFactoryTests.cs`、`DelegationOwnershipTests.cs`、`AgentWorkControlTests.cs` |

02A 先定义端口与独立测试，再在 S5 接入适配器。02B 不同时更改模板含义、配置存储格式和 SubAgent 推理逻辑；若抽取后必须反向引用 Runtime，该文件留在适配侧并报告原因。

## 3 契约：意图、所有权与幂等

下面是拟新增能力，不是现有 SDK 示例；主代理须交付可编译接口再允许消费。

| 端口 | 方法形状 | 必须保证 |
|---|---|---|
| `IAgentDirectoryApi` | `ListAsync(AgentListQuery, ct)`、`GetAsync(AgentRef, ct)` | 仅受权实例；分页给 Cursor/Complete；不能因截断返回空而声称不存在 |
| `IAgentFactoryApi` | `CreateAsync(CreateAgentRequest, ct)`、`ArchiveAsync(ArchiveAgentRequest, ct)` | 创建需 OperationId、Workspace、模板引用与版本；同键同内容同回执，冲突拒绝；不启动推理 |
| `IAgentWorkControlApi` | `ObserveAsync(ObserveWorkRequest, ct)`、`CancelAsync(CancelWorkRequest, ct)` | 绑定精确工作回执和事件序号；取消是持久业务命令；不能按“最后一条消息”找目标 |
| `IAgentDelegationApi` | `SubmitChildAsync(DelegateWorkRequest, ct)`、`AcquireAsync(AcquireSubAgentRequest, ct)`、`ReleaseAsync(ReleaseSubAgentRequest, ct)` | 父身份、预算由 bound context 注入；不能让调用方伪造 root/actor；原子租用 |

`CreateAgentRequest` 的配置种子只能是已公开的类型化字段；未知字段拒绝，不接受任意 JSON 文件路径。模板更新不静默覆盖现有实例。Archive 只封存目录实例，不删除用户产物；有活跃工作时返回 `agent_busy`，本任务不提供递归强删。

句柄所有权：Get/List 得到 **borrowed handle**，Dispose 只释放订阅/本地代理，不销毁 Agent。Create 返回持久实例回执，不因 HTTP 断连删除。会话若由调用方显式创建，必须有 SessionId、OwnerId、关闭策略；关闭会话和取消其中工作分别请求，不能把 `DisposeAsync` 作为无审计的全局取消。

`CancelWorkRequest` 必须有 OperationId、目标 WorkId/原回执身份与期望版本。SDK 的等待 token 取消只退出 Observe，不发送 Cancel。取消服务找到 canonical Command/Turn 后调用既有取消入口，并传播 01 的根/子账户；返回 Requested/AlreadyTerminal/NotFound/Forbidden 等效果，Requested 不等于已终止。

委派请求含任务文本、能力集合、子级限额、结果交付方式；宿主从真实 parent Work 生成委派身份。子级限额不能大于父级可分配范围，不能借新 Session/模板换根。等待子任务不能持有父 Worker 请求槽造成“子任务调用同一插件”的死锁；此类调用用 07A 持久任务票据，或明确返回 `reentrant_dependency`。

## 4 原子池租约

租用回执至少含 `LeaseId/SubAgentId/Generation/OwnerWorkId/ExpiresAt/Status`。Acquire 在一把领域锁或存储 CAS 中执行选择与占有，不能 List 后再 Execute 抢同一实例。复用条件包括模型/工具/上下文策略版本匹配；身份不兼容则新建或拒绝，不能串用户历史。

Release 校验 owner 和 generation。忙碌租约返回 `lease_busy`；如调用者需要停止，先显式取消关联工作，观察真实排空后再 Release。重复释放幂等，旧 generation 的迟到 Release 不得释放新持有人。过期租约先隔离再调和，不能直接把仍在执行的 Agent 标为可用。父任务取消默认取消其 owned 子任务；脱离父级的后台任务必须另经授权创建，不能偷偷转成孤儿任务。

## 5 实施步骤与验收

1. **02A-1**：公共工作控制/父子 DTO 与绑定后端端口；用假 canonical store、假 01 账户写独立测试。
2. **02A-2**：接既有 CancelTurn 与事件 store；明确 cursor 过期时返回 `snapshot_required`，事件过滤后仍能判断是否缺页。测试跨用户伪造工作 ID 必须拒绝。
3. **02A-3**：给 03 的验收 Job 只读子任务身份/账户租约，不强制完整子 Agent 推理。禁止把已经 Dispose 的 dispatch scope 传下去。
4. **02B-1**：目录/创建/封存与受权分页；接现有 WorkspaceAgent 文件服务，不复制读写实现。
5. **02B-2**：池的原子租用与代际释放；保留现有 SubAgent 执行入口，验证上下文隔离。
6. **02B-3**：SDK 和插件 callbacks 由主代理接入；声明新 capability/version，缺少授权返回能力不可用，不退回反射找服务。

必须取红的场景：同 OperationId 不同创建参数；两个并发 Acquire 只有一个得到实例；旧租约 Release；等待取消不取消工作；父取消后新子任务零派发；重新连接观察不漏终态；不同 Workspace 相同 Agent 名不串实例；故意给 public/领域工程添加 Runtime/Platform 引用时 build 失败。只有假实例测试通过不算真实池适配通过，S5 必须对实际 `SubAgentPool` 的竞态做集成测试。

新独立测试按总则运行 `Source/PuddingAgentsTests/PuddingAgentsTests.csproj`。再跑现有 SDK 单轮测试、DS-W01 示例测试和原 SubAgent/取消回归；不得通过改变原 `Completed` 语义让新测试变绿。

## 6 DS-PA02-A：代理目录消费示例

状态 `blocked-on-contract`。主代理交付 `IAgentDirectoryApi` 与 SDK 后，只允许创建：

```text
Samples/AgentDirectoryClient/AgentDirectoryClient.csproj
Samples/AgentDirectoryClient/Program.cs
Samples/AgentDirectoryClient/DirectoryDemo.cs
Samples/AgentDirectoryClient/code_map.md
Tests/AgentDirectoryClient.Tests/AgentDirectoryClient.Tests.csproj
Tests/AgentDirectoryClient.Tests/DirectoryDemoTests.cs
Tests/AgentDirectoryClient.Tests/code_map.md
```

固定入口 `DirectoryDemo.RunAsync(string[] args, IAgentDirectoryApi api, TextWriter output, TextWriter error, CancellationToken ct = default)`。仅 `get <workspaceId> <agentId>` 与 `list <workspaceId> [cursor]`；恰好一次 API，原样保留字符串，输出原 DTO JSON，包括 Cursor/Complete；不自动翻页、不创建、不取消、不销毁实例。合法空页 exit 0，get 不存在 exit 3，参数错 2，调用方取消 130，类型化错误仅 code exit 1。Program 只给固定离线提示 exit 2。

测试 null、partial 空页、跨 Workspace 参数原样传递、空白参数零调用、无隐式第二次查询。按总则构建白名单测试工程，产物 `.work/ds-pa02-a/<run>/`，sample 仅引用 SDK，test 仅引用 sample。交付仅 `ready-for-review`；不能为了示例可运行增加 HTTP、环境变量、密钥文件或修改工厂实现。
