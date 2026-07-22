# PuddingAgent 前端 Chat UI 打磨方案 — Software Design Specification + ADR

> **ADR-062** | 2026-07-21 | K3 Planner | Status: In Progress

---

IMPLEMENTATION_PROGRESS:
  UPDATED_AT: 2026-07-21
  CURRENT_MILESTONE: P1 useChatState modularization complete; P2 pending
  COMPLETED: |
    - P0-1/P0-2/P0-3: 纯函数、共享类型/常量、诊断工具已迁出，并保留 useChatState 兼容导出。
    - P1-1: Session 域拆为 useSessionCatalog、useSessionSelection 与 useSessionHistoryProjection。
    - P1-2: SSE 域按生命周期进一步拆为 useSessionEventBuffers、useSessionEventConnection、useSessionEventReplay 与 useSessionEventProjection；持久事件规范化/分页请求迁到 utils/sessionEventReplay.ts。
    - P1-3: 发送事务迁到 useMessageSend；输入、服务端队列与 steering 队列迁到 useMessageInteractionQueue。
    - P1-4/P1-5: Compaction 与 Workspace 通知分别迁到 useCompaction、useWorkspaceNotifications。
    - P1-6/P1-7/P1-8: Workspace/Agent、Modal、Runtime Event 分别迁到专用 hook。
    - 历史分页迁到 useMessageHistoryPagination；useChatState 只保留跨域组合、兼容导出与少量页面级协调。
    - 复杂 hook 使用分组 port object 和 bindable callback ref 连接，避免 8-15 个散乱参数，也避免额外状态仓库。
    - useChatState.ts 从审计基线 6,209 行降至 1,314 行；UseChatStateReturn 与后端 API 契约保持不变。
  VERIFICATION_SNAPSHOT: |
    - Chat 重构定向集: 16 suites / 94 tests passed。
    - npm run build: passed。
    - 本轮核心组合/发送/历史/SSE 投影模块 Biome check 无 error。
    - 全量 tsc 仍有仓库其他页面与 fixture 的既有错误；筛选本轮 useChatState 及新模块无命中。
    - Jest 完成后仍报告既有 open-handle 提示，但退出码为 0；本轮未把它误记为断言失败。
    - 全量 Jest 的旧快照为 5 suites / 9 tests 的无关 UI/日期/文案基线；本轮未重跑该噪声集。
  NEXT: |
    - P2-1: 在不覆盖当前 DevPanel 用户改动的前提下，单独冻结边界后拆分 DevPanel tabs。
    - P2-2/P2-3: 完成 residual styles 迁移，再评估 Chat Context 分层。

---

SUMMARY:
  PROBLEM_STATEMENT: |
    Chat UI 是 PuddingAgent 用户与 AI Agent 的核心交互界面。当前 useChatState.ts 是一个 6,209 行 / 211KB 的巨型 hook，包含 SSE 生命周期、事件 replay、compaction、消息发送、会话管理、子代理事件、工作区通知、消息队列等全部逻辑。DevPanel.tsx 55KB 未拆分。styles.ts 仍有 76KB residual styles。状态管理碎片化（UmiJS model + 5 个自建 store + 35+ useState），无统一模式。

    为什么现在解决：
    - ADR-057 迁移基础设施（conversationStore, gapRecoveryEngine, useConversation, connectionManager）已就绪但未被 useChatState 采用
    - ADR-054 Runtime Store 骨架（messageRuntimeStore, composerStore, sessionLifecycleStore, agentStatusStore）已创建但标记为 EXPERIMENTAL，未接入生产
    - 每次功能修改都需要在 6,209 行文件中定位代码，开发效率极低
    - 无法对任何子功能独立测试
    - 新人入职无法理解整体架构

  GOALS_AND_NON_GOALS: |
    **Goals:**
    1. 将 useChatState.ts 从 6,209 行拆分为 8-10 个独立可测试的子 hook
    2. 将 DevPanel.tsx 从 55KB 拆分为独立 Tab 子组件
    3. 完成 styles.ts residual 部分的渐进式迁移到 styles/ 目录
    4. 建立统一的状态管理模式（React Context 分层 + selectors）
    5. 每步操作后 52 个测试全部通过

    **Non-Goals:**
    1. 不改变任何 UI 外观或交互行为
    2. 不重写 api.ts（3406行）— 它是 service 层，不在本次范围
    3. 不迁移到 Zustand/Redux 等外部状态管理库
    4. 不修改后端 API 契约
    5. 不处理 MessageList.test.tsx (49KB) — 它是测试文件，不是生产代码
    6. 不做虚拟滚动策略变更
    7. 不处理 GlobeSphere / Phaser 3D 组件

  SUCCESS_CRITERIA: |
    1. useChatState.ts 从 6,209 行降至 <1,500 行（主 hook 只做组合，不含业务逻辑）
    2. DevPanel.tsx 从 55KB 拆分为 ≤5 个独立文件，每个 <15KB
    3. styles.ts residual 从 76KB 降至 0（全部迁移到 styles/ 目录）
    4. 所有 52 个测试在每步操作后通过（npm test -- --watchAll=false）
    5. 无 TypeScript 编译错误
    6. 每个新提取的子 hook 有对应的独立测试文件
    7. useChatState 返回值接口（UseChatStateReturn）在 P0 阶段保持完全不变

  ESTIMATED_TOTAL: |
    - P0（纯函数/常量/类型提取）: 2-3 天，低风险
    - P1（子 hook 提取）: 5-8 天，中风险
    - P2（Context 分层 + DevPanel 拆分 + styles 完成）: 3-5 天，中低风险
    - 总计: 10-16 天
    - 关键路径: P0-1 (纯函数提取) → P0-2 (常量提取) → P1-1 (SSE lifecycle hook) → P1-2 (message send hook) → P1-3 (session management hook) → P2-1 (DevPanel 拆分)

