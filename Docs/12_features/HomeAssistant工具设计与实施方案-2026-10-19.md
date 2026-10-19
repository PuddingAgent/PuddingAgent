---
title: Home Assistant 工具设计与实施方案（调研阶段）
author: 默认助手
date: 2026-10-19
last_reviewed: 2026-10-19
status: proposed
description: 调研 Home Assistant 的 REST 与 WebSocket API 能力边界，给出 Agent 工具的功能切片、权限分级、危险域拦截、凭据存储与叶子组件架构提案；本阶段只调研与设计，不含实现。
categories:
  - docs
  - features
tags:
  - home-assistant
  - tools
  - architecture
  - security
  - rest
  - websocket
related_docs:
  - ../10_conventions/组件化交付规程.md
  - SSH工具组件化设计与实施方案-2026-10-08.md
  - ../07_architecture/110ADR-096SSH执行前置围栏与有界终止ADR.md
related_files:
  - Source/PuddingCore/Tools/PuddingToolContracts.cs
  - Source/PuddingCore/Models/ToolPermissionLevel.cs
  - Source/PuddingHost/Tools/ScreenCaptureTool.cs
slug: features-home-assistant-tool-design-2026-10-19
draft: false
---

# Home Assistant 工具设计与实施方案（调研阶段）

本文只做**调研与设计**，不含实现。目标是先回答三个问题：HA 能做什么、我们该暴露哪些能力、以什么边界与权限暴露。

实测基线：`http://192.168.100.10:8123`，HA Core **2026.2.1**，容器 `home-assistant`，长期访问令牌（JWT，10 年有效期）已保存于 `D:/Keys/ASUS.md`，实测 `GET /api/` 返回 `API running.`、`GET /api/states` 返回 **2,217 个实体**、`/api/config` 报告 **123 个组件**。

## 1. 结论摘要

| 决策 | 结论 | 理由 |
|---|---|---|
| 首发协议 | **REST（`/api/…`）** | 一次性请求、无连接生命周期、最易做有界超时与独立测试 |
| 订阅能力 | **WebSocket 后置（切片 C）** | 需要重连、背压、订阅归属与资源回收；官方亦建议持续变更用 WS |
| 工具粒度 | **4 个工具**（不是 1 个万能 action 工具） | 读与写必须是**不同权限等级**，合并会导致「读被迫升到 High」或「写被降级」 |
| 危险动作 | **域白名单 + 永久黑名单** | HA 一个 service call 可开锁、开车库门、撤防、跑 `shell_command` |
| 实例寻址 | **预登记实例，工具参数不得携带 URL/Token** | 否则等价于开放 SSRF（让 Core 请求内网任意地址） |
| 凭据存储 | **Agent 私有目录** `secrets/home-assistant.json`，不进仓库、不进工具参数 | 与 SSH 组件同口径：不引入 KeyVault，不接受调用方覆盖 |
| 组件形态 | 叶子组件 `PuddingHomeAssistant`（`ProjectReference=0`） | 遵守[组件化交付规程](../10_conventions/组件化交付规程.md) S1–S5 |

## 2. 调研：HA 的两套 API 能力边界

### 2.1 REST（官方定位：简单、一次性请求）

| 端点 | 方法 | 权限 | 用途 |
|---|---|---|---|
`/api/` | GET | 用户 | 健康检查（`{"message":"API running."}`） |
`/api/config` | GET | 用户 | 位置、单位、时区、版本、组件 |
`/api/core/state` | GET | 用户 | 快速健康检查（是否运行中、是否在迁移） |
`/api/components` | GET | 用户 | 已加载组件列表 |
`/api/config/core/check_config` | POST | **Admin** | 校验 configuration.yaml |
`/api/states` | GET | 用户 | 全部可读状态 |
`/api/states/<entity_id>` | GET / POST / DELETE | 用户 / **Admin** / **Admin** | 读单个；**POST 只改 HA 显示、不驱动设备** |
`/api/services` | GET | 用户 | 已注册动作（旧名 services） |
`/api/services/<domain>/<service>` | POST | 用户 | **调用动作**（可 `?return_response`） |
`/api/events` | GET | 用户 | 有监听者的事件类型 |
`/api/events/<event_type>` | POST | **Admin** | 触发事件（写入数据库） |
`/api/stream` | GET | **Admin** | SSE 全部事件（官方建议改用 WS） |
`/api/template` | POST | **Admin** | 渲染 Jinja 模板 |
`/api/history/period/<ts>` | GET | 用户 | 状态历史（需 `filter_entity_id`） |
`/api/logbook/<ts>` | GET | 用户 | 日志簿 |
`/api/error_log` | GET | **Admin** | 当前会话日志（纯文本） |
`/api/camera_proxy/<entity_id>` | GET | 用户 | 摄像头当前帧（二进制） |
`/api/calendars` / `/api/calendars/<id>` | GET | 用户 | 日历与事件 |
`/api/intent/handle`、`/api/conversation/process` | POST | 用户 | 意图 / 对话代理 |

