# Pudding 脆弱性清单（2026-08-03，任务 2 事件驱动唤醒施工中暴露）

记录人：蜜糖。来源：事件驱动唤醒任务全链路调查 + 测试矩阵自验 + 子代理诊断。
状态标记：✅已修 / 🔧修复中 / ⏳待修 / 📋待设计。

## A. 子代理 / Agent 执行子系统（本次重点）

### A1. 🔧 轮次上限耗尽 = 成果全损（24h 内 10 次失败中 6 次为此模式）
- 现象：子代理跑满 max_rounds 后以 `"(no response)"` 结束，父代理只收到错误消息
  `"Maximum agent rounds reached (N) before a final response."`，子代理上下文中
  已完成的全部调查/结论丢弃。实测 sub-b9f86e57 跑满 60 轮、6.4 万 token，落盘与报告均为零。
- 代码位置：`Source/PuddingRuntime/Services/AgentExecution/AgentExecutionService.Buffered.cs`
  - for 循环 :367；轮次耗尽终态判定 :1654-1662（execState Running → Failed，
    finalMessage 保持初始值 "(no response)" :334）
  - 已有扩展点：`OnMaxRoundsReachedAsync` 钩子 :403/:1226/:1659
  - 已有可复用件：`ExpectedOutputCandidateTracker`（AgentLoop/CanonicalWorkReport.cs:119）
    全程跟踪符合输出合同的候选文本
- 修复方案（已定稿，待实施）：轮次耗尽时执行一次"抢救回合"——禁用工具、注入系统指令
  要求按输出合同（SUMMARY/CHANGES/EVIDENCE/RISKS/BLOCKERS）立即输出最终报告；
  或退而取 ExpectedOutputCandidateTracker 的最佳候选。结果写入 ReplyText，
  状态保持 failed 但附带 partial 标记。
- 配套：spawn_sub_agent 工具在子代理失败时应把 ReplyText 一并返回给父代理
  （当前只返回 errorMessage，抢救文本到不了父代理手里）。

### A2. ⏳ 子代理默认 max_rounds=10 过低
- `Source/PuddingRuntime/Services/SubAgentInvocationService.cs:206 和 :241`：
  `MaxRounds = request.MaxRounds ?? 10`。实测两个 run 恰好死于 10 轮。
- 建议：默认提到 30，并纳入运行时配置（SubAgents options）。

### A3. ⏳ 异步子代理 "(no response)" 基础设施失败不透明
- 实例：sub-6cd10e05（只读测绘任务）仅 1 个 LLM 请求即 failed、无错误详情，
  diagnostics 归类 unknown。24h 内 10 次失败全部归类 unknown——错误分类本身缺失。
- 建议：run 记录补充最后一次 LLM 请求错误 / 异常栈；diagnostics 细分 unknown。

### A4. 📋 重启杀死运行中子代理，无恢复无通知
- 实例：run_20260803_093151 状态 interrupted，错误
  "Runtime process restarted before the sub-agent committed a terminal state."
- 建议：至少向父代理投递 sub_failed 消息说明原因；中期考虑 run 恢复或检查点。

### A5. ⏳ 子代理幻觉目录空转无熔断
- 两个 run 死于反复 file_search 不存在的 "PuddingCode" 目录（错误消息已给出可用盘符
  提示但子代理不读）。MaxSameToolRepeat 只拦"同工具同参数"，不拦"同工具同族错误"。
- 建议：同一工具连续 N 次失败（不同参数）注入强制系统提示或熔断。

### A6. 📊 子代理成功率 54.2%（24h，13/24），平均 34.2 轮/49 次工具调用
- 一半的委派成本打水漂。A1-A5 修复后应显著回升；建议把子代理成功率纳入日常 diagnostics 巡检。

## B. 消息投递 / 唤醒子系统

### B1. ✅ busy 推迟 AvailableAt=+30s 使 idle 事件排空失效（今日修复 ea16b5d）
- 事件驱动唤醒的发布/订阅两侧早已存在（RuntimeAgentDispatcher 发布
  agent.availability.changed；MessageDeliveryDispatcher.HandleAvailabilityChangedAsync
  排空），断点在 busy 分支 RetryAsync(+30s) 与 ClaimNextAsync 的 `AvailableAt <= now`
  过滤互相抵消。修复：busy 推迟改 UtcNow（排队语义），附回归测试
  （MessageDeliveryDispatcherTests 24/24 绿）。
- 教训：此类"闭环断裂"只能靠全链路 file:line 测绘发现，单点 review 看不出。

### B2. ✅（昨日）心跳重复投递 / 消息去重一期二期（403bbe1/5d4b30d/d01304c/ca730b0/05e7ede）
- 已重启生效，生产日志见 [MessageFabric][dedup] 拦截记录。

