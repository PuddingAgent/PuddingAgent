---
title: file_patch 归因复核（2026-10-07）
author: hyfree
date: 2026-10-07
last_reviewed: 2026-10-07
status: active
description: "以当前源码六组独立探针复核原报告，区分精确匹配拆分 CRLF、容忍匹配删除行边界和 diff 序号比较三条机制。"
categories: [docs, reports]
tags: [file_patch, attribution, verification]
related_docs: [Docs/14_reports/工具调用归因分析-2026-10-07.md, Docs/12_features/file-patch文本边界修复方案-2026-10-07.md]
related_files: [Source/PuddingRuntime/Tools/BuiltIns/Files/FilePatchTool.cs, Source/PuddingRuntimeTests/Tools/FilePatchToolTests.cs, Source/PuddingRuntime/Tools/BuiltIns/Skills/AgentSkillTool.cs, Source/PuddingRuntime/Tools/BuiltIns/Diagnostics/AgentDiagnosticsTool.cs]
slug: reports-file-patch-attribution-review-2026-10-07
draft: false
---

# file_patch 归因复核（2026-10-07）

## 范围与证据等级

输入为用户粘贴的自审摘要及仓库[原报告](工具调用归因分析-2026-10-07.md)。本次复核当前工作树源码，不将原报告的历史调用统计视作本次实测。

- **已验证**：从 `Source/PuddingRuntime/Tools/BuiltIns/Files/FilePatchTool.cs` 原样抽取匹配、EOL 规范化、拼接、diff 私有方法，编译为无项目依赖的 .NET 10 控制台探针，运行六组案例。没有改写被测算法。
- **源码确认**：精确匹配优先、容忍匹配前扩逻辑、两份 diff 序号比较实现、诊断与技能参数的当前契约。
- **未验证**：原事故 raw payload、历史运行中程序集、原调用统计与子代理失败事件；本次未执行完整 Tool/审计/文件写入链路，未证明线上归因同一性。

## 更正原报告的核心结论

原报告的 87 字节、孤立 CR 证据可以复现，但**对应精确匹配，不是 `ExpandLeadingWhitespace` 分支**。

`FindReplacementCandidates`（当前行 423 起）先执行 `FindLiteralMatches`，有结果立即返回。对于 CRLF 文件，`old_text="\n| target | two |"` 可逐字命中 CRLF 的 LF 半段；`FindLiteralMatches`（行 519 起）给出的跨度从 LF 开始，前面的 CR 留在原文中。替换后成为 `...one |\r| target...`。

强制空白差异后，才进入 `ExpandLeadingWhitespace`（行 468 起）；此分支删除整个 CRLF，产生无分隔符粘连，**不会由该分支本身留下孤立 CR**。两种症状都存在，修一条不能覆盖另一条。

补充：显式将完整 `\r\n` 替换为无前导换行的文本，按普通字符串替换契约本来就会合并两行。不能仅凭“行合并”判工具损坏；应区分用户有意删除边界、CRLF 被拆开，以及模糊匹配隐式扩大跨度。

## 六组控制实验

所有实验独立使用相同输入，ASCII、无 BOM、74 字节、5 个 CRLF：

```text
| a | b |\r\n|---|---|\r\n| keep | one |\r\n| target | two |\r\n| tail | three |\r\n
```

默认 new_text 为 `"| target | two |\n| ins1 | x |"`，经当前实现规范化为 CRLF。

| 组 | old_text / new_text 变化 | 实际策略 / 起点 / 长度 | 输出字节 / CRLF / 孤立 CR | 观察 |
|---|---|---|---|---|
| T-exact-LF | old 以 LF 开头 | exact / 37 / 17 | 87 / 5 / 1 | 原报告字节证据的同形复现 |
| T-fuzzy-LF | old 以 LF 开头且 target 前多一空格 | whitespace-tolerant / 36 / 18 | 86 / 5 / 0 | 前扩删除整个 CRLF，两行粘连 |
| C-no-leading | old 不含前导换行 | exact / 38 / 16 | 88 / 6 / 0 | 插入一行正确 |
| C-fuzzy-no-leading | old 无前导换行且多一空格 | whitespace-tolerant / 38 / 16 | 88 / 6 / 0 | 插入一行正确 |
| T-exact-CRLF | old 以完整 CRLF 开头 | exact / 36 / 18 | 86 / 5 / 0 | 按替换参数显式删除边界 |
| C-leading-preserved | old 以 LF 开头；new 也以 LF 开头 | exact / 37 / 17 | 89 / 6 / 1 | 残留 CR + 新 CRLF = CRCRLF；补回 LF 仍失败 |

