# Agent Harness 兼容与工具调用效率修复设计方案

> 日期：2026-08-26  
> 状态：H0、H1 重复失败熔断、内置模板单一权威源与 canonical Token 归因已实现并通过定向测试；聚合报表、进程外部署和真实模型 smoke 未完成
> 架构决策：[ADR-081](../07架构/95ADR-081AgentHarness兼容边界与工具协议适配ADR.md)

## 1. 问题与目标

模型后训练时熟悉的 Harness 与 Pudding Runtime 不同。模型经常按已有先验尝试 `rg`、`exec_command`、`write_stdin`、Codex patch 或 `pwsh` 参数，而 Pudding 暴露的是 `search_grep`、`shell`、`terminal_input` 和自己的参数名。协议差异会产生三类浪费：

1. 第一次调用因工具名、参数名或补丁形状不匹配而失败，模型随后猜 schema；
2. `rg/grep` 的 exit code 1 被误判为命令失败，引发无意义改写和重试；
3. 子代理已经输出完整五段报告，但未包装 Runtime JSON 信封，普通文本降级为 `CONTINUE`，系统又发起一轮 LLM 调用。

目标不是把 Pudding 伪装成另一套完整 Harness，而是在不改变任务、授权和 canonical 工具语义的前提下，让最常见的训练先验一次落到 Pudding 的统一执行管线。

## 2. 设计原则

- **Pudding canonical 工具是唯一权威**：工具目录、权限、审计和实现不复制第二份。
- **兼容发生在统一执行边界**：别名先归一化，再进入 RuntimeControl、WorkspaceGuard、Firewall、参数哈希、遥测和 Registry；别名不得绕过任何门禁。
- **提示短而稳定**：系统提示只给高频词汇映射，不把完整工具说明重复一遍，避免扩大固定前缀和破坏缓存稳定性。
- **优先模拟语义，不盲目安装二进制**：`rg` 的常见内容检索先映射到托管 `search_grep`；无需依赖机器 PATH、安装状态和 shell quoting。
- **真实 WSL 与结构化工具双轨**：目标 Windows 环境已有 WSL；真实 Unix 行为显式走 `shell=wsl`，普通检索/读写仍优先 Pudding 结构化工具。
- **退出码不是业务语义**：工具结果应区分 `success`、`no_match`、`failed`、`timeout`、`cancelled`；文件变更还必须检查目标后置条件。
- **兼容必须可删除、可计量**：每条别名是窄范围、确定性的映射，不接受任意命令改写。

## 3. 三层兼容模型

### 3.1 L0：熟悉环境提示

`ToolLoopInstructionBuilder` 固定追加一个短兼容块：

- `search_grep` 对应 rg-like 内容搜索；
- `shell` 对应短命令 `exec_command`；
- `terminal_start` + 有界 `terminal_wait` 对应长命令会话；
- `file_patch` / `apply_patch` 接受 unified diff 与 Codex patch；
- 工作目录使用 `working_directory` / `cwd`，不要在命令前拼 `cd` 或 `Set-Location`。

这一层减少模型第一次猜错，但不能作为可靠性边界，因为不同模型可能忽略提示。

### 3.2 L1：工具名与参数归一化

`HarnessToolCompatibilityAdapter` 在 `ToolInvocationService` 最前端执行确定性归一化：

| 熟悉调用 | Pudding canonical | 主要参数映射 |
|---|---|---|
| `rg` / `grep` 工具调用 | `search_grep` | `pattern/regex/needle -> query`，`path/root/cwd -> directory`，`glob -> pattern` |
| `exec_command` / `run_command` | `shell` | `cmd -> command`，`workdir/cwd -> working_directory`，`timeout_ms -> timeout_seconds` |
| `write_stdin` | `terminal_input` | `session_id -> job_id`，`chars -> input` |
| `read_file` | `file_read` | 工具名归一化；canonical 参数优先 |
| `write_file` | `file_write` | 工具名归一化；canonical 参数优先 |
| `list_directory` | `list_dir` | 工具名归一化；canonical 参数优先 |
| raw `apply_patch` | `apply_patch` | 原始 patch 包装为 `patch_text` |
| `pwsh` / `powershell.exe` | `powershell` | shell 值归一化 |
| `wsl.exe` / `linux` / `unix` / `ubuntu` | `wsl` | 显式进入 WSL，不把它伪装成宿主 Bash |

