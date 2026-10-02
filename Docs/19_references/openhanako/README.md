# OpenHanako 项目评估

> 来源：[liliMozi/openhanako](https://github.com/liliMozi/openhanako)  
> 参考：[DeepWiki](https://deepwiki.com/liliMozi/openhanako)  
> 日期：2026-05-05

---

## 1. 项目概览

Hanako 是一个**个人 AI Agent 平台**，运行在桌面端 (Electron)、后台服务 (Node.js Daemon) 和 CLI 三种模式。核心特点：

- **持久记忆系统**：SQLite `facts.db` + FTS5 全文检索 + 记忆衰减算法
- **分层人格模型**：Yuan (元/先天性格) → Ishiki (意识/后天人格) → Identity (身份)
- **三栏桌面 UI**：会话侧栏 + 对话主体 + Jian/Desk 预览面板
- **多语言**：中文、English、日本語、한국어、繁體中文
- **沙箱安全**：PathGuard + macOS Seatbelt / Linux Bubblewrap 双隔离
- **插件系统**：工具/技能/命令/路由/UI 五类扩展，Restricted → Full-Access 两级权限

| 维度 | Hanako |
|------|--------|
| 技术栈 | Electron + React + Vite + Hono + better-sqlite3 + Pi SDK |
| 数据库 | SQLite (better-sqlite3, 原生绑定) |
| LLM | Pi SDK 封装，支持多模型 |
| 部署 | Desktop (Electron) / Server Daemon / CLI |
| 跨平台 | macOS (Seatbelt) / Linux (Bubblewrap) / Windows (PathGuard) |
| 国际化 | 5 种语言 |

---

## 2. 架构设计

### 2.1 双进程模型

```
┌──────────────────────────────────────────────────┐
│ Electron Main Process (desktop/main.cjs)          │
│   ├─ 窗口管理 (Main/Settings/Onboarding)          │
│   ├─ 系统托盘                                     │
│   ├─ 崩溃恢复 (自动重启 Server)                   │
│   └─ IPC 路由                                      │
└────────────┬─────────────────────────────────────┘
             │ spawn + health check + restart
┌────────────▼─────────────────────────────────────┐
│ Server Process (server/index.js)                  │
│   ├─ Hono HTTP/WebSocket Server                  │
│   ├─ HanaEngine (核心编排)                       │
│   ├─ SessionCoordinator (会话管理)              │
│   ├─ ModelManager (模型管理)                    │
│   ├─ Hub (事件总线)                              │
│   └─ Memory / Sandbox / Plugin 子系统            │
└──────────────────────────────────────────────────┘
```

关键设计：Server 进程独立于 Electron，UI 关闭后后台常驻。Electron 崩溃不会导致 AI 任务中断。通过 `HANA_TOKEN` 安全通信。

### 2.2 核心路由

```
User Input → ChatRoute → HanaEngine → SessionCoordinator
  → Pi SDK SessionManager → LLM API
  → Stream → ContentBlock 解析 → WebSocket broadcast → UI
```

---

## 3. 记忆系统 (Memory System)

### 3.1 多层混合存储

Hanako 的记忆系统是最值得借鉴的部分，采用"渐进式披露"策略：

| 层次 | 文件 | 内容 |
|------|------|------|
| **Session 摘要** | `memory/summaries/*.json` | 每 10 轮或会话结束时 LLM 生成摘要 |
| **日记 (today)** | `memory/today.md` | 当日所有会话摘要汇总 (04:00 为"逻辑日"起点) |
| **周记 (week)** | `memory/week.md` | 滚动 7 天滑动窗口 |
| **长记 (longterm)** | `memory/longterm.md` | 周记"折叠"入长期背景 (最多 300 词) |
| **事实 (facts)** | `memory/facts.md` | 从 30 天内提取的稳定用户属性 |
| **编译** | `memory/memory.md` | 以上四文件组装 → 注入系统提示词 |
| **钉选** | `memory/pinned.md` | 用户定义的不朽记忆，总是存在 |
| **经验库** | `experience/*.md` | 按领域分类的"经验教训" |

### 3.2 Fact Store (v2)

SQLite FTS5 + 标签双检索策略：

```
search_memory(query, tags) →
  1. 标签匹配 (Tag Matching) — 精确检索
  2. 结果 < 3 → 降级到全文搜索 (FTS5)
  3. 返回组合结果
```

### 3.3 编译管道防抖 (Fingerprint)

每次编译前计算源文件哈希 (fingerprint)，若与上次一致则跳过 LLM 调用 —— 解决了"每次都调 LLM 编译记忆"的成本问题。

### 3.4 PII 脱敏

会话摘要存盘前自动调用 `scrubPII` 移除敏感信息。

### 对比 Pudding

| 维度 | Hanako | Pudding |
|------|--------|---------|
| 存储 | Markdown 文件 + SQLite 并存 | SQLite (pudding_memory.db) + JSONL |
| 检索 | FTS5 标签优先 + 全文降级 | FTS5 trigram + 将来向量 |
| 编译 | 四层管道 (today/week/longterm/facts) | 暂无 |
| 防抖 | Fingerprint 跳过重复 LLM 编译 | 暂无 |
| 经验 | `experience/` 目录按领域分类 | 三元组实体存储 (Phase 3 规划) |
| 钉选 | `pinned.md` 永久存在 | `Memories.SupersededBy` 替代链 |

---

## 4. 人格模型 (Personality)

### 4.1 三层人格体系

```
Yuan (元) ── 先天性格，决定思维风格
  ├─ hanako   均衡 (Balanced)
  ├─ butter   感性 (Emotional)
  ├─ ming     理性 (Rational)
  └─ kong     空 (Vanilla/原始模型)

Ishiki (意识) ── 后天人格，定义行为准则
  └─ identity.md ← 核心指令文件

Identity ── 对外身份，Bridge 中使用
  └─ 公开人格子集
```

### 4.2 对比 Pudding

| 维度 | Hanako | Pudding |
|------|--------|---------|
| 人格层数 | 3 层 (Yuan → Ishiki → Identity) | 7 层 (IDENTITY→SOUL→AGENTS→TOOLS→USER→MEMORY→RUNTIME) |
| 性格类型 | 4 种预设 (均衡/感性/理性/原始) | 无预设，完全由 Prompt 定义 |
| 文件管理 | `ishiki.md` 独立文件 | `PersonaPrompt` 字段 (数据库) |
| Avatar | 在 Yuan 中定义 | `AvatarEmoji` 字段 |

---

## 5. 工具与执行

### 5.1 工具系统

基于 Pi SDK 的标准工具接口，per-session 创建 (会话隔离)：

| 分类 | 工具 |
|------|------|
| 文件 | `read` / `write` / `edit` / `ls` / `find` / `grep` / `glob` |
| Shell | `bash` (沙箱化) |
| 网络 | `web_search` / `web_fetch` |
| 浏览器 | `browser` (WebContentsView 自动化) |
| 子代理 | `subagent` / `ask_agent` (最多 5 并发, 5 分钟超时) |
| 记忆 | `search_memory` / `record_experience` / `recall_experience` |
| 技能安装 | `install_skill` (带安全审计) |

### 5.2 Deferred Result (延迟结果)

`subagent` 等耗时任务使用 `DeferredResultStore`：Agent 派发任务后继续和用户聊天，后台完成后结果异步注入。

### 5.3 文件检查点 (CheckpointStore)

`write`/`edit`/`bash`(rm/mv) 操作前自动备份原文件。二进制文件 (>1024KB) 跳过。7 天自动清理。

### 5.4 对比 Pudding

| 维度 | Hanako | Pudding |
|------|--------|---------|
| 工具隔离 | Per-session 创建 | Singleton DI + SkillRuntime |
| 延迟执行 | DeferredResultStore + steer inject | 暂无 |
| 文件回滚 | CheckpointStore (修改前备份) | 暂无 |
| 浏览器 | WebContentsView 全交互 | 暂无 |
| 子代理 | 5 并发, 5min 超时 | AgentOrchestrator + SwarmOrchestrator |
| 技能安全 | LLM 审计 SKILL.md + GitHub Star 阈值 | 无审计机制 |

---

## 6. 沙箱安全

### 6.1 双层隔离

```
┌──────────────────────────────────┐
│ Layer 1: PathGuard               │  所有文件工具通
│   ├─ 路径解析 (symlink → 真实路径)│  过此层
│   ├─ 白名单验证 (allowedPaths)    │
│   └─ 拒绝返回 blockedResult (LLM可读)│
├──────────────────────────────────┤
│ Layer 2: OS Sandbox              │
│   ├─ macOS: sandbox-exec + Seatbelt│
│   ├─ Linux: bwrap (mount namespace)│
│   └─ Windows: PathGuard only     │
└──────────────────────────────────┘
```

### 6.2 双模式

| 模式 | PathGuard | OS Sandbox | 场景 |
|------|-----------|------------|------|
| **standard** | ✅ | ✅ (macOS/Linux) | 默认安全模式 |
| **full-access** | ❌ | ❌ | 专家模式，无限制 |

### 6.3 Bash 安全

- **Preflight 检查**：匹配 `sudo`/`chmod`/`reg delete` 等模式立即阻断
- **路径提取**：启发式提取命令行中的绝对路径，逐一验证

### 6.4 对比 Pudding

| 维度 | Hanako | Pudding |
|------|--------|---------|
| 文件沙箱 | PathGuard + Seatbelt/bwrap | DockerSandboxProvider |
| 工具包装 | wrapPathTool + wrapBashTool | SkillRuntime + Guardrails |
| 平台差异 | 三个平台不同实现 | Docker 统一抽象 |
| 阻止反馈 | blockedResult (LLM 可读) | Exception + error message |
| 技能安全 | LLM 审计 + GitHub Star 阈值 | 暂无 |

---

## 7. 插件系统

### 7.1 两级权限

| 权限 | 可用能力 |
|------|---------|
| **Restricted** (默认) | Tools / Skills / Commands / Agent Templates / Private Config |
| **Full-access** | 以上全部 + index.js lifecycle / HTTP Routes / Pi SDK Extensions / LLM Providers / UI Pages |

### 7.2 热插拔

安装/启用/禁用插件不需要重启应用。

### 7.3 EventBus

插件通过 `PluginContext.bus` 与核心系统和彼此通信，信任级别限制 bus 访问范围。

### 7.4 对比 Pudding

| 维度 | Hanako | Pudding |
|------|--------|---------|
| 扩展方式 | `manifest.json` + 目录扫描 | DI 注册 (编译时) |
| 热插拔 | ✅ | ❌ (需重启) |
| UI 扩展 | Pages + Widgets 均可 | 暂无插件 UI 机制 |
| 事件通信 | EventBus | EventBus (已废弃 RabbitMQ, 待 P2P) |

---

## 8. 关键借鉴点 (对 Pudding 的启发)

| 优先级 | 特性 | 说明 |
|--------|------|------|
| **P0** | 记忆编译管道 | today→week→longterm→facts 四层管道，fingerprint 防抖 |
| **P0** | 文件检查点 | 写/删文件前自动备份，7 天自动清理 |
| **P1** | Deferred Result | 耗时任务异步执行，完成后 steer inject 到对话 |
| **P1** | blockedResult | 工具被阻断时返回 LLM 可读结果而非抛异常 |
| **P1** | PII 脱敏 | 存盘前自动脱敏 |
| **P2** | 标签 + FTS 双检索 | 标签精确匹配优先，不足时降级 FTS |
| **P2** | Fingerprint 防抖 | 编译前计算哈希，避免重复 LLM 调用 |
| **P2** | 插件热插拔 | 动态扫描 + 生命周期管理 |

---

## 9. 技术亮点

1. **双进程保活**：Electron 崩溃不影响后台 AI 任务
2. **逻辑日**：04:00 为日切点，自然对齐人类作息
3. **渐进式记忆披露**：注入`memory.md`到系统提示词，深层细节通过工具检索
4. **Yuan 性格系统**：不是简单的 prompt 模板，而是影响 UI 动画/启动画面/措辞
5. **Experience 经验库**：按领域分类的"教训"存储，不同于简单的事实键值对
6. **Windows 兼容**：在无法使用 bwrap 的平台上回退到纯 PathGuard + custom exec
