---
title: How-Debuge — Pudding 调试与诊断手册（主索引）
author: hyfree
date: 2026-10-02
last_reviewed: 2026-10-02
status: active
description: 先按下面的《主题索引》找到分册；时间紧时直接看 03-五分钟快速分诊。 按症状找：05-常见症状排查；按耗时找：06-延迟问题定位。 要跑最少验证集合：10-修改后的最低验收；要按层进源码：08-代码调试入口。 历史一次性案例按日期归档在 15-按日期归档的诊断记录。
categories: [docs, how-debug]
tags: [readme, how_debuge]
related_docs: [Docs/08_how_debuge/03-五分钟快速分诊.md, Docs/08_how_debuge/05-常见症状.md, Docs/08_how_debuge/06-延迟问题定位.md, Docs/08_how_debuge/10-修改后的最低验收.md, Docs/08_how_debuge/08-代码调试入口.md, Docs/08_how_debuge/15-按日期归档的诊断记录.md, Docs/08_how_debuge/01-定位与基本原则.md, Docs/08_how_debuge/02-日志位置与埋点约束.md, Docs/08_how_debuge/04-Conversation命令链路.md, Docs/08_how_debuge/07-浏览器与WebView2验收.md, Docs/08_how_debuge/09-测试诊断与门禁.md, Docs/08_how_debuge/11-案例-桌面与Core启动.md, Docs/08_how_debuge/12-案例-平台与会话.md, Docs/08_how_debuge/13-案例-浏览器Bridge与Chat.md, Docs/08_how_debuge/14-案例-调度与Token内核.md]
related_files: []
slug: how-debuge-readme
draft: false
---

# How-Debuge — Pudding 调试与诊断手册（主索引）

> **2026-10-02 用户裁定**：原根目录单文件 `How-Debuge.md`（4034 行 / 322 KB）按主题拆分为本目录下的分册，本文件是**唯一入口**。原路径 `How-Debuge.md` 已废弃，请勿再引用或新增内容到已删除的根文件。
>
> 新增调试/日志经验：写到对应主题分册；若出现新主题，在本目录新建分册并在下方索引登记。

## 使用方式

1. 先按下面的《主题索引》找到分册；时间紧时直接看 [03-五分钟快速分诊](03-五分钟快速分诊.md)。
2. 按症状找：[05-常见症状排查](05-常见症状.md)；按耗时找：[06-延迟问题定位](06-延迟问题定位.md)。
3. 要跑最少验证集合：[10-修改后的最低验收](10-修改后的最低验收.md)；要按层进源码：[08-代码调试入口](08-代码调试入口.md)。
4. 历史一次性案例按日期归档在 [15-按日期归档的诊断记录](15-按日期归档的诊断记录.md)。

## 主题索引