若 canonical 字段和别名字段同时存在，以 canonical 字段为准并删除别名，防止双义输入。无法安全解析的 JSON 保持原样，让既有 schema 校验返回结构化错误。

### 3.3 L2：结果语义兼容

对独立 `rg`、`grep`、`findstr` 命令，exit code 1 且不存在“命令未找到”等诊断时，结果归类为 `status=no_match`，不是失败。exit code 2、缺失二进制、权限问题、语法错误仍然失败。

短命令和长终端路径使用相同判定。模型收到明确的 no-match next action，不再盲目重复原命令。

## 4. 终态协议修复

Runtime JSON 信封仍是正常路径：完成时必须 `status=DONE`、`tool=null`，完整五段报告放入 `message`。为兼容已经给出完整交付物但遗漏信封的模型输出，新增严格恢复规则：

1. 当前回合没有 native tool call；
2. 响应不是成功解析出的结构化信封；
3. 响应满足当前委派的 canonical 五段输出合同；
4. 仅此时 Runtime 把结果提升为 `DONE` 并记录 `subagent.output_contract.completed`。

显式结构化 `CONTINUE/WAIT/FAILED` 永远不被覆盖。该规则只消除协议遗漏导致的额外一轮，不根据自然语言猜测一般任务是否完成。

## 5. WSL 熟悉环境与 rg 决策

目标机器已有 Ubuntu WSL，`HostShellExecutor` 的 `shell=wsl` 使用 `wsl.exe --cd <Windows working_directory> -- bash -lc <command>`；本机验证仓库路径可正确映射为 `/mnt/e/...`。这提供真实的 Bash、管道、`grep`、`find`、Git 和 Python 语义，不需要 Pudding 模拟整个 Unix CLI。

但 WSL 不是默认工具路径：首次冷启动有额外延迟，发行版和包集合会漂移，且当前 2026-08-26 探测到 `/usr/bin/grep`、`git`、`python3`，没有 `rg`。因此：

- 代码内容检索仍优先 `search_grep`，结果结构、预算和工作区边界稳定；
- 确实需要 Unix shell 行为时使用 `shell=wsl`，一次调用内完成有界管道；
- 每个 run 最多做一次必要的 `command -v <tool>` 能力探测，不反复探测；
- Agent 不得自行 `apt install` 或修改发行版；依赖安装需要明确产品配置/用户授权；
- 长期 WSL 任务后续应给 Terminal 增加显式 shell mode，而不是把 `wsl.exe` 加入普通终端白名单后允许任意嵌套命令。

当前仍不直接内置 rg：

当前选择是“给 `rg` 熟悉入口，底层优先使用托管 `search_grep`”，而不是立即在产品包中附带 ripgrep：

- Pudding 是 Windows First，托管工具可统一工作区边界、排除目录、结果预算、artifact spill 与遥测；
- 外部 `rg` 依赖 Windows/WSL 各自 PATH、版本、shell quoting 和安装状态，无法天然进入 Pudding 的结构化分页合同；
- 仅为熟悉感安装完整 Unix 工具链会扩大供应链、安装包和命令攻击面。

只有真实模型评测证明 `search_grep` 无法覆盖必需的 `rg` 能力时，才进入 H2：把固定版本、校验哈希和许可证明确的 ripgrep 放在应用私有工具目录，仅修改 Core 子进程 PATH，不修改系统 PATH；仍由 `shell` 权限、WorkspaceGuard、超时、输出预算和审计约束。禁止为兼容性自动下载最新二进制。

