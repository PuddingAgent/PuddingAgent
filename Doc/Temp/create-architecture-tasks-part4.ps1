# 批量创建架构改进任务卡 - Part 4 (P2 + 文档管理)
$ErrorActionPreference = "Stop"
$python = "python"
$script = ".github/skills/todo-api/todo_api.py"

function Invoke-CreateTask {
    param($Title, $Priority, $Stage, $Tags, $Goal, $OutOfScope, $Acceptance, $Impact, $EntryPoints, $Risk, $ExecType, $HumanOwner, $Description)
    $cmdArgs = @("create","--title",$Title,"--project","Pudding","--task-owner","WangXianQiang","--priority",$Priority,"--stage",$Stage,"--goal",$Goal,"--out-of-scope",$OutOfScope,"--impact-scope",$Impact,"--risk-notes",$Risk,"--executor-type",$ExecType,"--human-owner",$HumanOwner,"--description",$Description)
    foreach ($tag in $Tags) { $cmdArgs += "--tag"; $cmdArgs += $tag }
    foreach ($ac in $Acceptance) { $cmdArgs += "--acceptance-criteria"; $cmdArgs += $ac }
    foreach ($ep in $EntryPoints) { $cmdArgs += "--entry-point"; $cmdArgs += $ep }
    & $python $script @cmdArgs
}

# ============================================
# 上下文压缩系统完善
# ============================================
Invoke-CreateTask -Title "上下文压缩系统完善 — 4层压缩架构设计与实现" -Priority "P2" -Stage "ready" `
    -Tags @("architecture","context","compression","LLM","ClaudeCode对比") `
    -Goal "参考Claude Code的4层压缩体系（MicroCompact/SnipCompact/AutoCompact/ReactiveCompact），完善Pudding的IHistoryCompressor接口，实现完整的上下文窗口管理" `
    -OutOfScope "V1仅实现MicroCompact（单轮摘要压缩），AutoCompact和ReactiveCompact为V2范围。不实现Claude Code的cache_edits API相关压缩" `
    -Acceptance @("MicroCompact：单轮工具调用后自动清除冗余工具结果","AutoCompact：Token使用超过阈值时触发LLM摘要压缩","ReactiveCompact：API返回prompt-too-long时紧急压缩","CompressDecision类型定义：保留/摘要/截断/清除") `
    -Impact "Source/PuddingRuntime/Services/（IHistoryCompressor实现） / Docs/07架构/03PuddingRuntime.md" `
    -EntryPoints @("Docs/claude-reviews-claude/architecture/11-compact-system.md","Docs/claude-reviews-claude/architecture/10-context-assembly.md","Source/PuddingRuntime/Services/（IHistoryCompressor相关接口）","Docs/07架构/03PuddingRuntime.md","参考OpenAI token计数：tiktoken或SharpToken库") `
    -Risk "压缩可能导致LLM丢失关键上下文，需在压缩提示中明确保留TODO/错误/关键决策。LLM摘要压缩增加API调用成本（每次压缩消耗1次API调用）" `
    -ExecType "hybrid" -HumanOwner "WangXianQiang" `
    -Description @"
## 背景

Claude Code的11-compact-system.md展示了4层上下文压缩架构——这是LLM Agent最核心的基础设施之一。关键设计：
- **MicroCompact**（每轮自动）：清除旧工具结果（FileRead/Bash等可重现结果），使用cache_edits API无成本清理
- **SnipCompact**：直接截断最旧的对话轮次
- **AutoCompact**（Token阈值触发）：fork子Agent生成对话摘要，9节结构化摘要格式
- **ReactiveCompact**（API返回prompt-too-long时紧急触发）：紧急压缩后重试

Claude Code还有**断路器保护**：连续3次自动压缩失败后停止尝试。

## 问题

当前V1中IHistoryCompressor接口已定义但实现有限，缺少：
1. Token预算监控和自动触发机制
2. 压缩后的上下文恢复（最近读取的文件、已调用技能等）
3. 压缩前后的摘要持久化（便于session恢复）

## 参考来源

- Claude Code压缩系统：Docs/claude-reviews-claude/architecture/11-compact-system.md
- Claude Code上下文装配：Docs/claude-reviews-claude/architecture/10-context-assembly.md
- OpenAI token计数：SharpToken库（https://github.com/dmitry-brazhenko/SharpToken）
- 我们的Runtime文档：Docs/07架构/03PuddingRuntime.md

## 具体修改方案

1. 扩展IHistoryCompressor接口：
   - CompressDecision Evaluate(MesssageHistory, TokenBudget) — 评估是否需要压缩及压缩级别
   - Task<CompressResult> CompactAsync(MesssageHistory, CompressDecision) — 执行压缩
2. 实现MicroCompact：清除可重现的工具结果（内容<10KB或超过3轮旧的）
3. 实现Token预算监控（在每次LLM API调用前检查）
4. 实现AutoCompact触发（Token使用>80%窗口时触发）
5. 定义CompressResult结构（压缩后历史+摘要文本+压缩统计）
"@

# ============================================
# 钩子系统设计
# ============================================
Invoke-CreateTask -Title "钩子(Hook)系统设计 — 生命周期拦截与事件驱动扩展" -Priority "P2" -Stage "ready" `
    -Tags @("architecture","hook","plugin","extensibility","ClaudeCode对比") `
    -Goal "参考Claude Code 20事件类型钩子系统，设计Pudding的轻量钩子架构：在关键生命周期节点（SessionStart/ToolPreExecute/ToolPostExecute/SessionStop）插入用户/插件自定义逻辑" `
    -OutOfScope "V1仅设计架构和核心接口，不实现完整的20事件类型。仅实现最关键的3个钩子事件：PreToolExecute/PostToolExecute/SessionStop" `
    -Acceptance @("定义IHook和IHookManager接口","定义至少5个钩子事件类型（参考Claude Code）","实现PreToolExecute钩子（可修改工具输入/拒绝执行/附加上下文）","钩子执行失败不影响主流程（fail-open）") `
    -Impact "Docs/07架构/（新增14钩子系统.md） / Source/PuddingRuntime/（新增Hooks目录）" `
    -EntryPoints @("Docs/claude-reviews-claude/architecture/05-hook-system.md","Docs/claude-reviews-claude/architecture/04-plugin-system.md（技能系统与钩子的关系）","Source/PuddingRuntime/Services/AgentExecutionService.cs（钩子注入点）") `
    -Risk "钩子执行可能引入性能开销（每个工具调用前都要检查钩子）。钩子的fail-open设计需要仔细考虑——钩子失败是静默跳过还是通知用户？" `
    -ExecType "hybrid" -HumanOwner "WangXianQiang" `
    -Description @"
