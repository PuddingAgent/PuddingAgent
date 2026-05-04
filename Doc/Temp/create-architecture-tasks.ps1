# 批量创建架构改进任务卡
$ErrorActionPreference = "Stop"
$python = "python"
$script = ".github/skills/todo-api/todo_api.py"

function Invoke-CreateTask {
    param($Title, $Priority, $Stage, $Tags, $Goal, $OutOfScope, $Acceptance, $Impact, $EntryPoints, $Risk, $ExecType, $HumanOwner, $Description)
    
    $cmdArgs = @(
        "create",
        "--title", $Title,
        "--project", "Pudding",
        "--task-owner", "WangXianQiang",
        "--priority", $Priority,
        "--stage", $Stage,
        "--goal", $Goal,
        "--out-of-scope", $OutOfScope,
        "--impact-scope", $Impact,
        "--risk-notes", $Risk,
        "--executor-type", $ExecType,
        "--human-owner", $HumanOwner,
        "--description", $Description
    )
    
    foreach ($tag in $Tags) {
        $cmdArgs += "--tag"
        $cmdArgs += $tag
    }
    foreach ($ac in $Acceptance) {
        $cmdArgs += "--acceptance-criteria"
        $cmdArgs += $ac
    }
    foreach ($ep in $EntryPoints) {
        $cmdArgs += "--entry-point"
        $cmdArgs += $ep
    }
    
    & $python $script @cmdArgs
}

# ============================================
# Task 021: 工作流文档与实现对齐
# ============================================
Invoke-CreateTask `
    -Title "工作流文档与代码实现对齐 — 11工作流与任务图.md 重写" `
    -Priority "P0" `
    -Stage "ready" `
    -Tags @("architecture", "doc-sync", "ClaudeCode对比", "workflow") `
    -Goal "解决Docs/07架构/11工作流与任务图.md声明Workflow为V3远期特性，但实际L5自动化层（Cron调度器+Workflow CRUD）已实现（task-20260502-028）的矛盾" `
    -OutOfScope "不新增Workflow功能，仅修正文档描述" `
    -Acceptance @(
        "文档中Workflow/TaskMap/DAG的描述与实际代码状态一致",
        "L5自动化层已实现的功能在文档中有明确说明",
        "V1/V2/V3各阶段的工作流能力边界清晰标注",
        "废弃声明或未来方向标注[DEPRECATED]或[PLANNED]"
    ) `
    -Impact "Docs/07架构/11工作流与任务图.md" `
    -EntryPoints @(
        "Docs/07架构/11工作流与任务图.md",
        "Source/PuddingAgent/Services/CronSchedulerService.cs",
        "Docs/Tasks/（查找Workflow相关任务卡）"
    ) `
    -Risk "需先确认CronSchedulerService和Workflow CRUD的实际实现范围，避免文档过度承诺" `
    -ExecType "hybrid" `
    -HumanOwner "WangXianQiang" `
    -Description @"
## 背景

Claude Code架构分析报告（Docs/claude-reviews-claude/architecture/）展示了一个极其严谨的文档管理范式——每个子系统的文档都与代码版本严格对齐。我们的Docs/07架构/11工作流与任务图.md 声明 Workflow/TaskMap/DAG 为 V3 远期特性，但实际代码中 L5 自动化层已实现 CronSchedulerService 和 Workflow CRUD（参见 task-20260502-028）。

## 问题

架构文档与实际实现存在严重矛盾：
- 文档说：V1范围仅限「单轮对话、多轮对话、工具调用」
- 实际存在：CronSchedulerService、Workflow CRUD API、任务图数据结构
- 这导致新成员阅读文档后对项目能力产生错误认知

## 参考来源

- Claude Code架构分析：Docs/claude-reviews-claude/architecture/00-overview.md（「12步状态机」精确描述，无模糊空间）
- Claude Code架构分析：Docs/claude-reviews-claude/architecture/03-coordinator.md（「协调器模式」文档与实现一一对应）
- 我们的架构文档：Docs/07架构/11工作流与任务图.md
- 实际实现：Source/PuddingAgent/Services/CronSchedulerService.cs

## 具体修改方案

1. 移除文档中「V1范围仅限单轮/多轮对话/工具调用」的错误声明
2. 新增「V1已实现」章节，列举当前工作流相关功能：
   - CronSchedulerService：定时任务调度
   - Workflow CRUD：工作流定义的管理API
   - TaskGraph：任务图基本数据模型
3. 将原「V3远期特性」改为「V2/V3规划」，标注[DEPRECATED]或[PLANNED]
4. 参考Claude Code文档风格，为每个已实现功能标注代码入口文件和关键类名
"@

# ============================================
# Task 022: PuddingCore 抽象接口文档补全
# ============================================
Invoke-CreateTask `
    -Title "PuddingCore 抽象接口文档补全 — 从10行到完整契约" `
    -Priority "P0" `
    -Stage "ready" `
    -Tags @("architecture", "doc-sync", "ClaudeCode对比", "Core") `
    -Goal "将Docs/07架构/02PuddingCore.md从约10行简要描述扩展为完整的接口契约文档，列出所有关键抽象接口及其约束条件" `
    -OutOfScope "不修改任何接口定义（接口本身正确），仅补充文档" `
    -Acceptance @(
        "列出所有关键接口：ILlmGateway/ITool/ISkill/IMemory/ISession等",
        "每个接口标注方法签名、参数约束、返回值语义",
        "标注接口间的依赖关系和调用顺序约束",
        "标注哪些接口是同步/异步、线程安全要求"
    ) `
    -Impact "Docs/07架构/02PuddingCore.md" `
    -EntryPoints @(
        "Docs/07架构/02PuddingCore.md",
        "Source/PuddingCore/Abstractions/（所有接口定义）",
        "Docs/07架构/12多轮会话与工具调用执行.md（Tool接口fail-closed设计已有详细文档）"
    ) `
    -Risk "Core层接口可能在持续演化中，文档需建立同步更新机制（每次接口变更同步更新文档）" `
    -ExecType "hybrid" `
    -HumanOwner "WangXianQiang" `
    -Description @"
