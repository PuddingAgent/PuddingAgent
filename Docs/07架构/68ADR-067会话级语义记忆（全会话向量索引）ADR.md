# ADR-067 会话级语义记忆（全会话向量索引）

- 状态：已实施（代码入库，待发布激活）
- 日期：2026-08-06
- 关联：ADR-028/029（记忆图书馆）、ADR-042（Agent 记忆隔离）、ADR-057/058（Conversation 事件流与消息持久化）
- 实施提交：53b34d4 / 5366392 / 7600b2e / 92aede9（前置 hook 修复 51d5f03）

## 1. 背景与问题

用户诉求："跨越多个会话窗口，瞬间回忆起 N 个会话之前的某个内容"。诊断暴露三个缺口：

1. **会话语料不是记忆**：原始对话在 platform.db 的 ChatMessages（约 43 万行），无语义索引；压缩冲洗只带少量幸存事实进记忆图书馆，未被提取的原话永久不可召回。
2. **召回不覆盖会话**：RRF 四路的向量路只检索 Chapters（整理后的知识），不是对话本身。
3. **embedding 链路断裂**：章节向量在 Streaming 执行路径从不生成（同日定案修复，见 §5）。

## 2. 决策

建立 SessionChunkVectors 会话块向量索引，作为召回第 5 路，形成"落库 → 切块 → 向量化 → RRF 召回"闭环。

**数据模型**（落 memory.db，MemoryLibraryDbContext）：ChunkId(PK) / WorkspaceId（ADR-042 隔离）/ SessionId（会话归属——投影落库时 AgentInstanceId 恒为 null，故不用它）/ MessageId / ChunkSeq / Role / SourceText / Embedding(BLOB, float32×1024) / CreatedAt。索引：(WorkspaceId,SessionId,ChunkSeq) + **(MessageId,ChunkSeq) 唯一索引**（幂等兜底）。Schema 走自定义 DDL bootstrapper 三处同步（DbSet/映射/MemoryLibraryDbInitializer），不用 EF Migrations（仓库惯例，EnsureCreated 不兜底共享库）。

**写入侧**：TextChunker 纯函数切块（≤1024 字符、句子边界优先、128 字符重叠、超长硬切）；SessionChunkIndexer 在消息落库成功后 fire-and-forget 索引（仅 user/assistant、过滤 <20 字符、批量 embed、唯一索引冲突视为已索引、异常全日志）。接入 AgentConversationLogService（主）+ ChatTranscriptWriter（兜底）双落库点；接口置 PuddingCore.Abstractions 防循环依赖。

**读取侧**：MemoryLibrary.SearchSessionChunksByVectorAsync（工作区过滤 + 余弦 topK）；MemoryRecallService 第 5 路 RRF 权重 0.7（与章节路同权）；**查询 embedding 第 4/5 路共享同一 Task，一次召回只调一次模型**，失败两路同时优雅降级；SourceId 编码 `chunk:{SessionId}:{ChunkId}` 防碰撞可溯源。

**存量回填**：SessionChunkBackfillService（一次性 IHostedService）：键集分页（Id > lastId，避免 O(N²)）扫 ChatMessages，角色预过滤，批查已索引 MessageId 跳过，批间限速；配置门控 SessionChunkBackfill { Enabled=false, BatchSize=50, DelayMs=200 }，仅 Enabled=true 时注册（双组合根一致）；幂等可重跑。

**Provider 与路由分离（用户原则）**：providers.json 只注册服务商与模型元数据（lmstudio-local → 本地 LM Studio qwen3-embedding-0.6b, 1024 维）；采用哪个模型由 agent 配置决定——manifest 的 EmbeddingProviderId/EmbeddingModelId 字段已存在，本 ADR 未接线（§5），接线后全局路由节降级为 fallback。

## 3. 备选与否决

| 备选 | 否决理由 |
|---|---|
| EF Migrations | 违背仓库 schema 管理惯例（bootstrapper 刻意抑制 pending-model 警告） |
| 向量数据库扩展 | 当前规模内存余弦足够且与章节路同构，避免新依赖；规模化另立 ADR |
| 只扩展章节向量路 | 章节粒度不覆盖原始对话，与诉求不符 |
| 召回时实时 embed 历史 | 延迟不可接受，43 万行必须离线预索引 |
| 落库同步索引 | embedding 阻塞对话主链；fire-and-forget + 幂等是正确权衡 |

## 4. 后果

收益：新对话落库即可语义召回；回填后 43 万行历史可召回并溯源到会话；第 5 路零破坏并入既有 RRF（无 embedding 服务时优雅降级）；幂等使回填/双写/重复触发全部安全。

成本与局限：向量检索为全量加载内存余弦 O(N·dim)，百万级块需近似索引/分区（后续）；回填全量约 4 小时（DelayMs=200）；Embedding 维度随 provider 变化时旧向量需重建（当前单一 provider 无此问题）。

## 5. 前置修复与后续

前置（51d5f03）：EmbeddingGenerationHook 零向量根因 = Streaming 路径从不 fire OnRoundCompleteAsync；修复为多触发点（RoundComplete/LoopComplete/Cancelled/Failed）+ Interlocked 守卫 + 异常日志。教训：双执行路径钩子不对称是隐蔽 bug 源；fire-and-forget 必须自带异常日志（健康自证）。

后续：L2e agent 作用域 embedding 接线（manifest 字段已就位）；L3 实体卡片；检索规模化（近似索引）；system 日志保留卷数调大。

验收：见 Docs/Tasks/2026-08-06-三层记忆架构-L2施工总结与待办.md
