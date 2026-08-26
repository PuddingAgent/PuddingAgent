# ADR-080：任务看板分层读取、子任务与命令化拖拽

> 状态：Proposed
>
> 日期：2026-08-26
>
> 范围：WorkspaceTask、Task API/Tools、Task Board Projection、Admin 看板
>
> 详细设计：[任务看板状态机、子任务、渐进披露与高性能拖拽优化设计方案](../Features/任务看板状态机子任务渐进披露与高性能拖拽优化设计方案.md)

## 1. 背景

现有任务看板已经具备十二态任务状态机、五列投影、Internal/External API、Agent/管理工具、评论和列内虚拟化，但出现了六类产品缺口：

1. `Ready` 任务无法直接核销为 `Completed`；
2. `WorkspaceTask` 没有父子关系；
3. List、工具和 Watch 广泛传输完整 Task DTO，违背渐进披露；
4. 单任务详情不返回评论/备注，且两者没有清楚的领域语义；
5. 看板不支持拖拽移动和稳定用户排序；
6. 大列滚动卡顿，并存在 Watch cursor、重复请求、伪全量搜索和错误空态等正确性问题。

只在前端加入 DnD 或只扩大状态转换表会绕过完成证据、Assignment、CAS 和审计，并进一步放大性能问题，因此必须以任务应用服务和读取投影为边界整体收敛。

## 2. 决策

### 2.1 允许 `Ready -> Completed`，但只走 `Complete` 命令

在 `TaskStateMachine` 中加入 `Ready -> Completed`。新增 `TaskCommand.Complete`：

- `resultSummary` 必填；
- Acceptance Criteria 声明必需 Artifact 时必须提交 Artifact；
- 使用 expectedVersion/ETag、幂等和 Task Event；
- 有未闭合子任务时拒绝；
- 有活跃 Assignment 的任务不允许管理员直接完成，继续走 Agent disposition/执行链；
- 完成证据与 Task/event 同事务提交。

通用 PATCH 改为元数据专用，不再接受 status。状态只能由结构化 Command、Agent disposition 或调度状态机推进。

### 2.2 子任务复用 `WorkspaceTask`

子任务不是新表或第二状态机。`workspace_tasks` 增加 `parentTaskId/rootTaskId/depth`，第一期只允许一层父子关系。

每个子任务有独立 status、version、Assignment、Execution、评论、备注和事件。父任务只派生子任务计数/进度，不级联状态。父任务完成、归档和删除受子任务终态门禁约束。

### 2.3 冻结三种读取投影

- `TaskIndexProjection`：普通 List、`task_list`、`manage_tasks list`，item 只能是 `taskId/title`。
- `TaskCardProjection`：专用 Board bootstrap/column/watch，返回固定、有界的卡片字段。
- `TaskDetailProjection`：单任务详情，返回完整任务、allowedActions、执行摘要、子任务卡片页以及 comments/notes 的有界窗口。

禁止 Detail DTO 复用于 List，也禁止 id 列表后对每个卡片发详情 N+1 请求。

### 2.4 评论与备注成为可区分的 Annotation

采用追加式 `TaskAnnotation(kind=comment|note)`。详情和 `task_get/manage_tasks get` 默认返回最近 20 条 comment 与 note、各自 total/nextCursor；完整历史使用分页子资源。

空集合必须在 HTTP、工具和 Skill 中保持 `items: []/total: 0/nextCursor: null`，不得被包装为 `null`；读取失败是独立结构化错误。

External V1 `/comments` 保持已承诺语义；External V2 使用 `/annotations`，新增 `tasks.note` 写 scope。

### 2.5 拖拽是命令适配器，不是状态写入口

服务端在 `TaskCardProjection.allowedBoardMoves` 中声明目标列、命令、目标状态和必填信息。跨列 drop 调用统一 Move API，由服务端重新校验并原子执行命令与排序；列内 drop 只更新 rank。

第一期映射：Backlog→Todo 为 promote，Ready→Done 为 complete，Failed→Todo 为 reopen，InProgress/Blocked→Todo 为 return/resume；Ready/Deferred 拖向 InProgress 打开真实 Run Now/Assignment 面板，不伪造 InProgress。

UI 必须有键盘“移动到”能力、不可投放原因、完成/原因表单和冲突回滚。

### 2.6 Board 使用 Bootstrap + Global-Cursor Delta

Board bootstrap 在同一只读事务返回五列首屏卡片、真实总数、分页 cursor 和 `task_events.id` 全局 event cursor。Watch 只发送 CardDelta：

- 全局恢复游标统一命名 `afterId`/`Last-Event-ID`；
- per-task `sequence` 只用于任务内单调性，不能作为全局 cursor；
- 不发送被客户端忽略的完整 snapshot；
- delta 已带 CardProjection，前端不再每事件二次 GET；
- cursor 过期时 fail closed 并重新 bootstrap。

