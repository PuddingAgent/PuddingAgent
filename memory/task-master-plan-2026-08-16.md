# 任务看板主线总任务清单（47 项 Canonical Task）

> 生成日期：2026-08-16
> 权威来源：ADR-073 §7「完整任务清单与施工顺序」+ ADR-072（领域设计）+ 88 任务看板施工合同冻结 v1
> 上游背景：P0-4f canonical 化已全线闭环（session_event_log 已删表），主线切换到「任务看板前端开发」

---

## 一、权威设计文档位置（唯一参照，禁止另起炉灶）

| 文档 | 路径 | 角色 |
|------|------|------|
| 合同冻结 v1 | `Docs/07架构/88任务看板施工合同冻结v1.md` | **枚举/错误协议/FeatureFlag/唯一Owner 的唯一权威**（TB-01~08 均以此为准）|
| 施工排序基线 | `Docs/07架构/87ADR-073任务看板优先的Agent工作台轨迹与实时指标施工ADR.md` | **47 项任务清单 + 施工顺序 + 里程碑** |
| 领域设计 | `Docs/07架构/86ADR-072工作区TODO峰谷Auto派发与定时任务第一阶段ADR.md` | ST-00~ST-11 领域模型 |
| 上位架构 | `Docs/deepseek-harness-pi-plugin-hook-event-architecture-2026-08-14.md` | 插件/Hook/生命周期/事件驱动 |
| 总蓝图 | `Docs/deepseek-reference-architecture-master-plan-2026-08-14.md` | T00~T16 平台底座 |
| 消息 UI | `Docs/deepseek-harness-message-card-alignment-2026-08-14.md` | 消息/推理/工具调用 UI |
| 工具对齐 | `Docs/deepseek-harness-tool-system-alignment-2026-08-14.md` | ToolCallId/工具对齐 |

工作量单位（熟悉仓库工程师，含实现+定向测试+文档）：`XS=0.5~1 人日`、`S=1~2`、`M=3~5`、`L=6~10`、`XL=11~20`

---

## 二、P0 任务看板先行（TB-00 ~ TB-08，当前主线）

> 全部完成才算「基础任务看板完成」。预计总量 42~64 人日，纯前端看板仅 6~10 人日，主工作量在可靠执行与恢复闭环。

| 顺序 | ID | 任务目标 | 优先级 | 工作量 | 难度 | 设计位置 |
|------|----|----------|--------|--------|------|----------|
| 1 | TB-00 | 冻结五列/Failed/Command/错误码/Flag/唯一Owner | P0 | S | 中 | 合同冻结 v1；ADR-072 §1、§5.2 |
| 2 | TB-01 | WorkspaceTask/Attempt/Binding/Event Core 合同 + 纯状态机 | P0 | M | 高 | ADR-072 §4、§5、ST-02 |
| 3 | TB-02 | SQLite Task Ledger、索引、CAS、Task+Event 原子提交、归档 | P0 | L | 高 | ADR-072 §11、ST-02、§15 |
| 4 | TB-03 | Task CRUD/Transition/RunNow API、Snapshot、Cursor Watch | P0 | L | 高 | ADR-072 §10.1、ST-03；ADR-050 §2.4 |
| 5 | TB-04 | 五列 Board、虚拟化、筛选、排序、Editor/Details Drawer | P0 | L | 中高 | ADR-073 §4；ADR-072 §10.2、ST-08A |
| 6 | TB-05 | Assignment→Outbox→Message Fabric→Delivery→Execution Binding | P0 | L | 极高 | ADR-072 §8.1、ST-03；ADR-057 §9 |
| 7 | TB-06 | task_list/get/claim/update + Active Task Runtime Context | P0 | L | 高 | ADR-072 §9.2、ST-04 |
| 8 | TB-07 | 状态自动回写、失败/重开、执行会话深链、Task Timeline | P0 | M | 高 | ADR-073 §4.2~4.3；ADR-072 §12 |
| 9 | TB-08 | 刷新恢复、并发/CAS、迟到提交、Fake LLM E2E、退役 manage_tasks | P0 | L | 极高 | ADR-073 §6；ADR-072 §16~18 |

---

## 三、P1 Auto 与 Cron（AU-01 ~ AU-06）

