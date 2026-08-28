# ADR-081：Agent Harness 兼容边界与工具协议适配

> 状态：Accepted；H0、H1 重复失败熔断、模板单一权威源、round-boundary 工具激活、discovery-only 熔断、canonical Token 归因与 WorkUnit 调用边界 Token/成本止损已实现，聚合 Goodput 报表、部署与真实模型验收未完成
>
> 日期：2026-08-26
>
> 范围：Agent Loop、Tool Invocation、Shell/Terminal、委派终态协议
>
> 详细设计：[Agent Harness 兼容与工具调用效率修复设计方案](../Features/AgentHarness兼容与工具调用效率修复设计方案.md)

## 1. 背景

LLM 在后训练 Harness 中形成了 `rg`、`exec_command`、`write_stdin`、Codex patch 与特定终态表达的强先验。Pudding 的 canonical 工具协议不同，近期主代理和子代理轨迹中已出现工具名/参数猜测、no-match 误判、补丁重试和完整报告后额外续轮。这是 Harness 适配问题，不应归因于模型“不会使用工具”。

## 2. 决策

1. Pudding canonical 工具保持唯一权威，不复制第二套 Registry 或权限系统。
2. 在 `ToolInvocationService` 最前端增加窄范围、确定性的兼容适配；适配后再进入 RuntimeControl、WorkspaceGuard、Firewall、哈希、遥测与实际执行。
3. 对高频训练先验提供固定、短小的提示映射；不按 provider/model 动态改变工具 schema 和排序。
4. `rg/grep/findstr` exit code 1 在确认不是命令缺失等诊断后建模为 `no_match`。
5. 继续要求 Runtime JSON 终态信封；仅在无工具调用、非结构化响应且完整满足 canonical 输出合同时自动提升为 `DONE`。
6. 已安装 WSL 的 Windows 主机提供显式 `shell=wsl` 真实 Unix 执行通道；结构化文件/搜索工具仍为默认。不得假设发行版内存在 `rg`，不得由 Agent 自动 `apt install`。
7. H0 不捆绑 ripgrep。优先让 `rg` 工具别名落到托管 `search_grep`；只有评测证明存在必要语义缺口时，才以固定版本的应用私有二进制补充，禁止自动下载和修改系统 PATH。
8. 兼容层不得改写任意 shell 语句、放宽命令白名单含义、隐式授权或吞掉真实错误；在 Terminal 支持嵌套命令策略前，不把 `wsl.exe` 加入普通 terminal 白名单。
9. 适配命中记录为 `tool.harness_compatibility`；LLM 流式等待分别记录 `llm.rate_limit.wait` 与 `llm.stream.provider_first_chunk_wait`，避免以总耗时猜测瓶颈。
10. 相同 canonical 工具、参数和失败结果连续两次不变时，第二次显式返回 `execution_stalled`；后续原样调用在 Agent Loop 内阻断，改变输入或结果后才重新计数。
11. 主/子代理身份与逐轮 Token attribution 只来自 `RuntimeExecutionIdentity`。Agent Loop 的逐 LLM round 行是 `TokenUsageEvents` 唯一归因事实，`SubAgentManager` 终态 usage 只用于运行摘要，不再重复写入该表。
12. Streaming 必须先提交 direct Token attribution，再向 Conversation Event 管线发布 `usage`；投影器仅作失败补记并以持久父子关系和 invocation index 归因，避免 direct/fallback 竞态双写。
13. `BuiltInAgentTemplates` 只能由 `PuddingCore` 定义；Host/UI/Runtime 共用同一实例源，不得在 Host 再声明同全名类。Low 权限投影以 V2 `DefaultToolNames + RequiresGrantToolNames` 为权威，文件读取/搜索/发现工具进入 Default，写入和 Shell 进入 Grant。
14. 提示词不得宣称不在当前 function schema 中的工具可用；只有 `search_tools` 实际可见时才提示递延工具发现。
15. 工具循环采用两层止损：`FailedToolCallTracker` 处理完全相同的 canonical tool+args+失败结果；`RuntimeControlService` 按 kind+component+归一化错误构造失败族指纹，参数改变也在第 5 次同族失败熔断。窗口总量阈值的保留队列容量必须不小于阈值，Buffered/Streaming 均将熔断终态报为 Failed，不得降级为普通 Cancelled。
16. `search_tools` 激活语义是“当前 provider request 冻结、下一次 LLM round 生效”，不是“下一个外部用户 Turn 生效”。dispatch 内 catalog/capability/schema 为权威冻结面，可见工具 ID 只在 round 边界单调增加，并记录一次 `tool_spec_changed`。连续 8 次只调用 `search_tools`、没有执行任何已发现业务工具时，参数文本即使变化也按同一 discovery-only 族返回 `tool_discovery_stalled`。
17. 自动 WorkUnit 的 input/output/cost 预算由 Execution Kernel 按实际 provider/model 冻结，Buffered/Streaming 在每次 provider call 后统一记账，并在工具或下一轮 LLM 前硬停止；价格或 usage 缺失时 fail closed。缓存命中率不是完成事实，验收必须同时报告 verified Goodput。

