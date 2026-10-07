---
title: file_patch 文本边界修复方案（2026-10-07）
author: hyfree
date: 2026-10-07
last_reviewed: 2026-10-07
status: proposed
description: "修复精确匹配拆分 CRLF，明确模糊匹配边界契约，以序列对齐替换 diff 序号比较，并建立真实工具入口回归与外部部署验收。"
categories: [docs, features]
tags: [file_patch, newline, remediation]
related_docs: [Docs/14_reports/file-patch归因复核-2026-10-07.md]
related_files: [Source/PuddingRuntime/Tools/BuiltIns/Files/FilePatchTool.cs, Source/PuddingRuntimeTests/Tools/FilePatchToolTests.cs, Source/PuddingRuntime/Tools/BuiltIns/Diagnostics/AgentDiagnosticsTool.cs, Source/PuddingRuntime/Tools/BuiltIns/Skills/AgentSkillTool.cs]
slug: features-file-patch-text-boundary-remediation-2026-10-07
draft: false
---

# file_patch 文本边界修复方案（2026-10-07）

## 问题与目标

依据[归因复核](../14_reports/file-patch归因复核-2026-10-07.md)，当前精确匹配能切开 CRLF，容忍匹配能隐式包含上一行边界，diff 按序号比较会把插入显示成级联替换。目标是让替换忠实执行明确的参数意图、避免半个 CRLF 留在原文，并使预览解释实际变更。

不采用“吞一个换行必须补一个”或“右端扩到行尾”的通用修复：前者会阻止有意删除换行，后者会删除未请求修改的尾部边界，且都未解决精确匹配留下 CR 的机制。

## P0：匹配跨度与换行契约

### 1. 将 CRLF 视为不可拆开的边界

在 `FindReplacementCandidates` 的精确路径加入候选边界检查：起点不能处于 CR 和 LF 之间，终点也不能切开 CRLF。所有匹配策略共享这一检查，不只修改 `ExpandLeadingWhitespace`。

推荐匹配顺序：逐字匹配且边界合法 → **仅 EOL 等价匹配** → 原有容忍策略。仅 EOL 等价路径把 CRLF/LF 规范为逻辑换行，但保留空格、tab、标点；维护每个逻辑字符对应的原文起点与终点，CRLF 的映射长度为 2，不能复用“原字符索引 + 1”的单字符假设。生成候选后在原文坐标做 scope/重叠检查。

对于纯 CRLF 文件中 `old="\nB"`，EOL 等价应匹配完整 `\r\nB`；`new="\nC"` 按原有宿主 EOL 策略插入 `\r\nC`，不会产生 CRCRLF。若 `new="C"`，参数明确删除逻辑换行，应允许得到 `AC`；调用方若想保留前导换行必须在 new 中保留。返回结果说明实际采用 EOL 等价策略与替换跨度。

混合 EOL、单独 CR、无 EOL 文件先固定当前策略并测试；对无法安全映射的候选返回边界错误且不写盘，不自动重写全文件 EOL。不能以重编码/全局替换掩盖局部损坏。

### 2. 模糊匹配不隐式删除结构边界

保持 D2 的同一行缩进修复，分别记录内容跨度、扩展缩进和前后 EOL。容忍策略若需要跨行扩大边界，或 old/new 的前导/尾随逻辑换行形态不同，默认拒绝写入，返回 `ambiguous_boundary_change`，要求调用方提供可 EOL 等价精确命中的完整片段。正常无边界变化的缩进容忍替换继续工作。

规范化映射还应核对 old 的尾部空白/换行，而不是只恢复前导：末字符后存在行尾换行不能被静默忽略；无法对应时拒绝。多空白、空白-only 片段、多个候选和跨行容忍必须有确定结果或明确失败。

所有操作先形成候选变更并验证，再提交文件；dry_run 与实际写入使用相同候选内容和检查。replace_all 每个跨度都检查，不绕过告警或结构拒绝。

### 3. 避免把 warning 当成功验收

结果应区分已应用数量、失败操作、采用策略、结构边界提示；警告不放进模糊的 errors 字符串后让消费者自行猜测。优先沿既有 ToolExecutionResult 结构扩展，避免新建平行结果协议。测试固定混合成功/失败操作的既有语义；若要改变事务政策，另立任务，不能随本修复悄然改变。

## P1：可信 diff 与坐标修复

