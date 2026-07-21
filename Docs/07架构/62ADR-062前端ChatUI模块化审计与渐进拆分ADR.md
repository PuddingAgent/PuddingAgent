# ADR-062 前端 Chat UI 模块化审计与渐进拆分

> 状态：Accepted，Implementation In Progress  
> 日期：2026-07-21  
> 详细实施清单：[frontend-chat-ui-audit-plan.md](../../memory/plans/frontend-chat-ui-audit-plan.md)

## 1. 背景

`PuddingPlatformAdmin/src/pages/chat/hooks/useChatState.ts` 同时承担工作区与 Agent
选择、Session 生命周期、SSE/replay、消息发送、compaction、通知、队列与 UI 状态。
该文件在本轮开始时有 6,209 行，任何局部修改都需要理解多个不同生命周期，纯函数也
无法在不加载主 hook 的情况下独立测试。

ADR-054 已给出 Chat Runtime 与 UI 性能重构的总体方向，ADR-057 已冻结可靠
Conversation 事件流的事实源、sequence 和 replay 语义。本 ADR 只决定现有前端代码的
渐进式模块边界，不改变上述运行时协议。

## 2. 决策

### 2.1 使用组合式拆分

保留 `useChatState` 作为页面组合入口，按以下顺序逐步迁移：

1. P0：先移动纯函数、共享类型/常量与诊断工具。
2. P1：再按 Workspace/Agent 选择、Modal、Runtime Event、通知、Session、
   Compaction、SSE、Message Send 拆分专用 hook。
3. P2：最后拆 DevPanel、residual styles，并在 hook 接口稳定后评估 Context 分层。

每个波次必须保持用户可见行为与后端 API 契约不变，并保留必要的模块兼容导出。

### 2.2 不引入新的状态管理库

本次不引入 Zustand、Redux 或其他全局状态库。已经存在的 ADR-054 Runtime Store 与
ADR-057 Conversation Store 继续作为后续接入边界，但不得为了减小文件行数而并行创建
第三套状态体系。

### 2.3 单一职责边界

- `hooks/useChatState.ts`：组合 hook 与跨域协调。
- `utils/chatStateUtils.ts`：无 React 依赖的纯转换和判定。
- `types/chatStateTypes.ts`：P0-2 后承载共享类型和常量。
- `utils/chatDiagnostics.ts`：P0-3 后承载 ChatDiag 横切逻辑。
- 后续专用 hooks：拥有各自 state/ref/effect，主 hook 只组合其稳定接口。

### 2.4 验证门槛

每个波次至少执行：

- 新模块的直接单元测试；
- `useChatState.recovery`、`useChatState.selection` 等聚焦回归；
- TypeScript 与前端构建检查；
- 行为相关波次的浏览器手工验证。

若仓库基线已经失败，必须记录迁移前后的通过数和精确失败项；不能把既有失败误记为本轮
回归，也不能以基线噪声掩盖新增失败。

## 3. 与既有 ADR 的关系

- ADR-054：本 ADR 是其巨型 hook、样式和 DevPanel 拆分方向的可执行细化；不废弃
  Runtime Store 设计。
- ADR-057：Conversation Event Log、sequence、幂等 reducer、gap recovery 与历史
  projection 语义优先级更高。本 ADR 的文件移动不得改变这些协议。
- ADR-055：消息 viewport 的单一滚动权威不在本次 P0/P1 重构范围内。

## 4. 当前实施状态

2026-07-21 已完成 Phase 0、基线修复与首个 P1 hook：

- 模块级纯函数已接入 `utils/chatStateUtils.ts`；
- 共享类型、常量与主 hook 返回接口已迁移到 `types/chatStateTypes.ts`；
- ChatDiag 已迁移到 `utils/chatDiagnostics.ts`；
- `useChatState.ts` 从 6,209 行降至 5,156 行；
- 保留原模块导出以兼容现有调用方；
- error diagnostic 纯函数、错误终态气泡与可检索字段契约已补齐；
- `useWorkspaceAgentSelection.ts` 已拥有路由解析、Workspace/Agent 初始化、默认
  Agent 创建、选择项 memo、`creatingSession` 与主会话重建抑制状态；
- Chat 定向基线从 60/63 恢复为 63/63；连同新模块测试共 73/73 通过；
- 全量 Jest 的 only-failures 仍有 5 个既有 suite / 9 个 test 失败，集中在语音 UI
  fixture、MessageList 旧滚动断言、固定月份和 Agent 模板旧文案，均不在本轮修改边界；
- 前端生产构建通过，新增模块 Biome 通过；全量 TSC 只剩主 hook 已知的
  `updateSystemTurn` 两条类型基线错误；
- 浏览器刷新后默认工作空间/Agent 正常恢复，当前开发数据只有一个 Agent，跨 Agent
  点击留由自动化测试覆盖。

下一步按低耦合优先顺序提取 `useChatModals`，随后提取
`useChatRuntimeEvents`。

## 5. 后果

正向后果：纯逻辑可以独立测试，主 hook 的阅读面缩小，后续拆 hook 时依赖边界更明确。

代价与风险：迁移期间存在兼容导出和临时共置；如果一次移动跨越多个 state/effect
生命周期，可能引入隐式依赖或循环引用。因此每个波次必须小步提交并记录基线差异。