官方明确提示：**POST `/api/states` 不驱动设备**，要控制设备必须调用动作（service）。

### 2.2 WebSocket（`/api/websocket`，前端用的主 API）

协议：连接 → 服务端发 `auth_required` → 客户端发 `auth`（token）→ `auth_ok` / `auth_invalid`；之后每条命令带自增 `id`，服务端用同 `id` 回 `result`；订阅消息为 `type=event`。

| 命令 | 权限 | 说明 |
|---|---|---|
`subscribe_events` / `subscribe_entities` / `subscribe_trigger` | 用户 / 用户 / **Admin** | 实时事件、状态增量（`a` 新增 / `c` 变更 / `r` 移除） |
`unsubscribe` | 用户 | 结束订阅 |
`call_service` | 用户 | 调用动作，可 `return_response` |
`fire_event` | **Admin** | 触发事件 |
`get_states` / `get_config` / `get_services` / `get_panels` | 用户 | 读取 |
`ping` | 用户 | 心跳 |
`validate_config` | 用户 | 校验触发/条件/动作 |
`extract_from_target`、`get_triggers_for_target`、`get_conditions_for_target`、`get_services_for_target` | 用户 | 目标解析与能力发现 |
`render_template` / `test_condition` / `execute_script` | 用户 | 模板、条件、脚本 |
`entity/source`、`manifest/list` | 用户 | 实体来源、集成清单 |

稳定错误码：`invalid_format`、`not_found`、`not_allowed`、`not_supported`、`unauthorized`、`unknown_command`、`id_reuse`、`home_assistant_error`、`service_validation_error`、`template_error`、`timeout`、`unknown_error`。

### 2.3 选型对照

| 维度 | REST | WebSocket |
|---|---|---|
实现复杂度 | 低 | 高（握手、id 复用、重连、背压） |
取消语义 | HTTP 取消即断 | 需显式 `unsubscribe` + 连接回收 |
有界输出 | 逐响应天然有界 | 流式，需自建预算 |
适合 | 读状态、调动作、查历史 | 实时跟随、事件驱动 |
结论 | **首发主力** | **切片 C**（仅在确有实时需求时） |

## 3. 工具设计

### 3.1 工具清单与权限（关键：读写分离）

| 工具 | 用途 | `ToolCategory` | `ToolPermissionLevel` | `ToolSafetyFlags` |
|---|---|---|---|---|
`ha_instances` | 列出预登记实例 + 健康检查（含 `GET /api/` 与 `/api/config` 摘要） | Query | **Low** | `ReadOnly \| RequiresNetwork \| ConcurrencySafe` |
`ha_states` | 读状态：全量/按 domain/按 entity_id；支持字段投影与条数上限 | Query | **Low** | `ReadOnly \| RequiresNetwork \| ConcurrencySafe` |
`ha_history` | 历史与日志簿（`history/period`、`logbook`） | Query | **Low** | `ReadOnly \| RequiresNetwork \| ConcurrencySafe` |
`ha_call_service` | 调用动作（含 `return_response`） | Execute | **High** | `Destructive \| RequiresNetwork` |

> **为什么必须拆开**：`[Tool]` 属性只有一个 `Permission`。若把读和写合进一个工具，读操作会被迫继承 High（每次询问用户），或写操作被降级为 Low（物理副作用无门禁）。这与 `PuddingToolBase<TArgs>` 的描述符模型直接冲突，因此拆成两个工具是**结构性要求**，不是风格偏好。

