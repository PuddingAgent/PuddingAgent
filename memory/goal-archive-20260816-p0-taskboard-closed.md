# P0 任务看板闭环 + P0-4f 删表闭环（2026-08-16）

> 本文件是 goal.md 精简前的完整状态归档。

## 一、P0 任务看板（WorkspaceTask 前端开发）全线闭环 ✅

TB-00 → TB-08-D 全部完成，16 个 commit，最后 ee4d312 已 push（HEAD==origin 干净）。

### commit 链
f4ca0ed(TB-00 合同冻结) → 9e5b825(TB-01 Core合同+状态机) → 59bd873(TB-02 SQLite Ledger) → 411426b(TB-03 CRUD API) → 2f39f7a(TB-03.1 增补B1/B2) → d2dfd27(TB-04 五列Board前端) → 81b53a4(TB-05 Assignment) → 5b7eb04/d389940(TB-06/07 工具+核心) → 92ce8b6/9d226a6(TB-07 B1/B2) → ddfcd7e/1ec856a/932b5934/ee4d312(TB-08-A/B/C/D)

### 各步骤要点
- TB-00：88合同冻结v1（枚举/错误协议/FeatureFlag/唯一Owner）
- TB-01：PuddingCode.Tasks（12态枚举+状态机+ITaskStore契约），22测试
- TB-02：SqliteWorkspaceTaskStore（两表+CAS+原子提交），9测试
- TB-03：TaskController 13端点 + TaskCommandService + TaskWireMaps，24测试
- TB-03.1：PATCH status字段(CanTransition) + boardColumn过滤 + /tasks/watch SSE
- TB-04：前端五列Board（workspace-tasks页面），jest 56/56
- TB-05：Assignment打通
- TB-06/07：task_*工具类 + ActiveTask注入主链路
- TB-07 B1：WAIT→wakeup ActiveTask语义缺口修复（92ce8b6）
- TB-07 B2：端到端测试（进程内集成式，5用例，9d226a6）
- TB-08-A：退役 manage_tasks（ddfcd7e）
- TB-08-B：移除 AgentCheckpointService 骨架（1ec856a）
- TB-08-C：四链E2E测试（7测试，932b5934）
- TB-08-D：封板回归，修复1真实回归（TaskWireMaps 测试字典漏2枚举，ee4d312）

### 关键教训
1. 枚举新增必须同步：①生产 TaskWireMaps switch ②测试期望字典（foreach Enum.GetValues 会暴露遗漏）
2. 子代理 failed ≠ 交付失败，必须亲自 build+test 验收
3. 前端 api.ts 共享文件用 Python 分离补丁 + git apply --cached，避免代推蜜糖改动

## 二、P0-4f 删表（session_event_log → conversation_events）全线闭环 ✅

物理 DROP 已完成，session_event_log 表已不存在，生产代码零残留。

### 三重备份（D:\data\backups\）
1. pudding_platform_pre_drop_session_event_log_20260815.db（5.16 GiB）
2. pudding_platform_pre_schema_align_20260816_073835.db（5.17 GiB）
3. session_event_log_archive_20260816.jsonl（2.8 GiB，4,772,648 行）

### 关键决策
- B/C 全部迁 canonical，不保留 legacy-only 运行模式
- 迁移顺序：D→B→C→A→死代码→⑥关双写→⑦删表
- 物理删表不可逆，归档后仍需用户显式确认

## 三、遗留观察
- 运行库 workspace_tasks / task_events 表尚不存在（TB-02 新表，需重启进程 EnsureCreated 建表）