## 背景

Claude Code的05-hook-system.md展示了一个5,023行的成熟钩子系统——20个事件类型覆盖几乎所有操作（PreToolUse/PostToolUse/SessionStart/SessionStop等）。最关键的PreToolUse钩子可以在工具执行前：允许（跳过权限提示）、拒绝（阻止工具）、修改输入（更改参数）、停止会话。

钩子类型包括：命令钩子（shell脚本）、HTTP钩子（webhook）、Agent钩子（子Agent）、函数钩子（进程内JS）。匹配器系统支持精确匹配（Bash(git *)）和通配（*匹配所有）。

## 问题

当前Pudding缺少可扩展的钩子机制，所有扩展都需修改核心代码。钩子系统可以：
1. 让用户自定义工具调用前后的行为（如自动格式化代码）
2. 让插件介入Agent执行流程
3. 支持审计/日志/遥测等横切关注点

## 参考来源

- Claude Code钩子系统：Docs/claude-reviews-claude/architecture/05-hook-system.md
- Claude Code插件系统：Docs/claude-reviews-claude/architecture/04-plugin-system.md
- GitHub Webhook设计：https://docs.github.com/en/webhooks
- 我们的架构文档：Docs/07架构/01总览与分层.md

## 具体修改方案

1. 新建Docs/07架构/14钩子系统.md
2. 定义核心接口：
   - IHook：ExecuteAsync(HookContext) → HookResult（Allow/Deny/Modify/AdditionalContext）
   - IHookManager：Register/Unregister/GetMatchingHooks(eventType, toolName)
3. V1实现3个关键钩子事件：
   - PreToolExecute：工具执行前拦截
   - PostToolExecute：工具执行后处理结果
   - SessionStop：会话结束清理
4. 钩子配置格式（参考Claude Code的.claude/settings.json）：
```json
{
  "hooks": {
    "PreToolExecute": [
      {"matcher": "Bash(*)", "command": "echo 'Bash called'"}
    ]
  }
}
```
"@