切片 C（按需）：
| `ha_subscribe` | 建立写入 Core 的订阅（状态变更/事件），后续 `ha_events` 读取缓冲 | Execute | High | `RequiresNetwork` |

切片 B（可选，需要时）：`ha_camera_snapshot`（`/api/camera_proxy/<entity>` → Vision Artifact），`ToolPermissionLevel.Low`，但需**隐私开关**（默认关闭）。

### 3.2 参数形状（草案）

`ha_states`：
```json
{ "instance": "asus", "domain": "light", "entityId": "light.study", "limit": 50, "fields": ["state","last_changed"] }
```
- `entityId` 存在时忽略 `domain`
- `limit` 默认 50、上限 500；超限**明确回报 `truncated=true` 与总数**，不静默截断
- `fields` 缺失时返回精简投影（不含 `attributes` 全量，避免 2,217 实体的 MB 级载荷）

`ha_call_service`：
```json
{ "instance": "asus", "domain": "light", "service": "turn_on",
  "target": { "entity_id": ["light.study"] },
  "data": { "brightness": 128 },
  "returnResponse": false }
```
- 必须显式给出 `domain` + `service`；**拒绝**任何形如通配或列表的 domain
- `target`/`data` 原样透传，但受第 4 节域策略约束

### 3.3 输出有界

`GET /api/states` 在实测实例上有 **2,217 个实体**，原始 JSON 达 MB 级。约束：

- 服务端先按 `domain` / `entityId` 过滤，再投影字段，最后按 `limit` 截断
- 返回体带 `total`、`returned`、`truncated`
- 单次响应序列化上限（建议 256 KiB），超限按条截断而非报错
- 禁止把原始 `attributes` 全量回传（相机实体带 base64 缩略图，单条可达数百 KB）

## 4. 安全模型（本设计最重要的一节）

### 4.1 物理副作用是真实风险

一个 `POST /api/services/<domain>/<service>` 能做的事远超「开灯」：

| 危险面 | 例子 | 后果 |
|---|---|---|
门锁 | `lock/unlock` | **开锁** |
安防 | `alarm_control_panel/alarm_disarm` | **撤防** |
车库/门 | `cover/open_cover` | **开门** |
任意脚本 | `script/*`、`automation/trigger` | 连锁动作 |
主机命令 | `shell_command/*` | **在宿主机执行任意命令** |
实例管理 | `hassio/*` | Supervisor 级操作 |
自毁 | `homeassistant/restart`、`homeassistant/stop` | 服务中断 |

### 4.2 域策略（默认拒绝 + 永久黑名单）

```
永久拒绝（不可配置放开）：shell_command, hassio, homeassistant, python_script, pyscript, node_red
默认拒绝（需用户显式加入白名单）：lock, alarm_control_panel, cover, climate, vacuum, camera, script, automation
默认允许（白名单起点）：light, switch, fan, media_player, scene, input_boolean, notify, tts
```

- 白名单存于实例配置（`secrets` 同级，仅用户可改），**Agent 不可改**
- 命中黑名单 → 返回稳定错误码 `ha.domain_blocked`，消息里说明该域被策略禁止，不透露是否真实存在
- 即便域在白名单内，`ha_call_service` 仍是 `High` 权限，需运行时确认

### 4.3 实例寻址（防 SSRF）

- 实例**必须预登记**：`{ alias, baseUrl, token, allowInsecureHttp, caBundle? }`
- 工具参数只能传 `alias`；**不接受 `url` / `host` / `token`**
- `baseUrl` 建议限制为私网地址或显式 allowlist；若允许公网域名，需校验协议（默认 https，`http` 需实例级开关）
- 重定向：`HttpClientHandler.AllowAutoRedirect=false`，避免被 302 引向内网其它服务

### 4.4 凭据存储

```
<DataRoot>/agents/<agentInstanceId>/secrets/home-assistant.json
```

- 文件权限收紧（Windows ACL 仅当前用户 + SYSTEM）
- `token` 不写入日志、不进工具回执、不进错误消息（错误只报「认证失败」与状态码）
- 工具**不接受**调用方覆盖凭据