两份 `GenerateSimpleDiff` 复用同一纯函数：按逻辑行序列做 Myers 或现有成熟序列差分，保留上下文与旧/新行号；一行插入只报一行新增，不将后续行报成替换。展示上限截断按 hunk，并明确剩余数量。孤立 CR、EOL-only 差异、文件末尾无换行单独标注。

顺带单列一个坐标缺陷任务：`CollectReplacements` 的 scope 用规范化后的行长度计算索引，却对原始 CRLF 字符串过滤；应按原文实际换行长度生成行起止映射。该问题由源码可见，本次没有另行复现，不将其写作已实测事故原因。

## 回归矩阵与完成标准

在 `Source/PuddingRuntimeTests/Tools/FilePatchToolTests.cs` 经真实 ExecuteAsync 入口测试；隔离文件放系统 Temp 或 `temp/test-out`，不使用产品 DataRoot。

| 维度 | 必需案例 / 判据 |
|---|---|
| 精确 / EOL 等价 | CRLF 文件、old 前导 LF / CRLF；new 有/无前导换行；完整期望字节相等；无新生孤立 CR、CRCRLF |
| 用户意图 | 精确删除整段换行仍可成功；保留换行必须保留；不能以总行数不变作为万能断言 |
| 容忍 | 缩进差异 D2 保持；前导/尾随边界歧义拒绝且文件字节不变；跨行片段、空白-only、重复候选 |
| 文件格式 | LF、CRLF、混合 EOL、已有孤立 CR、BOM、Unicode、无末尾换行；未触及字节保持原状 |
| 多操作 | replace_all、重叠、scope 起止、多个文件、dry_run 与应用内容一致；记录现有部分失败语义 |
| diff | 中部插入/删除、重复行、多 hunk、EOL-only；序列对齐且不丢后续变更 |
| 旧功能 | D1/D2/D3 及既有 regex/行操作/unified patch 测试继续通过 |

“先红后绿”限于真实缺陷断言：旧实现应在半 CRLF/CRCRLF、模糊边界拒绝和 diff 对齐上失败。**不能要求所有 T 组行合并都取红**，显式删除完整换行是合法对照。修复后用完整字节期望验收，计数只作辅助。

## P2：其他工具与工作流

| 工作项 | 实施建议 | 验收 |
|---|---|---|
| 工具统计普查 | tool_stats 增加明确 all 模式、分页和稳定排序；单工具契约保留 | 多页不重不漏，计数与明细一致 |
| skill 参数失败 | 保存脱敏 action/参数、schema 版本、绑定结果、失败分类；先查调用层再查平台 | list/get_index 无 id 可用；get/read_file 无 id 明确参数错误，子代理可反馈或恢复 |
| 运行身份 | 诊断返回进程 PID/启动时间、实际程序集路径、MVID/InformationalVersion/构建哈希 | 外部重启后与目标产物一致；不回显 Token/Secret |
| 写后验收 | 工具记录写入前后内容 hash；需要时读回核对实际写入字节；Agent 再做结构验收 | 执行成功、字节写入正确、用户目标完成分别记录；读回一致不能代替语义验收 |
| 报告脚本 | UTF-8 明确编码、唯一锚点、表格列数断言、TRX 解析；并发证据绑定提交/hash/采集时间 | 不凭日志关键词或子代理自述定通过 |
| 历史损坏 | Git/备份与当前内容比较，以差异清单人工审阅后恢复 | 禁止全局替换孤立 CR 或自动重写历史文件 |

## 交付顺序与部署验收

1. 真实工具入口固化缺陷，再实现 P0；通过 Runtime 定向测试与 D1/D2/D3 回归，更新 Runtime code_map 受影响条目，独立提交与日志。
2. 独立完成 diff 与 scope 任务，各自测试、提交；P2 分开交付。若抽出独立组件，按仓库 S1–S5 先组件内测试后宿主接入。
3. 构建输出限 `temp/build`，测试结果限 `temp/test-out`；完成后清理本任务产物。不覆盖正在运行的 Core，不碰生产数据。
4. 代码通过后报告 `ready-for-external-deploy`；外部控制器部署并重启到已核对身份的新构建。
5. 在新会话用隔离 fixture 执行真实工具 smoke、读取磁盘内容、确认换行与 diff，交付 `in-product-functional-complete`。Desktop/Core 生命周期仍由外部控制器验收。

本方案尚未实施。没有生产修复、运行实例更新或历史数据恢复结论；本次已完成的仅为六组方法级归因探针及文档门禁。
