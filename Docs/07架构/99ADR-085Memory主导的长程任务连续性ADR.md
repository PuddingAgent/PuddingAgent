# ADR-085：Memory 主导的长程任务连续性

- 日期：2026-09-12
- 状态：Proposed；设计交付，未宣称运行中已实现。
- 详细设计：[长程自治设计 §4–5、§8](../Features/PuddingAgent长程自治与缓存99优化设计-2026-09-12.md)
- 2026-09-14 用户定位纠偏：[Memory快照索引与历史溯源](../Features/Memory快照索引与历史溯源设计-2026-09-14.md)。Memory为主动维护的当前快照/分层目录；历史全文与向量命中不代表最终裁决，历史仅按需溯源。此补充优先解释旧自动召回描述，实施仍待验收。
- 关系：修订 ADR-042/ADR-074 中把会话压缩承担为续行主通道的范围；保留显式compact、压力压缩、canonical原文导入与覆盖证明。沿用 Wiki Book v1 简单写入合同，不重新启用F0–F10。

## 背景

现有新Session首轮会注入最近完整摘要并叠加多种Memory/Skill层。SessionSummaryStore只寻找最近7天，不能独立承接数十天任务；“已压缩”和“下一轮一定找得到必要事实”也不是同一事实。

## 决策

1. Task/Goal为状态、依赖和下一动作的权威；Memory为可复用事实与工作知识的权威；Session为对话证据；Run为执行事实。三者通过稳定ID/版本引用连接。
2. 使用现有checkpoint存储一份current续行胶囊：Task/WorkUnit、revision、nextAction、evidenceRefs、memoryRefs、pendingOperationIds、waitingOn、executionRoot和已观测build。禁止Memory正文复制第二个任务状态机。
3. Agent在关键事实确认、阶段交付、长等待、计划性切换/压缩、交付部署前主动写入下一轮所需Memory。重复事实upsert，不每工具调用写摘要；关键写入须有持久回执才能写checkpoint引用。
4. 沿用save_memory/manage_memory现有页/章节更新与统一Wiki写入口；edit_page是拟收敛的语义，不是假称现有工具已支持该action。scope由框架约束，补稳定key、版本冲突和明确回执即可；不加复杂语义审批链。跨库采用先Memory成功再checkpoint CAS；幂等重试，不伪造跨库原子性。
5. 新Session加载有界续行文本、记忆引用目录和必要约束；不自动塞完整旧摘要。按需检索返回ID/version、短摘要、来源与读取指针；用query+scope+权限+indexRevision等构成稳定cache key。
6. current记录按主题更新、归档与正文分离；偏好不按时间删除，活跃任务引用不受7天TTL清理。重复固定事实写入不增加current页数，真正新事实允许增长。GC先确认无活跃引用。
7. Session切换不触发全量压缩；压力、显式请求和容量变化按统一预算决定压缩。保留tool-call/result原子组、原文覆盖与summary-only no-op，失败保持可恢复旧状态。
8. 取消每5轮自动强制Recall和无变化后台整理。检索/整理按实际需要、数据版本和积压触发，辅助模型调用单独计量。
9. Memory按项目/概念/场景建立多级索引，根目录不装所有叶子正文；完整内容在唯一权威外部文件/目录或图书馆页，索引保存短结论与版本引用。沿用Book/Page/Pointer，不造平行知识库。
10. 区分current版本与已确认结论：最新草案、旧决策的后来引用不能覆盖已确认快照。向量和FTS仅发现候选，按scope/状态/源版本过滤，再按需读取来源；不以相似度、时间或出现次数裁决有效性。
11. 默认装配不做历史日志召回和重建。溯源由明确需要触发，沿命中前后及后续修订读取有界证据；结果无结论时返回未确认，不默认为current。保留当前会话有序工作上下文和压力压缩，不能误删canonical hydration。
12. 先交付M02最小快照写读，再由M01移除旧自动注入；C03整合需求驱动历史与向量检索。Vector离线仍支持索引导航/FTS，空结果不自动跨范围或开探索子代理。各级展开共享总预算，历史GB增长不扩大默认上下文。

## 验收与所有者

Platform维护Task/Goal checkpoint，MemoryEngine维护Book/Page和索引，Runtime组织上下文与工具回执；Desktop不承担任何记忆业务。

测试跨Session、checkpoint冲突、Memory写成但引用未写成、崩溃后原文补查、删除/归档索引、撤权、不存在引用、旧事实覆盖、30日活跃任务与重复事实负载。主Session全新/子Session/恢复/rollover分别测量首轮装配空间，不用TurnRound=0单独判定新会话。

72小时与7日验证运行质量，30日墙钟验证逻辑任务持续性；模拟时钟可证明TTL和状态机，但不能替代实际30日连续工作。新编译代码由外部部署并证明加载后才计入产品结论。

## 代价与取舍

显式检索可能增加少量工具轮次，必须用找回准确率、总成本和恢复成功率衡量；不能只追求零检索。弱化自动摘要可能暴露过去被大段历史掩盖的漏写，因此关键边界回执和原文恢复是先决条件。

拒绝无限增长的单一MEMORY.md、每Session重复总结全部历史、删除压缩能力，以及先建设完整新记忆调度平台再兑现基本写读。