---

CHANGES:
  ARCHITECTURE_DECISIONS:
    - ADR-062-001: 采用"组合模式"而非"状态管理库迁移"拆分 useChatState
      Context: useChatState.ts 有 35+ useState, 15+ useEffect, 30+ useCallback, 10+ useMemo, 25+ useRef。返回 75 个属性。ADR-054 的 Runtime Store 和 ADR-057 的 conversationStore 已存在但未接入。
      Decision: 将 useChatState 拆分为 8 个专注子 hook，每个返回一个明确领域的状态+操作。主 useChatState 只做组合（compose），不包含业务逻辑。
      Rationale: 最小化风险——不引入新的状态管理库，不改变数据流方向，保持 React 原生 hook 模式。每个子 hook 可以独立测试。
      Alternatives Rejected:
        - 迁移到 Zustand: 引入外部依赖，改变数据流模式，风险过高
        - 一次性重写为 conversationStore: ADR-057 的 useConversation 只覆盖 SSE + 消息发送，不覆盖 workspace/agent 选择、compaction、通知、消息队列等
        - 使用 useReducer 替代 useState: 收益不大，且增加学习成本

    - ADR-062-002: DevPanel 按 Tab 拆分为独立子组件
      Context: DevPanel.tsx 55,090 字节，单个 DevPanel 箭头函数包含 20+ 内部方法（refresh, schedule, cancel, onVisibility, loadBenchmarkCases, loadLatestSession, loadContext, loadSubconscious, copyDiagnosticSnapshot, updateDiagnosticsEnabled, startCapture, stopCapture, downloadDiagnosticSnapshot, sendSelectedBenchmarkCase 等）。
      Decision: 按 Tab 拆分为 4 个独立子组件文件，DevPanel.tsx 只做 Tab 路由和共享状态。
      Rationale: 每个 Tab 有独立的数据加载逻辑和 UI 渲染，天然分离。拆分后可独立开发和测试。
      Alternatives Rejected:
        - 保持单文件但提取自定义 hook: 无法解决文件大小问题
        - 按功能而非 Tab 拆分: Tab 是最自然的用户界面边界

    - ADR-062-003: styles.ts residual 采用"逐域迁移"策略完成拆分
      Context: styles.ts 已从 149KB 降至 76KB。10 个域样式文件已迁移到 styles/ 目录（agent, composer, layout, markdown, message, panel, process, reasoning, user, voice）。useChatStyles 已经是组合函数，合并 11 个域。residual 部分仍有 ~2,677 行。
      Decision: 将 residual 中的样式按组件域逐一提取到 styles/ 目录的新文件中，每提取一个域就更新 useChatStyles 组合函数。
      Rationale: 渐进式迁移已被证明有效（从 149KB → 76KB）。继续此模式可以安全完成。
      Alternatives Rejected:
        - 一次性迁移全部 residual: 风险过高，可能遗漏样式引用
        - 保持 residual 在 styles.ts: 无法解决单文件过大问题

    - ADR-062-004: 引入 ChatContext 分层替代 Props Drilling
      Context: useChatState 返回 75 个属性。ChatPage → ChatLayout → ChatMain → MessageList/MessageGroup/AgentMessageBubble 层层传递。
      Decision: 创建 ChatStateContext + ChatDispatchContext 两层 Context。State Context 提供只读状态，Dispatch Context 提供操作方法。使用 memoized selectors 避免不必要重渲染。
      Rationale: 减少 props drilling，使中间层组件（ChatLayout, ChatMain）不需要知道所有 75 个属性。
      Alternatives Rejected:
        - 保持 props drilling: 随着功能增加，props 链会越来越长
        - 引入全局状态管理库: 过度设计，React Context 足够

    - ADR-062-005: 复杂生命周期使用分组端口与可绑定回调
      Context: applySessionEvent、sendMessage、handleSelectSession 等函数各自依赖 8-15 个 state/ref/callback；直接平铺参数只会把闭包复杂度改写成参数复杂度。
      Decision: 按 identity、turns、stream、catalog、feedback 等职责定义 port object；遇到初始化顺序环时，以稳定 callback ref 提供 bind 接口。SSE 不再收拢为单个 1,200 行 hook，而拆成 buffers、connection、replay、projection 四个所有者。
      Rationale: 端口对象显式暴露跨域依赖，保留 React 状态的单一所有者；binder 只连接行为，不复制 state，不引入第三套 store。
      Guardrails: binder 必须保存到稳定 ref，调用方每次 render 同步绑定；不得把事件历史放入 Channel/ref 充当持久事实；复杂协调路径必须由 useChatState.selection 集成测试覆盖。

  DETAILED_TASKS:
    - ID: P0-1
      NAME: 提取纯函数到 utils/chatStateUtils.ts
      ACTIONS: |
        从 useChatState.ts (L1-L1184) 提取以下已在模块级导出的纯函数到新文件 utils/chatStateUtils.ts:
        - confirmOptimisticTurn (L94)
        - parseSessionEventTimestampMs (L107)
        - stringToColor (L116)
        - getAgentName (L130)
        - formatCompactSuccessMessage (L133)
        - removeInjectedSteeringQueueItem (L196)
        - resolveSubAgentTaskSummary (L279)
        - toChatInteractionQueueItem (L289)
        - resolveSubAgentTerminalOutput (L304)
        - toChatInteractionRuntimeEvent (L314)
        - getChatRouteSelectionFromSearch (L352)
        - resolveInitialWorkspaceId (L368)
        - resolveInitialAgentId (L389)
        - buildAgentMainSessionRequest (L400)
        - toSessionListItem (L412+)
        - 以及所有其他模块级纯函数 (toChatDiagValue, logChatDiag, getStringValue 等)
      LOGIC: 这些函数不依赖 React hooks，是纯数据转换/格式化函数。提取后 useChatState.ts 从 ~1,184 行模块级代码降至 ~100 行。
      DEPENDENCIES: 无
      DELIVERABLES: |
        - 新建 utils/chatStateUtils.ts (~600 行)
        - 新建 utils/chatStateUtils.test.ts (~200 行)
        - 修改 hooks/useChatState.ts — 移除已提取的函数，改为 import
      VERIFY: |
        - npm test -- --watchAll=false 全部通过
        - npm run build 无 TypeScript 错误
        - 检查所有 import 路径正确

    - ID: P0-2
      NAME: 提取类型和常量到 types/chatStateTypes.ts
      ACTIONS: |
        从 useChatState.ts 提取以下类型和常量:
        - 常量: MESSAGE_PAGE_SIZE, SESSION_EVENT_PAGE_SIZE, ACTIVE_SESSION_REPLAY_POLL_INTERVAL_MS, IDLE_SESSION_REPLAY_POLL_INTERVAL_MS, SSE_HEALTHY_REPLAY_SUPPRESSION_MS, MAX_CHAT_INTERACTION_RUNTIME_EVENTS, STEERING_INJECTED_QUEUE_RETENTION_MS, CHAT_DIAG_STORAGE_KEY, CHAT_DIAG_MAX_EVENTS
        - 类型: SessionEventPageResponse, ChatRouteSelection, ChatSendOptions, ChatInteractionQueueStatus, ChatInteractionQueueItem, ChatInteractionRuntimeType, ChatInteractionRuntimeEvent, CHAT_INTERACTION_RUNTIME_EVENT_TYPES, ChatDiagPayload, ChatDiagWindow
        - 接口: UseChatStateReturn (L1072)
      LOGIC: 类型和常量定义与业务逻辑分离，便于其他模块引用而不依赖整个 hook。
      DEPENDENCIES: P0-1 (部分类型引用了纯函数)
      DELIVERABLES: |
        - 新建 types/chatStateTypes.ts (~200 行)
        - 修改 hooks/useChatState.ts — 移除已提取的类型/常量，改为 import
      VERIFY: 同 P0-1

    - ID: P0-3
      NAME: 提取诊断/日志工具到 utils/chatDiagnostics.ts
      ACTIONS: |
        从 useChatState.ts 提取诊断相关纯函数:
        - toChatDiagValue (L245)
        - logChatDiag (L268)
        - ChatDiagPayload / ChatDiagWindow 类型 (已在 P0-2 提取)
      LOGIC: 诊断工具是独立的横切关注点，与业务逻辑无关。
      DEPENDENCIES: P0-2
      DELIVERABLES: |
        - 新建 utils/chatDiagnostics.ts (~60 行)
        - 修改 hooks/useChatState.ts
      VERIFY: 同 P0-1

    - ID: P1-1
      NAME: 提取 useSessionManagement hook
      ACTIONS: |
        从 useChatState.ts 提取会话管理相关逻辑到 hooks/useSessionManagement.ts:
        - state: sessions, selectedSessionId, sessionsLoading, groups, sidebarOpen
        - refs: sessionIdRef, selectedSessionIdRef, mainSessionIdRef, forceNewSessionRef
        - actions: handleSelectSession (L4464), handleDeleteSession (L5086), handleArchiveSession (L5102), handleRenameStart (L5118), handleRenameSubmit (L5124), handleSetMainSession (L5020), resetConversation (L4155), ensureAgentMainSession (L4805), refreshSessions (L4288)
        - effects: 会话列表加载 (L4240), 会话选择路由同步 (L4280), mainSessionId 同步 (L5027)
      LOGIC: 会话管理是独立的领域——CRUD 会话列表、选择会话、重命名、删除、归档。与 SSE 流和消息发送无直接耦合。
      DEPENDENCIES: P0-1, P0-2
      DELIVERABLES: |
        - 新建 hooks/useSessionManagement.ts (~600 行)
        - 新建 hooks/useSessionManagement.test.ts (~200 行)
        - 修改 hooks/useChatState.ts — 移除会话管理代码，调用 useSessionManagement
      VERIFY: |
        - 全部测试通过
        - 手动验证: 会话切换、重命名、删除、归档功能正常

    - ID: P1-2
      NAME: 提取 useSseLifecycle hook
      ACTIONS: |
        从 useChatState.ts 提取 SSE 生命周期管理到 hooks/useSseLifecycle.ts:
        - state: loading, workingAgentIds, error, latestUsage
        - refs: abortRef, sessionEventsAbortRef, sessionEventsPollTimerRef, sessionEventsReconnectTimerRef, sseSessionIdRef, lastSseEventAtRef, lastSequenceNumRef, startSessionEventStreamRef, projectionOwnedSessionIdsRef
        - delta/thinking batching: pendingDeltaRef, deltaFlushTimerRef, pendingThinkingRef, thinkingFlushTimerRef, duplicateDeltaReplayOffsetRef, eventCountsRef, streamStartAtRef, activeMessageIdsRef, messageIdToAgentIdsRef, sessionIdToAgentIdsRef
        - callbacks: startSessionEventStream (L3758), stopSessionEventStream (L1534), applySessionEvent (L2706), replayMissedSessionEvents (L3391), replayMissedSessionEventsIfNeeded (L3587), replayLatestTurnSessionEvents (L3653), normalizeSessionEvent (L1854), listSessionEventsPage (L1944), syncCompletedHistoryEventCursor (L1971), resolveEventTurnId (L2028), mapEventToTurn (L2046)
        - effects: SSE 健康检查/重连 (L4214), 会话切换时重置流游标 (L1803)
      LOGIC: SSE 生命周期是最复杂的子系统——事件流管理、delta 批处理、replay 补偿、重连逻辑。提取后可独立测试 SSE 行为。
      DEPENDENCIES: P0-1, P0-2, P1-1 (需要 selectedSessionId)
      DELIVERABLES: |
        - 新建 hooks/useSseLifecycle.ts (~1,200 行)
        - 新建 hooks/useSseLifecycle.test.ts (~300 行)
        - 修改 hooks/useChatState.ts
      VERIFY: |
        - 全部测试通过
        - 手动验证: SSE 连接、消息流式输出、重连、compaction 后 replay

    - ID: P1-3
      NAME: 提取 useMessageSend hook
      ACTIONS: |
        从 useChatState.ts 提取消息发送和队列管理到 hooks/useMessageSend.ts:
        - state: inputValue, loading, serverInteractionQueue, steeringInteractionQueue
        - refs: abortRef (发送相关部分), loadingRef, inputValueRef
        - callbacks: sendMessage (L5143), submitInteraction (L5698), enqueueInteraction (L5679), updateQueuedInteraction (L5726), deleteQueuedInteraction (L5818), sendQueuedInteractionNow (L5839), steerQueuedInteraction (L5856), handleKeyDown (L5952), refreshAgentMessageQueue (L5742)
        - memo: visibleInteractionQueue (L5805)
        - effects: 消息队列刷新 (L5784, L5799)
      LOGIC: 消息发送是独立的用户交互路径——输入 → 发送/排队 → SSE 流式响应。
      DEPENDENCIES: P0-1, P0-2, P1-2 (需要 turns state)
      DELIVERABLES: |
        - 新建 hooks/useMessageSend.ts (~700 行)
        - 新建 hooks/useMessageSend.test.ts (~200 行)
        - 修改 hooks/useChatState.ts
      VERIFY: |
        - 全部测试通过
        - 手动验证: 发送消息、排队、steering、快捷键

    - ID: P1-4
      NAME: 提取 useCompaction hook
      ACTIONS: |
        从 useChatState.ts 提取 compaction 相关逻辑到 hooks/useCompaction.ts:
        - refs: compactionTurnIdsRef, compactionLifecycleTurnsRef, activeCompactionTurnIdRef, pendingCompactSessionSwitchRef, compactSessionSwitchRef, compactLifecycleEventRef
        - callbacks: formatCompactAnswer (L3164), appendCompactTurn (L3168), updateCompactTurn (L3234), switchToCompactedSessionPreservingTurns (L4323), handleCompactCommand (L4705)
        - effects: compaction 事件处理 (L4777)
      LOGIC: Compaction 是独立的生命周期——触发压缩 → 切换会话 → 保留 turns。与常规消息流不同。
      DEPENDENCIES: P0-1, P0-2, P1-1 (需要会话切换)
      DELIVERABLES: |
        - 新建 hooks/useCompaction.ts (~400 行)
        - 新建 hooks/useCompaction.test.ts (~150 行)
        - 修改 hooks/useChatState.ts
      VERIFY: |
        - 全部测试通过
        - 手动验证: /compact 命令、compaction 后会话切换、lifecycle 事件

    - ID: P1-5
      NAME: 提取 useWorkspaceNotifications hook
      ACTIONS: |
        从 useChatState.ts 提取工作区通知到 hooks/useWorkspaceNotifications.ts:
        - state: sessionUnreadCounts
        - refs: workspaceNotifyAbortRef, workspaceNotifyReconnectRef, workspaceNotifyWsIdRef
        - callbacks: startWorkspaceNotificationStream (L4400), stopWorkspaceNotificationStream (L4389), clearSessionUnread (L1703)
        - effects: 工作区通知 SSE (L4455)
      LOGIC: 工作区通知是独立的 SSE 流，与会话 SSE 平行运行。
      DEPENDENCIES: P0-1, P0-2
      DELIVERABLES: |
        - 新建 hooks/useWorkspaceNotifications.ts (~150 行)
        - 新建 hooks/useWorkspaceNotifications.test.ts (~100 行)
        - 修改 hooks/useChatState.ts
      VERIFY: 同 P1-1

    - ID: P1-6
      NAME: 提取 useWorkspaceAgentSelection hook
      STATUS: completed (2026-07-21)
      ACTIONS: |
        从 useChatState.ts 提取工作区/Agent 选择逻辑到 hooks/useWorkspaceAgentSelection.ts:
        - state: workspaces, workspaceId, workspaceLoading, agents, agentId, agentLoading, creatingSession
        - memo: routeSelection, selectedAgent, wsOpts, agOpts
        - callbacks: 工作区/Agent 切换逻辑 (L4240, L4319), suppressMainSessionEnsureRef 相关
        - effects: 路由参数解析 (L4214), 工作区列表加载, Agent 列表加载
      LOGIC: 工作区和 Agent 选择是页面初始化阶段的独立流程。
      DEPENDENCIES: P0-1, P0-2
      DELIVERABLES: |
        - 新建 hooks/useWorkspaceAgentSelection.ts (~300 行)
        - 新建 hooks/useWorkspaceAgentSelection.test.ts (~150 行)
        - 修改 hooks/useChatState.ts
      VERIFY: 同 P1-1

    - ID: P1-7
      NAME: 提取 useChatModals hook
      ACTIONS: |
        从 useChatState.ts 提取模态框状态到 hooks/useChatModals.ts:
        - state: createSceneOpen, createSceneLoading, createSceneForm, renameModalOpen, renameTitle, renameSessionId
        - callbacks: setCreateSceneOpen, setRenameModalOpen, setRenameTitle
      LOGIC: 模态框是纯 UI 状态，与业务逻辑无关。
      DEPENDENCIES: P0-1, P0-2
      DELIVERABLES: |
        - 新建 hooks/useChatModals.ts (~80 行)
        - 修改 hooks/useChatState.ts
      VERIFY: 同 P1-1

    - ID: P1-8
      NAME: 提取 useChatRuntimeEvents hook
      ACTIONS: |
        从 useChatState.ts 提取交互运行时事件到 hooks/useChatRuntimeEvents.ts:
        - state: chatInteractionRuntimeEvents
        - callbacks: 事件追加/清理逻辑
        - constants: MAX_CHAT_INTERACTION_RUNTIME_EVENTS
      LOGIC: 语音/摄像头/视觉推理事件是独立的通知通道。
      DEPENDENCIES: P0-1, P0-2
      DELIVERABLES: |
        - 新建 hooks/useChatRuntimeEvents.ts (~60 行)
        - 修改 hooks/useChatState.ts
      VERIFY: 同 P1-1

    - ID: P2-1
      NAME: DevPanel.tsx 拆分为 Tab 子组件
      ACTIONS: |
        将 DevPanel.tsx (55,090 bytes) 按 Tab 拆分:
        1. components/DevPanel/index.tsx — Tab 路由 + 共享 props (~100 行)
        2. components/DevPanel/SessionInfoTab.tsx — 会话信息 Tab (~300 行)
           - refresh, schedule, cancel, onVisibility (L169-L212)
           - loadLatestSession (L247)
        3. components/DevPanel/ContextTab.tsx — 上下文层信息 Tab (~200 行)
           - loadContext (L274), schedule, cancel, onVisibility (L291-L323)
        4. components/DevPanel/SubconsciousTab.tsx — 潜意识 Tab (~200 行)
           - loadSubconscious (L324), schedule, cancel, onVisibility (L341-L398)
        5. components/DevPanel/PerfTab.tsx — 性能诊断 + Benchmark Tab (~600 行)
           - copyDiagnosticSnapshot (L399), updateDiagnosticsEnabled (L404)
           - startCapture (L424), stopCapture (L444), downloadDiagnosticSnapshot (L466)
           - sendSelectedBenchmarkCase (L495), loadBenchmarkCases (L213)
           - SpaceLine (L1424), PerfMetric (L83), getEventTone (L99)
        6. 移动 components/DevPanel/types.ts (已在正确位置)
      LOGIC: 每个 Tab 有独立的数据加载和 UI 渲染。拆分后可独立开发。
      DEPENDENCIES: 无（独立任务）
      DELIVERABLES: |
        - 重组 components/DevPanel/ 目录
        - 修改 components/DevPanel.tsx → 变为 components/DevPanel/index.tsx
        - 更新所有 import DevPanel 的文件路径
      VERIFY: |
        - 全部测试通过（特别是 DevPanel.test.tsx）
        - 手动验证: DevPanel 各 Tab 功能正常

    - ID: P2-2
      NAME: styles.ts residual 迁移完成
      ACTIONS: |
        将 styles.ts 中 useResidualStyles (L18-L2694, ~2,677 行) 按组件域提取到 styles/ 目录:
        1. 分析 residual 中的样式 key，按使用组件分组
        2. 可能的新域文件: styles/devpanel.styles.ts, styles/sidebar.styles.ts, styles/modal.styles.ts, styles/notification.styles.ts 等
        3. 每提取一个域 → 新建文件 → 更新 useChatStyles 组合函数 → 运行测试
        4. 最终 styles.ts 只保留 SIDEBAR_WIDTH 常量 + useChatStyles 组合函数
      LOGIC: 继续已验证有效的渐进式迁移模式。
      DEPENDENCIES: 无（独立任务，但建议与 P2-1 并行）
      DELIVERABLES: |
        - 新建 2-4 个 styles/ 域文件
        - styles.ts 从 76KB 降至 <2KB
        - useChatStyles 更新为合并全部域
      VERIFY: |
        - 全部测试通过
        - 视觉回归: 聊天页面外观不变

    - ID: P2-3
      NAME: 创建 ChatContext 分层
      ACTIONS: |
        1. 新建 context/ChatStateContext.tsx — 只读状态 Provider
           - 组合 useChatState 返回的所有状态属性
           - 使用 React.memo + useMemo 优化重渲染
        2. 新建 context/ChatDispatchContext.tsx — 操作方法 Provider
           - 组合 useChatState 返回的所有 action 函数
           - 使用 useCallback 稳定引用
        3. 修改 index.tsx — 用 Context Provider 包装 ChatLayout
        4. 修改 ChatMain.tsx — 使用 useContext 替代 props
        5. 修改 MessageList.tsx 等子组件 — 逐步迁移到 useContext
      LOGIC: 减少 props drilling，使中间层组件不需要知道所有 75 个属性。
      DEPENDENCIES: P1-1 ~ P1-8 (useChatState 已拆分后接口更稳定)
      DELIVERABLES: |
        - 新建 context/ChatStateContext.tsx (~100 行)
        - 新建 context/ChatDispatchContext.tsx (~80 行)
        - 修改 index.tsx, ChatMain.tsx 等
      VERIFY: |
        - 全部测试通过
        - 手动验证: 消息流、输入、会话切换

    - ID: P2-4
      NAME: useChatState.ts 主 hook 精简
      ACTIONS: |
        在 P1-1 ~ P1-8 全部完成后，useChatState.ts 应该只包含:
        1. 调用 8 个子 hook
        2. 组合返回值
        3. 少量跨 hook 协调逻辑
        目标: 从 6,209 行降至 <1,500 行
      LOGIC: 主 hook 变为纯组合层。
      DEPENDENCIES: P1-1 ~ P1-8
      DELIVERABLES: |
        - 修改 hooks/useChatState.ts
      VERIFY: |
        - 全部测试通过
        - 手动验证: 全部功能正常

  EXECUTION_ORDER: |
    Phase 0 (低风险，纯移动):
      P0-1 → P0-2 → P0-3 (可并行)

    Phase 1 (中风险，hook 拆分):
      依赖关系:
        P1-1 (SessionManagement) ──→ P1-2 (SseLifecycle) ──→ P1-3 (MessageSend)
        P1-1 (SessionManagement) ──→ P1-4 (Compaction)
        P1-5 (WorkspaceNotifications) — 独立
        P1-6 (WorkspaceAgentSelection) — 独立
        P1-7 (ChatModals) — 独立
        P1-8 (ChatRuntimeEvents) — 独立

      推荐顺序: P1-6 → P1-7 → P1-8 → P1-5 → P1-1 → P1-4 → P1-2 → P1-3
      (先做简单的、独立的，再做复杂的、有依赖的)

    Phase 2 (中低风险，UI 层):
      P2-1 (DevPanel) 和 P2-2 (styles) 可并行
      P2-3 (Context) 依赖 Phase 1 完成
      P2-4 (主 hook 精简) 最后执行

