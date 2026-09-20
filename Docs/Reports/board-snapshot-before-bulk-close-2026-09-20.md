# 看板快照（批量关闭前留档）

导出时间：2026-09-20T08:09:05.287659+08:00

- 总卡数：248
- 非终态（本次将被关闭）：**173**
- 已终态：75

## 一、非终态卡（按优先级、更新时间排序）

| # | 优先级 | 状态 | task_id | 标题 | 更新时间(+08) |
|---|---|---|---|---|---|
| 1 | 0 | Blocked | `ef7d6378814e421482c41e42fcbf6e5b` | [平台][P0] Task-bound Goal 不可达：task_type=general 无执行计划（205/213 张卡）+ implementation 计划 16/16 Failed | 09-19 11:19 |
| 2 | 0 | Backlog | `9bdf9f061abc49be948bf98dc9e42ac6` | [P0][安全] 审批信任根加固：四份存储加签章 + 匹配强校验（Source/ApprovalTicketId/DefinitionHash）+ CommandContains 换 ArgumentsHash + Deny 前置 | 09-18 22:37 |
| 3 | 0 | Backlog | `ad069621eacd4d40af4a7ebf4cfbe2c9` | 接入 gpt-5.6-sol（Plan 阶段首选模型）并验证可用性 —— 配置已就位，仅待重启生效 | 09-18 20:45 |
| 4 | 0 | Ready | `b849ef8d750143fdaaa2a70d7b024a6c` | P0 G92-1 [S1] Goal 合同覆盖与纯决策入口：固化误完成负例、删除工程门禁兜底完成 | 09-18 12:15 |
| 5 | 0 | NeedsReview | `666b07e03a1649299817ca362b5883c3` | [V5] 原生视觉能力与图片预算纠偏 | 09-18 22:36 |
| 6 | 0 | Backlog | `71c11991b6fd456b93c16aab1b758df1` | P0 [G92] 等待期不得反复调用模型：pending/依赖不可用须持久化为等待并以事件唤醒 | 09-17 18:13 |
| 7 | 0 | Backlog | `600d5a9c28574fb48fce32855c867fbc` | P0 [G92] 目标合同覆盖不足（业务误完成）：显式 Task 目标须绑定权威任务并冻结验收义务 | 09-17 18:13 |
| 8 | 0 | NeedsReview | `30eb371e7527424c83107dde5bf99d94` | P0 G92-0 Goal结算修复：回合结束不等于步骤或目标完成 | 09-17 12:16 |
| 9 | 0 | Backlog | `a6a74a5aef054133902be7f1dac5ab70` | 并行子代理写隔离：worker 独立 git worktree/checkout | 09-16 23:17 |
| 10 | 0 | Backlog | `066f8c76a27649519e4993a16ce4c9e0` | 崩溃恢复链：独立会话诊断 + handoff 注入续跑 | 09-16 23:11 |
| 11 | 0 | Ready | `f1d45a1501f04b62bc25e6c2afedf8f0` | [PuddingAgent 自进化] 终端 runner/cmd-pwsh 语义怪癖与 file_patch 反序列化 bug 修复 | 09-16 19:26 |
| 12 | 0 | Backlog | `d43e61db2bb14fd0a82a63a47cada4de` | [P0][平台] 陈旧 blocker 不自愈 + 恢复后 19–108s 再闩锁：Blocked 卡长期滞留的第二根因（tgb-*/task-auto-dispatch 路径） | 09-16 15:01 |
| 13 | 0 | Blocked | `f8a9c164ea4549e0bfc6c9d80d49506f` | [A91-0][P0] 审查模型与审计Agent解耦，明确依赖等待和人工决定 | 09-15 08:36 |
| 14 | 0 | Backlog | `8cc96d6451d04b4e9a92fa52b9a2bd88` | [A91-1][P0] 统一工具执行准入：硬边界优先、操作计划与一次授权原子消费 | 09-15 07:15 |
| 15 | 0 | Backlog | `e187a8bbd2d640bb87b96fd3cf548966` | 自动审计系统修复：执行准入、行为审计与自修复闭环（ADR-091） | 09-15 07:14 |
| 16 | 0 | Backlog | `a951d3537f6e4b5ba991144a94de9030` | [P0][M01] 新Session启动减负：有界Memory索引与压力驱动压缩 | 09-15 00:11 |
| 17 | 0 | NeedsReview | `9494be33635e4717ba1271a6105aec6a` | [GLM下一步][C01] Composition 执行状态独立提交、单调修订与恢复 | 09-14 23:44 |
| 18 | 0 | InProgress | `af25d72c75634aae8579ec2c0a26ce08` | P0 Token缓存命中率 >99%：最终请求稳定、Memory启动减负与7日真实验收 | 09-14 23:44 |
| 19 | 0 | Backlog | `de9863916058485db7359a6a745efb08` | [P0][C03] 后台记忆与召回miss减量：增量、去重和按需模型调用 | 09-14 23:16 |
| 20 | 0 | Backlog | `8335911d077d4cbea3e2fcf513cc4957` | [P0][M02] 当前Memory快照：分层索引、主动写回与按需溯源 | 09-14 22:51 |
| 21 | 0 | Blocked | `3bd2a4b0ef5f4bff8f175fb7655927ad` | P0 统一 Scheduler 内核：Backlog Refinement、事件驱动候选与 Task-bound Goal 原子启动 | 09-14 03:22 |
| 22 | 0 | Ready | `e2c35d6eeae244c191ed508ccd85b6fe` | [P0][平台] Blocked 单向闩锁：TaskExecutionRepairCoordinator 只写 Blocked 不 re-arm，worker 侧永不可恢复（仅管理面可达） | 09-14 03:13 |
| 23 | 0 | Backlog | `8ee47996cc014290b90eec635b3b2705` | [V6-T2] image_reader 去"外挂化"：主模型原生读图为默认与唯一主路径 | 09-12 22:44 |
| 24 | 0 | Backlog | `a2849320b7be4f7fbe414d034c9567b5` | [V6-T1] P0 BUG：主模型支持视觉却报 capability_mismatch —— 原生读图自检与修复 | 09-12 22:44 |
| 25 | 0 | Backlog | `a8e5340ebb9f421bac7c8b3babb768bc` | [P0][C02] 最终 Provider 请求 Manifest、首轮占用与缓存失效归因 | 09-12 18:07 |
| 26 | 0 | NeedsReview | `791d062fa6ea44f18bfe5027a37696d0` | [P0][SA-TRACE] 修复Web子代理轨迹不显示：事件源、快照与SSE回放 | 09-13 00:22 |
| 27 | 0 | Backlog | `183b8f736fbf44e18c8d488d57568af5` | [P0 fix-forward] Turn terminal 前置提交与潜意识记忆 outbox 解耦 | 09-04 21:22 |
| 28 | 0 | Backlog | `3f987cef736c4b5f8caf3b44337243a9` | [P0-S1b] 重复 Tool 参数键 fail-closed，不再击穿整轮 Turn | 09-04 21:21 |
| 29 | 0 | Backlog | `ca80f48dff4d4db39e27feb5ae29edec` | [P0-S1a] Execution monitor fault 隔离、续租 fail-closed 与 lease 风暴回归 | 09-04 21:21 |
| 30 | 0 | Backlog | `99fc3747873f4a70b18685ffca01bfd1` | [ADR-076][S1/P0] 存储采样与维护作业安全、正确性、耐久性收口 | 09-02 10:47 |
| 31 | 0 | Backlog | `57809f837b23453eaad3f44473ebd6e6` | [ADR-076][S0/P0] Web 存储管理恢复可用：Host DI、失败态与 API smoke | 09-02 10:46 |
| 32 | 0 | Backlog | `6f49d33e900c4e7e960c630fa7d7c2fb` | 统一任务调度器：夜间吞吐端到端治理（总任务） | 09-01 16:02 |
| 33 | 0 | Backlog | `c7dc8f5d490e4a38ab47b5a5a71b69e1` | P0 Scheduler 真实自动派发验收：预算硬门禁与 canonical E2E 证据 | 09-01 15:58 |
| 34 | 1 | Backlog | `483eb9f8e4524a78a23c50935e129411` | [RSI-P1] 评估闸门（evaluator harness）：可重放评测集 + 评分器 + 退化判定 | 09-19 19:48 |
| 35 | 1 | Backlog | `acd22542b30c487596ee25c3d14f820d` | [前端][P1] chat bundle 预算仅余 ~6KB：按脚本已登记正解把 GoalStepsPanel 改 React.lazy 移出首屏 chunk（S2/S3/S4 的前置阻塞） | 09-19 12:57 |
| 36 | 1 | Backlog | `9e7b67a39c0c48eba82ab573e80bc542` | YOLO 运行模式未持久化：重启后静默回落 Normal 导致隐式自动审批失效 | 09-19 10:57 |
| 37 | 1 | Backlog | `4bc6d50b18494093a64ed7865efeb072` | [平台][P1] file_search 的 provider 被解析为 "auto-select" 导致子代理整轮失败并触发会话 fuse（父级无恢复通道） | 09-18 21:25 |
| 38 | 1 | Backlog | `b03caf63bd234c998db552acfb20abc5` | [平台][P1] fastrouter 要求流式，但平台存在非流式调用路径导致 BadRequest non_stream_not_allowed（gpt-6-astra 作为规划子代理不可靠） | 09-18 18:41 |
| 39 | 1 | NeedsReview | `8c559bf06a6240eb98e9bdb73e558e4d` | [P1][PLAT-T2b] SubAgentTool.cs 其余 37 处代码级乱码修复（按行定位；禁止整文件恢复/CRLF 脚本） | 09-18 21:29 |
| 40 | 1 | Backlog | `516a19e65ff8441f88385710a8a26c1f` | P1 [平台] 审批依赖阻塞代理私有状态维护（file_write 工作区外）＋委派失败零产出放大 | 09-17 18:55 |
| 41 | 1 | Backlog | `d356c701f36e499b86a7c50d1d5c7c94` | 平台门禁缺陷：terminal_start/shell/file_patch 间歇报 approval_review_profile_not_configured（执行类工具整体不可用） | 09-17 05:56 |
| 42 | 1 | Backlog | `286798e5eb2644e6b13abf50ec0e198f` | Core 全量套件红灯：Swarm null 输入 NRE（产品缺陷）+ 多处测试断言参数写反 | 09-17 05:12 |
| 43 | 1 | Backlog | `4ea88548d77640108f001980e2ef01ef` | 契约冲突：冻结合同 v1 将「子任务树」列为范围外，而看板母/子层级已按用户指示实现并推送 | 09-17 01:29 |
| 44 | 1 | Backlog | `122a3f2e3f124a9984458e6c26e6fd21` | [P1][工具链] code_outline 相对路径解析与 file_read 不一致：子代理因「文件不存在」连续失败致任务被误判 failed | 09-16 23:55 |
| 45 | 1 | Backlog | `140ae7fd43074f98af37ec28218fa90a` | [Goal] 检查记录 epoch 口径不一致：查询侧用 goal.ActivationEpoch、结算侧用 iteration.ActivationEpoch，导致查询面看不到检查记录 | 09-16 23:20 |
| 46 | 1 | Backlog | `e53f22c1ac81496baef8183796385f0f` | [Goal] 无绑定 Plan 的 Goal：检查 inputFingerprint 退化为 epoch:objective，同 epoch 内检查结果永久复用 | 09-16 23:20 |
| 47 | 1 | Backlog | `a6f5c8404c724fed829208d8f8718dca` | 基线固化与 pre-existing failure 区分（回归门禁） | 09-16 23:17 |
| 48 | 1 | Backlog | `384731a5173f4cd2b182dbfeeba9ba79` | 平台缺陷：子代理派发默认模型路由解析失败并静默回退 | 09-16 23:12 |
| 49 | 1 | Backlog | `cff9a40fd0ea4314a083b9b9d5daabdb` | 共享工具服务：消除并发下的 LSP/索引重复实例 | 09-16 23:12 |
| 50 | 1 | Backlog | `740f27e3191c46d4875795196d052f3e` | 不可动契约清单 + 自动对拍（tool schema / 公开 API / CLI 输出） | 09-16 23:12 |
| 51 | 1 | Backlog | `2a92b3edb69f45c588c1c27cf6cc8eeb` | [BOARD] 卡状态机不允许 Backlog 直达 NeedsReview/Completed，导致已交付卡长期滞留 Backlog（V6-T4 实例） | 09-16 15:07 |
| 52 | 1 | Backlog | `f0cf2e1e0f4d43bb944a2ab843054c3c` | Goal 迭代缺"上轮裁决回灌"：payload 不带 verdict/blocker，且 agent 无只读查询通道 | 09-16 14:43 |
| 53 | 1 | Backlog | `4109e9a5368e4b5bb58090954dafa8d8` | [P1] Goal 门禁结构性修复：环境性/预存失败不得卡死所有 Goal（13 个失败已修复并验证，见 commit 425cdc8） | 09-16 00:17 |
| 54 | 1 | Backlog | `fe099b22622648439e81f149a6dd0153` | [P1] Goal 验收合同是 generic：不读 objective，任何目标的"完成"都等于"工程 build+test 绿" | 09-15 23:22 |
| 55 | 1 | Backlog | `133711b4d1e8428e9df6f0c9797ba4ce` | [P1] 高危工具被不可解的"依赖等待"永久阻塞：ToolApproval:Reviewer=llm 但未配置 ToolApproval:Llm 评审模型档案 | 09-15 23:21 |
| 56 | 1 | Backlog | `fb46400c57d148b1a6c91d03295cbb44` | [A91-4][P1] 审计服务端状态统一Web与无人值守，完成72小时及7天验收 | 09-15 07:15 |
| 57 | 1 | Backlog | `4fc21bf91e784628936f74efdac906a3` | [A91-3][P1] 接通审计问题到自修复：持久唤醒、去重派单与外部验收 | 09-15 07:15 |
| 58 | 1 | Backlog | `8176891893c74dfc9d69f0451bc9332c` | [A91-2][P1] 建立增量Agent行为审计：事实游标、规则、Finding与证据 | 09-15 07:15 |
| 59 | 1 | Backlog | `bfe2286047da49ec969c11717c46a81c` | [P1] 修复记忆归档的 Agent 身份与 DataRoot 归属 | 09-14 22:51 |
| 60 | 1 | Blocked | `8df6a4d6d66d4c0cb6b02e838ea2e6c3` | P1 ExecutionPlan/WorkUnit：弹性预算、持久等待与系统自动续行 | 09-14 21:27 |
| 61 | 1 | Backlog | `b74e561e5299479e9afdd5123f26490d` | ADR-089｜PuddingAgent 统一检索与渐进展开工具链（总任务） | 09-13 12:35 |
| 62 | 1 | Backlog | `2d5bc2c901f249cba585b0a85c883465` | ADR-089 U5｜统一检索端到端评测与新构建产品验收 | 09-13 11:06 |
| 63 | 1 | Backlog | `8bddf9017b2d40049f8ea88823c2078a` | ADR-089 U3｜后台异步索引维护、文件监听与失效清理 | 09-13 11:06 |
| 64 | 1 | Backlog | `418495ede7ed4210b8e53cd143ee83cf` | ADR-089 U4｜收敛旧工具、模板权限与 SmartWorkflow | 09-13 10:53 |
| 65 | 1 | Backlog | `52ac97d8231e40a59d2fa23214ee4dfb` | ADR-089 U2｜实现 workspace_open 与精确结果引用 | 09-13 10:53 |
| 66 | 1 | Backlog | `205a678a7db14d189dc887c74e877315` | ADR-089 U1｜实现 workspace_search 联合检索入口 | 09-13 10:53 |
| 67 | 1 | Backlog | `0a32b57b43ec48228c615a16abc1b5ad` | Scheduler §7.3：Goodput 状态 API 从 task_scheduler_scan_runs 表投影（重启不丢最后一轮） | 09-13 09:43 |
| 68 | 1 | NeedsReview | `813ad427c0d54fd6a67e9bd39b03d4c4` | [平台] Blocked+active assignment 的卡对其所属 Task-bound Goal 不可上报：task_update 报 task.active_context_missing | 09-14 01:01 |
| 69 | 1 | NeedsReview | `479bab22e9fe431e8217f72eb4f6329f` | ADR-089 U0｜统一检索合同、匹配语义与正确性基线 | 09-13 13:44 |
| 70 | 1 | Backlog | `c1ecfe403e7b47f69f720128ea0f4d3f` | V6-T3 部署缺口：运行中 PuddingAgent bin 缺 SkiaSharp 托管程序集（图像变换运行时不可用） | 09-13 02:06 |
| 71 | 1 | Backlog | `8ae6e073d3fb4b7983086398e9ac2de0` | [V6-T8b] 纯图片用户消息仍可能不渲染：buildMessageBlocks 文本门槛 + modality 由 inputMode 推导 | 09-12 23:54 |
| 72 | 1 | Backlog | `70700a1fc70f4787ac430232e9ceaafc` | [FAV-T1] 右键菜单新增「加入收藏」并与「加入记忆」打通（用户侧收藏库 + Agent 可反查） | 09-12 23:36 |
| 73 | 1 | Backlog | `7b73661fd3cd4418971447a363f1a62d` | [PLAT-T1] 孤儿 testhost 持文件锁 → 后续所有构建 MSB3027；且进程终止被策略硬禁，无受控回收通道 | 09-12 22:51 |
| 74 | 1 | Backlog | `c3e51f67f6c641cfa8e0c02dd42342c3` | [V6-T5] Web：上传图片→点击编辑/涂鸦→随消息一起发送 | 09-12 22:44 |
| 75 | 1 | Backlog | `1a224e79ddbd47b0b1353d395f47257b` | [V6-T3] image_reader 增强：放大/缩小/裁剪/旋转 + 高级图片操作，供主模型精确理解 | 09-12 22:44 |
| 76 | 1 | Backlog | `f752a6dd2aab423d86798cb783e164c2` | [V4] 当前原生视觉与 Web/Desktop 截图真实验收 | 09-12 19:48 |
| 77 | 1 | Backlog | `4df6d9d731ab45dba3a08dcec1f5043e` | [V10] 用户附件与主子代理视觉轨迹统一展示 | 09-12 19:47 |
| 78 | 1 | Backlog | `96bd3e3369484ea88f09488a959ca2e8` | [V9] Desktop 通用截图能力与无人值守采集边界 | 09-12 19:47 |
| 79 | 1 | Backlog | `4659949dea114abbad38c192e228912e` | [V8] 补齐 WebView2 原生截图与图片型 Browser Snapshot | 09-12 19:47 |
| 80 | 1 | Backlog | `114d067a549c41e2a5d15fb82b9350e5` | [V7] 视觉制品流式读取与多模型传输策略收敛 | 09-12 19:47 |
| 81 | 1 | Backlog | `04f8ab1892f24ba2a43f305bff399054` | [P1][SA-UI] Web子代理入口与检查器：状态、轨迹、插嘴、答复、停止 | 09-12 18:24 |
| 82 | 1 | Backlog | `2167bd7f323f496e98f1c39197bab73c` | [P1][SA-ASK] ask_question：子询父、阻塞120秒与持久等待复用 | 09-12 18:24 |
| 83 | 1 | Backlog | `0a6b012b59e64c5dbf5be034a52496c1` | [P1][SA-MSG] 主子双向send_message、Run插嘴与尽快停止 | 09-12 18:24 |
| 84 | 1 | Backlog | `3d30f71b9da340448664724eda911bfe` | [P1][SA-OBS] 共享子Run快照：已用预算、分项Tokens与最近行为查询 | 09-12 18:24 |
| 85 | 1 | Backlog | `0114731d95e343faa23b975b459f5533` | [长程自治验收] 72小时、7日及30日：无Web运行、弹性子代理与持久Memory | 09-12 18:24 |
| 86 | 1 | Backlog | `1473dd05d79c4e9987ee16adcdff4c95` | [DY-01] 抖音只读 Adapter：复用 WebView2 读取作品与评论 | 09-12 18:11 |
| 87 | 1 | Backlog | `4fa25304b48243f6bbe2e5d954594243` | [DY-00] WebView2 真实 Agent 浏览器前置验收（复用现有七工具） | 09-12 18:11 |
| 88 | 1 | Backlog | `74a7dccca66f4e67bc17f11bdecd4802` | [P1][A09] 移除反馈与兼容补丁：canonical工具、统一审计与Memory入口 | 09-12 18:08 |
| 89 | 1 | Backlog | `0d191866028a4a89ab00f0419f39463c` | [自主改进 A08] 运行问题到源码修复、外部部署和原任务恢复的证据闭环 | 09-12 18:07 |
| 90 | 1 | Backlog | `38c449e1941e441c8ff99474e2c08de6` | Agent Harness 收缩与模型原生 Runtime：RAW 会话、显式共享记忆与端云执行内核 | 09-12 18:07 |
| 91 | 1 | NeedsReview | `3133b14961c24a34a8e0b3459f6409fd` | [平台] task_claim/task_update 反查失败归因丢失：四类根因压成同一句 active_context_missing，破坏「全部心跳可解释」 | 09-13 06:53 |
| 92 | 1 | Blocked | `06898d5dfe004c69ab6d5baf18b2674a` | P2 Scheduler 遥测降噪：五分钟汇总 schedule_skip，停止双库每两秒空轮询写入 | 09-13 02:16 |
| 93 | 1 | Blocked | `0b16740022f84b58a9532a87f1bc5509` | P1 调度控制面与 Goodput SLO：Task→Goal→WorkUnit→模型→成本全链路对账 | 09-13 01:57 |
| 94 | 1 | NeedsReview | `066c64e9a79c4ccbafb432a229c36d73` | [V6-T8] Web 前端无法正确显示用户发送的图片（降级为「图片」占位符） | 09-12 23:54 |
| 95 | 1 | Backlog | `02f85ce4f5a949ebad04278854707512` | 统一 Shell Profile 选择与主/子代理命令执行环境路由（PS7/PS5/CMD/WSL） | 09-12 06:54 |
| 96 | 1 | Blocked | `77883a50d4c8453cbd05c38ee1719f0e` | P1 Task Tracker/Watchdog：五分钟进度对账、停滞检测、重试与重规划 | 09-12 06:54 |
| 97 | 1 | Backlog | `b5ee0cd2c85844a6aca7b17df8699e56` | Chat 前端回归失败清单与基线归因（GLM 首批验收） | 09-11 21:41 |
| 98 | 1 | Backlog | `ed88185f1d3b4e16a70e9b9ea0f0e040` | Chat Composer 独立“⚡ 插嘴”按钮（当前 Turn Steering 直达） | 09-11 21:41 |
| 99 | 1 | Backlog | `deb25eb07e20471e8c5a5f70098d5d4e` | Chat 首批交互产品验收：明确新 Bundle、真实插嘴取消与阅读连续性 | 09-11 21:41 |
| 100 | 1 | Backlog | `a851a41a07f64b8ca9a08ab77272f985` | Chat 首批交互收口：提交受理边界、草稿恢复与幂等对账 | 09-11 21:41 |
| 101 | 1 | Backlog | `a2f78a033f41422a81c6aa91d5bd4bd0` | Chat 首批交互收口：停止当前执行闭环与取消范围纠正 | 09-11 21:41 |
| 102 | 1 | NeedsReview | `863193411fa2437babdb37d7f08af325` | [GLM下一步][S01-B] 请求级用量归因与迟到落账恢复 | 09-12 06:13 |
| 103 | 1 | NeedsReview | `9e007db123b04c5fb0f369e2cc126b6f` | [GLM审计][F01-R] 修复明细水合取消、失败预算与生命周期 | 09-12 06:12 |
| 104 | 1 | Backlog | `f834c902793d450bbe3bffd8ad8a849d` | P1 Core 预热后内存回升：Chat 快照与后台扫描分配归因 | 09-05 09:34 |
| 105 | 1 | Backlog | `5c6aa2e9a8d44e029bab08a710770309` | P1 短只读回合 161.749 秒事件空档：流接收、消费与落库分段诊断 | 09-05 09:34 |
| 106 | 1 | Backlog | `4b904200a4ff4bbc8303285d4f5ee720` | 第三轮：效率修复部署、进程基线与 canonical 产品验收 | 09-05 08:50 |
| 107 | 1 | Backlog | `84ee6099c128442194bc9e2eb4d7c870` | 实现 Agent 预制模板完整快照、自动填充与 DeepSeek 鲸鱼娘模板 | 09-02 11:14 |
| 108 | 1 | Backlog | `34ab3f3bcca2461ba3076074f4dc9e88` | [ADR-076][S2/P1] Rollup-first 原始遥测与上下文指标自动过期闭环 | 09-02 10:47 |
| 109 | 1 | Backlog | `6b3707d733ca45e5a509a91c3e2f3b4f` | P1 Scheduler Blocked 恢复控制面：因果证据、预检与批量 CAS | 09-01 15:53 |
| 110 | 1 | Blocked | `cec285c367324e4a90ef62dbef1b0404` | P1 阶段感知模型路由与自适应并发：质量、速度、成本、缓存、健康度联合调度 | 08-29 11:10 |
| 111 | 1 | Backlog | `d66314c1ea844214ac22a1bd0b6e25e7` | 自动点火改进：重启前强制增量编译 + DLL 新鲜度校验 | 08-27 21:34 |
| 112 | 1 | Backlog | `ceba781342aa4353901654d1897092cb` | 修复 Chat 图片消息回放丢失与前端旧 Bundle 缓存 | 08-26 19:43 |
| 113 | 1 | Backlog | `97e3eb46b2aa499d9a16abf41046f615` | 任务看板状态机、子任务、渐进披露与高性能拖拽优化（ADR-080） | 08-26 07:25 |
| 114 | 1 | Backlog | `5145da0d00ad47fcaa7a7e10d0f40fb4` | YOLO 信号文件自提权链修复（Critical，当前未激活）【安全审查 B3】 | 08-21 12:00 |
| 115 | 1 | Backlog | `54ee0c2a22ab40e1a3edd760182ec17e` | 【流程建设】多 Agent 合作协议与分工机制固化 | 08-21 07:49 |
| 116 | 1 | Backlog | `010ab28089c14c689109d3a02e44bc94` | CU-11 Chat/Composer 视觉密度规范落地 | 08-20 21:15 |
| 117 | 2 | Backlog | `f4ef27e25d6249e8a536651d7ece987e` | [平台][P2] SSE 时效语义 S3+S4：前端显式游标 + 后端无游标改 live-only | 09-19 22:11 |
| 118 | 2 | Backlog | `39ec09f4518442c0a00f61a929731596` | [平台][P2] 预压缩 .gz 可能遮蔽手工部署的前端产物：index.html.gz 滞后导致部署「看似成功但界面未更新」 | 09-19 21:27 |
| 119 | 2 | Backlog | `a0c0930b94194ef38db65af9ea6c1107` | [诊断器] SubAgentDiagnosticsReport 缺预算耗尽计数，subagent.budget_exhausted 规则永远跳过 | 09-19 19:57 |
| 120 | 2 | Backlog | `c698ccfcc31543dfbe15b5dc7c42eef8` | [RSI-P0] 自我改进信号落库：失败/效率/结果三类信号 + 维度聚合查询 | 09-19 19:48 |
| 121 | 2 | Backlog | `a1f3310cf70840d182ad3d7af847d38c` | [平台][P2] 重启后前端资产未落到 wwwroot/admin：Core 已重启但前端停留在 15:05 旧版本 | 09-19 16:15 |
| 122 | 2 | Backlog | `dd9c3714d92e489e95952f9fec9e5c7f` | [S4] 观感对齐：步骤行标签配色收敛 + 推理块左侧竖线容器 + ModelRetryRow chrome 迁移 | 09-19 14:03 |
| 123 | 2 | Backlog | `769f31a132bb4240a151dacdcda5f3cd` | [P2][平台] Agent 主动飞书推送持续失败：无最近飞书会话时 send_message 无法投递（14 次连续失败） | 09-19 13:43 |
| 124 | 2 | Backlog | `11f84ccf49d54995b21e200bcae2f9d8` | [测试] AgentMessageBubble.test.tsx 3 个既有失败：推理行渲染断言（reasoning-disclosure-row / 思维链第四行 / 思考）疑似被 14feceb 重构落下的过时断言 | 09-19 12:46 |
| 125 | 2 | Backlog | `3da9e53829a648afb80e7ffee05a32b4` | [P2][平台] 执行 run 以 succeeded 结束但未落 task settlement ⇒ 任务被误判 legacy blocked（canonical 恢复通道同时不可用） | 09-19 10:40 |
| 126 | 2 | NeedsReview | `238c48c4e801435a9fc1061e7c3d00b0` | [平台][P2] SSE 无游标时退化为整份历史事件重放（live 通道失去时效语义） | 09-19 22:43 |
| 127 | 2 | Reserved | `8c8f9f843f1c433a85e14df40f44350e` | [前端] 消息卡片过程输出对齐参考设计：步骤行折叠/展开 + 底部实时状态行 + 消息头预计消耗与耗时 + 流式最小态 | 09-19 13:44 |
| 128 | 2 | Backlog | `230a24dd8a3142718c5db0befc160ff3` | [平台][P2] 子代理连续同参数工具失败缺少早期中断：直到会话 fuse/failed 才终止，整轮委派作废且父级无法恢复 | 09-18 22:15 |
| 129 | 2 | NeedsReview | `0278f10610d74a0cbb4d70a0ebf2a3d5` | [路由卫生] deepseek/deepseek-v4-flash 未注册却仍在文档/预设/smart 角色模型中被引用 | 09-18 23:15 |
| 130 | 2 | NeedsReview | `f25c679c474a462d8a7e0cedcddbb09a` | P2: LlmStreamObservabilityTests 3 个 pre-existing 失败治理（P0-6 C2） | 09-18 21:14 |
| 131 | 2 | Backlog | `67a3c66bfd96451fa1288e58f409a42b` | [已修复待部署] Goal 面板受阻码映射不全（1/9）致「未知受阻码」兜底 | 09-17 10:41 |
| 132 | 2 | Backlog | `a2cebc882d9e49fcad26f384ee864313` | 记忆图书馆 → 可复用 Skill 层（含 author/时间戳/准入审核） | 09-16 23:17 |
| 133 | 2 | Backlog | `d8ec7c8550854978add8d6f2eaa1c71c` | 大规模并行分区必须由测量得出（禁止用卡片猜分区） | 09-16 23:12 |
| 134 | 2 | Backlog | `b44b33c099464803839a8279331a5d98` | 【P2】PuddingRuntimeTests 工程级编译失败（CS0246 Moq）—— 整个测试工程静默失效，新增用例无法验证 | 09-16 09:34 |
| 135 | 2 | Backlog | `55f3c8fdc63d4fde9721fd85d17ab55a` | 【P2】task-bound Goal 无法 extend：预算耗尽时 binding 已被释放 ⇒ /goal extend 恒 fail-closed，主场景（Goal 驱动看板任务）缺少人工补授权路径 | 09-16 08:54 |
| 136 | 2 | Backlog | `94797cf0a8054589837ffc07e2f9e582` | 【P2】GoalApiContractTests 套件级确定性失败（单测通过/套件失败）—— 测试隔离缺陷，会淹没真实回归信号 | 09-16 08:54 |
| 137 | 2 | Backlog | `d2b70816e62a4f79b7d3f0ab4802a114` | 【P2】ToolApproval 审查器结构性不可达：profiles 为空 + ProfileId 必填，任何配置组合都无法启用 LLM 审批 | 09-16 08:10 |
| 138 | 2 | Backlog | `5665ffefa50a4f469d55f7955bec11f1` | [P2] Goal 域诊断不可观测 + test 检查失败被误分类为 check_timeout/等待（unmet_criteria 为空） | 09-16 00:03 |
| 139 | 2 | Backlog | `6042730f62db429b9c1b44465e6e6cd9` | 【P2】子代理运行检查器：UI 状态口径与 Runtime API 不一致（含僵尸子代理不可终结） | 09-15 21:52 |
| 140 | 2 | Backlog | `e3ece70cd24e46faa87b37a483f7a407` | 【P2】spawn_sub_agent 增加 Name / Description 参数，便于识别与跟踪子代理 | 09-15 21:52 |
| 141 | 2 | Backlog | `2fe16ed017864f399031c1222db6a3cd` | 【P2】子代理未指定 model 时静默回退默认模型，应改为继承父级路由并显式告警 | 09-15 21:36 |
| 142 | 2 | NeedsReview | `fa0704553b11441d97b3d5a73e33411e` | ADR-089 U0 残差｜统一 glob 匹配合同（G1–G3，跨后端同一语义） | 09-13 17:12 |
| 143 | 2 | Backlog | `e60e3854ffda425b8739255eccbe752b` | [PLAT-T2] spawn_sub_agent 返回值 summary 字段中文全部乱码为 U+FFFD（静默编码降级） | 09-12 23:25 |
| 144 | 2 | Backlog | `efb33c39498645139b45d0ac4f60c6d2` | [V6-T7] 调研：意图同步新模态——从"聊天"升级为"共享工作物" | 09-12 22:44 |
| 145 | 2 | Backlog | `810f1cce6fdf4d3cab346e496f2e64c3` | [V6-T6] Web：历史消息中的图片可再次编辑并追问 | 09-12 22:44 |
| 146 | 2 | Backlog | `c404c60689d343cd9296f3a72b6bc501` | goal.md 无大小护栏：告警阈值 32KB 是 goal_read 读取上限 16KB 的 2 倍，存在无告警静默截断区间 | 09-12 22:30 |
| 147 | 2 | Backlog | `8d3583f3ac2f46e4b000eae94ba9a810` | V5-A：视觉合同可静默放宽产品护栏（ToPolicy 覆盖语义违背设计交集语义） | 09-12 22:28 |
| 148 | 2 | Backlog | `92b69aae46224014937be23a6b2e8345` | [P2] PuddingCoreTests 全量测试挂起（>30 分钟不退出），验证只能退化为定向过滤 | 09-12 21:51 |
| 149 | 2 | Backlog | `01f23c44076547c69ae592a29e02a0fd` | [DY-02] 抖音可靠回复：ReplyIntent、逐条决策与不确定结果对账 | 09-12 18:11 |
| 150 | 2 | Ready | `e093086585dc4659939de5b4db7010be` | 模型路由健壮性：spawn_sub_agent 对不可用模型应前置探测并自动回退 | 09-13 00:32 |
| 151 | 2 | Backlog | `59f9002ebd654e97a74004c0f87378e8` | 参考 deepseek-harness 0.1.2：Chat 可恢复连接、精确 Turn 指标与已加载 Turn 导航 | 09-11 21:41 |
| 152 | 2 | Backlog | `773e87e1c5434da0bc6ec2dc4c39daf4` | 工具层：worktree 文件写入受限（file_write 被执行根限定，需 temp 中转两段式） | 09-02 11:27 |
| 153 | 2 | Backlog | `a91c76817ef646058a5421071e06a713` | P2 平台缺口：workspace_tasks.allow_agent_fallback 不可经任务 update API 修改（卡面防抢派防线缺一环） | 09-02 11:26 |
| 154 | 2 | Backlog | `59ee1428824248e99f0e81caeecf7fd0` | 参考 deepseek-harness 0.1.2：子代理模型显式授权与安全失败诊断 | 08-31 16:27 |
| 155 | 2 | Backlog | `84427d2e05994d17b9d891814c4b9875` | PuddingAgent 自进化：ResourceGate 硬编码清单与 Classify 单一事实源收敛 | 08-27 18:25 |
| 156 | 2 | Backlog | `88414e5de53248789e0bfa11edae66b5` | [环境][PuddingAgent] 子代理启动即失败：browser_not_available（No authenticated Desktop connected），无浏览器需求的任务也被阻断 | 08-27 13:04 |
| 157 | 2 | Backlog | `a321f472054941999cbda8d0b3199908` | token 移 HttpOnly Cookie（触 Core，含 CSRF 防护设计）【审查 R3 / R1 修复 3】 | 08-21 10:25 |
| 158 | 3 | Backlog | `f3b6afc82019438cbea4374478ef2f25` | [平台][P3] GetFilteredAsync 的 from/to（DateTimeOffset 比较）在 SQLite 上不可翻译 —— 死路径，接线前须先修或删除 | 09-19 22:53 |
| 159 | 3 | Backlog | `d9fa53dc6a244da8aedc4179f823e700` | [看板] manage_tasks 缺少依赖边移除入口：依赖只能加不能删，建错只能重建卡 | 09-19 14:09 |
| 160 | 3 | Backlog | `48b53f02dfdd4661b43c19ef1fa88b3a` | [后端] LLM 消耗成本数据源：价格配置 → 成本换算 → 暴露给前端（供「预计消耗」展示）—— 前端已暂缓，本卡待启用 | 09-19 12:19 |
| 161 | 3 | Backlog | `52e6d850cb6448b493ec2206cc7f1fe7` | 度量口径：禁止以 LOC 下降作为重构验收 KPI | 09-16 23:12 |
| 162 | 3 | Backlog | `84c97d9bc62542e89293fdcce6f9bbc0` | 【P3】前端投放路径不统一 + 字面量 $outputWwwroot/$null 目录导致 pnpm run biome:lint 全仓失败 | 09-16 09:07 |
| 163 | 3 | Backlog | `bb3824663be3468e9c2612511b69d741` | 【P3】ADR-059 ControlInbox Steering 通道为半成品：生产者完整但 Runtime 无消费者 | 09-15 22:38 |
| 164 | 3 | Backlog | `e9da824742d8499e8de7d0c6a127d627` | 【P3】GoalCommandsController 未知 action 静默降级为 Status，应改为 fail-closed | 09-15 22:13 |
| 165 | 3 | Backlog | `4e925adf1f8c466693a5bc89ee4c8160` | 【P3】Agent 可用性投影陈旧：agent_status 报 unknown / Last Activity 滞后 6 小时 | 09-15 21:36 |
| 166 | 3 | Ready | `ff1106854d2c42beaab380b9941b51b3` | chat界面的UI优化 | 09-13 00:34 |
| 167 | 3 | Backlog | `5bcf4a1481ec4ceabc8cf0b941676917` | P3 历史跟进：2026-07-15 两次「检查一下你的工具」未见回复的工具状态确认与健康快照 | 09-02 11:26 |
| 168 | 3 | Backlog | `3237137bbf604edda665e5ebd952531e` | [#988] 平台缺陷登记：file_patch 缩进漂移 & 无 BOM UTF-8 剥离源文件 BOM | 08-27 15:23 |
| 169 | 3 | Backlog | `db53e3a5b0ba4ea79e892407e937292a` | [环境][PuddingAgent] goal.md 无损归档瘦身三通道受阻：shell 审批升级人工门 + 子代理离线 + 私有目录无托管编码通道 | 08-27 13:04 |
| 170 | 3 | Backlog | `46c742e8fe5248dab36852fa10dc5585` | 记忆层：save_memory 被安全策略反复拦截，关键决策只能落 goal.md | 08-27 07:04 |
| 171 | 3 | Backlog | `06bf4dbbd61044a18447e4980ec5fd55` | 消息通道：send_message 广播不支持（仅 1:1），无法主动飞书投递 | 08-27 07:04 |
| 172 | 3 | Backlog | `b7cdc9a16e914fff9c389bd81127bc43` | plan流程 | 08-20 21:22 |
| 173 | 3 | Backlog | `11d2790393ea42409c64c4c1fbc3b6ba` | 子代理修复 | 08-20 21:11 |

## 二、已终态卡（仅计数）

- Cancelled: 24
- Archived: 51

> 说明：本快照用于在批量关闭后保留议题入口。卡被关闭 ≠ 卡面工作已完成。
