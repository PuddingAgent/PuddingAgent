# SKILL Hub · B3 人类管理界面 · 实施 Brief（原子委派用）

> 状态：**已冻结，可一击执行** ｜ 作者：dsh(0e0) ｜ 日期：2026-09-21
> 上游契约：`Docs/Features/SKILL-Hub技能中心与EVO-MAP设计方案-2026-09-21.md`（§5 后端 API 冻结 / §7 界面规格）
> 本文件只补「实施者开工所需、而设计文档未显式给出」的可操作信息；**契约本身以设计方案为准，不得改写**。

---

## 一、为什么要写这份 Brief

B1（后端）与 B2（Agent 工具）已交付并推送，**B3 是 SKILL Hub 交付链的最后一环**。
设计文档 §7 已给出界面规格与函数签名，但缺三类实施细节：①前端调用惯例 ②验收方式 ③交付与提交规约。本文件补齐，使 B3 可被**一次委派、一次验收**。

---

## 二、现状事实（已核实，2026-09-21）

| 项 | 事实 |
|---|---|
| 目标页面 | `Source/PuddingPlatformAdmin/src/pages/skill-management/index.tsx` — **17,653 B / 单文件**，LastWrite 2026-07-12，仅支持「上传 zip 技能包」 |
| API 封装 | `Source/PuddingPlatformAdmin/src/services/platform/api.ts` — **4,372 行 / 142,600 B**，末尾需**追加** Skill Hub 段 |
| 后端就绪度 | B1 已交付（`SkillHubService` + `SkillHubController` 15 端点）；**B1.1 已接线**：`Source/PuddingHost/Hosting/PuddingApplicationInitializer.cs:70` 已调用 `SkillHubSchemaBootstrapper.EnsureCreatedAsync(...)` ⇒ 启动时会幂等建 4 张 Hub 表 ✅ |
| 进程状态 | ⚠️ **尚未重启**加载新端点 —— 前端可先按冻结契约实现，**端到端联调需重启窗口**（已向 6a8 申请） |

---

## 三、冻结映射表（前端函数 → 后端端点）

调用惯例（**与 `api.ts` 既有 4,300 行完全一致**，不得自创）：

```ts
return request('/api/xxx', { method: 'GET' });
return request(`/api/xxx/${encodeURIComponent(id)}`, { method: 'GET' });
return request('/api/xxx', { method: 'POST', data: req });
return request(`/api/xxx/${encodeURIComponent(id)}`, { method: 'PATCH', data: req });
return request(`/api/xxx/${encodeURIComponent(id)}`, { method: 'DELETE' });
```

| 前端函数（§7.2 冻结签名） | 端点（§5.2 冻结） |
|---|---|
| `getSkillHubStats()` | `GET /api/skill-hub/stats` |
| `listHubSkills({query,tag,status,page,pageSize})` | `GET /api/skill-hub/skills?query=&tag=&status=&page=&pageSize=` |
| `getHubSkill(skillId)` | `GET /api/skill-hub/skills/{skillId}` |
| `getHubSkillVersion(skillId, version)` | `GET /api/skill-hub/skills/{skillId}/versions/{version}` |
| `getHubSkillLineage(skillId)` | `GET /api/skill-hub/skills/{skillId}/lineage` |
| `getHubLineage(skillIds?)` | `GET /api/skill-hub/lineage?skillIds=a,b,c` |
| `updateHubSkillMeta(skillId, req)` | `PATCH /api/skill-hub/skills/{skillId}` |
| `retireHubSkill(skillId)` | `DELETE /api/skill-hub/skills/{skillId}`（软删 → `retired`） |
| `listHubInstalls({agentInstanceId,skillId,page,pageSize})` | `GET /api/skill-hub/installs?...` |
| `listHubEvents({skillId,limit})` | `GET /api/skill-hub/events?skillId=&limit=` |

**未列出但可用的两个端点**（按需追加，同属冻结契约）：
`GET /api/skill-hub/skills/{skillId}/versions`（版本列表）、`GET /api/skill-hub/updates?agentInstanceId=`（待更新清单），
以及写侧 `POST /api/skill-hub/skills`、`POST /api/skill-hub/skills/{skillId}/versions`、`POST /api/skill-hub/installs`。
> B3 是**人类管理界面**：写侧以「退役 / 改元数据」为主即可；「发布 / 进化」按 §7 非必做，若做则必须复用同一冻结路径。

---

## 四、六 Tab 实施要点（含一处文档不一致的裁定）

⚠️ **设计文档 §7.1 标题写「5 个 Tab」，但其表格实列 6 行**（①概览 ②技能库 ③EVO MAP ④安装台账 ⑤事件审计 ⑥技能包-旧），
`goal.md` 亦记为「6-Tab」。⇒ **实施以表格 6 行为准（6 Tab）**，标题的「5」为笔误。