## 3. 结果

### 正向结果

- 模型熟悉调用可在第一次执行进入 Pudding canonical 工具；
- 工具 schema 对模型保持稳定，避免为兼容别名扩大 prompt 与缓存前缀；
- no-match、真实失败和任务完成的语义更清楚，减少低效重试与额外 LLM 轮次；
- 所有别名仍受现有安全、工作区、审计和预算约束。

### 代价与风险

- 适配器成为需要版本化和 fixture 回归的协议边界；
- 参数名存在歧义时只能采用保守规则，无法安全映射的输入仍会失败；
- 兼容观测会增加每次命中的一条低基数 metric；参数原文不入指标，只记录名称、适配类型和版本；
- H0 单测通过不表示运行中的 Desktop/Core 已加载新代码。

## 4. 被否决方案

### 4.1 只增加提示词

否决。提示可以降低错误率，但不是可靠协议，模型仍可能按训练先验发出别名调用。

### 4.2 完整复刻 Codex/Claude/Unix Harness

否决。它会复制工具目录、引入跨平台与供应链成本、扩大攻击面，并造成 Pudding canonical 权限和审计语义分叉。

### 4.3 在 Registry 之后兼容

否决。Registry 或 Firewall 可能先拒绝别名，且适配后的执行身份、参数哈希和审计不一致；更严重时可能形成策略旁路。

### 4.4 把所有 exit code 1 当作成功

否决。只有明确的搜索命令 no-match 具有该语义，其他 exit code 1 仍可能是编译、测试、权限或语法失败。

## 5. 实施与验收状态

- H0 源码：已实现；
- 定向自动测试：已实现，结果以本次变更交付记录为准；
- H1 canonical 重复指纹、`execution_stalled` 熔断、逐轮 Token 归因、Streaming `tool.call`、适配命中、限流等待和首块等待遥测：已实现；
- 2026-08-27 `browser_context` 循环事故修复：模板已收敛到 Core 单一权威源，Low 投影保留读取/搜索/`search_tools`，发现提示按实际可见集生成，同失败族 5 次熔断且窗口总量阈值可达；已补定向测试；
- 2026-08-28 `search_tools` 空转事故修复：现场 Run 约 36 分钟内 216/216 工具调用均为 discovery、实际任务工具 0 次；源码已把动态定义从错误的“下一外部 Turn”改为下一 LLM round 单调生效，Buffered/Streaming 共用同一提升函数，并增加 discovery-only 熔断；12/12 定向测试通过；
- H1 历史 fixture 聚合报表与进程外新数据 A/B：未实现；历史 Token 行不回填，部署前 `sub_agent:*` 汇总行不得与逐轮 `agent_llm` 相加；
- H2 bundled ripgrep：未批准、未实现；
- 当前 Desktop/Core 部署和真实模型主/子代理 smoke：未完成。
