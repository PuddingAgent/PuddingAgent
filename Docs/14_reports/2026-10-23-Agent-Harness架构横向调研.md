# Agent Harness 架构横向调研：dsh / Hermes / Codex / opencode

- 日期：2026-10-23
- 目的：对照四个主流 Agent harness 的架构设计，找出对 PuddingAgent 可借鉴与不可借鉴的部分
- 证据等级：**一手**（官方 README、本地实机目录）／**二手**（第三方实测文章）／**未证实**（仅有摘要）

## 0. 一句话结论

四者共性极强：**model-agnostic + 子代理并行 + MCP/工具协议 + 会话持久化（SQLite/JSONL/FTS5）**。
真正的差异只在**一个问题上**：**agent loop 是不是可替换的**。

- dsh 把 agent loop 做成**插件**（连主循环都能换）
- Codex 把 agent loop 当作**唯一值得复用的那部分**，其余分层剥开
- Hermes 把**学习闭环**（技能自创建/自改进）做成产品核心
- opencode 把**进程边界**（client/server）做成架构主轴

## 1. DeepSeek Harness（`dsh`）

| 项 | 内容 |
|---|---|
| 仓库 | `github.com/deepseek-ai/deepseek-harness` |
| 许可 | MIT |
| 状态 | developer preview（实测版本 `0.1.1-rc.2`，README 明确警告会破坏兼容） |
| 底层框架 | **Cordis**（插件框架） |
| 核心主张 | **everything is a plugin** —— 模型适配器、工具注册表、会话日志、**agent loop 本身** |
| 语言/安装 | `npx @deepseek-ai/dsh web`（web UI `127.0.0.1:3080`），或 `pnpm install && pnpm run build` |

**命名的替换点（配置选择，不改代码）**：

| 替换点 | 已实现的后端 |
|---|---|
| `ctx.sessionPersistence` | JSONL / SQLite |
| `ctx.subprocess` | 本地执行器 / E2B 沙箱 |
| `ctx.shell` | bash / sandboxed bash / PowerShell |
| `ctx.subagents` | **6 个 provider** |

**两个值得注意的设计细节（二手来源，DeepInfra 实测）**：

1. 仓库里带 `subagent-claude-code` 与 `subagent-codex` —— **它能把自己的活派给竞争对手的 agent 并回收结果**。
2. 有 hook bridge 直接读你为 Claude Code 写的 `hooks.json` —— 迁移成本导向的兼容层。

**凭据处理**：`$DSH_HOME/.credentials.yaml`，settings 里只存**引用**不存密钥；`provider ID` 一旦创建**永久不可改**（sessions / model defaults / credential refs 全指向它）。

**判读**：dsh 不是"一个产品"，是**一个底盘**。它的赌注是「工作流不该锁在框架里」。

## 2. Hermes Agent（Nous Research）

| 项 | 内容 |
|---|---|
| 仓库 | `github.com/NousResearch/hermes-agent` |
| 许可 | MIT |
| 语言 | Python 3.14 |
| 定位 | **self-improving agent**，内置**学习闭环** |

**官方 README 列出的架构能力（一手）**：

| 能力 | 具体形态 |
|---|---|
| **学习闭环** | Agent 自策展的 memory + 周期性 nudge；**复杂任务后自主创建 skill**；**skill 在使用中自我改进** |
| 跨会话回忆 | **FTS5 session search** + LLM 摘要；Honcho dialectic 用户建模；兼容 `agentskills.io` 开放标准 |
| 多渠道网关 | Telegram / Discord / Slack / WhatsApp / Signal / CLI，**单一 gateway 进程**；语音备忘录转写、跨平台会话连续性 |
| 调度 | 内置 cron，自然语言编写，投递到任意平台 |
| 并行 | 派生隔离子代理；Python 脚本经 **RPC 调工具**，把多步流水线压成**零上下文成本**的轮次 |
| **六种终端后端** | local / Docker / SSH / **Modal** / **Daytona** / **Vercel Sandbox**（+ Singularity 等插件后端）；Daytona/Modal 支持**serverless 休眠**，空闲时几乎零成本 |
| 研究用途 | 批量 trajectory 生成 + trajectory 压缩（用于训练下一代工具调用模型） |

