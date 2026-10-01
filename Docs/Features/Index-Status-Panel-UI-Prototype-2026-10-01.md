# 索引与检索 · 状态面板 UI 原型设计（2026-10-01）

> 交付物：本文件 + 两张线框图（同目录）
> - `admin-wireframe.svg` —— Admin「索引与检索」页（L0/L1/L2 三层 + 状态条六形态）
> - `chat-entry-wireframe.svg` —— chat 侧入口（状态条 + popover + 四态）
>
> 依据：`GET /api/admin/index/status`（`PuddingHost/Controllers/IndexAdminController.cs`）
> 契约真源：`PuddingHost/Services/FullTextIndexStatusProbe.cs` ↔ 前端 `src/pages/index-status/types.ts`

---

## 0. 一页速览

| 项 | 结论 |
|---|---|
要解决什么 | 索引静默失效（2026-10-01：E: 盘消失 ⇒ 索引 225 → 0 条目）**只能靠「搜不到」发现** |
设计目标 | 让「检索好不好用」从**需要理解**变成**不需要理解**：一行结论 + 坏了自己找上门 |
信息架构 | **L0 结论条**（常驻 1 行）→ **L1 四张诊断卡** → **L2 原始字段**（默认折叠） |
面板形态 | **Admin 独立路由**（详情唯一真源）+ **chat 一行入口**（跳转） |
后端改动 | **零**。L0 聚合态由现有字段在前端推导（纯函数、可单测、可变异取红） |
不做 | 「重建索引」按钮（端点只读）· 自动弹窗/跳转 · 每页全局轮询 · 复活 `IndexIndicator.tsx` |

**一句话判据**：用户不打开任何页面，也应该在索引坏掉的当天知道它坏了。

---

## 1. 信息架构：三层，按「要回答的问题」切

| 层 | 回答的问题 | 内容 | 默认 |
|---|---|---|---|
**L0 结论条** | 「现在能不能放心让 Agent 去搜？」 | ① 索引就绪态 ② 新鲜度（距今） ③ scope 受理/拒绝 ④ 体积占用 | 常驻 1 行 |
**L1 诊断卡** | 「哪儿不对？」 | A 全文索引 · B 符号索引 · C 供给台账 · D 配置与受理 | 四张卡 |
**L2 原始字段** | 「原始事实是什么？」 | 逐 scope 8 列 + 逐 job 9 列 + `generatedAtUtc` | **折叠**，由 L0 的「原始字段 ☐」联动 |

**为什么这么分**：现有 `/index-status` 页把三层压成一层（字段名当列标题、8 列宽表），信息齐全但**没有结论**。分层后，默认视图是结论，排障时才下钻到字段——**字段一个不丢**，只是不再抢占首屏。

### 做减法（同样重要）
- `jobId` 默认层不显示（L2 才出现）
- `compositionCreated` 不直接露面 → 翻成「供给组件：已启动 / 未启动」
- `indexDirectory` 等长路径放 L2；L1 只显示「索引根」
- **不加**「重建索引」按钮：端点只读，写操作需另立切片 + 审批

---

## 2. 状态矩阵（L0 聚合态 · 首条命中即生效）

这是本设计的核心，**必须按此顺序实现**，每一行对应一条单测（可变异取红）。

| # | 条件 | level | 标题文案 |
|---|---|---|---|
1 | `enabled === false` | `off` | 全文索引已关闭 |
2 | `rejectedReasons.length > 0` | `error` | 配置被拒：N 条 scope 未受理 |
3 | 任一 scope `scopeExists === false` | `error` | scope 路径不存在 |
4 | 关键字段为 `null`（见 §4 清单） | `unknown` | 状态未知 |
5 | `jobs` 含非终态（`finishedAt === null`） | `busy` | 正在重建 |
6 | 全部 scope `hasIndex !== true` 或 `indexEntryCount === 0` | `warn` | 索引为空 · 尚未建立 |
7 | `indexBytes / maxIndexBytes ≥ 0.8` | `warn` | 索引体积接近上限 |
8 | 最近终态 job 状态含失败语义 | `warn` | 最近一次供给失败 |
9 | 以上皆否 | `ok` | 索引就绪 |

