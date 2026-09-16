# goal.md 快照规范（Agent 私有目标文件治理）

> 适用对象：`D:\data\agents\<agentId>\goal.md`（即 `goal_read` 读取的那份）
> 落定：2026-09-16 ｜ 依据：用户裁定「goal 应是简略的快照，完整性的信息应存储在外部的文件；goal.md 只存储名称、简略描述、优先级等简短的信息」

## 一、问题（有先例，非推测）

| 时间 | 体量 | 后果 |
|---|---|---|
| 2026-09-13 19:25 | 37,833 B → 首次瘦身至 9,716 B | 超 `goal_read` 16 KB 上限时**只返回尾部** |
| 2026-09-16 | 涨回 34,531 chars / 394 行 | 同上；**已造成实际需求遗失**：W4 的「start 须能派生验收条件否则 fail-closed」是在被迫整读全文时才被找回 |

结论：**没有格式约束的 append 式台账，会在 3 天内重新超限**（+256%）。只做一次性瘦身不解决问题。

## 二、白名单：goal.md 允许承载的 6 类信息

1. **主线**：一句话
2. **活跃目标**：表 —— 优先级 / 名称 / 简略描述（≤40 字）/ 状态 / 详情指针（外部文件锚点或看板卡号）
3. **待办**：每条 ≤30 字 + 优先级，按 P0→P3 排序
4. **硬约束与关键结论**：每条 ≤40 字，**只保留当前仍有效的**
5. **指针表**：用途 → 路径
6. **session_chain**

## 三、黑名单：禁止写入 goal.md（一律外部化）

执行过程叙述 · 命令与输出 · `file:line` 证据 · 验收报告 · 心跳/迭代日志 · 决策论证过程 · 已完成事项的细节 · 大段引用与重复段落

判据一句话：**每条信息必须能回答「下一轮是否需要它来做决策」**；否则不进 goal.md，只在指针表留一行。

## 四、容量纪律

| 项 | 值 |
|---|---|
| 硬上限 | 16 KB（`goal_read` 可读阈值，超限即失明） |
| 目标体量 | **≤4 KB** |
| 预警线 | >8 KB 即触发归档外置 |
| 自检 | 每次写入后 `goal_read` 必须 `truncated=false` |
| append 纪律 | `goal_update(append)` 单次 ≤800 字符，只允许追加「待办条目 / 新目标 / 指针」；更长的内容先写外部文件，再 append 一行指针 |

## 五、外部文件分层（完整信息的新家）

| 用途 | 路径 |
|---|---|
| **完整历史副本（权威）** | `memory/goals/goal-detail-<yyyyMMdd>.md`（字节级迁移，正文 Δ=0，含元信息头） |
| 历史归档 | `memory/goal-archive-<yyyyMMdd-HHmm>.md` |
| 专题（环境 / 模型路由 / 工具坑 / DB 认知） | `memory/agent-env-and-conventions.md` |
| 设计与报告 | `Docs/Features/*.md`、`Docs/Reports/*.md` |
| 索引 | `memory/INDEX.md` |

⚠️ **版本控制事实**：`.gitignore:433` 忽略 `/memory/` ⇒ 上述 `memory/` 产物**不进 git**（仅本地持久）。若某份外部信息必须随仓库版本化，请放 `Docs/`。

## 六、归档流程（标准动作，7 步）

1. 读全文：`file_read` 绝对路径 + `offset_lines`/`limit_lines` 分页（`goal_read` 超限时不可依赖）
2. 字节级外置到 `memory/goals/goal-detail-<date>.md`：**分块写入** —— 每块 ≤4000 字符、按行边界切分、首块 `file_write` 覆盖 + 后续 `append=true`；最少调用次数 = 字符数 ÷ 4000（写 10~16 次属正常，合并大块会因单次输出超限直接失败）
3. 回读核对：size / lines 与源对齐（正文部分 Δ=0）+ 抽样锚点逐字比对
4. 用 `goal_update(content_base64=...)` **整体覆盖**为快照版
5. `goal_read` 验证 `truncated=false`
6. 在 `memory/INDEX.md` 登记本次归档
7. 快照版内容必须过 §三 黑名单自检

## 七、已知事实（避免重复踩坑）

- `goal_update(append)` 与 `goal_read` 指向**同一文件**（2026-09-16 实证：append 后 `goal_read` 尾部可见该段）。历史笔记中「两者不是同一文件」的结论已过时。
- `goal_read` 超 16 KB 只返回尾部 + 告警 —— **这就是丢需求的直接机制**，不是提示性问题。
- 写 `D:\data\**` 的 `file_write` 被审批依赖拦截（`approval_review_profile_not_configured`）⇒ 改该文件只能走 `goal_update`。
