# 历史调用记录

> Lead 在此维护 QA 交错调度记录和 Architect 领航审查记录，用于调度决策。
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
| 0504 | task-20260504-006 | GPT-5.3-Codex | — | PASS | _histories/_instances过期清理 |
| 0504 | GM-001~003 | GPT-5.3-Codex/DeepSeek | — | PASS | SM2/SM3/SM4国密集成完成 |
| 0504 | E2E-Playwright | — | — | DEFERRED | task-002待办，前端Jest需修复@umijs/max/test |

## Architect 领航审查

| 日期 | 触发原因 | 审查范围 | 产出 | 备注 |
|------|---------|---------|------|------|
| 0503 | Claude Code 架构评估 | 全局架构 | 架构.md 新增参考章节 + 3 个新任务卡 + 9 个文档修订 | [Docs/claude-reviews-claude/](claude-reviews-claude/) 作为子模块引入 |
| 0504 | Anthropic Building Effective Agents 对比审查 | 全模块 Agent 模式 | 10 个新任务卡 (task-010~019)：3×P0 Eval-Opt/Routing/ACI + 4×P1 规划可见性/人机检查点/上下文压缩/PromptChaining + 3×P2 并行Voting/容错降级/抽象审计 | [Docs/context.md](context.md) 本记录 |