另一篇技术分析（二手）称其 harness 核心是一个 Python 类 `AIAgent`，owns model / budget / callbacks。

**判读**：Hermes 与 PuddingAgent 的**能力面最像**（memory + skill + subagent + cron + 多渠道），但 Hermes 把 **skill 的自动生成与自我改进**做成了主轴，而不是靠人手写。

## 3. Codex（OpenAI）

| 项 | 内容 |
|---|---|
| 仓库 | `github.com/openai/codex` |
| 许可 | **Apache-2.0** |
| 实现 | `codex-rs`（Rust），本机实测 `codex-cli 0.163.0-alpha.11.2` |
| 官方定位 | 「**可复用的部分是 agent loop**；一个可检查、可改造的开放 harness」 |

**本机 `~/.codex/` 实机观察（一手）**：

| 观察 | 含义 |
|---|---|
| `state_5.sqlite` + `-wal`/`-shm` | 运行状态用 **SQLite** 持久化，WAL 模式 |
| `thread_history_1.sqlite`（**67 MB**） | 会话线程历史单独一个库 —— 与状态库**分离** |
| `config.toml` 的 `[plugins.*]` 13+ 条 | 已经插件化（browser / computer-use / code-review / pdf / spreadsheets / presentations …） |
| `[mcp_servers.node_repl]` | MCP 作为一等公民 |
| `.sandbox/` 目录 | 沙箱是运行期实体，不是开关 |
| `model_reasoning_effort = "xhigh"` | 推理强度是一等配置 |

**未证实**：二手文章称其 harness 是「三层架构：模块化 prompt/context 生成器 → loop 执行 → 多路（multi-*）」，并有「prompt 与 context 生成器」独立成层。**我只读到摘要，未读原文**，故不作为结论。

**判读**：Codex 与 dsh 走的是**相反方向** —— dsh 说"连 loop 都能换"，Codex 说"loop 是唯一值得保留的东西，其余都该被拆成可替换的分层"。

## 4. opencode（SST）

| 项 | 内容 |
|---|---|
| 仓库 | `sst/opencode`（社区另有 `anomalyco/opencode` 镜像） |
| 定位 | 开源的终端 AI 编码 agent，Claude Code 替代品 |
| 后端 | **TypeScript on Bun** + **Hono** HTTP server |
| 前端 | **Golang TUI**（默认 `opencode` 命令同时拉起 server 与 TUI 两个进程） |
| 模型层 | provider-agnostic，重活交给 **AI SDK**（统一 Anthropic / OpenAI / Gemini / 自建 OpenAI 兼容端点） |
| SDK | 用 **Stainless** 吃 OpenAPI spec **自动生成**类型安全客户端 |

**架构要点（第三方深挖，二手）**：

- **client/server 分离**：任何客户端（移动端、Web、脚本）都能发 HTTP 建会话、驱动 agent；实际工作在 server 所在的机器上执行。
- **两层结构**：Harness（工具层：读写文件、跑 bash、抓 URL、接 LSP）+ Agent。
- **LSP 集成**：通过 **event bus + 全局 diagnostics map**，把 `textDocument/didChange` 的诊断喂回模型上下文。
- **每个 provider 一份定制 system prompt**（如 `packages/opencode/src/session/prompt/gemini.txt`），不是一份提示词打天下。

**判读**：opencode 的赌注是**进程边界**。它把 agent 做成了服务，TUI 只是第一个客户端。

## 5. 横向对比