**顺序理由（三条容易搞错的）**
- `off` 排第一：**关闭是明确状态，不是故障**，必须早于任何 null/空判定，否则关闭态会被判成「未知」或「空」。
- `busy` **排在 error/unknown 之后**：正在重建不能掩盖「scope 不存在」这类硬错误。
- `unknown` 排在 error 之后：明确的错误优先于未知（已知坏 > 不知道）。

**`off` 的视觉**：中性灰（`unk`），**不染红**。否则每次正常关闭都像故障 → 告警疲劳。

**`busy` 不许造假进度**：后端只给 `indexedFileCount` / `totalBytes` / `elapsedMs`，**没有** processed/total ⇒ 只能说「已清点 4,560 文件 · 82.8 MB · 已用 46 秒」。**禁止编造百分比进度条**（详见 §6 缺口 ①）。

---

## 3. 文案表（逐字使用，不得改写）

| level | 第一行 | 副行 / detail |
|---|---|---|
`ok` | `索引就绪 · <相对时间>更新` | 最后写入 <绝对时间 UTC> |
`off` | `全文索引已关闭（enabled=false）` | 配置未开启，不是故障 |
`error`(scope) | `scope 路径不存在：<path>` | 检查 `D:\Data\config\system.json` → `FullTextIndex.Scopes` |
`error`(rejected) | `配置被拒：<n> 条 scope 未受理` | 逐条列出 `rejectedReasons` 原文 |
`unknown` | `状态未知 · 探测失败` | **不代表**「未启用」或「不存在」 |
`busy` | `正在重建 · 已清点 <n> 文件 · <bytes> · 已用 <s> 秒` | 无进度百分比（后端未提供） |
`warn`(empty) | `索引为空（0 条目）· 尚未建立` | scope 存在、索引目录存在，但无条目 |
`warn`(size) | `索引体积接近上限（<pct>%）` | <bytes> / <max> |
`warn`(job) | `最近一次供给失败：<state>` | <message> 原文 |

**台账为空的三种如实说法**（`jobsReason`，禁止显示空表格）
- `composition-not-created` → 「供给组件尚未启动（无人触发过预建）」
- `no-jobs-recorded` → 「供给组件已启动 · 暂无 job」
- `ledger-read-failed` → 「台账读取失败 ⇒ 内容不可知（≠ 没有 job）」

---

## 4. 渲染纪律

| 纪律 | 做法 |
|---|---|
**三态贯穿到人话层** | `null` → 灰「未知」+ tooltip「探测失败，不代表不存在」；`false` → 「否」；`0` → 「0」。**禁止 `?? 0` / `\|\| '否'`** |
**判 `unknown` 的字段清单** | `hasIndex` · `indexDirectory` · `indexDirectoryExists` · `indexEntryCount` · `indexBytes` · `indexDirectoryLastWriteUtc` · `maintenance.enabled` 任一为 `null` |
**时间** | 相对为主（「12 分钟前」），绝对放 tooltip |
**字节** | 人类可读（106.0 MB），原始字节进 tooltip |
**颜色** | 只用 5 族：ok 绿 / warn 黄 / error 红 / **unknown 灰** / busy 蓝。**关闭态用灰** |
**双编码** | 每个状态**同时**有图形（● ▲ ○ ■ ◐）与文字标签 ⇒ 灰度打印/色盲可辨 |
**快照时刻可见** | 页首显示 `generatedAtUtc`；30 秒轮询（沿用 storage 页 `window.setInterval` + 卸载清理） |
**⚠️ 不说满话** | 本页只证明「**索引就绪**」，**不等于「搜得到」**；后者需真跑一次查询（建议做成手动「试搜」按钮，只读、用户触发、不自动跑） |

---

## 5. 入口设计