| 顺序 | ID | 任务目标 | 优先级 | 工作量 | 难度 | 设计位置 |
|------|----|----------|--------|--------|------|----------|
| 10 | AU-01 | TimeProvider、时区、Work Policy、Fence、Heartbeat 0 | P1 | L | 高 | ADR-072 §6、ST-01 |
| 11 | AU-02 | Agent Availability Projection、Reservation、Lease/Fence、用户优先 | P1 | L | 极高 | ADR-072 §7、ST-05 |
| 12 | AU-03 | Auto Dispatcher、确定性选择、三次 Fence、拒绝轮换、恢复扫描 | P1 | L | 极高 | ADR-072 §8.2、ST-06 |
| 13 | AU-04 | Automation/Occurrence、受限 Cron、Next Fire、Misfire、Overlap、Outbox | P1 | L | 高 | ADR-073 §5；ADR-072 §5.3~5.4、ST-07 |
| 14 | AU-05 | Auto/Cron Editor、未来五次预览、Occurrence History、Work Policy UI | P1 | M | 中高 | ADR-072 §10.2、ST-08B |
| 15 | AU-06 | 双 Scheduler、Crash Matrix、峰谷边界、重启恢复、单一 Owner 验收 | P1 | L | 极高 | ADR-072 ST-10、§16.3~16.4 |

---

## 四、P2 完整运行轨迹（TR-01 ~ TR-06）

| 顺序 | ID | 任务目标 | 优先级 | 工作量 | 难度 | 设计位置 |
|------|----|----------|--------|--------|------|----------|
| 16 | TR-01 | 冻结 Run/Turn/Step/Request/Tool/Subagent 轨迹 DTO、排序、部分流合同 | P2 | M | 高 | 消息 UI §7；ADR-057 §6、§12；ADR-060 §3 |
| 17 | TR-02 | 服务端 TrajectoryProjection、Snapshot/Watch、分页、gap recovery | P2 | L | 极高 | 上位架构 §7.4、§26；ADR-057 §4.8、§12 |
| 18 | TR-03 | 独立 Chat/Trajectory Tab、虚拟表格、搜索、折叠、时间范围、深链 | P2 | L | 高 | deepseek-harness ui-trajectory；消息 UI §4、§8 |
| 19 | TR-04 | 收敛 reasoning/message/tool 行、按 callId 配对、Tool-owned presenter | P2 | L | 高 | 消息 UI §5；工具对齐 §14 |
| 20 | TR-05 | 子代理建模 parent delegation + child run、child session 完整轨迹 | P2 | L | 极高 | ADR-060 §3.7~3.8；上位架构 §8.10 |
| 21 | TR-06 | 500 Turn/2000+ 行性能、流式滚动、可访问性、replay 一致性 | P2 | M | 高 | 消息 UI §10；trajectory E2E |

---

## 五、P3 实时 Token 与性能指标（MT-01 ~ MT-05）

| 顺序 | ID | 任务目标 | 优先级 | 工作量 | 难度 | 设计位置 |
|------|----|----------|--------|--------|------|----------|
| 22 | MT-01 | LLM request started/first-token/completed/failed 时序事实 + requestId | P3 | M | 高 | ADR-073 §3.4；ADR-057 §6 |
| 23 | MT-02 | RunMetricsProjection、去重 usage、TTFT/TPS/LLM/Tool/Cache/Context | P3 | L | 高 | ADR-043 §2、§4；消息 UI §9 Phase C |
| 24 | MT-03 | 修正上下文压力口径、区分 billed usage 与 projected next prompt | P3 | M | 高 | ADR-043；ADR-018；ADR-073 §3.4 |
| 25 | MT-04 | Composer 下 StatsLine + ContextMeter、详情渐进披露 | P3 | M | 中 | StatsLine/ContextMeter；消息 UI §4.2 |
| 26 | MT-05 | 流式估算转准确值、刷新恢复、跨模型、无 usage 失败测试 | P3 | M | 高 | ADR-073 §3.4、§6；ADR-043 §8 |

---

## 六、P4 插件化与收口（PL/CL）

| 顺序 | ID | 任务目标 | 优先级 | 工作量 | 难度 | 设计位置 |
|------|----|----------|--------|--------|------|----------|
| 27 | PL-01 | PresentationContribution Registry（工具声明 summary/card/inspector/timeline）| P4 | L | 极高 | 上位架构 §26.3；工具对齐 §14、§16 |
| 28 | PL-02 | 统一 Chat/Board/Trajectory 语义 Token、权限动作、插件 Owner 生命周期 | P4 | M | 高 | 上位架构 §26；消息 UI §6 |
| 29 | CL-01 | 删旧 Todo、重复前端 reducer/时间线、旧指标路径，更新文档与 Code Map | P4 | M | 高 | ADR-073 §2、§6；总蓝图 T16 |
| 30 | CL-02 | 组合测试、故障注入、产品新构建 Smoke、外部部署验收 | P4 | L | 极高 | 总蓝图 T13/T15；ADR-072 ST-10/ST-11 |

---

## 七、平台底座 T00~T16 全量登记（AX-T00 ~ AX-T16）

> 底座任务自身毛估算，与产品任务有交付物复用，不能机械相加。全局仍先完成 TB-00~08。

