# ADR-079：Agent 消息交错内容流与最新行为组披露

> 状态：Accepted（设计决策已冻结；实现与生产验收尚未完成）  
> 日期：2026-08-25  
> 范围：Conversation canonical projection、Chat TurnSurfaceStore、AgentTurnCard、消息 viewport  
> 实施方案：[Agent 消息交错内容流与最新行为组披露完整实施方案](../Features/Agent消息交错内容流与最新行为组披露完整实施方案.md)

## 1. 背景

当前 Chat 曾先后出现以下失败形态：

1. reasoning/tool 组成一个大过程区，最终正文组成第二个大区，丢失真实交错顺序。
2. `answerMarkdown` 与 canonical TextBlock 同时渲染，造成同一正文重复出现。
3. 最终正文到达后所有行为轨迹折叠或消失，用户无法理解最近一步。
4. 历史水合只处理前两个目标，较新消息永久退回旧的“轨迹 + 整段正文”路径。
5. 展开组后 reasoning 仍是单行 ellipsis，要求二次点击；工具长 JSON 又可能自动撑开卡片。
6. 全量重投影、隐藏而不卸载、多个 scrollTop 写入者造成渲染和滚动卡顿。

deepseek-harness 证明了 assistant 内容块、reasoning disclosure、tool callId 配对和增量渲染的可行性，但 Pudding 需要额外满足“最近行为持续可见、历史行为自动收敛”的产品目标，不能机械复制其每段 reasoning 独立默认折叠策略。

## 2. 决策

### 2.1 单一有序内容流

一个 Agent 回合由一个 `AgentTurnCard` 承载。`TurnContentStream` 按服务端 canonical `sequence` 渲染 TextBlock 与 ActivityGroup；不得把行为链和最终正文拆成两个固定区域。

### 2.2 唯一正文源

存在 canonical TextBlock 时，正文只由块流渲染。`answerMarkdown` 只用于复制/TTS及无 canonical 正文时的兜底。`message.completed.reply` 不覆盖已有正文段。

### 2.3 ActivityGroup

两个正文段之间最大连续非正文节点构成 ActivityGroup。组 key 锚定首 source event；tool result 原位更新；折叠时只保留摘要 header，成员 DOM 必须卸载。

### 2.4 默认只展开一个最新行为组

默认披露 owner 为：

```text
当前最新 Agent 回合中的最后一个 ActivityGroup
```

这里的“最后一个”是块流中逆向找到的最后一个 ActivityGroup，不要求它是最后一个 block。即使最终 TextBlock 已经到达，最近 ActivityGroup 仍保持展开。

默认 owner 只在以下条件变化：

1. 同回合出现更新的 ActivityGroup；或
2. 更新的 Agent 回合开始产生 ActivityGroup。

普通正文 delta、工具 result 原位更新和回合完成不会关闭当前 owner。用户手动展开/折叠形成显式 override，优先于自动默认值。

### 2.5 柔和收起，随后卸载

owner 转移时，旧组以 220ms 高度和 160ms 透明度过渡收起；完成后卸载成员 DOM。首次 hydration 不播放批量动画；reduced-motion 立即切换或短淡出。行为组件不得写 scrollTop，高度变化交给 message viewport 的单一 scroll authority。

### 2.6 展开组的信息密度

- reasoning：直接显示完整 canonical 可披露文本，保留换行，不再有二级 disclosure。
- tool：直接显示名称、可读主参数、running/success/failure、耗时和 exit code；原始 presentation/IN/OUT 按需展开。
- delegation：显示任务摘要和状态，完整运行细节进入检查器。
- 默认组过大时只延迟挂载更早成员，不改变 canonical 顺序和统计。

### 2.7 Canonical-only

跨 bootstrap/detail/active/live 的顺序只接受服务端 `sequence`。缺失 sequence 的旧轨迹 fail-closed，不在前端用数组下标、负数或时间戳伪造。开发阶段完成数据重置/升级后删除临时 adapter，不长期维护双状态机。

## 3. 架构边界

```text
Conversation canonical events
        │ eventId + sequence + turnId/runId/toolCallId
        ▼
AgentConversationProjectionService
        │ snapshot/detail + TurnEventWindow
        ▼
TurnSurfaceStore
        │ idempotent merge + monotonic state
        ▼
ExecutionFlowProjector            （纯函数，不持 UI 状态）
        │ ordered nodes
        ▼
buildTurnContentBlocks            （纯函数，不重排）
        │ TextBlock / ActivityGroup
        ▼
LatestDisclosureOwner             （默认值）
        │ + user override
        ▼
TurnContentStream / ActivityGroup （渲染与过渡）
        │ size changes only
        ▼
MessageViewportRuntime            （唯一 scroll authority）
```

职责约束：

- Core/Platform 产生事实、顺序、窗口和终态。
- projector 只解释事实。
- disclosure 层只决定可见性，不改变事实顺序。
- viewport 只决定滚动，不参与内容投影。

## 4. 状态与标识

- `eventId`：幂等键。
- `sequence`：唯一跨源顺序。
- `turnId/runId`：回合与执行身份。
- `toolCallId`：工具 call/result 配对键。
- `messageId`：最新 Agent 回合 owner 判定。
- `ActivityGroup.key`：首 source event 锚定，后续更新稳定。
- disclosure override：`unset | expanded | collapsed`。
- visual phase：`closed | opening | open | closing`。