### 位置
| 入口 | 位置 | 状态 |
|---|---|---|
① Admin 菜单 | `/index-status`（`config/routes.ts` 已注册） | 已有。**两处待改**：名称 → 「索引与检索」；图标 `database` → `search`（现与 `/memory-library` 重复） |
② chat 状态条 | `ComposerStatusDetails` 的「索引」槽位 | **待做**。现状 `IntentConsole.tsx:819-823` 硬编码 `index: 'disabled'` ⇒ **永远显示「未启用」** |
③ 异常告警 | 同一槽位转红 | 随 ② 一起（同一纯函数，改色即可） |

### 触发
| 入口 | 触发 | 明确不做 |
|---|---|---|
Admin | 菜单点击 / URL 深链 / 从 chat 跳转 | — |
chat | hover 或点击 → popover → 「打开完整面板 →」（新标签） | ❌ 自动弹窗 ❌ 自动跳转 ❌ 每页全局轮询 |

### 为什么详情不进 chat
`maxChatRouteChunkBytes = 496 KiB`，实测 `chat=343282`，余量 **164,622 B（32.3%）**。表格/Descriptions/Drawer 会持续吃这份余量，而 chat 是高频页——风险不对等。⇒ **一行 + 轻量 popover，其余留 Admin。**

### 菜单归并
短期保持顶层（已交付、有测试，改动有回归成本）；中期并入 `/diagnostics` 或 `/system-config` 分组——与 `/storage`、`/diagnostics` 同族。

---

## 6. 后端缺口（诚实登记，不在本片解决）

| # | 缺口 | 影响 | 归属 |
|---|---|---|---|
① | **无进度百分比**（无 processed/total，只有 `indexedFileCount`/`totalBytes`/`elapsedMs`） | 「索引进度」只能给「已清点 + 已耗时」，给不了「还剩多久」 | 可选：`S-A2+` 增字段；UI 已预留位置 |
② | **符号索引块未接入**（`codeIndex` 不在 wire 里） | L1 的 B 卡为占位 | `S-A2`：并入 `ICodeIndexMaintenance.GetScopeStatuses()` |
③ | **「能否搜到」无法从状态推导** | 面板可能报绿而检索实际不可用 | 设计上已用「索引就绪」而非「检索可用」措辞；如需强证据，需手动「试搜」探针 |
④ | `maintenance.configured=false`（本线无绑定器） | D 卡显示「未配置 · 是否生效：未知」 | **这是本线真相，不是缺陷** |

---

## 7. 实现要点（给实现者）

1. **零后端改动**：端点与本切片无关，保持不动。
2. **聚合态务必抽成纯函数**（如 `deriveIndexHealth(snapshot) → {level,title,detail}`），不要写在 JSX 里；否则无法单测、无法变异取红。
3. **既有 16 条 jest 断言必须保持绿**（`index-status/index.test.ts` 覆盖三态纯函数，是后端 R5 纪律的 UI 侧保护）。
4. **不得引入 `src/pages/chat/**` 的任何模块**（保 chat 目录零接触）。
5. **门禁**：`pnpm run build` 必须过，且逐字记录 `[chat-bundle-budget] ok sync=<n> chat=<m> common=<k>`，三项**不得劣于** `sync=1375874 chat=343282 common=391155`。
6. **变异取红**：至少两条 —— ① 打乱状态矩阵优先级（把 `off` 移到 `unknown` 之后）⇒ 关闭态用例必须红；② 把三态折叠成 `?? 0` ⇒ 未知态用例必须红。
7. **提交**：白名单提交，只含本切片文件；chrome 侧大量他人 WIP 一律不碰。

---

## 8. 待裁定（用户）

| # | 问题 | 建议 |
|---|---|---|
1 | 面板定位：L0+L1 默认、L2 折叠 | 建议采纳（字段一个不丢，只是不再抢首屏） |
2 | 符号索引：等 `S-A2` 一起做，还是先占位 | 建议先占位（占位也需文案，避免读成「坏了」） |
3 | chat 状态条是否做（会动 `chat/**`） | 建议做——**它是「坏了找上门」的唯一载体**；余量 32.3% 够 |
4 | 菜单改名「索引与检索」+ 图标 `search` | 建议现在做（一行改动，成本近零） |
