# task40 — 上下文压缩设计

> **创建日期：** 2026-05-03
> **优先级：** P1（L1 记忆重要）
> **状态：** ✏️ 设计中
> **依赖：** task26 (Runtime 基础宿主)、task39 (会话持久化)
> **参考：** [Claude Code EP11 Compact System](../../Docs/claude-reviews-claude/architecture/11-compact-system.md) — 3 层压缩（Micro→Session→Full）、Token 预算管理

---

## 任务目标

设计并实现 Pudding Agent 的上下文压缩系统，使 Agent 能在 Token 预算范围内维持"无限对话"的体验——在上下文接近上限时自动压缩历史，同时保留关键信息。

## 参考设计：3 层压缩架构

借鉴 Claude Code 的分层压缩策略——从"低成本修整"到"高成本总结"：

| 层级 | 机制 | 触发条件 | 压缩率 | 成本 |
|------|------|---------|--------|------|
| **MicroCompact** | 清除旧工具结果 | 每轮自动（按时间/数量） | ~10-50K tokens | 极低 |
| **SessionMemory** | 用会话记忆替换历史 | 自动压缩阈值 | ~60-80% | 低（无 LLM 调用） |
| **Full Compact** | LLM 总结整个对话 | 自动或手动 `/compact` | ~80-95% | 高（1 次 API 调用） |

### Tier 1: MicroCompact（每轮自动）

每次对话轮次后，自动清除**旧的、可重现的**工具结果：

```
可压缩的工具类型：FileRead、Bash、Grep、Glob、WebSearch、WebFetch
不可压缩的类型：AgentTool 结果、MCP 工具结果、用户消息
```

保留最近 N 个结果，其余替换为 `[Old tool result content cleared]` 占位符。

### Tier 2: SessionMemory Compact（中成本）

当 Token 用量超过阈值（如 60%）时，使用**正在维护的会话记忆**作为压缩摘要——不调用 LLM，直接用已总结的内容替换历史：

```
压缩前: [msg1, msg2, ..., msg_summarized, ..., 最近5条]
压缩后: [boundary, session_memory_summary, 最近5条]
```

保留策略：至少保留 10K tokens + 最近 5 条带文本的消息，硬上限 40K tokens。

### Tier 3: Full Compact（高成本）

当 Token 用量接近上限（如 85%），调用 LLM 生成完整会话摘要：

```
压缩前: [msg1, msg2, ..., msg_N]
压缩后: [boundary_message, summary, 最近3轮对话]
```

LLM 提示策略：
- 要求 LLM 总结关键决策、待办事项、重要发现
- 保留文件修改记录（哪个文件被改了，做了什么修改）
- 以 `<summary>` 标签包裹，结构化为 Markdown

## Pudding 具体实现方案

### Token 估算

```csharp
// 简化的 Token 估算（非精确，但足够用于触发判断）
public static class TokenEstimator
{
    // 粗略比例：中文 ~1.5 chars/token，英文 ~4 chars/token
    public static int EstimateTokens(string text)
    {
        var chineseChars = text.Count(c => c >= 0x4E00 && c <= 0x9FFF);
        var otherChars = text.Length - chineseChars;
        return (int)(chineseChars / 1.5 + otherChars / 4.0);
    }
    
    // 保守系数：实际 Token 数可能比估算多 25%
    public static int SafeEstimate(string text) 
        => (int)(EstimateTokens(text) * 1.25);
}
```

### 压缩触发器

```csharp
public class CompactTrigger
{
    private readonly int _warningThreshold;  // 60% — 触发 SessionMemory Compact
    private readonly int _criticalThreshold; // 85% — 触发 Full Compact
    private readonly int _maxTokens;         // 模型上下文窗口大小
    
    public CompactDecision Evaluate(int currentTokens)
    {
        if (currentTokens > _maxTokens * 0.95)
            return CompactDecision.ForceCompact; // 必须压缩
        if (currentTokens > _maxTokens * 0.85)
            return CompactDecision.FullCompact;
        if (currentTokens > _maxTokens * 0.60)
            return CompactDecision.SessionMemoryCompact;
        return CompactDecision.MicroCompact; // 仅清理旧工具结果
    }
}
```

### 与记忆引擎的联动

- Full Compact 触发的 LLM 总结结果 → 自动写入长期记忆
- SessionMemory Compact 依赖已有的会话记忆（由 DreamTask 在后台维护，参考 EP08）
- 压缩后保留最近 N 轮对话作为即时上下文

## 实现步骤

1. **TokenEstimator** — 简易 Token 估算器
2. **MicroCompact** — 旧工具结果清理器
3. **SessionMemoryCompact** — 使用已有记忆替换历史
4. **FullCompact** — LLM 驱动的完整对话总结
5. **CompactTrigger** — 阈值判断与触发决策
6. **CompactManager** — 协调三层压缩的统一入口

## 验收标准

1. 长对话（>50 轮）不因 Token 超限而中断
2. 压缩后 Agent 仍能正确回答关于对话早期内容的提问
3. MicroCompact 每轮自动执行，不产生额外 API 调用成本
4. Full Compact 的总结自动写入长期记忆
5. 用户可手动触发 `/compact` 命令

## 不做

- Prompt Cache 优化（依赖 LLM 服务商能力，不属于 Pudding 控制范围）
- 精确 Token 计数（使用 tiktoken 等效库，精确但引入额外依赖，V2 评估）
- 多模型混合压缩（V2）