## 6. 缓存与 Token 效率

- L0 兼容提示是固定短块，不按模型或轮次动态拼接。
- L1 只在执行边界工作，不向每个模型重复发布别名工具 schema，因此 canonical tool list 与顺序保持稳定。
- 参数归一化发生在统一调用哈希之前，使 `rg` 与等价 `search_grep` 进入同一执行身份和审计语义。
- 完整报告自动终止直接避免一轮 LLM 输入重放和一次可能失败的 reasoning continuation。
- buffered outer loop 的重复调用指纹与 streaming 的 RuntimeControl/历史已使用 canonical 调用；`tool.harness_compatibility` 固化 `requested_tool/canonical_tool/adapter_version/adaptation_kind`，可以按模型、Agent 和时间窗口计算别名命中率与退役依据。
- `llm.rate_limit.wait` 与 `llm.stream.provider_first_chunk_wait` 把限流排队和 Provider 首块等待从总 LLM 延迟中拆开，避免把工具低效循环误判为 Provider 或并发瓶颈；无数据块时不再产生三类 chunk 零值指标。

## 7. 已实现范围

- `AgentLoopResponse.IsStructured` 区分 JSON 信封与普通文本 fallback；
- `ExpectedOutputCandidateTracker.ShouldAutoComplete` 严格提升完整普通文本报告；
- 委派提示统一为“信封外不输出五段报告”，删除“子代理绝不执行 shell”和 SmartDevelop 自验证之间的冲突；
- `HarnessToolCompatibilityAdapter` 完成高频工具名和参数归一化；
- `ToolInvocationService` 在所有策略门禁前应用适配；
- buffered/streaming Agent Loop 在重复指纹、RuntimeControl 与工具历史前使用 canonical 调用，防止交替别名绕过循环门禁；
- `HostShellExecutor` 接受 `pwsh` 别名；
- `shell=wsl` 提供真实 Unix/Linux 执行通道，`linux/unix/ubuntu/wsl.exe` 参数别名归一化为 `wsl`；
- shell/terminal 把正常 no-match 与失败分离；
- terminal 默认入口白名单加入 `rg`、`npx`；
- `tool.harness_compatibility` 记录请求工具、canonical 工具、参数/名称适配类型和适配器版本；
- `ProviderRateLimitLease` 暴露有界等待诊断，流式调用记录 `llm.rate_limit.wait` 与 `llm.stream.provider_first_chunk_wait`；
- buffered/streaming 共用 `FailedToolCallTracker`：相同 canonical 工具、相同参数和相同失败结果第二次出现时返回
  `execution_stalled`，后续原样调用不再进入底层工具；改变参数、失败结果变化或成功都会重置该指纹；
- Agent Loop 从 `RuntimeExecutionIdentity` 直写 `ParentSessionId/SubAgentId/TurnRound/ToolCallCount/ToolNames`，
  不再根据 session 文本猜主/子代理；Streaming 工具执行补齐与 Buffered 一致的 `tool.call` 指标和 trace 身份；
- Streaming 在发布 `usage` SSE 前先提交逐轮 direct attribution，消除 `ConversationProjector` fallback 抢先写入的
  竞态；只有 direct write 明确失败时才由投影补记，补记身份来自持久子代理关系、轮次来自 invocation index，
  未知工具数保持 `null` 而不是伪造 0；
- `SubAgentManager` 只保留终态 usage 摘要，不再把整段子代理 usage 二次写入 `TokenUsageEvents`；逐 LLM round
  的 `agent_llm` 行是唯一归因事实，避免明细与终态汇总双计；
- 添加终态、适配器、统一执行入口、no-match、terminal policy 和 PowerShell/WSL 工作目录映射定向测试。

## 8. 后续阶段

### H1 剩余：聚合报表与真实轨迹回归

