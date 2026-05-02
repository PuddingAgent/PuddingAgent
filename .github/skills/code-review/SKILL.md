---
name: code-review
description: Use when reviewing code changes — before merging, after implementation, or during QA. Multi-perspective automated code review with confidence scoring. Triggers: code review, pull request review, QA audit, pre-merge check, compliance check.
argument-hint: "指定审查范围，例如 '审查最近3次提交' 或 '审查 Source/Pudding.Agent/Services/'"
---

# Code Review — 多视角代码审阅

## 概述

使用多个专门化审阅视角，独立审查代码变更，用置信度评分过滤误报，
确保只有高质量、可行动的反馈被采纳。

**核心原则：多视角独立审阅 + 置信度评分 = 低误报率。**

## 何时使用

**必须使用：**
- PR/MR 合入前
- 功能实现完成后的 QA 阶段
- 安全敏感代码变更（配合 `@security-reviewer`）

**可选：**
- 涉及关键代码路径的变更
- 来自多人的变更
- 规范合规性重要的地方

**跳过：**
- 已关闭/已废弃的代码
- 纯配置/文案变更
- 已有 QA 审阅且无新增变更的代码

## 审阅流程

```
1. 确认审阅范围 → 2. 收集规范文档 → 3. 变更摘要 → 4. 多视角并行审阅 → 5. 置信度评分 → 6. 过滤（≥80）→ 7. 输出报告
```

### 步骤 1: 确认范围

```bash
# 确认要审查的变更范围：
# 1. 最近 N 次提交
git log --oneline -5
# 2. 或上次审阅以来的变更
git diff HEAD~3 --stat
# 3. 或指定文件
git diff --stat -- Source/Pudding.Agent/
```

### 步骤 2: 收集规范文档

读取相关规范文档（供审阅 Agent 参考）：
- `Docs/架构.md` — 编码规范和流程
- `Doc/Map.yaml` — 架构约束
- `Doc/Memory/Self-reflection.md` — 历史踩坑
- 相关设计文档（`Doc/设计文档/`）
- `.github/copilot-instructions.md` — 项目规则

### 步骤 3: 变更摘要

用 Haiku 级 Agent 生成变更摘要，供审阅 Agent 理解上下文。

### 步骤 4: 多视角并行审阅

启动 **4 个独立审阅视角**：

| 视角 | 关注点 | 模型 |
|------|--------|------|
| **规范合规 (#1)** | 对照 CLAUDE.md / 项目规范 | Standard |
| **Bug 扫描 (#2)** | 变更中的明显缺陷和边界情况 | Standard |
| **历史上下文 (#3)** | git blame 历史中的相关问题 | Standard |
| **架构一致性 (#4)** | 分层、依赖方向、模块边界 | Capable |

### 步骤 5: 置信度评分

对每个发现的问题评分 0-100：

| 分数 | 含义 |
|------|------|
| 0 | 不自信：误报，经不起推敲 |
| 25 | 有些疑虑：可能是真问题，也可能是误报 |
| 50 | 中等：验证过是真问题，但较小/不太常见 |
| 75 | 高度自信：双重检查，很可能是实践中的真实问题 |
| 100 | 完全确定：确认是真实问题，会频繁出现 |

### 步骤 6: 过滤

**仅保留 ≥ 80 分的问题。** 低于 80 的不输出。

### 步骤 7: 输出报告

```markdown
## Code Review — <变更摘要>

发现 N 个问题：

1. **<简述>**（规范来源：<CLAUDE.md/架构文档>）
   `<文件路径>#L<起始行>-L<结束行>`
   严重程度：<Critical/Important/Minor>
   建议：<修复建议>

2. **<简述>**（Bug：<原因>）
   `<文件路径>#L<起始行>-L<结束行>`
   ...
```

## 误报排除清单

以下情况**不作为问题**报告：
- 已存在的问题（非本次 PR 引入）
- 看起来像 bug 但实际不是
- 吹毛求疵的细节（资深工程师不会提的）
- Linter/编译器能自动发现的（缺少 import、类型错误、格式问题）
- 通用代码质量问题（除非 CLAUDE.md 明确要求）
- CLAUDE.md 有要求但代码中有 `// lint ignore` 注释
- 功能变更很可能是故意的，与整体变更直接相关

## Pudding 项目特定审阅规则

### 架构分层检查

```
UI → Controller → Runtime → Core（SQLite）
```

- Controller 层不直接访问数据库
- Runtime 层不处理 HTTP 路由
- P2P 通信封装在独立模块### 熟知的坑

- SQLite 需 WAL 模式，避免并发写锁
- P2P 发现依赖网络环境，Docker 需特殊配置
- 前端 SPA 路由需回退到 index.html
- CancellationToken 必须正确传递### 异常处理检查

- 被吞掉的异常必须有 `ILogger` 日志
- 传播的异常不应该重复记录

## 配合 QA 工作流

审阅结果记录到任务卡：

```bash
# 提交审阅结果
python todo-api/todo_api.py qa <task-id> --file qa-result.json

# 或快捷操作
python todo-api/todo_api.py qa-approve <task-id>   # 无问题
python todo-api/todo_api.py qa-reject <task-id>     # 有问题
```

## 注意事项

- 不要检查构建信号或尝试构建/类型检查（CI 会单独运行）
- 使用 `todo-api` 记录审阅结果，不用 GitHub PR comment
- 审阅聚焦在变更代码上，不要扩散到未修改的文件
- 对于安全敏感审阅，必须并行拉 `@security-reviewer`