六组孤立 LF 均为 0。起点为零基字符索引。观察结果来自 .NET 输出的 JSON 字符串与 UTF-8 字节数，不从 diff 推断。

探针生成于 `temp/build/filepatch-attribution`，执行 `dotnet run --project temp/build/filepatch-attribution/Probe.csproj --nologo`，退出码 0。生成器通过源码方法签名截取 `NormalizeEolToHost` 至 `ApplyRegexOperation` 之前的完整方法，以及第一份 `GenerateSimpleDiff`，补入两个内部 record；清理被自动审批以 `blocked by policy` 拒绝，产物暂留已忽略目录、不入提交。该探针是方法级归因证据，生产回归必须另走真实工具入口。

## diff 的独立根因

`GenerateSimpleDiff`（行 849 起及 1117 起）以 `oldLines[i]` 和 `newLines[i]` 比较，未做序列对齐。插入一行后，后续每一行都与错误位置比较，产生级联增删。CRLF 在两边都被 `TrimEnd('\r')`，因此**只改 CRLF 切行不能解决级联错位**。

C-no-leading 的实际新增只有 `| ins1 | x |`，diff 却将 tail 报成被替换，并继续对末尾空行报增删。10 处差异的截断还可能隐藏后续真实变更。换行风格差异、孤立 CR 也需要独立展示，不能被行级摘要掩盖。

## 其他问题的责任复核

| 问题 | 可支持的归因 | 证据缺口 / 处理 |
|---|---|---|
| `rfind("|")` 丢列 | 上游污染与脚本缺少结构断言共同作用 | 修复脚本应校验列数、唯一锚点、孤立 CR；修上游不能替代脚本自身门禁 |
| Python raw string 的 Unicode 转义、英文模式匹配中文摘要 | 调用方语义/locale 假设错误 | 使用实际 Unicode 路径、TRX 结构化结果；不依赖成功文本关键词 |
| UTF-16 / findstr | 编码不匹配是合理线索 | 原报告 `/U` 建议未验证，不沿用；显式读写 UTF-8，使用 Unicode 感知工具 |
| coverlet 后处理失败 | 仪器异常假设 | 未持有堆栈、版本和最小复现，不能直接判 coverlet 根因；禁覆盖率仅是隔离实验 |
| slnx 当全量项目清单 | 单点外推 | csproj 存在、解决方案登记、索引登记分别检查；未登记 slnx 不等于项目不存在 |
| tool_stats 要求 tool_name | 当前明确契约与普查需求不一致 | 属能力缺口，不能仅凭调用报错归为实现缺陷 |
| agent_skill 缺 skill_id、子代理终止 | 当前 get/read_file 等 action 明确要求 skill_id | 没有 raw action/参数与绑定证据，无法判平台缺陷；分开调查参数产生、绑定、失败终止政策 |
| hash/diff 数字不一致、瞬时崩溃 | 快照与计数口径不一致待查 | 并发写者是候选解释，未具时间线不得定责 |
| cwd 没有 Runtime DLL | 仅说明该路径无文件 | 不能推出进程从何处启动或加载何版；用实际进程与程序集身份验证 |
| 工具 success=100% | 执行成功与语义正确性不同 | 原统计未重取；建议分别记录执行状态、写后字节验证、业务验收状态 |

工具数量、私有技能数量及历史提交日期均属原报告陈述，本次不重新认证。构建时间晚于某修复提交不能证明二进制包含它。

## 修复决策

采用[文本边界修复方案](../12_features/file-patch文本边界修复方案-2026-10-07.md)。优先级为：CRLF 原子边界与匹配语义 → 模糊跨度可解释/可拒绝 → 序列对齐 diff → 诊断、技能调用与证据采集。

本次交付归因复核与施工方案，未修改生产代码、未部署、未修复历史数据。原事故责任仍须 raw payload 与加载程序集证据闭环。
