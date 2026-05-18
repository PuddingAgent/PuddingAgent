# 历史调用记录

> Lead 在此维护 QA 交错调度记录和 Architect 领航审查记录，用于调度决策。
> 同时，这也是工作的交接记录，用于帮助明天的你在遗忘的情况下，快速回忆起今天的工作内容和决策过程。

## QA 交错调度

| 日期 | 任务ID | 开发模型 | QA模型 | 结果 | 备注 |
|------|--------|---------|--------|------|------|
| 0508 | ADR-014-E | DeepSeek-V4-Pro | — | BUILD_PASS | dotnet build 0 error, MemoryTests 47/47 PASS, WebApiTests 15 failures pre-existing |
| 0510 | Subconscious-Phase1 | GPT-5.3-Codex | Sonnet 4.6 | PASS_WITH_NOTES | P1-1 OnCompletedAsync阻塞SSE done/P1-2 DI重复注册，均已修复 |
| 0510 | Subconscious-Phase2 | GPT-5.3-Codex | DeepSeek-V4-Pro | PASS_WITH_NOTES | P1 IMemoryLibrary死代码/P1 DI冗余/P2 Token=0硬编码，不阻断联调 |
| 0512 | Fix-MemoryTools-Schema | GPT-5.3-Codex | Gemini 3.1 Pro | PASS | P2 SkillRuntime硬编码与Tool.Parameters微小差异，无功能性影响 |
> 同时，这也是工作的交接记录，用于帮助明天的你在遗忘的情况下，快速回忆起今天的工作内容和决策过程。

## QA 交错调度

| 日期 | 任务ID | 开发模型 | QA模型 | 结果 | 备注 |
|------|--------|---------|--------|------|------|
| 0503 | UIUX-Phase1 | GitHub Copilot | GLM-5.1 | PASS_WITH_NOTES | P1 重试/重新生成残留已修复 |
| 0503 | Menu-WorkspaceMgmt | DeepSeek-V4-Pro | Sonnet 4.6 | PASS_WITH_NOTES | 其他 locale 缺失 menu.workspace (P2) |
| 0503 | T-001 | GPT-5.3-Codex | Sonnet 4.6 | FAIL | P1 AbortError 未清理流式状态 |
| 0503 | T-001 | GPT-5.3-Codex | GLM-5.1 | PASS | 5项修复确认 |
| 0503 | T-NewChat-Session | DeepSeek-V4-Pro | Sonnet 4.6 | PASS_WITH_NOTES | P2 handleSelectSession 未复位 forceNewSessionRef（已修复） |
| 0503 | Bug-admin-chat-scroll-jitter | DeepSeek-V4-Pro | GPT-5.3-Codex | PASS_WITH_NOTES | scrollbar-gutter: stable 修复 hover 抖动，建议补做 Safari/移动端回归 |
| 0504 | task-20260504-003 | Claude Sonnet 4.6 | GPT-5.3-Codex | FAIL | 3×P0安全(端点无鉴权/API Key明文传输)+5×P1+6×P2，须修复后重审 |
| 0504 | task-20260504-001 | DeepSeek-V4-Pro | — | PASS | 88个MSTEST0037警告→0，dotnet build clean |
| 0504 | task-20260504-004 | GPT-5.3-Codex | — | PASS | LlmConfig.KeyVaultId替代ApiKey明文 |
| 0504 | task-20260504-005 | DeepSeek-V4-Pro | — | PASS | HttpClient池化+ToolCalls+Cookie修复 |
| 0505 | Chat-ContextMenu | GPT-5.3-Codex | GLM-5.1 | PASS_WITH_NOTES | P1-1 重复DTO已删；P1-2 Frozen=归档语义已注释说明；P2-6 前端过滤Frozen已加 |
| 0513 | RuntimeSpace-UI-P0-P3 | DeepSeek-V4-Pro/Flash | DeepSeek-V4-Pro | PASS_WITH_NOTES | P1 重复列已删+P2 死代码/硬编码，20文件全量改造通过 |
| 0505 | Session-Pagination | GPT-5.3-Codex | GLM-5.1 | PASS_WITH_NOTES | 3×P1：鉴权缺失/重复创建/异步竞态均已修复 |
| 0505 | Memory-Phase1 | dahuang | Sonnet 4.6 | FAIL | 1×P0 性能断言反转(假阳性)+3×P1(事务缺失/Singleton DbContext/连接未关闭)+6×P2 |
| 0505 | UI-1 (034~037) | DeepSeek-V4-Pro | GPT-5.3-Codex | FAIL | any/硬编码颜色/emoji/间距<12/缩放1.035 |
| 0505 | UI-1 (034~037) 修复 | DeepSeek-V4-Pro | Sonnet 4.6 | FAIL | P0: styles.ts:62 CSS值损坏 ext-primary) + P1: 头像fallback未用stringToColor |
| 0505 | UI-1 (034~037) 二次修复 | DeepSeek-V4-Pro | — | PASS | P0+P1 已修复，编译通过 |
| 0504 | GM-001~003 | GPT-5.3-Codex/DeepSeek | — | PASS | SM2/SM3/SM4国密集成完成 |
| 0504 | E2E-Playwright | — | — | DEFERRED | task-002待办，前端Jest需修复@umijs/max/test |
| 0504 | UI-Test | Lead(Browser) | GPT-5.3-Codex | PASS_WITH_FIXES | Docker启动修复+9页面测试，发现KeyVault 500/Runtime路径错/多页空白 |
| 0517 | T-102 | DeepSeek-V4-Pro | DeepSeek-V4-Pro | FAIL | 2xP0: 中止不可用 + Task.Run异常不可见 + 3xP1 + 5xP2 |
| 0517 | T-102 | dev | qa | PASS_WITH_NOTES | P0-1中止✅ P0-2 metadata超时→500✅；遗留低风险：流失败缺error帧(下迭代)；P1 session.closed/pending ID/8s窗口不阻断 |
| 0517 | T-CACHE-P0~P2 | Lead+dev+lw-dev | qa | FAIL | P0-1 done帧反序列化错误(TokenUsageStats全0)已修复；P1-2无缓存区分/P2-3缓存环显示条件已修复；遗留P1-1 TokenUsageDto重复定义/P2-2裸fetch/P2-4流式路径待确认 |
| 0518 | T-104 | dev | lead(自审纯删除) | PASS | 纯删除7项死代码+前端残留，0 error build，无残留引用 |