### 4.5 稳定错误码

| 码 | 触发 |
|---|---|
`ha.not_configured` | 无该 alias 的实例 |
`ha.instance_unreachable` | 连接失败 / DNS / TLS |
`ha.auth_failed` | 401 |
`ha.forbidden` | 403（或访问 Admin 端点而用户非管理员） |
`ha.not_found` | 404 |
`ha.invalid_request` | 400（含 `?return_response` 用错） |
`ha.service_error` | 500（动作执行失败） |
`ha.unavailable` | 503（HA 正在关闭） |
`ha.domain_blocked` | 命中黑名单 |
`ha.timeout` | 超过 deadline |
`ha.output_truncated` | 结果超预算被截断（非失败，带 `truncated=true`） |

## 5. 组件与依赖边界

```text
Agent 工具调用
      │
      ▼
PuddingHost/Tools/HomeAssistantTools.cs      ← 薄适配：参数 → 请求 DTO → 投影结果
      │
      ▼
PuddingHomeAssistant（叶子组件，ProjectReference = 0）
  ├─ Contracts/   实例 DTO、请求/结果、错误码（公开合同不出现第三方类型）
  ├─ Policy/      域白名单判定、动作名规范化、输出预算
  ├─ Http/        HttpClient 工厂、鉴权头、超时、重定向禁用
  ├─ Rest/        states / services / history / config 端点封装
  └─ Diagnostics/ 失败分类（HTTP 状态码 → 稳定错误码）
      │
      ▼
HttpClient（BCL）+ System.Text.Json
```

约束：
- 仅允许 `BCL + System.Text.Json`，**不得**引用 PuddingCore/PuddingRuntime/PuddingHost
- 组件内不读配置、不解析 DataRoot（由引用方冻结成 `HaInstance` 传进来）
- 输出预算在组件内强制（不是靠调用方记得截断）

## 6. 交付切片

| 切片 | 内容 | 验收终点（可证伪） |
|---|---|---|
A1 | 叶子组件 + 独立测试工程 | `dotnet test` 全绿；边界测试证明零 Pudding 引用；变异取红 |
A2 | 真实实例探针（S3） | 对本机 `192.168.100.10:8123` 实跑：`/api/`、`/api/config`、`/api/states?domain=light`，输出有界（≤256 KiB） |
A3 | S5 接入：`ha_instances` + `ha_states` + `ha_history` | 工具出现在目录、`Low` 权限自动授权、实测读到 2,217 实体中的前 N 条并带 `truncated` |
A4 | `ha_call_service` + 域策略 | 白名单域成功；黑名单域返回 `ha.domain_blocked`；变异取红证明拦截生效 |
B | 相机快照 / 日历（可选） | 按需评估 |
C | WebSocket 订阅 | 仅当出现「必须实时」的需求再立项 |

## 7. 风险与未决问题

| # | 问题 | 现状 | 建议 |
|---|---|---|---|
1 | 本机实例**已被公网扫描**（日志中 18 次 `/etc/passwd` 路径穿越） | 已观测 | 接入前先给 nginx/HA 加来源限制；工具不要成为新的暴露面 |
2 | 令牌 **10 年有效** | 已保存 | 建议重建为较短 lifespan（WS 命令支持 `lifespan` 天） |
3 | `ha_call_service` 的运行时确认粒度 | 未定 | 建议按 domain 分级：白名单域一次确认可批量，危险域逐次确认 |
4 | 是否需要 `ha_template`（`POST /api/template`，Admin） | 未定 | **暂不提供**：模板等价于在 HA 内执行任意逻辑，风险高于收益 |
5 | `?return_response` 的兼容性 | 官方明确：只对支持的动作加，否则 400 | 由调用方显式传 `returnResponse`，失败时把 400 原文回传 |
6 | 多实例支持 | 未定 | 设计上按 alias 寻址，天然支持多实例；首期只登记一台 |

## 8. 本阶段不做的事

- 不写任何实现代码（本文件仅为设计）
- 不申请/轮换令牌，不修改 HA 配置
- 不引入 WebSocket、不提供 `shell_command` / `hassio` / `homeassistant.*` 任何通路
- 不把令牌写入仓库（已实测 `git grep` 0 命中）