| 维度 | dsh | Hermes | Codex | opencode |
|---|---|---|---|---|
| 语言 | TypeScript | Python 3.14 | Rust | TS/Bun + Go |
| 许可 | MIT | MIT | Apache-2.0 | 开源 |
| **架构主轴** | 插件化到 loop | **学习闭环** | **loop 复用 + 分层** | **client/server** |
| agent loop 可换 | ✅ **是** | ✗ | ✗（但被抽成独立可复用层） | ✗ |
| 持久化 | JSONL / SQLite（可换） | FTS5 检索 + memory | **两个 SQLite 库分离** | session 存储 |
| 子代理 | 6 provider（含 claude-code / codex） | ✅ 隔离子代理 | 多层 multi-* | general subagent |
| 沙箱 | local / E2B | **6 后端 + serverless 休眠** | `.sandbox` + approval | 权限审批 |
| 多渠道 | ✗（web UI） | ✅ 6 渠道单网关 | IDE / CLI / App / Web | TUI 为第一客户端 |
| 生态接线 | 读 Claude Code `hooks.json` | `agentskills.io` 标准 | **MCP + 插件市场** | MCP + LSP |
| 最值得抄的一条 | loop 可替换 ⇒ 编排可实验 | **skill 自创建/自改进** | 状态与历史**分库** | **TUI ≠ 服务**，客户端可替换 |

## 6. 对 PuddingAgent 的判读

PuddingAgent 现状：C#/.NET 单体 + `PuddingRuntime` 编排 + memory（FTS5 + 向量）+ 手写 SKILL 库 + 子代理 + cron + 飞书渠道 + 本机沙箱。

**能力面最接近 Hermes**，但三条差距明显：

| 差距 | 对照 | 可借鉴程度 |
|---|---|---|
| **技能靠人手写**，没有自动生成与自改进 | Hermes 的 learning loop | **高**。本轮会话就反复出现"这个教训值得存成 skill"，但全靠手工触发 |
| **agent loop 不可替换**，实验新编排必须改主干 | dsh 的 `ctx.*` 替换点 | 中。我们的"组件独立 + 编译期强制依赖方向"已经是同方向的弱化版 |
| **状态与历史同库**（`code_index.db` / platform db 混在一起） | Codex 把 `state_5.sqlite` 与 `thread_history_1.sqlite` 分离 | **高**。刚做完的索引库治理就是被"同库共写锁"拖慢的直接后果 |
| 只有本机沙箱 | Hermes 的 6 后端 + 休眠 | 低（短期无云预算） |
| 渠道单一（飞书） | Hermes 6 渠道 | 低（够用） |

**最值得做的一件事**：把 **skill 的创建/改进**从"人工触发"改成"任务结束后自动提议 + 需我批准"。
理由：本轮已经出现明显信号 —— 同样的教训（变异测试值与真值必须可区分）是踩过一次坑才被我手工存下来的。

**第二件**：**状态库与历史库分库**。索引库锁竞争已经实证过一次（2.84 GB 库、`busy_timeout` 反复超时），Codex 的选择说明这是成熟做法而不是洁癖。

## 7. 证据与未证实清单

| 结论 | 证据等级 | 来源 |
|---|---|---|
| dsh 基于 Cordis、everything-is-a-plugin、替换点清单 | 二手（实测文） | DeepInfra review（已抓全文） |
| dsh 带 `subagent-claude-code` / `subagent-codex` | 二手 | 同上 |
| dsh MIT、developer preview、`0.1.1-rc.2` | 二手 + 官方站点摘要 | 同上 / deepseek.com |
| Hermes 全部能力清单 | **一手** | 官方 GitHub README（已抓全文） |
| Hermes 核心类名 `AIAgent` | 二手（未读原文） | kenhuangus substack 摘要 |
| Codex `~/.codex` 目录结构、两库分离、插件与 MCP | **一手** | 本机实测 |
| Codex 三层架构（prompt/context → loop → multi-*） | **未证实** | 仅摘要，原文 403/404 |
| Codex「agent loop 是可复用部分」 | 二手（官方博客摘要） | developers.openai.com 摘要，正文抓取失败 |
| opencode 的 Bun/Hono/Go TUI、AI SDK、Stainless、LSP event bus | 二手（深挖文） | cefboud.com（已抓全文） |
| opencode 两层 Harness+Agent | 二手（第三方指南摘要） | dashen-tech / cefboud |

**抓取失败记录**（如实留存）：`github.com/*` SSL 握手失败；`developers.openai.com` 403；`medium.com` 403；`kenhuangus.substack.com` 404。
