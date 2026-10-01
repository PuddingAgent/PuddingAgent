# 真实会话消息加载延迟评估（2026-10-01）

用户反馈：切换/打开默认助手时，消息区长时间停留骨架屏。编译器报错已由用户重启编译器解决。本轮仅测量与诊断，未修改产品代码、数据库、配置或索引，未重启 Desktop/Core，未发送聊天消息或调用模型。

## 结论

主要瓶颈在服务端 conversation 投影，而非 20 条消息的下载或 JSON 解析。默认助手接口连续三次 **5.755 / 5.834 / 5.963 秒**；SQLite 两项投影查询各约 2.8 秒，等待规模和接口耗时吻合。前端缓存未命中时，等待该接口期间显示骨架屏，是截图现象的合理解释；本轮未获取浏览器首个可读帧时间，不能把 API 时间直接当作完整页面加载时间。

此前 [合成数据实测](Chat-Frontend-Performance-Measurement-2026-10-01.md) 拦截 conversation 返回合成数据，测得的 124ms 不包含本次真实数据库查询成本，不能据此排除后端瓶颈。长消息布局/滚动问题仍是独立议题。

## 实测方法及接口结果

在运行中的 `http://localhost` 用用户提供的开发账号登录，令牌仅存进程内；Node 24 原生 fetch 串行调用真实 GET 接口三轮，不持久化消息正文或凭据。分开记录收到响应头、读完响应体和 JSON.parse。前置登录不计入接口统计。浏览器只读检查确认同站点真实聊天可显示消息；其 evaluate 只读环境未提供 Performance API，故未录制该浏览器的网络/React 首帧。

| Agent | 20 条消息接口三轮（ms） | 响应 UTF-8 字节 | 正文字符 |
|---|---|---:|---:|
| 默认助手 `.6a8` | 5754.7 / 5833.7 / 5963.0 | 106870 | 48456 |
| 审批审计员 `.001` | 85.7 / 91.3 / 82.5 | 61160 | 26106 |
| `.0e0` | 2527.1 / 560.5 / 615.3 | 79276 | 35827 |

默认助手首字节等待 5751.1～5960.5ms；读响应体 2.3～3.4ms；Node JSON.parse 0.1～0.2ms。三会话均无活动 run，最近消息 processItems 为 0。状态列表另测 5.3～6.1ms。携带相同 `knownCursor` 的条件 GET 均返回 304，耗时 **2.7～6.0ms**。以上各三样本，仅报告范围/原始值，不估计 p95。

首次 PowerShell Invoke-RestMethod 的状态请求约 2 秒，但随后 Node 连续同接口只需数毫秒；未把工具初始化/传输差异当作业务瓶颈。

## 数据库定位与只读对照

Node `DatabaseSync` 以 `readOnly:true` 打开当前平台库，并设置 `PRAGMA query_only=ON`。仅执行元数据、按会话计数、EXPLAIN QUERY PLAN、以及按最近消息/回合限定的查询；不读取凭据、不执行 DDL/ANALYZE/迁移。此操作是当前库只读性能诊断，不是旧代码兼容性试跑。

| 会话 | 事件行数 | 最近消息过程摘要（首轮复核，ms） | 最近回合终态（首轮复核，ms） |
|---|---:|---|---|
| 默认助手 | 1637800 | 2797.7 / 2931.4（返回 5033 行元数据） | 2935.6 / 2784.6（返回 10 行） |
| 审计员 | 21736 | 33.5 / 36.8 | 31.3 / 33.2 |
| `.0e0` | 160544 | 278.0 / 278.6 | 265.8 / 274.0 |

两项 SQL 对照 `AgentConversationProjectionService.GetConversationAsync`：

- `messageEvents`：按 ConversationId、最近 MessageId 集合、事件类型过滤，然后按 Sequence 排序，组装过程摘要。投影只返回 20 条正文，但摘要查询并非只读取 20 条事件。
- `terminalEvents`：按 ConversationId、可见 TurnId 集合、终态类型过滤，再按 Sequence 排序。

实际查询计划两项均选择 `ix_ce_seq (conversation_id, sequence)`，只用 `conversation_id=?` 定位会话范围；message_id / turn_id / type 再筛选。默认会话约 164 万条历史事件，使这种扫描昂贵。当前实际库与 EF 模型均没有支持 conversation_id + message_id 的索引；虽然已有 `(turn_id,type)`，终态查询仍选择会话顺序索引。

只读 A/B：终态 SQL 临时指定已有 `ix_ce_turn`（仅 SELECT 的 `INDEXED BY`，不改 schema），默认助手 **2542.4～2821.2ms → 14.5ms**，返回行数仍为 10。审计员对照 4.3ms，`.0e0` 对照 6.0ms。新计划按 `turn_id=? AND type=?` 精确查找，随后排序。该对照强力支持“查询路径”瓶颈；尚未逐字段验证全部返回记录等价，也未将索引提示接入产品，不能宣称整体已优化。

## 优先改进顺序

1. **服务端查询/索引优先。** 为最近消息摘要提供合适的 conversation_id + message_id 查询索引，终态按 conversation_id + turn_id + type 定位小结果，再排序。必要时拆查询/聚合以避免优化器为保序扫描整个会话。先在 `temp/test-out` 在线备份副本上验证计划、结果等价、索引构建耗时/空间与写入成本，再接入 schema bootstrap 和模型；不要直接在生产库创建索引或把 `INDEXED BY` 当作最终设计。
2. **暖会话重验证。** `chatClientStore.selectAgent` 始终不带 knownCursor，全量请求即使缓存已命中仍会支付 5.8 秒。后台 sync 已有条件请求及“投影尚未追平/activeRun 时必须全量”的保护，选择路径可复用同类准入；不能无条件用 304 让未完整物化快照永久停滞。
3. **先展示，再写缓存。** 当前 select 链为 cache.load → API → await cache.save → set fresh。把持久化移出首帧关键路径，保留版本顺序和错误处理。此前合成数据缓存写仅 1～18ms，故它不是本轮 5.8 秒主因。
4. **单独验证渲染。** 服务端修复后，再量 API 完成到消息可读、IndexedDB、React commit、Markdown/layout；不要先通过减少历史条数掩盖查询问题。旧报告的虚拟高度 flex 收缩缺陷按独立任务验收。

建议验收：同一真实数据副本重复冷/暖读，默认助手消息接口目标 p95 ≤300ms（目标而非已达成）；有缓存时切换先显示正文、后台刷新不清空；最终正文、终态、委派摘要、明细及权限语义一致。API 与端到端首帧分别报告。

原始聚合证据保存在忽略的 `temp/message-load-api-results.json`、`temp/message-load-query-plans.json`、`temp/message-load-query-timing.json`；临时脚本在 temp 根。记录不含消息正文和令牌。本轮没有实施或部署上述建议。