# ============================================
# 错误恢复与重试机制文档
# ============================================
Invoke-CreateTask -Title "LLM调用错误恢复与重试机制设计文档" -Priority "P1" -Stage "ready" `
    -Tags @("architecture","resilience","LLM","error-handling","ClaudeCode对比") `
    -Goal "参考Claude Code的withRetry引擎设计（429指数退避/529前台重试/401令牌刷新/ECONNRESET禁用keep-alive/回退模型），补充Pudding的LLM调用错误恢复文档" `
    -OutOfScope "不实现完整的回退模型切换（V2），V1仅文档化当前的错误处理逻辑并补充缺失的恢复策略" `
    -Acceptance @("定义LLM API错误的分类和恢复策略矩阵","实现429速率限制的指数退避重试","实现ECONNRESET/EPIPE的连接重置处理","实现最大输出Token不足时的自动降级（减少max_tokens重试）") `
    -Impact "Docs/07架构/03PuddingRuntime.md（新增错误恢复章节） / Source/PuddingCore/Core/OpenAiLlmGateway.cs" `
    -EntryPoints @("Docs/claude-reviews-claude/architecture/01-query-engine.md（错误恢复机制）","Docs/claude-reviews-claude/architecture/15-services-api-layer.md（重试决策矩阵）","Source/PuddingCore/Core/OpenAiLlmGateway.cs","Source/PuddingRuntime/Services/DirectLlmClient.cs") `
    -Risk "重试可能放大API负载，需谨慎设置最大重试次数和退避时间。回退模型切换需要模型池支持（依赖task-20260504-011 LLM路由）" `
    -ExecType "hybrid" -HumanOwner "WangXianQiang" `
    -Description @"
## 背景

Claude Code的15-services-api-layer.md展示了实战验证的重试决策矩阵：
| 错误类型 | 策略 | 细节 |
|----------|------|------|
| 429 | 指数退避 | 等待retry-after，快速模式冷却 |
| 529 | 最多3次重试 | 然后回退到Sonnet模型 |
| 401 | 强制刷新 | OAuth token刷新 |
| 400(prompt-too-long) | 减少max_tokens | 自动降级重试 |
| ECONNRESET/EPIPE | 禁用keep-alive | 新连接重试 |
| 非前台529 | 立即退出 | 关键反放大措施 |
| 空闲看门狗 | 45s警告/90s中止 | 监控SSE流中块间间隔 |

当前Pudding的错误处理较为基础（主要在OpenAiLlmGateway和DirectLlmClient中），缺少完整的恢复策略文档。

## 参考来源

- Claude Code重试引擎：Docs/claude-reviews-claude/architecture/15-services-api-layer.md
- Claude Code查询引擎：Docs/claude-reviews-claude/architecture/01-query-engine.md
- .NET resilience：Polly库重试策略（https://github.com/App-vNext/Polly）
- OpenAI错误码文档：https://platform.openai.com/docs/guides/error-codes

## 具体修改方案

1. 在Docs/07架构/03PuddingRuntime.md中新增「错误恢复」章节
2. 定义LLM错误分类表（类似上表，适配OpenAI协议）
3. 实现withRetry包装器（参考Claude Code的withRetry引擎）：
   - 429 → Retry-After头 + 指数退避（1s/2s/4s/8s，最多4次）
   - 5xx → 固定间隔重试（3次），然后fast-fail
   - Connection reset → 新HttpClient实例重试（禁用keep-alive）
   - Max tokens exceeded → 自动减半max_tokens重试
4. 增加空闲看门狗：SSE流超过30s无数据则记录警告，60s触发取消
"@

# ============================================
# 架构文档版本管理与索引
# ============================================
Invoke-CreateTask -Title "架构文档版本管理与交叉引用索引 — 建立文档同步规范" -Priority "P0" -Stage "ready" `
    -Tags @("architecture","doc-management","meta","ClaudeCode对比") `
    -Goal "建立架构文档的版本管理规范：每个文档标注最后更新日期/代码版本/审阅人；建立跨文档交叉引用索引，确保文档间一致性可追溯" `
    -OutOfScope "不修改现有文档内容（只加元数据）" `
    -Acceptance @("每个架构文档顶部增加元数据区：last_updated/last_commit/reviewer/status","新建Docs/07架构/README.md作为总索引（含交叉引用矩阵）","定义文档状态标记：DRAFT/REVIEWED/DEPRECATED/OUTDATED","建立文档更新checklist（代码变更→相关文档→更新→标记）") `
    -Impact "Docs/07架构/（全部12个文档的元数据 + 新建README.md）" `
    -EntryPoints @("Docs/claude-reviews-claude/README.md（架构文档管理规范）","Docs/07架构/README.md","Docs/07架构/（全部12个文档）","参考：https://www.docslikecode.com/（文档即代码实践）") `
    -Risk "文档元数据需要持续维护，建议在PR模板和CI中增加文档同步检查" `
    -ExecType "hybrid" -HumanOwner "WangXianQiang" `
    -Description @"
## 背景

Claude Code的架构分析项目（Docs/claude-reviews-claude/）展示了优秀的文档管理：18个架构文档统一编号（00-17）、README.md提供总索引、每个文档精确描述对应代码版本v2.1.88的功能。

我们的架构文档存在以下管理问题：
1. 无统一的元数据格式（部分文档有日期，部分没有）
2. 无跨文档交叉引用（如03Runtime提到Tool接口，但未链接到12多轮会话）
3. 无文档状态标记（哪些是当前准确、哪些已过时）
4. 09V1验收.md严重过时但无标记警告读者

## 参考来源

- Claude Code架构分析：Docs/claude-reviews-claude/README.md（总索引格式）
- Docs like Code：https://www.docslikecode.com/
- Kubernetes文档风格指南：https://kubernetes.io/docs/contribute/style/
- 我们的架构文档：Docs/07架构/README.md（已有但内容简单）

## 具体修改方案

1. 重写Docs/07架构/README.md 为完整的总索引：
   - 文档列表（编号/标题/状态/最后更新）
   - 交叉引用矩阵（哪个文档引用了哪个文档）
   - 阅读顺序建议（新手→深入）
2. 为每个架构文档添加统一元数据头部：
```markdown
---
last_updated: 2026-05-04
last_commit: abc1234
reviewer: WangXianQiang
status: REVIEWED
depends_on: [02PuddingCore.md, 08数据模型与配置.md]
---
```
3. 定义文档状态生命周期：DRAFT → REVIEW → REVIEWED → OUTDATED → DEPRECATED
4. 制定文档更新checklist
"@

Write-Host "Part 4 done!"