## 背景

Claude Code架构分析中，02-tool-system.md 对工具接口的描述极其精确——30+方法的统一接口，每个方法的签名、默认值、fail-closed设计原理都有详细说明。相比之下，我们的Docs/07架构/02PuddingCore.md 仅有约10行实质内容，仅声明「Core是纯抽象」「不持有运行状态」，但未列出任何具体接口定义。

## 问题

PuddingCore 是所有模块（Runtime/Controller/Platform）的依赖基础。如果Core层的接口契约不明确：
- 新开发者不知道有哪些抽象可以依赖
- 实现者不知道接口的约束条件（线程安全？可空性？）
- 接口变更时没有文档可以对照评估影响范围

## 参考来源

- Claude Code架构分析：Docs/claude-reviews-claude/architecture/02-tool-system.md（30+方法接口的精确文档）
- Claude Code架构分析：Docs/claude-reviews-claude/architecture/01-query-engine.md（接口生命周期描述）
- 我们的已有优质文档：Docs/07架构/12多轮会话与工具调用执行.md（Tool接口fail-closed设计文档，是Core文档的标杆）
- 代码实现：Source/PuddingCore/Abstractions/（ILlmGateway.cs/ITool.cs/ISkill.cs/IMemory.cs等）

## 具体修改方案

1. 在文档开头增加「接口索引表」，列出所有接口及其所在文件
2. 为每个关键接口编写契约卡片：
   - 方法签名（含参数和返回值）
   - 前置条件（调用前必须满足的状态）
   - 后置条件（调用后的状态变化）
   - 线程安全要求
   - 异常约定
3. 参考12多轮会话与工具调用执行.md 的风格，对Tool接口的fail-closed设计做交叉引用
4. 增加「接口演化规范」小节：如何新增/废弃/修改接口
"@

Write-Host "Tasks 021-022 created successfully!"
