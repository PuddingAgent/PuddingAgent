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

## Architect 领航审查

| 日期 | 触发原因 | 审查范围 | 产出 | 备注 |
|------|---------|---------|------|------|
| 0503 | Claude Code 架构评估 | 全局架构 | 架构.md 新增参考章节 + 3 个新任务卡 + 9 个文档修订 | [Docs/claude-reviews-claude/](claude-reviews-claude/) 作为子模块引入 |
