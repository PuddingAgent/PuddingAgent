# PuddingAgent 持续优化执行台账

用户于 2026-09-05 授权继续负责全部已登记优化，并已允许停机重启。本台账是后续自动唤醒与人工轮次的恢复入口；不是新的产品运行时调度器。Codex 的既有 `puddingagent` heartbeat 已更新为每 30 分钟推进当前线程，有可执行工作就做一个有界步骤，无变化且无行动时安静。

## 范围与顺序

“全部优化”指本线程审计发现与用户列出的吞吐、缓存/上下文、工具、调度、Chat 体验、CPU/内存和提交质量，不指实现初始讨论中的无限上下文架构设想或所有仓库 Backlog。

| 顺序 | 工作包 / 看板 | 当前状态 | 完成门禁 |
|---|---|---|---|
| 1 | 预热内存 / 有界后台扫描，`f834c902793d450bbe3bffd8ad8a849d` | 第四轮部署及19/96项回归通过；warm采样末尾Private仍1486.74MiB，整体内存未通过 | 查询计划/工作量有界回归；区分 Private/WS/GC；同负载预热、流式和历史加载 A/B，不强制 GC 或清库 |
| 2 | 161.749s 流空档，`5c6aa2e9a8d44e029bab08a710770309` | 层历史无界读取已修复并部署；同任务复测56.898s、预热13.253s，均1次file_read；首层创建间隔157s降至244ms/76ms | provider receive、消费、持久化、工具分段时序；有界空闲取消/恢复且不重复工具；相同模型/提示/工具工作量对照 |
| 3 | Chat 交互与旧历史状态 | 类型/样式/SSE 退避已部署，UX 未验收 | 新 bundle 加载证明；长历史、滚动、流消息、终态和断线恢复的测试与实机证据；不得把旧历史卡的投影当成当前 Run |
| 4 | 工具效率与执行边界 | file_patch/monitor 等修复已部署；单次 canary 1 工具成功 | 保持工具语义，短稳定 schema；无重复失败/发现循环，预算和后置条件可验证 |
| 5 | 任务库存、预算与自动调度，`c7dc8f5d490e4a38ab47b5a5a71b69e1` | 服务运行但无可派库存；Tracker Explore 输入预算失败 | 拆小 WorkUnit，逐卡确定范围/依赖；不手工 assign/run-now 假冒自动 E2E；10 个安全任务完整闭环及七夜记录 |
| 6 | 缓存与有效产出，`af25d72c75634aae8579ec2c0a26ce08` | 44k cold + 44k warm 单回合加权49.769%；七日未达标 | 模型/来源/layer/prefix-change/cold-warm 分桶，任务与工具工作量保持不变；沿用严格完整七日 >99% 合同，不降标准 |
| 7 | 代码审计、部署与收口 | dirty 工作树包含用户原有修改；现有依赖告警未关闭 | 逐个补丁审查、聚焦/组合回归、明确制品哈希；Desktop 外部部署 + canonical 功能 + 性能分开；不把未提交源码叫干净提交 |

## 本轮事实与下一步

- 当前基准部署证据在 `PuddingAgent第三轮部署与产品验收-2026-09-05.md` 和 `pudding-agent-round3-acceptance-2026-09-05.json`。历史 PID 47056 仅作引用；任何控制/采样前重新发现当前 PID。
- 已证明 `SELECT MIN(ts),MAX(ts)` 的 SQLite 查询计划是 `SCAN events`；替换为两个有覆盖索引的端点 seek。索引必须是完整、首列匹配、BINARY 排序，拒绝 partial/表达式/非首列索引；不因“有一个索引”就认为有界。
- 新增 `Source/PuddingPlatformTests/Services/StorageInventorySamplerQueryTests.cs`，覆盖查询计划、NULL/空表、DESC、复合索引、表达式与部分索引、标识符转义。
- 第四轮组合测试 19/19，结果 `.tmp-test-out/efficiency-results/round4-storage-token.trx`；发布成功。详细阶段证据和 GC 基线见 `PuddingAgent第四轮有界查询与流空档修复-2026-09-05.md`，制品见 `pudding-agent-round4-build-2026-09-05.json`。
- 第四轮已完成停机备份与部署：`D:\Keys\PuddingDeploymentBackups\20260905-100531-4264176f`；Core24524于10:15:33 Ready，264制品/148前端源文件核验。`pudding-agent-round4-runtime-evidence-2026-09-05.json` 保存实际两个回合的活动、canonical时序与用量。message `extmsg-4f782526d1b3d16a6e9f2852c8e18a5e` 和 `extmsg-6188a6104bc6e1dc652d7fbb35fe2220` 已 succeeded，不再重发。
- 下一项：token attribution/fallback 的稳定调用ID去重、Chat快照/后台高分配归因。第二段90秒mixed采样末尾Private1486.74MiB，不能把第一段454MiB当持续收益；此增长仍待定位。已有SQLite索引首次启动创建耗时约3分半，后续不应重建同索引。
- 用户随后转为只读检查 `save_memory` 的查看/检索配套。本轮未改记忆工具：四工具存在且默认助手capability已选择，但缺按chapter_id读回全文；search_memory自动探索、检索返回ID丢失、save_memory未知action落入upsert等问题需明确设计后处理。不能把检查请求当作已经授权实施该新设计。

## 持续执行纪律

1. 先 git status，不覆盖已存在的 ToolInvocationService、TaskRecallAuditEngineTests、memory/INDEX.md 和 external 子模块修改。跨模块检索只进明确源码目录，排除 bin/obj；大事件表按 turn_id 或有界尾部读。
2. 通过技能使用 External Task/Message API，doctor、ETag 与幂等均保留。没有 canonical terminal 时仅跟踪原 messageId，不重复指令。真实模型验收不绕过配置读取 LLM secrets。
3. 源码完成不等于运行代码。部署须有效停机备份、无活跃执行/预约、Desktop 正式控制面、Ready 与 DLL/前端 hash；保留可恢复旧产物。WPF build/test/publish 串行，构建输出不放 D:\data。
4. 统计按真实时间类型：execution_runs/chat_execution_commands 为 epoch ms，gateway 为 UTC 文本；北京时间完整日换算 UTC。任务 idle 样本与任何 Run 重叠就改标 mixed；不以重启冷态当优化后预热态。
5. 不为达吞吐数字批量开 Backlog、拉高并发/预算、清数据或强制 GC。未能证明的结果写 pending/failed。provider 外部延迟、凭据权限或用户登录需求需准确记录，不反复猜测或绕过。
6. 每个有实质结果的步骤更新本台账、code_map.md、Docs/README.md、必要调试经验及对应看板。七夜/完整七日是时间门禁，只能持续采样，不能在同一天宣布完成。
