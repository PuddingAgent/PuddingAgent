# PuddingAgent 第四轮：有界采样与流结束后阻塞

## 源码修复与验证

- `StorageInventorySampler`：组合 MIN+MAX 改为两个索引端点 seek，验证完整/BINARY/首列索引，拒绝部分、表达式和非首列索引。目录枚举改为惰性深度优先，根、目录、不匹配文件、reparse point 均消耗同一 2000-entry 上限；取消可观察、枚举器必释放、不跟随链接。100ms 仍是 slice 目标而非硬实时保证。
- `TokenUsageRecorder.RecordContextLayerMetricsAsync`：移除整段会话历史实体 ToList/GroupBy/Sort，按当前层名各取上一条 ContentHash。新增 `(session_id,layer_name,occurred_at_utc DESC,id DESC,content_hash)` 覆盖索引；保持 UTC 事件时间、Id 决胜顺序，不能用“最后插入的一条”替代晚到事实的事件顺序。写入参数归一到 UTC，不保存或修改提示内容，也不减少任务或工具工作量。
- `PlatformDbContext` 与 `TokenUsageSchemaBootstrapper` 同步新索引，已有数据只增加索引，不清库、删除历史或增加旧格式兼容层。现有日期列按 SQLite provider 的 UTC 规范格式排序。
- 定向组合 **19/19** 通过，TRX：`.tmp-test-out/efficiency-results/round4-storage-token.trx`。覆盖端点查询计划、NULL/空表、DESC/转义、目录预算和取消、1000 条历史/晚到时间/同时间 Id 决胜、跨会话隔离、schema 幂等重建，以及既有 prefix/layer 记账测试。
- 扩展组合 **96/96** 通过（17 秒）：加上 ContextLayer、token、run monitor、lease recovery、task tracker/settlement、intent outcome 和 archive store；`.tmp-test-out/efficiency-results/round4-combined.trx`。
- Desktop Release publish exit 0，制品清单 `pudding-agent-round4-build-2026-09-05.json`。Desktop shell、Runtime 和前端与第三轮相同，仅新 Core/Platform 部署待验；现有 NuGet 安全与编译告警未关闭。

## 161.749 秒空档的阶段证据

沿用第三轮真实回合：conversation `206a9b48ec904ebb93e7541131fbb835`，turn `d32dda3fe229441897e73074711e2a30`，run `e39a265a771442edb1c3290ed36b48b4`。以下为北京时间：

| 阶段 | 实际时间 / 耗时 |
|---|---|
| 第一轮 chat_stream | 09:22:29.262461 → 09:22:37.8755006，8612ms |
| 首 chunk / 最大 chunk gap | 4796ms / 266ms；238 chunks，finish_reason=tool_calls |
| Gateway 账本 CreatedAt | 09:22:38.5769611 |
| Runtime attributed token 记账开始 / Entity 创建 | 09:22:38.8873378 / 09:22:38.9475461 |
| 第一条新 layer 实体创建 | 09:25:16.349287；距 token 记账开始约 157.462s |
| 下一 canonical usage 事件 / 工具开始 | 09:25:19.418411 / 09:25:19.742 左右 |
| 第二轮 chat_stream | 3835ms，50 chunks，首 chunk 3219ms |

结论：空档不在模型流等待；已收窄到 Runtime attributed token 记账内部，首次层实体创建前的历史加载/分组阶段有明确无界读取缺陷。旧回合没有每条 SQL 的耗时和分配事件，157.462s 不能全部精确归给某个单独查询；部署后同任务复测才能确认实际收益。不能依据 occurred_at=committed_at 声称事件提交无延迟，两者使用同一个提交时间。

另发现首次 usage 被 ConversationProjector fallback 再写一条不同 SourceId；gateway 权威计费仍只有两次。源码按 token 指纹和 ±2 分钟事件时间窗口查重，本次约160秒本地停顿超出该窗口，解释了漏匹配。该去重/归因问题仍须用稳定 invocation identity 单列修复，不能仅扩大时间窗、混算 Runtime/gateway 条数或删除历史掩盖问题。

## GC 诊断基线（不是 A/B 收益）

10:04:28 开始，旧 Core 47056，30 秒、2 秒间隔、仅 EventCounters；未取 heap dump、未强制 GC。输出 `.tmp-test-out/efficiency-results/round4-before-gc.csv`。

- GC heap 平均 230.687 **MB（十进制）**，范围 133.225–318.622 MB；此前 Private 约 1342.3 **MiB**，两者不可等同。
- 30 秒增量分配约 247.433 MB（约 8.248 MB/s）；Gen0/1/2 次数 5/2/1，线程池队列为 0。
- LOH 约 60.329 MB。该样本与构建重叠、进程已长时间预热，只用于分配/保留量区分，不作为冷重启的性能对照；当前证据不够宣称内存泄漏。

## 产品验收

已完成：备份 `D:\Keys\PuddingDeploymentBackups\20260905-100531-4264176f`，Desktop正式预构建部署264文件，Core24524于10:15:33 Ready；Agent/Platform/Runtime/index hash与148前端源文件全部匹配。首次建立覆盖索引约3分半，启动栈证实停在TokenUsageSchemaBootstrapper，未修改超时或跳过初始化。

相同 prompt、同一默认助手/主会话、同一bigmodel/glm-5.3-flash模型/71工具集合、同一文件读取任务：两次均succeeded且只有一次file_read。首次从API受理到终态 **56.898s**（Run51.344s）；第二次预热 **13.253s**（Run12.527s）。对照第三轮196.996s属于阶段观测，不是严格多样本同uptime A/B。

第一条新layer从记账开始到创建：原157.462s → 首次244ms → 预热76ms；warm第二轮约3ms。模型结束到工具requested：首次8.300s，预热0.929s。首轮hydrate/context为10.123s/14.468s，预热为1.765s/52ms；没有以减少工具或忽略任务获得加速。完整运行ID/事件/用量在 `pudding-agent-round4-runtime-evidence-2026-09-05.json`。

首次输入89769 tokens，hit44736（49.83%左右，含44760 cold）；预热输入92011，hit90880（98.77%左右）。四次gateway调用、四条direct attribution；本次未再出现fallback重复条目，但稳定调用ID去重缺陷尚未修复，不能因本轮短于两分钟就判定修好。

资源验收仍不通过：120秒startup-to-canary mixed采样末尾Private454.03MiB；下一段90秒warm-canary mixed采样末尾 **1486.74MiB**，CPU均值0.858%（8逻辑核归一）。未强制GC，两个窗口不能只挑较小值；需继续定位后续增长及后台高分配。后台高分配、Chat长历史、夜间自动调度和七日缓存合同继续由持续优化台账跟踪。
