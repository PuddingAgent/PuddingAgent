# 经验沉淀

## 前端功能移除：全面检查引用链

**经验**：移除前端页面/API 时，必须检查三层引用——路由配置、国际化文本、API 定义与调用方——而非仅删除页面目录。

**原因**：仅删除页面目录会留下悬空的路由条目、未使用的 i18n key 和 API 函数，导致构建缓存中残留旧引用（如 `.umi/appData.json`），增加技术债务。

**改进方法**：
1. 删除页面目录后，全局搜索页面路径、API 函数名、类型名
2. 逐一清理：路由 → i18n → API 定义 → API 调用方
3. 构建后确认 `.umi/appData.json` 等缓存文件不再包含旧引用
4. 同步更新架构文档中对该功能的描述

## Chat UI 加载态：Skeleton 优于 Spin

**经验**：聊天页面加载态应使用骨架屏（Skeleton）模拟 2-3 条气泡轮廓，而非仅使用 Spin 转圈。

**原因**：骨架屏提供空间预占和内容结构预览，减少用户感知等待时间。Spin 只告知"加载中"但无结构暗示，用户不清楚即将出现什么。3 条不同宽度（30%/70%/50%）的气泡轮廓模拟真实对话形态，体验更自然。

**改进方法**：
1. 使用 antd `Skeleton.Button` + `active` 属性实现动画骨架
2. 气泡宽度差异化（短/长/中）模拟真实对话
3. 骨架屏仅在 loading 为 true 时渲染，加载完成后由消息列表替代

## 快捷提示词需与后端 Agent 能力对齐

**经验**：欢迎引导页的快捷提示词（Quick Prompts）硬编码在前端时，可能与不同 Agent 的实际能力不匹配，导致用户发送后得到"我无法完成此任务"的回复。

**原因**：不同 Agent 的能力边界不同（如代码助手 vs 生活助手），固定提示词无法适配所有 Agent。

**改进方法**：
1. 快捷提示词应从 Agent metadata 中动态获取推荐提示词
2. 或由后端提供 `/api/agent/{id}/suggested-prompts` 端点
3. 过渡期可在前端按 Agent 类型做提示词分组

## 消息时间戳：相对时间 + 精确 Tooltip + 分隔线三层设计

**经验**：聊天消息的时间展示应采用三层设计——气泡旁相对时间（刚刚/X分钟前）、hover 精确时间 Tooltip、间隔 >5 分钟的时间分隔线——兼顾简洁与精确。

**原因**：仅精确时间太占空间且视觉嘈杂，仅相对时间在回溯时不够精确。分隔线帮助用户感知对话节奏和断点。

**改进方法**：
1. 相对时间：<1min 显示"刚刚"，<60min 显示"X分钟前"，<24h 显示"X小时前"，≥24h 显示"MM-DD HH:mm"
2. 可进一步增加「昨天 HH:mm」（diffHours < 48）提升可读性
3. 分隔线阈值 5 分钟，格式 `—— HH:mm ——`

## 消息重试机制：失败保留原文 + 重新发送按钮

**经验**：消息发送失败时应保留原文内容、显示错误视觉反馈（红色边框）、提供「重新发送」按钮，而非丢弃用户输入。

**原因**：丢弃已输入的文本迫使用户重新打字，体验极差。保留原文 + 一键重试降低恢复成本。

**改进方法**：
1. 消息 status 字段支持 `'error'` 状态
2. 失败时 catch 块保留 text，设置 status='error'
3. 仅 user 角色的 error 消息显示重试按钮
4. loading 互斥锁防止重试期间并发发送

## 临时脚本：统一放入 temp 目录

**经验**：临时脚本（Python 或其他语言）、一次性验证脚本应放在 `temp/` 目录，不得放在 `Source/` 或项目根目录。

**原因**：`Source/` 下是正式源码，根目录放构建/部署配置。临时脚本混入会污染项目结构，增加维护成本。

**改进方法**：
1. 一次性脚本直接创建在 `temp/` 下，用完可删
2. 长期使用的脚本放 `Doc/Scripts/` 并加注释说明用途

## 架构决策：先更新文档再写代码

**经验**：任何新的设计决策必须先更新 `Docs/架构.md` 和 `Docs/07架构/README.md`，再编写代码。

**原因**：直接在代码里加注释或新建 instruction 文件会导致设计分散、不可追溯。架构文档是唯一真相源，代码是实现。

**改进方法**：
1. 设计前先读架构文档确认现状
2. 设计方案先更新架构文档，评审通过后再编码
3. 代码注释只解释"为什么这样实现"，不描述架构设计

## 流式 SSE 全栈穿透：分层 Relay 模式

**经验**：SSE 流式改造应逐层 relay，每层独立管理 CancellationToken 和 SSE 帧解析，而非跨层共享流对象。

**原因**：每层都需要独立处理取消信号（用户中止 → 释放下游连接）、日志记录（标注 ws/session 标识）、以及资源清理。共享流对象会导致取消无法逐级传播，异常处理边界模糊。

**改进方法**：
1. 每层定义独立的 SSE 端点（Platform → Controller → Runtime → LLM）
2. 上游通过 `ReadSseFramesAsync` 消费下游帧，通过 `WriteSseAsync` 向上游转发
3. 每次 hop 传递独立的 `CancellationToken`，由上游 AbortController 逐级触发
4. 帧类型使用 `event:` 字段区分：`delta`（流式增量）、`usage`（Token 统计）、`error`（错误）、`done`（完成）
5. 提取公共 `SseFrameReader` 工具类统一解析逻辑，避免多处理复实现