---

EVIDENCE:
  CONFIRMED_CONSTRAINTS: |
    1. useChatState.ts: 6,209 行, 211,150 bytes, 已确认 (file_read meta)
    2. UseChatStateReturn interface: L1072, 75 个返回属性, 已确认 (file_read L1072-1185)
    3. useChatState function body: L1185-L6209, ~5,024 行 hook 逻辑, 已确认
    4. 模块级纯函数: L1-L1184 (~1,184 行), 包含 15+ 纯函数 + 10+ 类型/常量定义, 已确认
    5. Hook 调用统计 (search_grep 确认):
       - useState: ~35 次
       - useEffect: ~15 次
       - useCallback: ~30 次
       - useMemo: ~10 次
       - useRef: ~25 次
    6. ADR-057 迁移基础设施已就绪:
       - conversationStore.ts: 220 行, useSyncExternalStore + selectors, 已确认 (file_read)
       - useConversation.ts: 144 行, connectionManager + gapRecoveryEngine 集成, 已确认 (file_read)
       - 但 useChatState 未采用, 已确认 (code comment at L1-L8)
    7. ADR-054 Runtime Store 骨架已创建但未接入生产:
       - messageRuntimeStore.ts: 297 行, 标记 EXPERIMENTAL, 已确认 (file_read L1)
       - composerStore.ts: 87 行, 已确认 (file_read)
       - sessionLifecycleStore.ts: 137 行, 纯函数状态机, 已确认 (file_read)
       - agentStatusStore.ts: 存在 (list_dir)
    8. DevPanel.tsx: 55,090 bytes, code_outline 确认 32 个符号, 主函数 DevPanel L115-~1423, 已确认
    9. DevPanel/types.ts: 73 行, DevPanelProps + DevRawEvent + ContextSnapshot + SubconsciousResult, 已确认 (file_read)
    10. styles.ts: 76,743 bytes, 已从 149KB 降至 76KB, 已确认
        - SIDEBAR_WIDTH (L14)
        - useResidualStyles (L18-L2694, ~2,677 行未迁移)
        - useChatStyles (L2695+, 组合 11 个域), 已确认 (shell tail read)
    11. styles/ 目录: 10 个域文件已迁移 (agent, composer, layout, markdown, message, panel, process, reasoning, user, voice), 已确认 (list_dir)
    12. 30 个组件 import useChatStyles from '../styles', 已确认 (search_grep)
    13. 测试文件: 49 个 .test.* 文件, 已确认 (file_search)
    14. 关键测试文件:
        - useChatState.recovery.test.ts: 30,698 bytes
        - useChatState.selection.test.tsx: 24,817 bytes
        - MessageList.test.tsx: 49,351 bytes
        - useChatState.terminalIdentity.test.ts: 1,530 bytes
    15. 现有文件结构:
        - hooks/: 16 个文件 (useChatState.ts 是最大)
        - components/: 30+ 个组件文件
        - runtime/: 5 个 store + types + selectors
        - reducer/: conversationReducer + subAgentReducer
        - state/: conversationStore
        - connection/: connectionManager + gapRecoveryEngine
        - outbox/: commandOutbox + outboxPersistence
        - transport/: sseClient
        - viewport/: useMessageViewportRuntime + messageProjection
        - projections/: messageProjection
        - styles/: 10 个域文件
        - client/: chatClientStore + agentChatApi + localCache + syncEngine + featureFlag + clientIdentity + types
        - domain/: contracts
        - utils/: inboundDebug + pinnedMessage
        - perf/: chatPerfScenario.test
        - outbox/: commandOutbox + outboxPersistence

  AFFECTED_COMPONENTS: |
    核心修改文件:
    - hooks/useChatState.ts — 6,209 行 → ~1,200 行
    - components/DevPanel.tsx — 55KB → 拆分为 DevPanel/ 目录
    - styles.ts — 76KB → <2KB
    - index.tsx — 734 行, 可能增加 Context Provider

    新建文件:
    - utils/chatStateUtils.ts (~600 行)
    - utils/chatStateUtils.test.ts (~200 行)
    - utils/chatDiagnostics.ts (~60 行)
    - types/chatStateTypes.ts (~200 行)
    - hooks/useSessionManagement.ts (~600 行)
    - hooks/useSessionManagement.test.ts (~200 行)
    - hooks/useSseLifecycle.ts (~1,200 行)
    - hooks/useSseLifecycle.test.ts (~300 行)
    - hooks/useMessageSend.ts (~700 行)
    - hooks/useMessageSend.test.ts (~200 行)
    - hooks/useCompaction.ts (~400 行)
    - hooks/useCompaction.test.ts (~150 行)
    - hooks/useWorkspaceNotifications.ts (~150 行)
    - hooks/useWorkspaceNotifications.test.ts (~100 行)
    - hooks/useWorkspaceAgentSelection.ts (~300 行)
    - hooks/useWorkspaceAgentSelection.test.ts (~150 行)
    - hooks/useChatModals.ts (~80 行)
    - hooks/useChatRuntimeEvents.ts (~60 行)
    - components/DevPanel/index.tsx (~100 行)
    - components/DevPanel/SessionInfoTab.tsx (~300 行)
    - components/DevPanel/ContextTab.tsx (~200 行)
    - components/DevPanel/SubconsciousTab.tsx (~200 行)
    - components/DevPanel/PerfTab.tsx (~600 行)
    - context/ChatStateContext.tsx (~100 行)
    - context/ChatDispatchContext.tsx (~80 行)
    - 2-4 个新 styles/ 域文件

  VERIFICATION_PLAN: |
    每步操作后:
    1. npm test -- --watchAll=false — 全部 52 个测试通过
    2. npm run build — 无 TypeScript 编译错误
    3. npm run lint — 无 ESLint 错误 (如有配置)

    集成测试场景:
    1. 消息发送 + SSE 流式响应 — 验证 useMessageSend + useSseLifecycle 拆分后正常
    2. 会话切换 — 验证 useSessionManagement 拆分后正常
    3. /compact 命令 — 验证 useCompaction 拆分后正常
    4. DevPanel 各 Tab — 验证 P2-1 拆分后正常
    5. 样式渲染 — 验证 styles 迁移后外观不变
    6. 工作区/Agent 切换 — 验证 useWorkspaceAgentSelection 拆分后正常
    7. 消息排队 + steering — 验证 useMessageSend 队列逻辑正常
    8. 语音/摄像头事件 — 验证 useChatRuntimeEvents 正常

    Edge cases:
    1. SSE 断线重连 + replay 补偿
    2. Compaction 后历史消息加载
    3. 多 Agent 并发工作
    4. 消息队列满/空状态
    5. 会话不存在 (404) 处理

    Rollback proof:
    - 每个 P0/P1/P2 任务都是独立 PR
    - 如果某个 PR 导致测试失败 → revert 该 PR
    - P0 阶段只是代码移动，不改变任何逻辑 → 可以安全 revert
    - P1 阶段每个子 hook 独立 → 可以单独 revert 某个 hook

