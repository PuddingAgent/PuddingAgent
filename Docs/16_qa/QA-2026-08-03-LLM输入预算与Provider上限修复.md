# LLM 输入预算与 Provider 上限修复验收

## 问题

Qwen 返回 HTTP 400：`Range of input length should be [1, 983616]`。模型配置只声明
1,000,000 总上下文，运行时没有表达 983,616 的单次输入硬上限；本地估算还遗漏部分
reasoning/tool-call payload，且曾允许较小估算覆盖 Provider usage。

## 修复边界

- Provider Model 新增可选 `maxInputTokens`，管理 API、管理前端、配置解析和运行时路由贯通。
- 有效输入预算为 `min(maxInputTokens, maxContextTokens - maxOutputTokens - safetyBuffer)`。
- 健康检查与自动压缩接收相同的 context/input/output 容量。
- 最终请求发送前重新计量 messages + tools；超限时按完整会话单元裁剪最旧历史，保留 System Prompt 和最近 8 条消息。
- usage 快照计入 reasoning、tool call id/name/arguments，并按 session + model 使用 Provider prompt usage 做保守校准。
- Provider total usage 是下一轮上下文用量的硬下界，不再被较小估算覆盖。
- Agent `maxReplyTokens` 收紧模型输出上限，`DirectLlmClient` 下传 `max_tokens`。
- 精确识别 Provider 输入范围错误，校准后在同一次 Agent 执行内只恢复一次。

## 配置

开发环境 `D:\data\config\llm.providers.json` 当前配置的 `qwen3.8-max`（事故日志中模型名为
`qwen3.8-max-preview`）：

```json
{
  "maxContextTokens": 1000000,
  "maxInputTokens": 983616,
  "maxOutputTokens": 65000
}
```

Agent 的 `maxReplyTokens=4096` 会把该次执行的最终输出上限收紧为 4096，因此发送预算按
`min(983616, 1000000 - 4096 - 1024) = 983616` 计算。

## 自动化验收

- `ContextCompactionServiceTests`：Provider usage 优先级、输出预留和显式输入硬上限。
- `LlmInputBudgetRegressionTests`：有效预算、Provider 校准、reasoning/tool-call 计量、历史裁剪、
  原始 HTTP 400 解析及单次恢复所需校准、`max_tokens` 下传。
- `AgentRuntimeProfileResolverTests`：Provider 容量解析及 Agent `maxReplyTokens` 收紧。

## 运行验收

重启 Pudding 后，用原蜜糖会话继续执行：

1. 日志不再连续出现相同的 `runtime_execution_failed` 输入范围错误。
2. 接近上限时出现 `[AgentExec:ContextBudget] Trimmed outbound history` 或自动压缩事件。
3. 若 Provider 首次报告未知硬上限，只出现一条 `Provider rejected input length; recalibrating and retrying once`，随后恢复或以本地预算错误明确终止。
4. 上下文状态接口的 `effectiveWindowTokens` 为 983,616，remaining/ratio 基于该值计算。
