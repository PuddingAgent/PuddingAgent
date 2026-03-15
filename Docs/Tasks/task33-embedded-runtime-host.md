# task33 - 嵌入式 Runtime 宿主与桌面软件节点

最后更新：2026-03-15

## 任务目标

支持将 `PuddingRuntime` 嵌入到其他 C# 开发的桌面软件中，把这些桌面软件视为可调度的 Runtime 节点，并以受控方式暴露宿主原生能力。

对应架构：
- [../07架构/03PuddingRuntime.md](../07架构/03PuddingRuntime.md)
- [../07架构/06PuddingAgent与客户端.md](../07架构/06PuddingAgent与客户端.md)
- [../07架构/07协作网络与治理.md](../07架构/07协作网络与治理.md)

## 前置依赖

- [task26-runtime-foundation.md](task26-runtime-foundation.md) 已建立 `PuddingRuntime` 基础宿主。
- [task27-controller-routing-session.md](task27-controller-routing-session.md) 已具备 Runtime 节点注册与基础调度协议。
- [task29-agent-template-and-audit.md](task29-agent-template-and-audit.md) 已具备模板权限和运行画像读取链路。

## 可并行关系

- 可与 [task30-knowledge-infrastructure.md](task30-knowledge-infrastructure.md) 后段并行。
- 可与 [task31-client-surfaces.md](task31-client-surfaces.md) 并行，但二者职责不同：客户端是控制面入口，嵌入式宿主是运行节点。
- 联调依赖 Controller、Runtime 和权限治理链稳定。

## 顺序任务

1. 定义嵌入宿主抽象
说明：建立 `EmbeddedRuntimeHost`、`NativeHostBridge`、`NativeCapabilityDescriptor` 等最小抽象。
输出：可嵌入宿主接口与能力描述模型。

2. 建立节点注册模型
说明：让嵌入宿主能够向 Controller 注册自己是一个 Runtime 节点，并携带宿主类型、原生能力、健康状态。
输出：嵌入节点注册协议与状态上报接口。
前置依赖：任务 1；依赖 [task27-controller-routing-session.md](task27-controller-routing-session.md)。

3. 建立宿主原生能力桥接
说明：把桌面软件原生功能包装为受控 Runtime 能力，例如查询软件状态、调用宿主命令、驱动测试、读取日志或对象模型。
输出：NativeHostBridge 最小桥接层。
前置依赖：任务 1。

4. 接入权限与审批链
说明：宿主原生能力不能默认开放，必须纳入模板权限、Workspace 策略、审批链和审计链。
输出：嵌入宿主能力的受控执行策略。
前置依赖：任务 3；依赖 [task29-agent-template-and-audit.md](task29-agent-template-and-audit.md)。

5. 建立测试软件场景样例
说明：至少选一个 C# 桌面测试软件或测试宿主，验证查询软件状态、执行测试、读取结果三类能力。
输出：嵌入式 Runtime 节点样例宿主。
前置依赖：任务 2、任务 3、任务 4。

6. 接入调度与审计查询
说明：让 Controller 能感知嵌入节点的可调度状态、原生能力调用记录和冻结控制。
输出：嵌入节点调度与审计链路。
前置依赖：任务 5。

## 验收标准

- 一个 C# 桌面软件可以嵌入 `PuddingRuntime` 并注册为平台节点。
- Controller 能识别其为可调度 Runtime 节点，而不是普通客户端。
- Agent 可以在审批和权限约束下调用宿主原生能力。
- 至少验证“查询软件状态”“驱动测试”“读取结果”三类原生能力。
- 宿主原生能力调用可审计、可限制、可冻结。