## Architect 领航审查
| 0517 | T-103 | DeepSeek-V4-Pro | DeepSeek-V4-Pro | PASS_WITH_NOTES | 3xP2 命名兼容+metadata持久化 |

## Architect 领航审查

| 日期 | 触发原因 | 审查范围 | 产出 | 备注 |
|------|---------|---------|------|------|
| 0503 | Claude Code 架构评估 | 全局架构 | 架构.md 新增参考章节 + 3 个新任务卡 + 9 个文档修订 | [Docs/claude-reviews-claude/](claude-reviews-claude/) 作为子模块引入 |
| 0504 | Anthropic Building Effective Agents 对比审查 | 全模块 Agent 模式 | 10 个新任务卡 (task-010~019)：3×P0 Eval-Opt/Routing/ACI + 4×P1 规划可见性/人机检查点/上下文压缩/PromptChaining + 3×P2 并行Voting/容错降级/抽象审计 | [Docs/context.md](context.md) 本记录 |
| 0505 | 累计12+任务卡完成 + 记忆引擎方向 | 全局架构（记忆与会话数据层） | ADR-013 (Docs/07架构/13记忆与会话数据层.md) + 25 个新任务卡 (task-20260505-007~031) 分4个Phase：P0 基础持久化8个/P1 向量检索7个/P2 知识图谱+压缩7个/P2+ 退场清理3个。另 Agent 个性层6个 (task-20260505-001~006) 互补。 | 参考 OpenClaw 智能体设计模型，适配 Pudding 多 Agent 多场景架构 |
| 0505 | Chat UI 评估与重设计 | Admin Chat 页面完整交互设计 | 设计方案 (Docs/Features/ChatUIRedesign.md) 含 3 方案对比 (Discord/ChatGPT/混合分层)+18 个新任务卡 (task-20260505-034~051)：Phase UI-1 基础重构4个P0 + BE-Chat 支撑3个P1 + Phase UI-2 交互增强4个P1 + Phase UI-3 Agent特性4个P1+ + Phase UI-4 高级功能3个P2。方案 C 混合分层推荐。 | 参考 Discord/WhatsApp/Slack/ChatGPT/Cursor 设计；依赖 ADR-013 消息树底座 |
| 0510 | 潜意识 LLM 子代理系统架构设计 | 记忆全链路（Runtime→MemoryEngine→MemoryLibrary→Admin UI） | ADR-015 (Docs/07架构/15潜意识LLM子代理系统ADR.md) 含 5 个 ADR 决策：编排层架在现有双轨之上/Channel队列+BackgroundService/三层MemorySearchMode调度/潜意识LLM路由/5Phase实施路线。新增接口 ISubconsciousOrchestrator、SubconsciousConsolidationHook、SubconsciousWorkerService、MemoryFacts/Preferences 表、MemoryManagementController、Admin 记忆图书馆管理页面+自测试套件。 | 满足 A(新架构模式)+B(跨3+模块数据变更)。复用现有 IMemoryLibrary/IMemoryEngine/IAgentLoopHook，不推翻重写。参考 Claude Code CLI 子代理 + OpenClaw 架构。
| 0515 | 会话状态层与客户端解耦架构设计 | 全链路（Runtime→Controller→Platform→前端+未来多客户端） | ADR-016 (Docs/07架构/16会话状态层与客户端解耦ADR.md) 含 5 个 ADR 决策：新增 ISessionStateManager 中间层/Channel 生命周期与 HTTP 解耦/EventLog append-only 不可变/前端双 SSE 连接模式/子代理状态独立追踪。新增接口 ISessionStateManager + SessionEventTypes 常量 + session_event_log / session_sub_agents SQLite 表 + SessionEventsController / WorkspaceNotificationsController。10 Phase 实施路线。 | 满足 A(新架构模式)+B(跨3+模块不可逆数据变更)。执行引擎与客户端彻底解耦，前端从"唯一观察者"降级为"多个观察者之一"。解决异步子代理完成不可见/思维链不可回放/切换会话空白/多客户端无感知 4 大痛点。复用现有 AgentExecutionService/SubAgentTool/AgentEventHandler/SessionEventHub，不推翻重写。