| 分册 | 主题 | 含章节 |
|---|---|---|
| [01-定位与基本原则.md](01-定位与基本原则.md) | 定位与基本原则 | 1. 基本原则<br>2. 先确定运行目录 |
| [02-日志位置与埋点约束.md](02-日志位置与埋点约束.md) | 日志位置与埋点约束 | 3. 日志位置<br>9. 日志埋点约束 |
| [03-五分钟快速分诊.md](03-五分钟快速分诊.md) | 五分钟快速分诊 | 4. 五分钟快速分诊 |
| [04-Conversation命令链路.md](04-Conversation命令链路.md) | Conversation 命令链路 | 5. Conversation 命令链路 |
| [05-常见症状.md](05-常见症状.md) | 常见症状排查 | 6. 常见症状 |
| [06-延迟问题定位.md](06-延迟问题定位.md) | 延迟问题定位 | 7. 延迟问题的定位 |
| [07-浏览器与WebView2验收.md](07-浏览器与WebView2验收.md) | 浏览器与 WebView2 验收 | 8. 浏览器验收 |
| [08-代码调试入口.md](08-代码调试入口.md) | 代码调试入口 | 10. 代码调试入口 |
| [09-测试诊断与门禁.md](09-测试诊断与门禁.md) | 测试诊断与门禁 | 11. 测试诊断 |
| [10-修改后的最低验收.md](10-修改后的最低验收.md) | 修改后的最低验收 | 12. 修改后的最低验收 |
| [11-案例-桌面与Core启动.md](11-案例-桌面与Core启动.md) | 案例：桌面与 Core 启动 | 11.9 Agent Benchmark 诊断<br>11.10 PuddingDesktop 发布与 Workbench 静态资源诊断<br>11.11 PuddingDesktop Storage 统计与旧日志清理诊断<br>11.12 PuddingDesktop 运行中心、单实例与自动恢复诊断<br>11.13 Phase 2A Agent Browser 与 Desktop Bridge 诊断<br>11.14 Phase 2A-2 Remote Browser 与 Agent Tools 诊断<br>11.15 Phase 2A-3 Snapshot、Locator、Interact 与 Wait 诊断<br>11.16 用户消息无输出、后台投递抢占与过期心跳执行诊断<br>11.17 Desktop 重启 Core 固定 60 秒失败，SessionChunk 回填反复从头开始 |
| [12-案例-平台与会话.md](12-案例-平台与会话.md) | 案例：平台与会话 | 11.18 platform.db 在线保留期裁剪与聊天 500<br>11.19 Chat 首屏、渐进消息与滚动性能<br>11.20 LLM 模型走错 Chat Completions / Responses / Anthropic Messages 协议<br>11.21 Storage 数据库与索引管理 API<br>11.22 Desktop 构建成功但新路由仍是空 404 / Layout PUT 被 SQLite writer 阻塞<br>11.23 Token 月度统计明显小于 Provider 官网 Usage<br>11.24 Runtime 移除 Platform 引用后的跨层编译错误<br>11.25 编排 Revision GET 正常，但 Validate / PUT 返回枚举 JSON 400<br>11.26 编排 Admin HTTP Hook 调试<br>11.27 图片生成编排的运行与诊断<br>11.28 Desktop 重编译成功后 Core 约 3 秒退出并触发恢复熔断<br>11.29 Agent Turn 显示“本轮运行失败”且日志为 SQLite Error 5/6<br>11.30 工具结果上下文膨胀与 LLM 前缀缓存诊断<br>11.31 `data/agents` 持续出现 `*-sub-*` 空目录<br>11.32 Admin Token 统计页慢、数字不更新与按日缓存诊断<br>11.33 Chat 流式 Markdown 出现原始管道文本 / 轨迹与正文分裂双状态 |
| [13-案例-浏览器Bridge与Chat.md](13-案例-浏览器Bridge与Chat.md) | 案例：浏览器 Bridge 与 Chat | 11.34 PuddingDesktop 调试模式（源码前后端 + 80 端口反向代理）<br>11.35 DeepSeek 缓存命中率日报与 miss 归因（>99% 验收）<br>11.36 Agent 可见新消息却继续执行上一轮请求<br>11.37 Agent Harness 适配、低效工具循环与首块等待诊断<br>11.39 插嘴（Steering）已受理但 Agent 没有改变方向<br>11.40 本地待发消息在 Turn 结束后长时间仍显示“排队中”<br>11.38 u1s1 Provider：模型列表正常但推理返回 403<br>11.41 长会话 Chat CPU、内存与 DOM 持续增长<br>11.42 子代理只有 Browser Tools 并连续 `browser_not_available` |
| [14-案例-调度与Token内核.md](14-案例-调度与Token内核.md) | 案例：调度与 Token 内核 | 11.43 调度/Token 执行内核的串行验证与产品态边界<br>11.44 五分钟调度器“在运行”但吞吐量仍为零<br>11.45 `refinementReady>0` 但全部 `backlog_route_changed`<br>11.46 自动 Goal 已启动但两分钟后 `evidence_incomplete`，同时 Token 账本翻倍<br>11.47 Task 已 Blocked，但五分钟调度器长期 `idle=0 / tracked=1 / stalled=1`<br>11.48 旧 task dispatch outbox 每五分钟重复发送并报 stale assignment<br>11.49 子代理耗尽百万 Token，但 WorkUnit/Goal 只显示主代理用量<br>11.50 Task Resume 后每五分钟都是 `task_goal_lost_race`<br>11.51 已结束子代理仍每 10 秒出现一次 `no_claim`<br>11.52 Codex 如何让 Desktop 加载新 Core/前端制品并取得诊断<br>11.53 最近日志同时出现 MCP 连接拒绝、HTTP TaskCanceledException 与登录 Warning<br>11.54 调度器显示“运行中”但 UI 无法管理、候选始终为 0<br>11.55 Goal 自动续行消息显示大量 `\uXXXX`<br>11.56 External Agent 消息返回 202，但迟迟没有回复 |
| [15-按日期归档的诊断记录.md](15-按日期归档的诊断记录.md) | 按日期归档的诊断记录 | 2026-09-17：GoalResume Singleton 捕获 Scoped 导致 Core 启动崩溃<br>2026-09-16：模型输出被 Agent 4096 限额截断<br>2026-09-05：综合效率审计的四个易错口径<br>2026-09-20：压缩历史动画与当前执行判别<br>2026-09-30：消息已受理却长时间没有执行<br>2026-09-30：运行中心「启动耗时」偏大（Core 启动做了什么、慢在哪）<br>2026-10-01 Chat 性能归因补充<br>11.40 Chat 长消息滚动「塌缩」与 WebView2 现场性能测量<br>2026-10-01：首 token 与会话缓存口径 |

## 最新条目（拆分前位于原文件顶部）

以下条目为拆分前 `How-Debuge.md` 顶部的 `###` 级日期记录，全文见 [15-按日期归档的诊断记录](15-按日期归档的诊断记录.md)。
- 消息骨架屏等待：真实接口与查询计划（2026-10-01）
- WinUI 图标与 XAML 启动错误（2026-10-01）
- WinUI 启动器恢复验证（2026-09-29）
- 全仓检索别用 search_grep 硬扫：2000 文件枚举上限与正确工具链（2026-09-21）
- Desktop 空闲高 CPU：日志刷新与隐藏 WebView 图像（2026-09-21）
- 工具参数重复键导致整个回合失败（2026-09-21）
- 夜间效率与自改进不能只看缓存/提交数（2026-09-21）
- 看板恢复后的去重与计划版本循环（2026-09-20）
- 心跳重启与登记（2026-09-20）
- CPU 占用：Core、Desktop 与 WebView2 分开采样（2026-09-19）
- 子代理耗时但检查器仍显示启动中与零指标（2026-09-19）
- 图片超限与续聊（2026-09-19）
- DeepSeek缓存首批优化：能力包与偏好快照（2026-09-17）
- 隔夜缓存：摘要复用、checkpoint冷首轮及台账差异（2026-09-17）