任何数组 index 都不得成为持久 key 或事件顺序。

## 5. 被否决方案

### A. “过程大块 + 最终正文大块”

否决。它按类型重新分区，无法表达 `text → tool → text → tool`，并容易形成第二正文源。

### B. 平铺所有 reasoning/tool，永不折叠

否决。长回合会淹没正文、扩大 DOM、降低滚动质量。

### C. 仅当 ActivityGroup 是整个块流最后一块才展开

否决。最终正文一到达，最近轨迹会被隐藏，直接违背“用户了解最近一段”的目标。

### D. 每条 reasoning/tool 都独立默认折叠

否决。外层组已是披露边界，再要求用户二次点击 reasoning 会产生过深层级；但工具原始 IN/OUT 因负载大仍保留二级按需披露。

### E. CSS `display:none` 隐藏历史成员

否决。DOM、Markdown、JSON 与 renderer 仍然存在，不能解决卡顿。必须在收起过渡结束后卸载。

### F. 每条消息各自默认展开最后一个组

否决。长会话会同时打开多个“最新组”。默认 owner 在会话视图内唯一，只属于当前最新 Agent 回合；历史消息全部折叠，用户可手动展开。

### G. React 合成 sequence 兼容旧数据

否决。跨 snapshot/live/detail 的数组顺序不具备同一语义，合成会制造不可重放的错误轨迹。

## 6. 后果

### 正面

- 恢复真实交错语义，正文和行为链不再互相覆盖。
- 用户始终看到最近 reasoning/tool 状态，同时历史过程保持干净。
- 默认只挂载一个行为组的成员，显著限制 DOM 与 Markdown/JSON 成本。
- 同一 projector 服务实时、刷新、gap recovery 和历史回放，减少双路径偏差。
- disclosure、projection、viewport 边界清晰，可分别测试。

### 代价

- MessageList 必须提供会话级最新 Agent 回合身份，不能只在单卡内部判断。
- 柔和收起需要短暂保留 closing DOM，并处理 transitionend、reduced-motion 和焦点迁移。
- 现有 ReasoningDisclosureRow 需要组内 full 模式；工具行需要区分“可读摘要展开”和“重载荷详情展开”。
- 旧数据若缺 sequence 将不显示伪造轨迹；开发环境需要原地升级或重置。

### 风险及缓解

| 风险 | 缓解 |
|---|---|
| 收起动画导致底部跳动 | 组件不写 scrollTop；viewport 单一写入并基于 ResizeObserver 收敛 |
| owner 快速切换产生竞态 | 稳定 group key + visual phase 状态机 + transition fallback timer |
| 最新组包含大量工具 | 原始详情懒加载；更早成员按需挂载；只让一个默认 owner open |
| 用户正在操作时自动卸载焦点 | 焦点先迁移到 group header；用户 override 阻止自动关闭 |
| 历史水合不完整 | 明确 TurnEventWindow；并发 2 的队列持续排空；hasMoreBefore 可见 |

## 7. 兼容与迁移

1. 新事件合同把 sequence 设为 required，并在服务端统一产生。
2. 修复/重放开发数据，或按项目开发阶段约定重置相关历史数据。
3. 新 UI 先在单一临时 rollout flag 下验证；不得长期保留路径 A/B。
4. 稳定后删除旧双区域渲染、数组 index 顺序、第二 answer bubble 与未使用 adapter。
5. 无 canonical 明细的记录只显示最终正文和轨迹不可用提示。

## 8. 验收门禁

ADR 被接受只表示决策冻结，不表示实现完成。实现必须依次通过：

1. Core/Platform canonical 合同与 chunk flush 测试。
2. Store/projector/block 的乱序、幂等、Snapshot+Watch 等价测试。
3. 最新 owner、最终正文后保持展开、owner 转移动画、用户 override 测试。
4. reasoning 完整换行、工具详情懒加载、折叠后 DOM 卸载测试。
5. viewport 的自动吸底、用户上滚、历史锚点和 reduced-motion 测试。
6. 前端生产构建与相关 .NET 构建。
7. 外部控制器部署明确新构建。
8. 新 Pudding 会话执行真实多轮工具调用 smoke，并保留 sequence、DOM 顺序、截图、性能与日志证据。

在第 8 项完成前，状态只能是“设计冻结/实现中/ready-for-external-deploy”，不得写成生产验收完成。

## 9. 与现有 ADR 的关系

- 补充 ADR-050 的统一会话投影与观察者模型，不创建第二套前端事实源。
- 遵守 ADR-056/057 的可靠事件流、幂等与重放边界。
- 复用 ADR-073 的 Projection 合同；本 ADR 只冻结 Chat 消息卡的交错内容流和 disclosure 策略，不替代完整 Trajectory 页面。
- 废止 `Docs/chat-ui-behavior-chain-quality-upgrade-2026-08-23.md` 中“新正文到达即折叠原尾组”以及“每条历史消息各自保留一个默认展开组”的旧描述；以本 ADR 为准。
