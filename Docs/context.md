# 历史调用记录

> Lead 在此维护 QA 交错调度记录和 Architect 领航审查记录，用于调度决策。
> 同时，这也是工作的交接记录，用于帮助明天的你在遗忘的情况下，快速回忆起今天的工作内容和决策过程。

## QA 交错调度

| 日期 | 任务ID | 开发模型 | QA模型 | 结果 | 备注 |
|------|--------|---------|--------|------|------|
| 0508 | ADR-014-E | DeepSeek-V4-Pro | — | BUILD_PASS | dotnet build 0 error, MemoryTests 47/47 PASS, WebApiTests 15 failures pre-existing |
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
| 0505 | Session-Pagination | GPT-5.3-Codex | GLM-5.1 | PASS_WITH_NOTES | 3×P1：鉴权缺失/重复创建/异步竞态均已修复 |
| 0505 | Memory-Phase1 | dahuang | Sonnet 4.6 | FAIL | 1×P0 性能断言反转(假阳性)+3×P1(事务缺失/Singleton DbContext/连接未关闭)+6×P2 |
| 0505 | UI-1 (034~037) | DeepSeek-V4-Pro | GPT-5.3-Codex | FAIL | any/硬编码颜色/emoji/间距<12/缩放1.035 |
| 0505 | UI-1 (034~037) 修复 | DeepSeek-V4-Pro | Sonnet 4.6 | FAIL | P0: styles.ts:62 CSS值损坏 ext-primary) + P1: 头像fallback未用stringToColor |
| 0505 | UI-1 (034~037) 二次修复 | DeepSeek-V4-Pro | — | PASS | P0+P1 已修复，编译通过 |
| 0504 | GM-001~003 | GPT-5.3-Codex/DeepSeek | — | PASS | SM2/SM3/SM4国密集成完成 |
| 0504 | E2E-Playwright | — | — | DEFERRED | task-002待办，前端Jest需修复@umijs/max/test |
| 0504 | UI-Test | Lead(Browser) | GPT-5.3-Codex | PASS_WITH_FIXES | Docker启动修复+9页面测试，发现KeyVault 500/Runtime路径错/多页空白 |

## Architect 领航审查

| 日期 | 触发原因 | 审查范围 | 产出 | 备注 |
|------|---------|---------|------|------|
| 0503 | Claude Code 架构评估 | 全局架构 | 架构.md 新增参考章节 + 3 个新任务卡 + 9 个文档修订 | [Docs/claude-reviews-claude/](claude-reviews-claude/) 作为子模块引入 |
| 0504 | Anthropic Building Effective Agents 对比审查 | 全模块 Agent 模式 | 10 个新任务卡 (task-010~019)：3×P0 Eval-Opt/Routing/ACI + 4×P1 规划可见性/人机检查点/上下文压缩/PromptChaining + 3×P2 并行Voting/容错降级/抽象审计 | [Docs/context.md](context.md) 本记录 |
| 0505 | 累计12+任务卡完成 + 记忆引擎方向 | 全局架构（记忆与会话数据层） | ADR-013 (Docs/07架构/13记忆与会话数据层.md) + 25 个新任务卡 (task-20260505-007~031) 分4个Phase：P0 基础持久化8个/P1 向量检索7个/P2 知识图谱+压缩7个/P2+ 退场清理3个。另 Agent 个性层6个 (task-20260505-001~006) 互补。 | 参考 OpenClaw 智能体设计模型，适配 Pudding 多 Agent 多场景架构 |
| 0505 | Chat UI 评估与重设计 | Admin Chat 页面完整交互设计 | 设计方案 (Docs/Features/ChatUIRedesign.md) 含 3 方案对比 (Discord/ChatGPT/混合分层)+18 个新任务卡 (task-20260505-034~051)：Phase UI-1 基础重构4个P0 + BE-Chat 支撑3个P1 + Phase UI-2 交互增强4个P1 + Phase UI-3 Agent特性4个P1+ + Phase UI-4 高级功能3个P2。方案 C 混合分层推荐。 | 参考 Discord/WhatsApp/Slack/ChatGPT/Cursor 设计；依赖 ADR-013 消息树底座 |
