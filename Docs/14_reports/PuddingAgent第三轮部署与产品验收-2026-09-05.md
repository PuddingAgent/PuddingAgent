# PuddingAgent 第三轮：部署、进程基线与产品验收

日期：2026-09-05（北京时间）。用户已明确授权停机/重启。看板跟踪卡：`4b904200a4ff4bbc8303285d4f5ee720`。本报告区分制品加载、单次功能 smoke、资源采样和持续调度验收，不以源码测试或 Delivery ACK 替代产品结果。

## 部署结果

- 通过现有 Desktop 的 `/desktop/bootstrap/core/deploy-restart` 预构建制品接口更新 Core 和前端；不使用 dev-up，不另起访问同一数据目录的 Core。
- Desktop 主管 PID **29532** 保持不变；新 Core PID **47056**，父 PID 29532，启动 **09:17:47**、Ready **09:18:04**。这不是 WPF 壳更新、崩溃恢复或退出回收的完整验收。
- 制品为第二轮 `.tmp-build/desktop-efficiency/core`；部署目标为原 `Source/PuddingAgent/bin/Debug/net10.0`。目录仍叫 Debug，但本次复制进去的是 Release 制品。
- Desktop 返回 `success=true`、`deploymentMode=prebuilt-artifact`、`coreRestarted=true`、errors 为空。**264 个受管制品清单 SHA 一致**：`64abf7e842ef1727a6adf7ac8b3be043518c588b7fd0038f8d6eeb4da96017a6`。
- 09:19:27 独立复核 `/health/ready=ready`；Agent/Platform/Runtime 三个业务 DLL 与第二轮清单一致；前端 **148 个源文件全部 SHA 一致，0 mismatch**。SDK 生成的 276 个压缩副本已在第二轮制品检查中验证，本轮没有冒称全部再次解压核验。
- 原始回执：`.tmp-test-out/efficiency-results/round3-deployment.json`、`round3-verification.json`。源码 HEAD 为 `731d5e063cdd888128cbcfc3ba53c32cfae2284d`，包含既有用户改动和未提交修复，不是干净提交制品。

## 停机备份与控制器修正

使用的最终备份：`D:\Keys\PuddingDeploymentBackups\20260905-091115-650d8c48`，于 **09:16:57** 完成；`QuiescentAtCompletion=true`，9 个数据库/伴随文件逐一源/副本 SHA 一致。包含数据库、WAL/SHM、配置、旧 Core 目录和 desktop.json，**不包含工作区归档**。ACL 仅当前用户和 SYSTEM；没有清理 D:\data。

两份早期备份保留但不作为本次部署门禁。第一次备份结束前已出现另一 Core 启动，现有证据不足以确定发起者。第二次失败已定位为本轮控制器自身缺陷：`diagnostics.coreProcessId` 会保留 `LastProcessId`，不能用空 PID 判断停机；误判后 catch 的恢复动作启动了 Core，并非证明产品自动恢复失控。已修正为状态加 OS 进程存活检查，失败恢复前也检查真实停机状态。`test-pudding-deployment-gates.ps1` **7/7**，仅提取纯函数测试，不执行维护操作。

本轮一次诊断错误输出了含 Desktop 控制令牌的配置对象。后续已改用字段白名单，未将该凭据写入项目或报告；该控制令牌需单独轮换，不能与 External Access Token 混为一谈。本轮未擅自改动访问控制配置。

## 调度与任务库存

新进程的调度状态为 `authoritative`，enabled=true、paused=false、eventDrivenEnabled=true，三项 Goal 前置均启用。09:18 recovery scan 耗时 **10,011 ms**，idleAgents=3、candidates=0、started=0、lastError=null。这证明服务启动并完成扫描，**不证明有效自动吞吐验收通过**。

看板共 106 张：Backlog 48、Blocked 13、Completed 36、Ready 1、InProgress 1、NeedsReview 2、Cancelled 3、Archived 2。无 Reserved/Assigned。该快照不能被解释为全量 Backlog 已授权自动执行。

- `77883a50d4c8453cbd05c38ee1719f0e`（Task Tracker/Watchdog）为 Blocked v20，auto=true；更新时间 **08:52:17，早于本次新构建部署**。原因 `work_unit_budget_exhausted`，input=**187,495 / 150,000**。未自动放大预算、resume 或 run-now。
- 唯一 Ready 是视觉 umbrella，auto=false；缓存卡仍 InProgress 但 activeAssignmentId=null、auto=false，不能称为正在执行。
- 下一步应先核对 Tracker 卡的 WorkUnit、使用量和已有产物，将剩余验收拆成有限输入、单一后置条件的小单元，再经真实自动派发验证。不能为提升吞吐全局启用 Backlog，或用手工发送消息假冒调度 E2E。

## 只读功能 smoke：通过，但延迟不合格

消息 `extmsg-ad7c91609ccc6578a8564e52e259b0f1`，会话 `206a9b48ec904ebb93e7541131fbb835`，command `0a192de6c3f64125ba768efef6152de1`，turn `d32dda3fe229441897e73074711e2a30`，run `e39a265a771442edb1c3290ed36b48b4`。一次发送、同一幂等键，没有重发。

