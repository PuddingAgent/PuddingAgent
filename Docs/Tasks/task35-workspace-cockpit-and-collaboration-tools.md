# task35 - Workspace 协作驾驶舱与共享工具台

最后更新：2026-03-18

## 任务目标

在 `PuddingPlatform` / `PuddingPlatformAdmin` 中建立 Workspace 内的多 Agent 协作驾驶舱（Collaboration Cockpit）与共享虚拟工作台（Virtual Workbench），让用户可以：

- 以 `TaskMap / DAG` 为主视图观察任务推进
- 以聚合状态而不是原子事件洪流理解多 Agent 协作
- 查看 Agent 当前活动节点与历史轨迹
- 查看和使用 Workspace 级共享工具：任务板、表格、Wiki、对象存储、事件中心等
- 在必要时对节点进行人工干预，而不阻塞自动协作流程

对应架构：

- [../07架构/05PuddingPlatform.md](../07架构/05PuddingPlatform.md)
- [../07架构/07协作网络与治理.md](../07架构/07协作网络与治理.md)
- [../07架构/11工作流与任务图.md](../07架构/11工作流与任务图.md)
- [../07架构/08数据模型与配置.md](../07架构/08数据模型与配置.md)

## 前置依赖

- `task28` 已稳定 Workspace 业务层与平台治理语义。
- `task30` 已提供知识库、统一存储与共享资产底层访问能力。
- `task34` 已稳定事件总线、订阅治理与关键事件流。

## 可并行关系

- 主视图 UI、Agent 侧栏、关键事件流、共享工具台可以并行设计与实现。
- 聚合查询接口可与 [task32-observability-integration.md](task32-observability-integration.md) 的指标链路部分并行。
- 共享工具台的数据层可与 [task30-knowledge-infrastructure.md](task30-knowledge-infrastructure.md) 持续联动。

## 设计原则

1. **抽象层级化**
   - 默认展示节点 / 任务 / Agent 的聚合状态
   - 允许展开查看事件细节与执行轨迹

2. **聚焦任务而非 Agent 原子行为**
   - 主视图是 `TaskMap / DAG`
   - Agent 作为节点上的执行者标记存在

3. **动态与历史结合**
   - 实时显示当前活动节点
   - 支持查看历史轨迹、关键事件与接管记录

4. **可视化简化**
   - 用颜色、图标、进度条、badge 表示状态
   - 默认只展示关键事件：`task_taken`、`task_done`、`task_failed`、`task_released`

5. **监测优先，干预可选**
   - 允许手动重分配、暂停、取消或释放节点
   - 不应把人工操作设计成自动协作的必经门槛

## 界面结构建议

### 1. Task DAG / Map 主视图

作为 Workspace 协作页的主视图。

建议能力：

- 节点颜色表示状态：`pending / running / done / failed / blocked`
- 每个节点显示执行 Agent 小头像、ID 或 role
- hover 显示：
  - 当前执行者
  - 最近状态变化
  - 资源绑定（sandbox / script / storage / knowledge）
  - 输入输出摘要

### 2. Agent 侧边栏

建议显示：

- 当前任务
- role
- 状态：`idle / busy / blocked / sleeping`
- 工作负载
- 最近一次关键动作

支持查看 Agent 历史执行轨迹，但不把整页变成“Agent 聊天记录浏览器”。

### 3. 关键事件流 / 日志面板

建议作为可折叠面板，默认只显示关键事件：

- `task_taken`
- `task_done`
- `task_failed`
- `task_released`
- `workflow_started`
- `workflow_completed`
- `sandbox_faulted`

支持筛选：

- agent
- task / node
- workflow
- workspace

建议进一步拆成三类可切换视图：

- `Agent Log`：单 Agent 局部执行日志
- `Event Timeline`：全局事件时间轴
- `Workspace Summary Log`：聚合汇总日志

### 4. 聚合汇总卡片

建议提供：

