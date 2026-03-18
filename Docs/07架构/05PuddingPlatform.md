# PuddingPlatform

## 定位

PuddingPlatform 是上层平台层与系统组合层。它不等于控制面进程，而是承载产品语义、业务模型、服务暴露策略与平台能力编排的上层概念。
比如：
工作台
Workspace 产品管理
业务流程
服务目录
运营配置
产品入口
管理后台
用户可见的业务语义



参考：
### 前端PuddingPlatformAdmin

前端PuddingPlatformAdmin，admin 管理后台。提供产品级别的服务（待补充设计）。


#### PuddingPlatform 

asp.net core 提供API



## 负责的内容

- Workspace 业务模型与产品入口。
- 服务暴露策略、业务流程编排、产品能力组合。
- 平台级部署形态、运维模型与演进路线。
- 组织 Controller、Runtime、Agent、客户端之间的产品协作关系。


## 与 Controller 的边界

- Controller 负责底层控制逻辑：接入、路由、鉴权、审批、调度、审计。
- Platform 负责上层业务逻辑：Workspace 服务形态、产品语义、平台能力编排。

## 与 Runtime 的边界

- Runtime 承担实际执行与会话权威。
- Platform 不直接持有执行态，而是通过 Controller 和控制协议治理 Runtime。

## 当前阶段的 Platform 关注点

- 把产品从 coding-agent prototype 推进到 Agent OS。
- 把多 Agent、多渠道、多工作空间的业务语义稳定下来。
- 为后续 Web、Avalonia 和外部集成提供统一的产品面定义。

## Platform 上的 Agent 配置入口

`PuddingPlatform` / `PuddingPlatformAdmin` 应成为用户与管理员配置 Agent 的主要入口，但它不直接取代 Controller 的控制权威。

用户在 Platform 上创建 Agent 或由编排 Agent 请求创建新 Agent 时，建议至少能配置：

- 选择哪个 `AgentTemplate`
- 是否显式指定 `runtimeId`
- 是否只给出 `requiredRuntimeTags` / `preferredRuntimeTags`
- 默认隔离级别：`none` / `workspace` / `dedicated`
- 是否允许自动重建 sandbox
- 可见的 Skill / MCP 引用集合

如果用户没有显式指定 Runtime，则 Platform 将请求交给 Controller，由 Controller 按节点画像、Workspace 亲和性、模板偏好和内置规则选择 Runtime。

## Platform 对 Skill / MCP 的职责

Platform 不是 Skill / MCP 的权威注册中心，但它应提供产品化管理入口：

- 浏览全局 Skill / MCP Registry
- 将 Skill / MCP 绑定到 AgentTemplate
- 配置模板默认运行画像与隔离策略
- 查看 Runtime 节点画像与放置结果
- 查看 sandbox 重建、失败与运行状态

也就是说：

- **Controller 管定义权与治理权**
- **Platform 管可视化配置与产品交互入口**

## Workspace 内的多 Agent 协作驾驶舱

当一个 Workspace 内开始承载多个 Agent、TaskMap、WorkflowRun 和共享工具时，`PuddingPlatform` / `PuddingPlatformAdmin` 不应只提供“列表页 + 详情页”，而应提供一个 **协作驾驶舱（Collaboration Cockpit）**。

这个界面的核心目标不是展示所有原子事件，而是帮助用户看清：

- 当前任务图推进到哪里了
- 哪些节点正在执行、失败、阻塞或等待接管
- 哪些 Agent 正忙、正闲、当前在做什么
- 当前 Workspace 的共享工具台里发生了什么重要变化

### 设计原则

1. **抽象层级化**
	- 默认只展示节点 / 任务 / Agent 的聚合状态
	- 原子事件只在需要时展开查看

2. **聚焦任务而非 Agent 聊天记录**
	- 任务 DAG / Map 是主视图
	- Agent 更像节点上的执行者标记，而不是单独霸占主界面

3. **动态与历史结合**
	- 实时显示当前活动节点
	- 支持查看历史轨迹、关键事件和执行回放

4. **可视化简化**
	- 用颜色、图标、进度条、badge 表示状态
	- 关键事件流只显示 `task_taken`、`task_done`、`task_failed` 等摘要事件

5. **监测优先，干预可选**
	- 默认是观察与理解系统行为
	- 允许手动重分配、暂停、取消、释放节点，但不阻塞自动协作流程

## 建议界面结构

### 1. Task DAG / Map 主视图

这是 Workspace 协作页的主视图。

建议：

- 节点颜色表示状态：`pending / running / done / failed / blocked`
- 每个节点显示执行 Agent 的小头像、ID 或 role 标记
- 节点 hover 时显示：
	- 当前执行者
	- 最近一次状态变化
	- 资源绑定（sandbox / script / knowledge / storage）
	- 输入输出摘要

### 2. Agent 侧边栏

作为辅助视图，而不是主画布。

建议显示：

- Agent 当前任务
- role
- 当前状态：`idle / busy / blocked / sleeping`
- 当前工作负载
- 最近一次关键动作

支持点击进入 Agent 执行轨迹详情，但默认不把整页变成“Agent 聊天监控面板”。

### 3. 关键事件流 / 日志面板