- API accepted：**09:22:09.666**；Run started：09:22:12.894；canonical succeeded：**09:25:26.662**；总计 **196.996 秒**。
- 唯一工具 `file_read`：09:25:19.742 requested → 09:25:20.058 completed，约 **317 ms**，exitCode=0；输出与磁盘 canary `pudding-round3-20260905-d82c9f` 一致。Agent 最终回执 readSucceeded=true、toolCallCount=1、error=null，与 canonical 工具事件相符。没有发现工具重试、discovery-only 或 sleep 循环。
- 工具前有 41 条 thinking-summary 增量；最大可观测事件空档 **161.749 秒**，边界 sequence **718247→718248**（09:22:37.669→09:25:19.418）。它占端到端约 82%，不能归因为“file_read 慢”。尚未区分 provider 流静默、客户端消费阻塞或持久化链等待，也不应直接称为持续推理 162 秒。
- `ConversationEventStore` 当前将 `occurred_at` 和 `committed_at` 都写为同一 `committedAt`；两者差为 0 **不是**落库零延迟证据。下一轮需增加/联查真实 receive→consume→persist 阶段时间。

路由保持原配置 **bigmodel/glm-5.3-flash**，未擅自换模型。网关账本记录两次 chat_stream：

| 请求 | 输入 token | hit | miss | 输出 token | 输入加权命中 |
|---|---:|---:|---:|---:|---:|
| 第一次 | 44,074 | 0 | 44,074 | 241 | 0% |
| 工具后第二次 | 44,398 | 44,032 | 366 | 55 | 99.176% |
| 本回合合计 | 88,472 | 44,032 | 44,440 | 296 | **49.769%** |

这是一次短任务样本，不是 DeepSeek 七日指标。网关 `occurred_at_utc` 对应请求开始附近时间，首条 usage 出现不意味着流当时已经结束。读取 199 字符文件仍带入约 44k 首轮输入，短任务上下文/前缀策略尚需优化；不能只报第二次请求 99% 来掩盖冷请求。

## 资源测量：不能报告修复收益

8 个逻辑 CPU，整机归一化。每个 PID 两秒采样；不强制 GC，不清理运行数据。

| 场景 | Core PID | 时长/有效样本 | 平均 CPU | 平均 Private MiB | 末尾 Private / WS MiB |
|---|---:|---:|---:|---:|---:|
| 部署前混合负载，原文件误标 idle | 46208 | 120s / 58 | 9.113% | 1,471.16 | 1,036.86 / 1,171.04 |
| 新进程冷启动后空闲，09:19:36–09:21:37 | 47056 | 120s / 58 | 0.189% | 276.05 | 397.37 / 536.74 |
| smoke 后短时空闲观察 | 47056 | 60s / 29 | 0.904% | 1,714.50 | 1,902.41 / 2,038.11 |

**更正**：`round3-before-idle.json` 的 label 命名错误。按 execution_runs 毫秒时间戳复核，08:47:45–08:49:47 与 Run `887d42bfbcdf49bc9c1eb02f028432ca` 重叠（后来预算失败）。用 julianday 解释该表的整数 epoch 会错误返回空集；不得据此宣称当时无 Run。原样本保留，实际分类是混合负载。

新进程首次 idle 数字低，但短任务后私有内存又升至约 **1.86 GiB**。因此既不能计算“降低 98% CPU/内存”，也不能说内存问题已解决；尚未区分 GC 高水位/保留对象/原生内存。Desktop 未替换，同三段平均 Private 为 303.64、308.65、310.39 MiB；WebView2 单独记录在采样文件中。

原始采样在 `.tmp-test-out/efficiency-results/round3-before-idle.json`、`round3-after-idle.json`、`round3-after-smoke-idle.json`。两次瞬时托管栈见到 StorageInventorySampler.ReadTimeRangeAsync、AgentConversationProjectionService.GetConversationAsync、WorkspaceAgentFileService 文件读取，以及后台遥测写入；只能作为 profiling 线索，不是 CPU 占比或泄漏归因。

## 后续优先级与未关闭门禁

1. **短回合停顿与预热后内存**：保持模型/提示/工具工作量不变，联查上述 161.749 秒空档；采 receive/consume/persist/dispatch 分段时间、分配率/GC 后保留量和 idle/stream/history 三场景。禁止先提高并发、放大预算或强制 GC 掩盖问题。
2. **恢复可执行库存**：Tracker 卡失败在 `Explore` WorkUnit（25 rounds / 60 tools / 150k input），后面的 Plan/Change/Test/Review 尚无完成证据。先收敛 Explore 的范围与输入，再决定新 WorkUnit/恢复门禁；不把 Backlog 批量自动启用。
3. **缓存**：给 firstChangedSegment/PrefixEpoch 与 cold/warm 分桶补证据，在相同工作量下对比，保留每模型的有效交付成本，不以高 hit 掩盖空转。
4. **UI/生命周期**：现有浏览器仍显示旧历史卡片长期“工具执行中”；Desktop 工作台处于未登录页面，未绕过登录 UI。已核实静态制品部署，不等于新 bundle 已在所有既有页面刷新，也没有完成 Chat 首屏/滚动/P95/长任务、完整视觉或 WPF 壳生命周期验收。

本轮未完成 10 个安全任务自动执行、连续七夜有效吞吐、DeepSeek 七日 >99% 缓存验收；现有依赖 NU1903 告警也未修复。没有 commit/reset/clean，没有替用户改动其他工作树内容。

已通过 External Task API 新增两个独立 P1 卡：`5c6aa2e9a8d44e029bab08a710770309`（流空档分段诊断）、`f834c902793d450bbe3bffd8ad8a849d`（预热内存归因）。两卡初始 Backlog，不自动启用或手工 run-now；第一小单元均先提交有限范围诊断证据。第三轮跟踪卡、缓存卡、Scheduler E2E 卡追加本轮事实，不强行改变生命周期或宣称夜间完成。