| 顺序 | ID | 任务目标 | 全局优先级 | 工作量 | 难度 | 拉入点/设计位置 |
|------|----|----------|------------|--------|------|-----------------|
| 31 | AX-T00 | 冻结 Microkernel、Capability、事实语言、插件注册合同 | P0最小/P4全量 | M | 高 | TB-00 拉入；总蓝图 T00 |
| 32 | AX-T01 | Storage/Session Log 插件、单一 append-only 运行事实 | P2 | XL | 极高 | TR-02 前；T01 |
| 33 | AX-T02 | Model/LLM 插件、ContentBlock、DeepSeek Responses 保真 | P2 | L | 高 | TR-01/MT-01 前；T02 |
| 34 | AX-T03 | 收敛 Agent Loop、Run/Turn/Step、单一优先级 Inbox | P2 | XL | 极高 | TR-01/02 前；T03 |
| 35 | AX-T04 | Prompt Section 和 Skill 变可组合插件贡献 | P4 | L | 高 | PL-02；T04 |
| 36 | AX-T05 | Tool Registry 插件、端到端 ToolCallId、canonical Tool Result | P2 | L | 高 | TR-04 前；T05、工具 P0-A/P0-B |
| 37 | AX-T06 | 纵向迁移 Tool/Sandbox、Typed Error、Hook Pipeline、spill、Job | P2~P4 | XL | 极高 | TR-04/PL-01；T06、工具 P0-C/P1 |
| 38 | AX-T07 | 单一 Standard Profile/Bundle/Overlay、组合快照 | P4 | L | 高 | PL-02；T07 |
| 39 | AX-T08 | 动态 Plugin Host、Typed Hook、reload、sidecar、撤销生命周期 | P4 | XL | 极高 | PL-01/02；T08、插件 P1/P5 |
| 40 | AX-T09 | Goal/Job/Schedule/Heartbeat 收敛为插件 + durable 事件消费者 | P1最小/P4全量 | XL | 极高 | AU-04 拉入 Occurrence；T09 |
| 41 | AX-T10 | 子代理变 Provider 插件、统一 delegation/child/settlement | P2 | L | 极高 | TR-05 前；T10 |
| 42 | AX-T11 | Compaction 插件、Checkpoint、Tool-pair 安全边界 | P4 | L | 高 | PL-02；T11 |
| 43 | AX-T12 | Projection/Presentation 插件、统一 SSE、可恢复前端读取模型 | P2~P3 | XL | 极高 | TR-02/MT-02/PL-01；T12 |
| 44 | AX-T13 | Runtime Invariants、composition dump、终态/顺序守卫 | P4 | L | 高 | CL-02；T13 |
| 45 | AX-T14 | 长效学习改 durable event→candidate→evaluate→canary→activate/rollback | P4 | XL | 极高 | 插件/Hook P4；T14 |
| 46 | AX-T15 | 组合测试、故障注入、真实 DeepSeek Smoke、Desktop 外部验收 | P4 | XL | 极高 | CL-02；T15 |
| 47 | AX-T16 | 删旧事实源、旧 Loop/Hook/DTO/兼容分支、收口文档 | P4 | L | 高 | CL-01 后；T16 |

---

## 八、里程碑（M0~M8）

| 里程碑 | 退出条件 |
|--------|----------|
| M0 | 五列/Failed/Cron/ID/错误/唯一Owner 冻结（ADR-073 + ADR-072）|
| M1 | Task 状态机、SQLite、CAS、Task Event、API、Snapshot/Watch 通过 |
| M2 | 五列 Board 能创建/编辑/筛选/拖动合法转换并恢复 |
| M3 | 卡片执行创建真实 Run，Agent 只能用结构化工具推进 Task |
| M4 | 自动回写/失败/重开/会话深链/刷新/E2E 通过，基础任务看板完成 |
| M5 | Auto/Cron/峰谷/Occurrence/恢复/Automation UI 通过 |
| M6 | 主/子 Agent 完整轨迹可搜索/虚拟化/回放 |
| M7 | TTFT/TPS/耗时/上下文/缓存/Token 准确可恢复 |
| M8 | Presentation 插件化、旧路径删除、性能与产品 Smoke 通过 |

依赖图：M0 → M1 → M2/M3 → M4 → M5 + M6 → M7 → M8

---

## 九、当前进度（2026-08-16）

- **TB-00/M0 合同冻结** ✅ 完成，commit `f4ca0ed`（88任务看板施工合同冻结v1.md）
- **现状调研** 🔄 `sub-d73cfcb0`（deepseek-v4-flash）在途
- 下一步：TB-01（Core 合同 + 状态机）

## 十、施工顺序总览（ADR-073 §12）

```
Task Contract → Task Ledger/API → 五列 Board CRUD → Manual Execution + task tools
→ 自动回写/失败/重开/会话深链 → 基础任务看板完成 → Availability/Fence/Auto
→ Cron/Occurrence → 完整 Trajectory → Run Metrics → Presentation Plugin → 删除旧路径与产品验收
```