### B3. ✅（2026-08-05）ADR-059 网关路径统一接入 AgentExecutionStateRegistry
- `TurnExecutorAdapter` 改经 `RuntimeAgentDispatcher` 执行，网关 Turn、Agent 消息和心跳共用
  `TryBegin/Complete` Busy/Idle 权威；用户 Turn 遇 Busy 时等待，不把暂时忙碌提交为失败终态。
- 同时补齐心跳用户抢占与 Message Fabric delivery 续租/fencing，避免旧心跳租约过期、被回收
  并 ACK 后仍继续执行。定向回归：`MessageDeliveryDispatcherTests` +
  `TurnExecutorAdapterTests` 27/27，`MessageFabricStoreTests` 10/10。

## C. 工程 / 测试基建

### C1. ✅ 从未编译的测试文件被提交入库（今日修复 f1ba0f3）
- HostLifecycleTests.cs：xunit 风格却在 MSTest.Sdk 项目、缺 PuddingHost 引用、缺
  using Microsoft.AspNetCore.Hosting——三重缺失，自提交起从未编译。同类前科：
  MessageFabricDedupTests.cs（昨日修复 0901174）。
- 建议：CI/提交门禁加"解决方案级 dotnet build 0 error"检查；把 PuddingRuntimeTests
  纳入测试矩阵（此前从未跑过）。

### C2. ⏳ PuddingRuntimeTests 首跑暴露 23 个预存在失败（474/497）
- 工具审批子系统 ~17（ToolApprovalService 系列断言与现行为不符）、LLM 可观测性 3、
  DI 解析 1（ITaskPlanStore 未注册）、架构守卫 2。需逐项定性：测试过期 vs 生产回归。

### C3. ⏳ 架构守卫红牌：PuddingRuntime 违规依赖 PuddingPlatform
- ArchitectureGuardTests 实锤：1 处项目引用 + 5 处 `using PuddingPlatform`
  （ContextPipeline / ConversationSkillEvolutionTrajectorySource / SubAgentTool /
  FilePatchTool / FileTools）。分层架构债务，需契约下沉 PuddingCore。

### C4. ⏳ CaptureBoundAddresses 疑似生产 bug
- 测试绑定 http://127.0.0.1:9852 后，CaptureBoundAddresses 抛
  "No loopback HTTP address found"（PuddingApplicationHost.cs:174-192）——
  IPuddingServerAddressAccessor.BaseAddress 对合法 loopback 地址返回 null。待查。
- 附带：PuddingApplicationHost.Build 实际返回 WebApplicationBuilder，
  与其文档注释（Calling order）描述不一致。

## D. 工具层 / 终端环境

### D1. ⏳ file_write 疑似写 UTF-8 带 BOM
- 证据：经 file_write 消息文件 + git commit -F 提交的两个 commit，message 头部出现
  BOM 字节（GBK 下显示"绱綺ix"，吞掉首字母）。已重建提交修复（f1ba0f3/ea16b5d）。
- 建议：file_write 提供无 BOM 选项或默认去 BOM；提交前可用
  PowerShell `[Text.UTF8Encoding]::new($false)` 重写消息文件规避。

### D2. ⏳ 终端包装层缺陷（CMD）
- 引号剥离：`git commit -m "multi word"` 碎裂成 pathspec 报错——必须走 -F 消息文件。
- findstr 静默失配：对已知存在的内容返回零匹配（多次复现），不可信赖。
- 输出编码：chcp 65001 后捕获层仍按 GBK 解码，中文输出全乱码，诊断成本大增。
- 建议：terminal 工具默认走 pwsh；或包装层保留原始 UTF-8 字节。

## 附：今日提交链（全部本地未推送，领先 origin/master 29 个）
- cbb261b docs(wake): 事件驱动唤醒设计稿
- f1ba0f3 fix(tests): HostLifecycleTests 迁移至 PuddingHost.Tests 并修复编译
- ea16b5d fix(wake): busy 推迟立即可认领，修复 idle 事件排空闭环（含回归测试）

## 附：测试矩阵现状（2026-08-03 18:2x）
| 套件 | 结果 | 备注 |
|------|------|------|
| PuddingPlatformTests | 342/342 ✅ | |
| PuddingBrowser.AgentTools.Tests | 15/15 ✅ | |
| PuddingHost.Tests | 64/66 | 2 失败=迁移测试首跑暴露（C4） |
| PuddingDesktop.Tests | 122/122 ✅ | |
| PuddingRuntimeTests | 474/497 | 首次入矩阵；23 失败均为预存在（C2） |
| MessageDeliveryDispatcherTests（过滤跑） | 24/24 ✅ | 含新增回归测试 |
