# Model-visible means logged 补强方案（2026-08-14）

> 出方案：蜜糖 ｜ 施工：默认助手 ｜ 用户强制不变量（2026-08-14 11:22）
> 审计依据：`temp/model-visible-means-logged-audit-20260814.md`（行级证据齐全，本文不重复引用，仅标关键锚点）

## 1. 现状结论（审计摘要）

不变量处于「半满足/双流分裂」：思维链✅/工具调用✅/子代理委派生命周期✅/compaction 摘要✅；**系统提示词与 context 各层正文❌、steering 正文❌、子代理内部轨迹⚠️旁路**；横切三缺：按来源查看缺统一 source 维度、恢复/分叉/检索/回放**不共享同一流**（6 处旁路数据源）、**仅追加性被 RetentionPruningService DELETE 破坏**。

## 2. P0 必修（违反不变量，五项）

### P0-1 context.assembled 事件（系统提示词/context 正文入日志）
- 目标：模型实际所见的 context 各层正文（脱敏后）可重建、可审计
- 写入点：`ContextAssemblyService.AssembleAsync`（76-92）完成后发射；`ConversationEventTypes` 增 `context.assembled`（或按层 `context.layer.*`，二选一，建议单层聚合+layers 数组，控制事件量）
- 契约：payload = { sessionId, turnId, layers: [{name, contentHash, content(脱敏后, 单层尺寸上限+超限截断标记)}], assembledAt }；密钥经 `_keyVaultService.StripAsync` 同路脱敏
- 验收：query/回放可取到某 turn 的完整 context 正文；`BuildStreamContextFrame` 长度元数据保留不破坏

### P0-2 steering.injected 正文留痕
- 目标：steering 干预正文可审计（合规盲区消除）
- 写入点：`AgentExecutionService.cs:358-399` steering 注入处，现有事件 payload 增 `content` 字段（messageChars 保留）
- 验收：steering 注入后事件流含正文；检索可按 source=steering 过滤（依赖 P0-4 source 维度）

### P0-3 子代理内部轨迹上卷（审计链不断裂）
- 目标：父流可追溯子代理内部模型可见内容
- 分步：P0 做「引用+关键投影」——父流写 `subagent.run.trace_ref`（run_id + 子归档路径 + 子会话 id）+ 子代理 thinking/tool 关键事件摘要投影（复用 ADR-060 投影通道 FileSubAgentRunStore:686-709）；P1 做全文上卷（run_id 关联查询接口）
- 验收：从父会话事件流可一键定位子代理完整轨迹（文件归档），审计链单入口

### P0-4 单流收敛（恢复/分叉/检索/回放共享同一事件流）
- 目标：消除双流双真相
- 方案：`conversation_events` 定为 canonical；`query_session_logs` 改读 conversation_events（或经统一投影层读，RawSessionLogService:15-23 改造）；旧路 session_event_log 降级为写入兼容期（双写保留 N 个版本后退役，退役时间入 ADR）
- 配套：`ConversationEvent` envelope 增 `AgentId` + `SourceKind`（user/agent/system/subagent/steering/compaction）——按来源查看的查询维度（横切 A 一并解决）
- 验收：检索与回放对同一会话给出同一事件序列；按 source 过滤可用

### P0-5 仅追加性恢复（Retention DELETE → 归档后删）
- 目标：append-only 语义不被破坏，超期不销毁证据
- 方案：`RetentionPruningService`（46-63,203-238）DELETE 前归档到 WORM 文件（按会话分片 jsonl，复用 AgentRawLogMirrorService 写路径模式）；归档完成才删表行；或两表直接排除出 pruning（配置开关，默认归档模式）；ADR 显式化 append-only + 归档语义
- 验收：pruning 运行后归档文件可查全量历史；表内仅保留窗口期

## 3. P1 增强（五项，P0 收口后排）

1. thinking 事件拆分 `thinking.delta`/`thinking.summary`，修正 TurnExecutorAdapter:127 名实不符 + ConversationProjector thinking 恒 null（137-160）
2. ChatMessages 投影增 thinking/tool 过程（刷新不丢失，ConversationProjector 扩展）
3. context 帧（Streaming.cs:668-669）持久化入 canonical 流（与 P0-1 互补：实时帧也留痕）
4. 子代理卡片改单一事件流来源（消除 subAgentReducer.ts:95-170 双源 reconcile 不一致窗口，前端）
5. 子代理全文上卷查询接口（run_id 关联，P0-3 的 P1 延伸）

## 4. 实施顺序与分工

顺序：P0-4（canonical+source 维度，其余事件的写入基座）→ P0-5（独立可并行）→ P0-1 → P0-2 → P0-3 → P1 序列。
分工：后端全项=默认助手施工；我=方案裁定+验收+前端 P1-2/P1-4 投影改造。
原子拆分建议：P0-4 拆（envelope 字段 / canonical 切换 / query 改读）三原子；P0-1 拆（事件类型+写入点 / 脱敏+尺寸上限）两原子。每原子独立 commit + 我验收（沿用 hunk 分离/push 前协调纪律）。

## 5. 不变量验收清单（施工完成后逐条核）

- [ ] 任取一 turn，可从 canonical 流重建：context 各层正文 / thinking / 工具调用与结果 / steering / 子代理轨迹引用
- [ ] Chat 与子代理视图可按 SourceKind 过滤查看
- [ ] 检索（query_session_logs）与回放对同会话同序列
- [ ] pruning 后归档可查全量，表行删除有据
- [ ] 分叉/恢复路径读 canonical 流，无旁路