## Token Usage 透传模式

**经验**：Token 用量数据应定义在 Contracts 层的单一 DTO，全链路逐层填充和透传，不做中途变换。

**原因**：透传避免信息丢失和格式转换错误。每层在转发前可附加本层信息（如 Runtime 补充 context window size），但不修改上游数据。前端收到最终汇总的 usage。

**改进方法**：
1. 在 `PuddingCore.Models` 定义 `TokenUsageDto`（PromptTokens / CompletionTokens / TotalTokens / ContextWindowTokens）
2. OpenAI Gateway 从 usage chunk 解析并填充前三项
3. Runtime 从 Agent template 获取 `MaxContextTokens` 并填充 ContextWindowTokens
4. 所有中间层（Controller、Platform）原样透传，不解析也不修改

## SSE 取消链路异常处理

**经验**：用户主动取消流式生成是正常行为，`OperationCanceledException` 应在每层被捕获并记录 Information 日志，非 Error 日志。finally 块用于资源清理。

**原因**：取消不是错误——记录 Error 日志会产生噪音告警。但各级资源（HttpClient、event registry、CancellationTokenSource）必须通过 finally 确保释放，无论取消还是正常完成。

**改进方法**：
1. catch `OperationCanceledException` → Information 级别日志（标注 session id），不记录 Error
2. catch 其他异常 → Error 级别日志
3. finally 块清理 registry（`_controlRegistry`、`_skillPackageRegistry` 等）
4. 前端 catch `AbortError` → 设置 status='success' 并显示"已停止生成"
5. 不 rethrow OperationCanceledException

## MSTest 比较断言参数顺序校验

**经验**：使用 `Assert.IsLessThan` / `Assert.IsGreaterThan` 这类比较断言时，必须先确认参数顺序，避免“断言逻辑正确但调用顺序反了”的假失败。

**原因**：不同测试框架的参数顺序不一致；在 MSTest 4 中，`Assert.IsLessThan` 的参数顺序并非直觉式“actual 在前”，误用会导致输出看似矛盾的失败信息。

**改进方法**：
1. 首次使用某断言前先看方法签名或 IDE 提示
2. 写性能阈值断言时，优先保留详细失败消息（包含实际耗时）
3. 断言改造后立即复跑对应测试，避免把“风格清理”变成“功能回归”

## 工具参数 schema 的两条传递路径
- **根因**：Capabilities 表 → ChatApiController.ResolveCapabilitiesAsync → ToolDefinitions 路径未包含记忆工具；回退路径 SkillRuntime.BuildDefaultParameters 返回通用 {input} schema。
- **经验/最佳实践**：新增 Agent Skill 时必须确保两条路径同步——
  1. 在 SkillRuntime.BuildDefaultParameters 中添加专用参数 schema（作为兜底）
  2. 在 Capabilities 表中注册对应的 Capability 记录
  3. 可通过 Program.cs 幂等种子或 EF Migration 完成数据库侧注册

## Capabilities 种子不在 JSON config 覆盖范围
- **根因**：JsonConfigSeedService 只种子 Providers/AgentTemplates/Workspaces，不覆盖 Capabilities 表。
- **经验**：新增工具能力时不能假设 Agent 模板配置会自动包含它。需在 Capabilities 表中显式注册。

## 文件编辑工具：同一文件修改必须串行
- **根因**：并行对同一路径执行删除/新增或补丁操作会出现路径冲突，且无法保证应用顺序。
- **经验/最佳实践**：批量工具只用于互不相关的文件；同一文件的多段修改应合并为单个 patch，或按“删除旧文件 → 创建新文件 → 校验”的顺序串行执行。
## 2026-05-18: Admin Chat 简约克制 UI 优化

### 经验：渐进式 UI 降噪——先修信任，再降噪，最后收敛入口
- **场景**：对已有 Agent Chat 页面做"简约克制"优化，不重写架构
- **正确做法**：按 P0→P1→P2→P3 严格递进，每阶段有独立验收+QA
  - P0 先修信任阻断项：清洗 `undefined`/`null`/`NaN` 泄漏 + Timeline 折叠
  - P1 再降噪视觉：关闭动画→纸面化→降饱和→无障碍
  - P2 最后收敛入口：状态栏从 10+ 图标收敛为状态胶囊+"更多"
- **错误做法**：一次性大改多个文件导致回滚困难；先做能看见的视觉效果而忽略用户信任问题
- **关键决策**：保留全部 @keyframes 定义和原有组件 import，只改触发条件和展示方式，确保回滚成本极低

## SQLite 幂等补列：先查列再 ALTER

- **根因**：直接执行 `ALTER TABLE ADD COLUMN` 再吞 `duplicate column` 异常，虽然功能幂等，但 EF Core 会先记录 Error 级 `Failed executing DbCommand`，污染部署日志。
- **经验/最佳实践**：启动期补列应先用 `PRAGMA table_info` 判断列是否存在；仅缺列时执行 ALTER。若同文件同时引用 Serilog 与 Microsoft.Extensions.Logging，辅助方法参数需使用全限定 `Microsoft.Extensions.Logging.ILogger`，避免同名类型歧义。