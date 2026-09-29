# 聊天消息排队延迟与状态显示修复

## 现场结论

2026-09-30，用户报告默认助手数分钟没有响应。仅凭 Core 的 21.5 秒启动耗时、996 MiB 工作集或低 CPU，不能判断本条消息卡在启动或模型推理。

只读查询 `D:\data\databases\pudding_platform.db`，并与 `logs/system/pudding-20260930_002.log`、错误日志关联，定位到：

- conversation：`206a9b48ec904ebb93e7541131fbb835`。
- 用户消息：ChatMessages `12006`，正文“检查一下启动时间，为什么这么长，Core启动的都做了什么”。
- command：`04cf6c7a3a894095a9d1ffeb161c09ab`。
- turn：`f7f300c458464be183598297fb1686fd`。

北京时间时间线：

| 时间 | 事实 |
|---|---|
| 07:00:31 | 截图中的 Core 启动时间，PID 26676 |
| 07:00:50 | 该会话恢复旧 heartbeat command `5f21d0ae8f6a407e945b35cde77d6af8`，该命令创建于前一天且已有多次租约恢复 |
| 07:03:51.101 | 用户消息 `turn.accepted`；command pending、attempt_count=0、started_at=NULL |
| 07:07:33～07:07:36 | **旧 heartbeat turn** 第24轮上游 Responses 流 `ResponseEnded`，随后失败。不是用户这条 turn 的模型请求失败 |
| 07:07:36 | 原 FIFO 随后领取前一天的子代理结果 command `d3dddbebc6bc4c5ab73cc493dfd13183`，用户消息继续 pending |
| 07:14:03.761 / .961 | 用户命令开始领取 / canonical `turn.started`；此前排队约612.7秒 |
| 07:14:07.214 | 本条 turn 首个 `message.thinking_summary.appended` |
| 07:19:05.974 | 本条 turn 自然 `turn.completed`，command succeeded |

本轮没有重启生产 Core，没有改写现场数据库、取消任务或向 Agent 补发消息。消息完成是原进程自然排空队列的结果，不是新补丁已在生产生效。

## 修复

1. `SqliteExecutionLeaseStore.TryAcquireAsync`：同会话存在前台 pending 消息时，暂不领取内部 Agent/System pending 消息。分类仅使用受理端剥离、可信 Message Fabric 重新写入的 `message_fabric_ingress` 与 `message_fabric_from_kind`；不按 `fabric:` 用户名猜测，Fabric 的 user 来源仍属于前台。无元数据、未知来源或非法 JSON 保留前台 FIFO，不阻塞领取。
2. 保留事务/CAS、fencing token、活跃租约互斥、过期回收。旧后台工作仍留在队列，前台完成后可继续；其他会话仍按创建顺序调度。前台优先只作用于尚未执行的任务，不自动中断已经运行的工具。持续前台输入可能推迟同会话后台任务，这是本次交互优先策略的明确边界。
3. Web 收集器原来过滤了 `turn.accepted/started`，状态组件又将“没有可见执行节点”显示为“正在运行”。现在保留 canonical 生命周期事实：仅 accepted 显示“已入队，等待执行”；started 后进入运行阶段；缺少生命周期和内容时显示“等待执行反馈”，不猜测已开始或已排队。
4. 消息头在排队时显示“已等待”；已开始后的处理时长使用 canonical `turn.started.occurredAt`，不把十分钟排队算成处理耗时。终态仍优先，事件去重、重放和实际内容阶段保持原有逻辑。

## 验证与部署边界

- 后端隔离内存 SQLite：`ExecutionLeaseStoreRecoveryTests`、ConversationAcceptance / SubmitTurnHandler 定向过滤共19项通过。覆盖新前台优先、后台不丢失、不抢占活跃租约、过期/释放后恢复、跨会话 FIFO、用户来源与无效元数据、已有恢复与取消路径。
- 构建使用 `--artifacts-path temp/build/recovery`；本轮没有在 `D:\data` 试跑修改后的宿主。
- 前端生产构建通过：隔离输出 `temp/build/message-response-web`，包体门禁通过（sync=1,375,474、chat=343,290、common=390,686 bytes）。前端6组101项定向测试通过（TurnStatus、TurnElapsedLabel、AgentMessageBubble、canonical collector、projector、projection index）。常规 Umi Jest 配置初始化/依赖转换耗时较长；验证使用临时配置，保留 Umi 源码转换器与项目 setup，显式配置路径别名。原依赖转换策略与跳过 node_modules 二次转换策略均101/101通过（约712秒/141秒）；后一种缓存写入 temp/test-out。未修改项目测试配置。
- 后端修复提交：`869a209`。
- 新源码需要重新部署 Core 和 Web 静态资源；当前运行的是 `Source/PuddingAgent/bin/Debug/net10.0/PuddingAgent.exe`，不能仅凭测试通过或重启旧文件宣称补丁生效。

本次未解决旧任务的上游 `ResponseEnded`，也未将21.5秒启动时间或996 MiB工作集归因为泄漏/启动性能缺陷；这些不是已定位的用户消息领取延迟根因。