---

RISKS:
  - RISK: useChatState 拆分后 hook 间依赖关系处理不当，导致 circular dependency 或状态不一致
    PROBABILITY: medium
    IMPACT: high — 功能异常，SSE 消息丢失或重复
    MITIGATION: |
      1. 按依赖顺序拆分: P1-6 → P1-7 → P1-8 → P1-5 → P1-1 → P1-4 → P1-2 → P1-3
      2. 使用 ref 透传打破循环依赖（已在现有代码中使用，如 startSessionEventStreamRef）
      3. 每步拆分后运行全部测试
      4. 拆分前先画出 hook 间依赖图

  - RISK: styles 迁移导致 antd-style createStyles hash 变化，样式断裂
    PROBABILITY: medium
    IMPACT: medium — UI 外观异常
    MITIGATION: |
      1. 每次只迁移一个域
      2. 迁移后立即视觉检查
      3. 保持 useChatStyles 组合函数接口不变
      4. 使用 CSS 类名对比工具验证

  - RISK: DevPanel 拆分导致 import 路径变化，引用断裂
    PROBABILITY: low
    IMPACT: medium — 编译错误
    MITIGATION: |
      1. 全局搜索所有 import DevPanel 的文件
      2. 一次性更新所有 import 路径
      3. 运行 TypeScript 编译验证

  - RISK: Context 分层引入不必要的重渲染
    PROBABILITY: medium
    IMPACT: medium — 性能下降，消息列表卡顿
    MITIGATION: |
      1. 使用 React.memo 包装 Context Provider 的 value
      2. 使用 useMemo 缓存组合状态
      3. 拆分 State Context 和 Dispatch Context
      4. 使用 selectors 只订阅需要的状态片段
      5. 性能测试: 对比 Context 前后的渲染次数

  - RISK: 子 hook 提取后测试覆盖率不足
    PROBABILITY: low
    IMPACT: medium — 重构引入 bug 但未被发现
    MITIGATION: |
      1. 为每个新提取的子 hook 编写独立测试
      2. 保持现有 52 个测试全部通过
      3. 使用现有 useChatState.recovery.test.ts 和 useChatState.selection.test.tsx 作为集成测试

  - RISK: P1-2 (SSE Lifecycle) 提取过于复杂，可能需要多轮迭代
    PROBABILITY: high
    IMPACT: medium — 进度延迟
    MITIGATION: |
      1. 将 P1-2 拆分为更小的步骤: 先提取 delta batching → 再提取 replay → 再提取 SSE 连接管理
      2. 每小步都运行测试
      3. 预留 2-3 天专门处理 P1-2