- 基于近期主/子代理归档建立 Harness fixture 集，按 provider/model 统计首调成功率、修复轮数、失败 Token、别名命中率、限流等待与首块等待。
- 进程外部署后以新写入的 canonical Token attribution 和 `tool.call` 指标复算主/子代理效率；历史行不回填，
  旧 `sub_agent:*` 汇总行只作为部署前遗留事实识别，不与逐轮 `agent_llm` 行相加。

### H2：按证据扩展

- 仅增加反复出现且可确定映射的别名；
- 评估私有目录 bundled ripgrep；
- 对不同训练 Harness 建 profile 仅用于评测和少量提示，不生成不同的动态工具列表；
- 别名长期低命中且 canonical 首调成功率稳定后可退役。

## 9. 验收门禁

1. 常见 Harness fixture 第一次调用成功，或一次返回明确的 canonical schema 错误；
2. `rg` no-match 不触发失败恢复，缺失二进制仍失败；
3. 别名调用经过与 canonical 调用相同的权限、WorkspaceGuard、审计和限额；
4. 完整普通文本五段报告同轮结束，显式 JSON `CONTINUE` 不被提升；
5. tool schema 快照和排序不因 provider/model 改变；
6. 定向单测、`PuddingRuntime` 构建与进程外部署后真实主/子代理 smoke 通过；
7. “代码实现并通过测试”与“当前运行进程已加载/生产验收”分别报告。
8. 有 WSL 的 Windows 主机通过 working-directory 映射 smoke；无 WSL 主机返回结构化不可用错误，不能自动安装。

## 10. 2026-08-26 只读基线

本轮在未修改 `D:\data` 的前提下，以最近 24 小时索引时间片抽样 `telemetry_metric_events` 和
`TokenUsageEvents`，并用 `llm_gateway_usage_events` 跑最近 7 个自然日权威缓存日报。该基线来自修复前已
部署进程，供进程外重启后的 A/B 对比：

- 24 小时 `tool.execution` 共 3,430 次，失败 379 次（11.05%）。按 session id 临时启发式分组：主路径
  1,011 次/失败 95 次，子代理路径 2,419 次/失败 284 次；这不是新的 canonical 身份口径。
- Shell 共 1,044 次，失败 238 次；其中 172 次仅报告 `exit code 1`。子代理 shell 失败 194 次，平均
  16.26 秒。该集中度直接支持 no-match 分类、`pwsh` 归一化、固定 cwd 提示和显式 WSL 通道。
- `terminal_start` 失败 33 次，包含 10 次把 `cd` 当入口、4 次 `npx` 未在旧白名单、4 次把
  `powershell` 当普通 terminal 入口、2 次直接传 `Get-ChildItem`；说明工具间职责和熟悉命令适配确有缺口。
- `file_patch` 失败 30/142 次；`apply_patch` 失败 15/17 次，主要是缺 `old_text`、raw patch 形状和 hunk
  漂移。24 小时内已出现同一 canonical `file_patch` 指纹连续 2 次全失败，以及同一 `shell` 指纹连续
  2 次全失败；最大相同指纹次数为 3，尚非无限循环，但已有明确的低效重试。
- 主路径 `spawn_sub_agent` 23 次中失败 10 次，失败调用平均约 22.1 分钟；失败集中在 Responses
  `reasoning_text` 回传、子代理 shell exit 1 和 provider rate limit。终态同轮收口能减少额外续轮，但
  provider 协议与限流仍须分别处理，不能归因成一个“子代理慢”。
- 最近 7 日权威输入 1,909,135,721 tokens、cache miss 54,902,889，Token 加权命中率 97.124%，未达到
  连续 7 日 >99% 门禁；2026-08-26 当日为 97.002%。miss 归因中未归因 incremental/full/half/partial
  合计占 90.3%，`system_prompt_changed/tool_spec_changed/session_rehydrated` 分别占 4.8%/2.8%/2.0%。