- 总任务数
- 运行中节点数
- 已完成数
- 失败 / 阻塞数
- 忙碌 Agent 数 / 空闲 Agent 数
- 平均节点执行时长

## 共享虚拟工作台

Workspace 应向 Agent 和用户同时暴露统一工作台，而不是多个散乱入口。

### 核心工具

1. **Task Board / TODO List**
   - 任务分配、状态变更、优先级、依赖关系

2. **Spreadsheet**
   - 数据协作、批量处理、过滤、公式、简单分析

3. **Wiki / Knowledge Base**
   - 知识共享、版本、标签、搜索

4. **Object Storage**
   - 文件、脚本、文档、产物共享

### 增强工具

- `WorkspaceEventHub`
- `ResourcePool`
- `GraphCanvas`
- `VersionHistory`
- `AnalysisPanel`

## Agent 访问方式

Agent 不应直接访问底层存储或数据库。

建议：

- 共享工具通过 Runtime API / HTTP API 暴露
- HTTP API 使用 `agent-token` 进行身份验证
- `agent-token` 至少绑定：`agentId`、`workspaceId`、`scopes`
- 所有关键读写都应触发关键事件与审计记录

## 顺序任务

1. 定义 Workspace 协作驾驶舱查询模型
说明：建立 `TaskMapSummary`、`AgentActivitySnapshot`、`KeyEventRecord` 等聚合对象。
输出：平台聚合查询模型。

2. 建立 TaskMap / DAG 主视图接口
说明：为前端提供节点状态、执行者、资源绑定、输入输出摘要查询。
输出：主视图聚合 API。
前置依赖：任务 1。

3. 建立 Agent 侧边栏查询接口
说明：支持查询 Agent 当前任务、状态、role、工作负载与历史轨迹。
输出：Agent 侧栏 API。
前置依赖：任务 1。

4. 建立关键事件流摘要接口
说明：从事件总线和执行日志中提炼关键事件，支持筛选与折叠展示。
输出：关键事件流 API。
前置依赖：任务 1；依赖 task34。

4A. 建立单 Agent 日志时间轴接口
说明：支持查看某个 Agent 的节点执行、资源使用、错误与结果时间轴。
输出：Agent 日志查询 API。
前置依赖：任务 1；依赖 task32。

4B. 建立 Workspace 聚合日志投影接口
说明：支持查询 Agent 活跃度、节点完成率、失败热点、事件密度等汇总数据。
输出：Workspace 汇总日志 API。
前置依赖：任务 1；依赖 task32。

5. 建立共享工具台目录与入口
说明：把 TaskBoard、Spreadsheet、Wiki、ObjectStorage 等能力组织成统一目录与访问入口。
输出：Workspace 虚拟工作台入口。
前置依赖：任务 1；依赖 task30。

6. 建立人工干预操作入口
说明：支持重分配、暂停、取消、释放节点等操作。
输出：最小干预控制接口。
前置依赖：任务 2、任务 3、任务 4。

7. 在 PlatformAdmin 中落地最小 UI
说明：实现主视图、侧边栏、事件流面板、Agent 日志时间轴和聚合卡片。
输出：Workspace 协作驾驶舱 v1。
前置依赖：任务 2、任务 3、任务 4、任务 4A、任务 4B、任务 5、任务 6。

## 验收标准

- Workspace 内至少有 1 个 TaskMap / DAG 主视图页面。
- 主视图能显示节点状态与执行 Agent 标记。
- Agent 侧边栏能显示当前任务、role、状态与工作负载。
- 关键事件流默认只显示摘要事件，并支持按 agent / task / workspace 过滤。
- 至少支持 1 个单 Agent 日志时间轴视图和 1 个 Workspace 汇总日志视图。
- 至少能暴露 4 类共享工具：TaskBoard、Spreadsheet、Wiki、ObjectStorage。
- Agent 通过统一 API 访问共享工具时具备身份令牌与 Workspace 边界约束。
- 用户可以对节点执行至少 1 种人工干预操作（如 release / cancel / reassign）。
