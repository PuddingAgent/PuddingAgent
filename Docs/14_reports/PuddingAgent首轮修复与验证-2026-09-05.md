# PuddingAgent 首轮效率修复与验证

日期：2026-09-05。依据同目录《PuddingAgent效率与代码审计-2026-09-05.md》，本轮已实施源码修复；**未部署、未重启当前 Desktop/Core、未修改运行数据库或派发新模型任务**。源码验收不等于产品验收。

后续更新：第二轮已关闭下述前端 tsc 门槛并生成隔离 Desktop/Core 发布包，见同目录《PuddingAgent第二轮前端修复与发布验证-2026-09-05.md》。下文保留首轮当时的验证记录，不改写历史失败为成功。

## 已实施

| 范围 | 修复 | 安全边界与验证 |
|---|---|---|
| 调度假忙 | `LegacyTaskExecutionProbe` 以精确 Run/Command 关联最新 attempt/fencing token；区分 active、retry pending、grace、terminal-without-settlement、orphan 和证据缺失 | Tracker 与 Repair 共享只读判断；Repair 在 Serializable 事务重验 Task version、assignment、binding id、Run fence；终态未结算进入 Blocked，不伪造 Completed |
| 并行的完成结算入口 | `TaskCompletionSettlementService` 复用同一 lineage resolver，替换 command-id 的 `SingleOrDefault` 多 attempt 查询 | 旧 attempt 的终态不再覆盖新 running attempt；command 重试 pending 时不结算 |
| 任务所有权 | 清除匹配 active assignment，追加带 Run correlation 的 TaskBlocked 事件；只释放同 Task/Agent、无 Goal owner、创建不晚于 Assignment 的 legacy reservation | 不释放新 reservation，不启用全部 Backlog；Goal reservation 仍由原有 fenced owner 处理 |
| 文件补丁 | 修正描述示例为 `type/old_text/new_text`；operation 拒绝未知字段；缺失/null new_text 或 regex replacement 拒绝执行；显式空串才代表删除 | 多文件中后一个参数错误时前一个也不得写入；补齐 `start_line/end_line` 序列化与工具 schema 一致性；保留现有行锚点警告语义 |
| Chat 连接 | 401/403 停 SSE、replay/poll/watchdog/reconnect；显示提示，401 移除失效 Token；不清除聊天内容或草稿；网络错误指数退避 1.2s 至 30s | reconnect 次数跨同 session 重连保留，只在收到事件后复位；旧 session 的异步错误不得停止新连接；保留原首连 cursor 测试 |
| Run 监视与时间 | 控制监视异常立即 cancel Run，结果明确为 `execution_monitor_failed`；监视查询传递取消令牌；Release/expired recovery 写 `completed_at` | 注入 inbox 异常验证取消及不能转成 success；正常 monitor shutdown 不误判；保留唯一续租器 |
| 归档空转 I/O | 目录发现缓存 30s；已完成投影按事件长度/mtime、manifest mtime、durable cursor 长度/mtime 做稳态跳过；缓存上限 4096 | 已完成归档被独占锁定仍能完成 sweep，不读正文；cursor 重置触发重放；保留每批轮转公平性；**首次读取和活动归档仍整读，尚非完整字节游标增量方案** |
| 验收脚本 | 源码定位测试改为 CallerFilePath 起点，支持系统 Temp 输出；cache 报告使用明确 UTC 半开区间，排除当天，缺日标 INCOMPLETE，小样本不伪装 PASS | 新增日期边界/加权统计测试；`--gateway-only` 可跳过昂贵的 attribution 表扫描 |

## 验证结果

- Platform：**158/158**。包含 Scheduling、ExecutionRunCoordinatorMonitor、ExecutionLeaseStoreRecovery、FileSubAgentRunStore、TaskRecallAuditEngine、TaskCompletionSettlement、TaskAgentDispositionCompletion、TaskCommandService。
- Runtime 补丁工具：**12/12**，包含未知/遗漏/null 替换文本、显式删除、多文件不写入、snake_case 行参数、dry_run 与 unified diff。
- Chat：**44/44，5 suites**。包含连接、session selection、executionFlowProjectionIndex、TurnContentStream、MessageRow.memo。Jest 仍提示 open handles，不能据 exit 0 宣称不存在资源残留。
- Python cache 脚本：**4/4**，含日期与 SQL 半开区间、加权分母；见 `TestScripts/test_deepseek_cache_hitrate.py`。
- `git diff --check` 通过。
- **全量 `npm run tsc -- --pretty false` 未通过**：PuddingAdminShell/EntityCard 等 style 类型，Chat ref/return 类型、dynamic import/module 配置、tokens 统计 DTO 等仍有错误；本轮未修改这些文件，也未将其标记通过。发布前需要独立清理这道门槛。

TRX：`.tmp-test-out/efficiency-results/platform-fixes.trx`、`runtime-fixes.trx`。构建输出在仓库 `.tmp-test-out`，没有落入 `D:\data`。当前共享工作区原有 `ToolInvocationService.cs`、`TaskRecallAuditEngineTests.cs`、`memory/INDEX.md` 与 external 子模块改动均保留，没有 commit/reset/clean。

## 用量口径修正，不是优化收益

原审计临时脚本用带 `T/+08:00` 的本地日期字符串预筛选 UTC 数据库时间，漏掉目标窗口开头八小时。原审计 Markdown 和 JSON 已增加 data-quality 警告，不再作为完整七日验收依据。

正确窗口：北京时间 **2026-08-29 00:00 至 2026-09-05 00:00（不含）**，对应 UTC `[2026-08-28 16:00:00, 2026-09-04 16:00:00)`。数据库实测为 UTC 空格分隔时间；查询直接使用同格式 UTC 参数，以保留时间索引，不对 WHERE 中的列做 datetime 转换。

| 模型 | 请求 | 输入 tokens | hit | miss |
|---|---:|---:|---:|---:|
| bigmodel / glm-5.3 | 60 | 8,606,960 | 7,997,504 | 609,456 |
| bigmodel / glm-5.3-flash | 2,184 | 197,146,386 | 182,476,096 | 14,670,290 |
| deepseek / deepseek-v4-flash | 1,105 | 76,456,907 | 70,225,280 | 6,231,627 |

DeepSeek 加权命中 **91.849%**，有记录 6/7 日，未达 >99% 连续七日验收。运行：`python -X utf8 TestScripts/deepseek-cache-hitrate.py --days 7 --gateway-only`。全归因扫描虽可完成，但明显慢于网关快报，不应放进高频轮询。归因账本与 gateway 不相加。

## 后续产品门槛

1. 收敛前端全量类型门槛，准备明确构建清单与 hash；通过 Desktop 进程外部署新构建后，核对实际加载程序集。
2. 新会话 smoke：旧 terminal assignment 在恢复 tick 内释放；Ready opt-in 任务实际产生新 ExecutionRun，而非只有 Delivery ACK；有活跃新 attempt 时不误清理。
3. 同一机器、同一会话、同一消息量做 idle/stream/history-load 三种负载对比，记录 CPU、Private/WS、GC、长任务与加载延迟。当前没有真实产品前后对照，不宣称内存已降低某个百分比。
4. 再推进归档字节游标/有界发现，以及稳定工具 schema、前缀漂移归因；保持任务与工具工作量不变做 cache A/B。夜间利用率与连续七日缓存目标仍需实测，不以本轮单测替代。