- 修复前已部署进程写入的 `TokenUsageEvents` 中，`SubAgentId/ParentSessionId/ToolCallCount/TurnRound` 直记不完整：带 `-sub-` 的
  session 仍会落入 main，工具数和轮次为 0；因此主/子代理 Token 对比暂只能用 session id 启发式诊断，
  不得当成正式验收口径。按该临时口径，24 小时主路径命中 96.234%，子代理 98.154%，后台管道
  14.761%。源码现已改为从 `RuntimeExecutionIdentity` 直写 canonical attribution，并删除 Manager 终态重复行；
  该结论须在进程外重启后仅用新数据验证，历史数据不原地回填。

## 11. 2026-08-27 `browser_context` 连续失败事故

### 11.1 证据轨迹

- 主会话 `206a9b48ec904ebb93e7541131fbb835`、Turn `a06d3bd381f94859b02c7f127169b7e7`
  两次委派 `workspace-task-agent`，请求文本明确要求 `file_read`，但传入 `permission_mode=low`
  后子代实际只看到 7 个 Browser Tools。
- 首个子运行 `run_20260827_044716_89e15ca4c288` 持续约 906 秒，339 轮、319 次工具调用；
  其中 309 次 `browser_context` 返回 `browser_not_available: No authenticated Desktop connected`，
  319 次调用出现 313 个不同 args hash。
- 第二个子运行 `run_20260827_050242_7f1a15cf8cac` 仅 4 轮，仍全部是 Browser Tools 且全部
  `browser_not_available`。Browser Bridge 断开时立即返回该结构化错误符合设计，它是暴露条件，不是选错工具的责任层。

### 11.2 根因链

1. `PuddingCore` 和 `PuddingHost` 同时声明了同全名 `BuiltInAgentTemplates`，两份 V2 工具集发生漂移。
   Runtime 引用 Core 副本，其 Task/Code `DefaultToolNames` 只有 7 个 Browser Tools；Host 和其测试却看到较完整的本地副本。
2. `SubAgentTool` 的 Low 权限投影只使用 V2 `DefaultToolNames + RequiresGrantToolNames`，不使用旧
   `AllowedToolNames`；因此旧测试把 V1/V2 集合求并后检查“存在 file_read”，也无法证明 Low 实际投影可用。
3. `ToolLoopInstructionBuilder` 和 Skills 层在 `search_tools` 不可见时仍宣称可用它发现工具。
   模型 reasoning 识别到应使用 `file_read/search_tools`，但 provider function schema 中两者均不存在，只能从 Browser Tools 中继续选择。
4. `FailedToolCallTracker` 按 canonical tool+args+结果阻断完全重复，313 个 args hash 绕过该层；
   `RuntimeControlService` 已计算忽略参数的同失败族计数，却未将它用于熔断。且默认总量阈值为 50，队列最多保留 10 条，总量熔断数学上不可达。

### 11.3 修复决策

- 删除 Host 副本，以 `PuddingCore/Platform/BuiltInAgentTemplates.cs` 作唯一权威源；Task/Code Low
  默认保留 `search_tools/file_read/list_dir/file_search/search_grep`，写入和 Shell 仍要求 grant。
- 只在 `search_tools` 真实出现在可见 schema 时注入递延发现提示，不再制造“提示可用、schema 不可用”的分裂。
- 保留精确调用第二次 `execution_stalled`，并增加失败族 5 次熔断；窗口保留容量至少等于配置总量阈值。
  Buffered 在 Runtime cancellation 后先检查 Faulted，将运行归档为 Failed 并返回熔断摘要，而不是误报普通 Cancelled。

### 11.4 验收边界

定向测试覆盖 Low 模板投影、模板类单一程序集、按可见集生成发现提示、
参数持续变化时第 5 次同失败族熔断，以及配置总量阈值大于 10 时仍可达。
这些证明源码修复，不证明当前 Desktop/Core 已加载新构建；还需进程外重启后用新子代会话进行功能 smoke。