建议作为可折叠底部面板或右下角抽屉。

默认只显示关键事件：

- `task_taken`
- `task_done`
- `task_failed`
- `task_released`
- `workflow_started`
- `workflow_completed`
- `sandbox_faulted`

并支持按以下维度过滤：

- agent
- task / node
- workflow
- workspace

这里的“日志面板”不应简单等同于原始文本日志，而应基于 Workspace 日志系统提供三层视图：

- `Agent Execution Log`：单个 Agent 的局部执行日志
- `Global Event Log`：Workspace 域内的关键事件时间轴
- `Workspace Log Projection`：用于 UI 和告警的聚合日志视图

### 4. 聚合指标与汇总卡片

建议在主视图上方提供总览卡：

- 总任务数
- 运行中节点数
- 已完成数
- 失败 / 阻塞数
- 忙碌 Agent 数 / 空闲 Agent 数
- 平均节点执行时长

这能让用户先理解全局，再决定是否下钻细节。

## Workspace 日志系统

对于多 Agent 协作来说，日志系统不应只是“把所有输出都落盘”，而应成为 Workspace 驾驶舱的基础观测层。

建议把日志拆成三层：

### 1. Agent 执行日志

面向单个 Agent 调试与轨迹追踪，记录：

- 时间戳
- 当前执行节点
- 资源 ID / sandbox / tool
- 执行结果
- 错误 / 异常信息

这类日志更接近局部执行痕迹，不直接作为主界面默认视图，而是在 Agent 详情或节点详情中按需展开。

### 2. 全局事件日志

面向 Workspace 协作分析，记录：

- `task_assigned`
- `task_taken`
- `task_done`
- `resource_locked`
- `resource_released`
- `alert`
- `exception`

这类日志的来源应以统一事件总线为准，是理解 Agent 之间协作关系的主时间轴。

### 3. Workspace 层汇总日志

面向驾驶舱、告警和自动监控，聚合：

- Agent 活跃度
- 节点完成率
- 失败热点
- 事件密度变化
- 当前高风险阻塞点

也就是说，Workspace 主页面默认看的不应是原始 log line，而是聚合后的日志投影。

## 日志视图建议

### 单 Agent 日志视图

建议采用时间轴展示：

- Agent 执行了哪个节点
- 使用了哪些资源
- 成功 / 失败 / 进行中
- 展开后可看详细输入输出和异常信息

### 全局事件日志视图

建议支持：

- 按 `workspace / task / agent / eventType / timeRange` 过滤
- 以时间轴方式查看协作关系
- 与 `TaskMap / DAG` 关联高亮，而不是只给一张表格

### Workspace 汇总日志视图

建议与聚合卡片和驾驶舱主视图联动，展示：

- 当前活跃 Agent 数
- 高失败率节点
- 关键异常趋势
- 最近一段时间的热点事件类型

## 日志展示原则

结合协作驾驶舱，建议继续坚持：

- 默认展示聚合视图，而不是 log dump
- 关键事件优先，原始日志按需展开
- 日志视图必须支持从 `TaskMap / DAG` 反向定位到节点、Agent 和资源
- Workspace 内默认共享日志查询能力，但跨 Workspace 必须受权限控制

## Workspace 共享虚拟工作台

除了界面层，Platform 还应把 Workspace 内的共享工具组织成一个 **虚拟工作台（Virtual Workbench）**，供多个 Agent 协作使用。

Agent 不应该直接感知 MINIO、PostgreSQL、消息队列等底层实现，而只应该感知：

- 任务
- 事件
- 表格
- 知识
- 文件对象
- 共享资源

## 核心协作工具

### 1. Workspace 共享 TODO / Task Board

用于：

- 分配任务
- 标记状态：`pending / running / done / blocked`
- 表达依赖关系与优先级
- 支持 Agent 订阅、领取、完成任务
- 支持人类用户查看与手动调整

### 2. 共享表格 / Spreadsheet

用于：

- 数据协作
- 批量处理
- 过滤、公式、简单分析

Agent 只通过统一 API 读取/写入表格，不感知底层表存储实现。

### 3. 知识库 / Wiki

用于：

- 共享知识
- 检索参考
- 标签与版本管理

### 4. 对象存储

用于：

- 文件、文档、脚本资源、产物共享
- 上传、下载、版本管理

Agent 只关心对象 ID / URL / 标签，不需要关心 MinIO、文件系统或对象协议细节。

## 增强协作工具

还应预留以下增强能力：

- 事件 / 消息中心
- 共享资源池 / 工具库
- 版本控制 / 历史记录
- 临时协作白板 / Graph Canvas
- 分析 / 汇总面板

这些能力共同构成 Workspace 级共享工作台，而不是分散在多个孤立页面里。

## Agent 访问共享工具的统一接口

Platform 不直接承载工具执行，但应定义清晰的产品语义和入口。

建议原则：

- Runtime 或 Controller 通过统一 Runtime API / HTTP API 暴露工具
- Agent 使用 `agent-token` 访问 HTTP API，以声明自己的身份与 Workspace 边界
- 底层数据库、对象存储、消息系统对 Agent 保持透明

也就是说，Agent 看到的是统一工具面，不是后端基础设施清单。