### 2.7 UI 采用有界渲染和规范化 Store

- 每列首屏 30、最大页 50，真实 count 与 loaded count 分开；
- 固定高度、两行标题、无完整描述的 memo 卡片；
- `tasksById + columnIds` 规范化状态，单事件只更新相关卡片和至多两列；
- `@tanstack/react-virtual` 与 `@dnd-kit` 协作，拖动 overlay 不破坏虚拟项；
- 服务端搜索、独立 Index 表格、详情单项刷新；
- 评论/备注/事件使用懒加载 Tab，错误态不得显示成空态。

## 3. 备选方案与否决

### 3.1 继续让 PATCH 写 status

否决。把 Ready→Completed 加入普通转换后，会允许调用者绕过 resultSummary、Artifact、子任务门禁和完成命令审计。

### 3.2 List 返回完整 DTO，由客户端自行裁剪

否决。它继续浪费网络、JSON 解析和模型上下文，并诱导看板、表格、工具依赖同一个过宽 DTO。

### 3.3 List 只返回 id，再对每个卡片读取详情

否决。会形成 N+1。看板需要专用批量 Card Projection。

### 3.4 子任务使用独立表/独立状态枚举

否决。会复制 Task 状态机、权限、Assignment、事件和工具链，并产生双事实源。

### 3.5 任意跨列 drop 直接映射到目标列默认状态

否决。Todo/InProgress 都聚合多个真实状态，目标列不足以表达 Assignment、Resume、Reopen 或 Complete 的业务语义。

### 3.6 只调整 virtualizer overscan

否决。当前主要瓶颈还包括重复完整响应、SSE 二次 GET、全列数组复制、动态卡片测量和错误的全局 cursor，单独调 overscan 不能修复。

## 4. 兼容与迁移

- Internal API 和 Runtime tools 属于同仓合同，按实施批次原子切换。
- External V1 是明确版本化合同，不静默改变 DTO；新增 External V2 并先迁移官方 Skill。
- V1 只保留一个明确迁移里程碑，随后删除；不得长期双写、复制状态机或维护两套 Task 数据。
- `task_comments` 一次性迁到 `task_annotations(kind=comment)`；本 ADR 不授权本轮修改运行数据库。
- 旧 `sortOrder=0` 数据在首次维护作业中按稳定顺序生成带间隔 rank。

## 5. 后果

### 正向

- 直接完成既满足用户工作流，又保留完成证据和状态可信度。
- 子任务复用全部既有 Task 生命周期和执行基础设施。
- 普通 List/token 成本显著下降，UI 不再被完整详情 DTO 绑定。
- Board DnD、实时 Watch 和性能优化共享同一 Card Projection。
- 评论、备注、事件、子任务在详情中可发现且可分页。
- 修复多任务 sequence 冲突导致的 Watch 漏事件风险。

### 代价

- 需要新投影、Move API、Annotation schema、External V2 和 Skill 迁移。
- DnD 与虚拟列表组合需要专门测试 auto-scroll、测量锁定和键盘交互。
- 一层子任务是有意限制；更深任务树需另行决策。

## 6. 不变量

1. 状态事实只有一个；BoardColumn 始终是后端投影。
2. Agent 不通过自然语言或管理 PATCH 宣告完成。
3. List item 永远只有 `taskId/title`；Card/Detail 是不同路由和 DTO。
4. 父子任务各有 version，父任务不级联改子状态。
5. 跨列移动必须经过 allowed action、CAS、权限和事件。
6. Watch 恢复只使用 global event cursor，不使用 per-task sequence。
7. Snapshot + Delta 的最终投影等于从头 replay。
8. 读取失败与空结果在 API/UI 中是不同状态。
9. External V1/V2 共享应用服务和数据库，不复制业务逻辑。

## 7. 验收门禁

- Core：Ready complete、证据校验、子任务门禁、非法层级、allowed actions 全覆盖。
- API/Tool：普通 list item 契约严格为 id/title；detail 带有界 comments/notes；External V2 ETag/幂等。
- Watch：不同 Task 的相同 sequence 不丢事件；global cursor 断线追赶不丢不重。
- UI：合法/非法拖拽、完成表单、CAS 回滚、键盘移动、详情错误态。
- 性能：10k Task、单列 2k；bootstrap P95 ≤250ms、单列页 P95 ≤150ms、滚动无 >50ms long task、目标 ≥55fps。
- 交付：定向测试、Release 前端构建、浏览器 smoke、External Skill smoke 和进程外新构建验收均有证据。

完成这些门禁前，本 ADR 保持 Proposed；文档和代码存在都不等于部署或生产接受。