---

BLOCKERS:
  ASSUMPTIONS_AND_MISSING_INPUTS: |
    1. **假设**: 52 个测试覆盖了 Chat UI 的核心功能。如果某些功能缺少测试，拆分可能引入未检测的 bug。
       解决方案: 在 P0 阶段开始前，先运行全部测试确认当前通过率。

    2. **假设**: UmiJS Max 4 的 model 系统与 React Context 不冲突。
       解决方案: 检查 UmiJS model 的使用范围，确认 Chat 页面是否依赖 UmiJS model。

    3. **假设**: antd-style 的 Composition API 支持当前的组合模式。
       解决方案: useChatStyles 已经在做组合，确认这不是 antd-style 的官方 Composition API，而是手动的。

    4. **缺失输入**: 不确定 useChatState.ts 中哪些 useEffect 的确切依赖数组。某些 effect 可能有隐含依赖，拆分后需要重新评估。
       解决方案: 在 P1 阶段每提取一个 hook 时，仔细检查其 useEffect 依赖数组。

    5. **缺失输入**: 不确定 MessageList.tsx (823行) 和 AgentMessageBubble.tsx (517行) 是否也需要拆分。
       解决方案: 本次方案聚焦于 useChatState、DevPanel、styles。MessageList 和 AgentMessageBubble 已有 React.memo，可在后续迭代中处理。

    6. **缺失输入**: ChatMain.tsx (511行) 的 props 接口。如果 ChatMain 使用 useChatState 的大部分返回值，Context 分层可能不需要修改 ChatMain。
       解决方案: 在 P2-3 开始前读取 ChatMain.tsx 确认其 props 接口。