| Tab | 关键实施点 |
|---|---|
| ① **概览** | 指标卡 7 项（技能总数/活跃/已退役/版本总数/安装总数/覆盖 Agent 数/已进化技能数）+ 进化动作分布条（7 种 action 计数）+ Top 安装榜 |
| ② **技能库** | 搜索 + 标签过滤 + 状态下拉；卡片/表格双视图（沿用既有 `viewMode`）；行操作：详情 / 版本 / 血缘 / 停用或退役 / 删除（删除前 `Modal.confirm`） |
| ③ **EVO MAP** | 左多选技能 → 右侧树。节点 = `{SkillId}@{Version}`；**动作→颜色**：`create` 绿 / `patch` 蓝 / `split` 紫 / `compress` 青 / `retire` 灰 / `merge` 橙 / `fork` 品红；点节点 → 侧栏（发布者、时间、字节数、安装数、**SKILL.md 全文**，全文经 `getHubSkillVersion` 拉取） |
| ④ **安装台账** | 表格列：技能 / Agent 实例 / 工作区 / 已装版本 / 是否落后最新 / 安装时间；按 `agentInstanceId` 过滤；「全部更新」**只提示不代执行**（人类决策） |
| ⑤ **事件审计** | 时间线或表格：事件类型 / 技能 / 版本 / 操作者（agent·user·system）/ 工作区 / payload / 时间 |
| ⑥ **技能包（旧）** | 原「上传 zip 管理 Skill 包」整套功能**原样保留**（供 Agent 模板选包）——这是回归红线，不得删减 |

---

## 五、实现约束（不可协商）

1. **不新增 npm 依赖**。EVO MAP 用 antd `Tree` / `Tag` / `Badge` 表达层级；如需「继承自」连线，用纯 CSS 左边框。
2. **空态必须友好**：⚠️ 当前线上页面一片空白的直接原因就是**空数据无空态**。每个 Tab 都要有 `Empty` 文案。
3. **所有 API 失败必须 `message.error`**，不得静默吞掉。
4. 风格对齐 `agent-template-settings` / `storage` 页面（`PageContainer` + `ProTable` + `Card`）。
5. 文案中文硬编码（与仓内既有页面一致）；`menu.skillManagement` i18n 键保留不动。
6. **状态颜色需可辨识但不刺眼**：动作色板建议用 antd 预设 token（`green/blue/purple/cyan/default/orange/magenta`），不要自造 hex。

---

## 六、文件边界与提交规约

| 允许改 | 说明 |
|---|---|
`Source/PuddingPlatformAdmin/src/pages/skill-management/**` | 重写（可拆多个组件文件放同目录） |
`Source/PuddingPlatformAdmin/src/services/platform/api.ts` | **仅追加** `─── Skill Hub API ───` 段 |

**禁止改**：`Source/PuddingPlatform/**`、`Source/PuddingRuntime/**`、`Source/PuddingHost/**`、任何测试工程、`api.ts` 既有函数。

**提交**：`[agent:dsh] feat(skill-hub-ui): …` 前缀（协作协议 §10.1）；**selective add 只加上述文件**（工作区常年有他方在途改动，严禁 `git add -A`）。

---

## 七、验收标准

| # | 判据 | 方式 |
|---|---|---|
| V1 | 类型检查通过 | `npx tsc --noEmit`（或仓内既有 `npm run tsc` 等价脚本）**0 error** |
| V2 | 生产构建通过 | 前端生产构建成功（仓内既有命令，见 `PuddingPlatformAdmin/package.json`） |
| V3 | **空态可见** | 在无数据状态下 6 个 Tab 均渲染出空态文案，**不出现白屏** |
| V4 | 每 Tab 有数据渲染路径 | 代码层可追踪：每个 Tab 至少调用一个冻结函数，且渲染分支覆盖「有数据 / 无数据」 |
| V5 | 旧功能零回归 | `⑥ 技能包（旧）` 的原有能力（上传/列表/删除 zip 技能包）代码路径仍存在 |
| V6 | 边界未越界 | `git diff --name-only` 只含 §六 允许的两个路径 |

---

## 八、已知前置与待办

- ⚠️ **端到端联调需重启进程**（新端点尚在源码层，运行中进程未加载）→ 已向 6a8(协调) 申请窗口，**前端实现不必等待**。
- 提交后建议在 `code_map.md` 的 L2 索引（`Source/PuddingPlatformAdmin/code_map.md`，若存在）补一行 Skill Hub 页说明。
- 设计文档 §7.1 标题「5 个 Tab」的笔误建议在下次文档维护时改正为「6 个 Tab」（本 Brief 已裁定以此表为准，不改上游文档以免与他方编辑冲突）。
