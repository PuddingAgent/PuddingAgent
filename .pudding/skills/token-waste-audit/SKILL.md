# Token 浪费自检流程 (token-waste-audit)

## 用途

系统化审计 PuddingAgent 的异常 Token 消耗，按八类逐一排查，产出带统计证据的分层报告与「只含已证明有价值规则」的整改建议。

## 数据源

- `agent_diagnostics`：token_breakdown / compaction_stats / cache_health / sub_agent_stats / slowest_tools
- `query_sub_agents`：list / stats（子代理 Token、缓存命中、费用、请求数）
- `query_session_logs`：会话转录（全文检索为主）
- 本地日志 / SQLite（仅当 Session JSONL 无法覆盖后台行为时，只输出聚合数据）

## 八类检查

### 1. 无效等待（Waiting）
`wait_agent / wait / 空 write_stdin` → timeout / 无新输出 → Parent 被重新唤醒 → 无新有效信息 → 再次等待。

### 2. Compaction 放大
异常频繁 compaction；`compact → 重读相同文件 → compact → 再重读`；compaction 后很快再逼近上限；compaction 后重复加载 AGENTS/Memory/Skills。
- 判据：`compactionRatio > 1`（afterTokens > beforeTokens）= 越压越大，异常信号。
- 用路径、内容 hash、时间线识别重复读取。

### 3. 巨型 Tool Output
异常大的 grep/rg/find、git diff/log、test/build/docker logs、JSON dump、自定义工具输出，进入历史 Context 后导致后续多个 inference 的 input tokens 持续显著增加。

### 4. 无进展 Retry / Agent Loop
相同或高度相似工具调用反复执行；相同错误反复出现；同一计划循环；working tree 无实质变化却持续新 inference。
- 区分「确定性失败后的重复尝试」与「正常迭代调试」。

### 5. 后台模型使用
memory generation / consolidation、hidden/background subagent、用户未主动操作时的 sampling request。

### 6. 持久化巨大 Payload
base64 图片、大型 image payload、超大 JSON/blob 首次出现后仍被后续 Context 反复携带。统计首次出现后的持续时间与后续 inference Token 放大。

### 7. Self-ingestion
puddingAgent 读取自己的 `~/sessions` / archived sessions / rollout JSONL / 大型日志，并把原始内容作为 Tool Output 塞回模型 Context。

### 8. 其他异常
高重复、低信息增益、无任务进展但显著增加 inference/context processing 的，单独归类并给证据。

## Token 计量原则

- 不简单累加所有 `last_token_usage`。
- 识别重复 token_count、resume/replay；优先依据 `total_token_usage` 的真实累计变化 / 单调增量确认模型消费。
- 每类统计：input / cached input / fresh(uncached) input / output / reasoning / total processed；占比。
- **cached input 与 fresh input 成本不同，不混算。**
- 能可靠确定模型与计量规则时估算 API-equivalent cost；无法确认时不猜测。
- 类别重叠（如「巨型输出 → Context 膨胀 → Compaction → 重读」）时标记 overlap，不强行给精确数字，避免重复计算。

## 输出模板

1. 总览表：类别 | 确认浪费 Token | 全局占比 | 受影响 Session | 置信度。
2. 回答七个问题（是否异常消耗 / 最大3来源 / 各消耗量 / 最严重 Session / 浪费 vs 正常占比 / AGENTS.md 可缓解项 / 代码层问题）。
3. 最严重几类各给 1 个统计证据。
4. 生成**尽可能短**的全局整改建议：只含本机数据已证明有价值的规则；不为未发现问题加规则；优先表达目标与行为原则；仅在必要时写具体 timeout/size/retry 数值；修改尽量少，不为省 Token 牺牲任务质量、交互能力或正常重试。
