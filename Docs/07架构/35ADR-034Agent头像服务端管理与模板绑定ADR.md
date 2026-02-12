# 35 ADR-034 Agent 头像服务端管理与模板绑定

> 状态：**proposed**  
> 日期：2026-05-23  
> 范围：Agent 头像资源归属、头像元数据 API、全局 / Workspace Agent 模板头像选择、聊天界面头像加载  
> 关联：[../Features/Agent头像服务端管理设计方案](../Features/Agent头像服务端管理设计方案.md)

---

## 1. 背景

当前头像资源位于前端目录：

- `Source/PuddingPlatformAdmin/src/assets/avatars/*.png`
- `Source/PuddingPlatformAdmin/src/assets/avatars/avatars.json`

这会导致三个问题：

1. **资源所有权错误**：头像是系统级预置资源，应由服务端统一管理；前端只负责读取配置和渲染。
2. **配置形态落后**：`/admin/global-agent-template` 仍使用 `avatarEmoji` 输入框，无法选择系统内预置头像，也无法展示头像元数据。
3. **聊天头像链路不闭环**：聊天界面已有图片头像渲染能力，但数据源没有稳定地从 Agent 模板配置解析到 `avatarUrl`。

本 ADR 决定将头像图片和描述 JSON 迁移到服务端，并把 Agent 模板的头像配置从自由 Emoji 输入升级为服务端头像 ID 选择。

---

## 2. 决策驱动因素

| 驱动因素 | 说明 |
|----------|------|
| 服务端单一事实源 | 头像图片、元数据、启用状态、排序和默认值由服务端负责。 |
| 前端只配置 ID | 前端不再 import 本地头像资源，只保存 `avatarId`，渲染使用服务端返回的 `avatarUrl`。 |
| 默认值明确 | 用户未配置头像时，默认使用服务端头像列表排序后的第一个启用头像。 |
| 兼容现有数据 | `AvatarEmoji` 和 `WorkspaceAgent.AvatarUrl` 暂时保留为 legacy fallback，不作为新功能主路径。 |
| 可扩展 | 后续可加入上传头像、禁用头像、按角色筛选头像，而不需要改前端静态资源。 |

---

## 3. 方案对比

### 方案 A：前端继续维护头像资源，只新增下拉选择

- **做法**：保留 `src/assets/avatars`，前端从本地 JSON 生成下拉菜单。
- **优点**：改动最小。
- **缺点**：头像仍是前端事实源；服务端无法校验模板头像；聊天 API 仍需绕回前端本地映射。
- **结论**：不采纳。

### 方案 B：服务端静态资源 + DB 元数据 + 模板存 `avatarId`（采纳）

- **做法**：图片和 `avatars.json` 移到服务端静态目录；启动时将 JSON 幂等种子到 `AgentAvatars` 表；模板保存 `AvatarId`；DTO 返回解析后的 `AvatarUrl`。
- **优点**：服务端可校验、可排序、可禁用；前端只消费 API；聊天链路能稳定拿到图片 URL。
- **缺点**：需要新增表、迁移、种子逻辑和 API。
- **结论**：采纳。

### 方案 C：只保留服务端 JSON，不入库

- **做法**：服务端读取静态 JSON 并提供 API，模板直接保存 JSON 中的 ID。
- **优点**：不需要数据库迁移。
- **缺点**：无法管理启用状态、排序和审计字段；每次请求读文件或做缓存；未来后台管理能力受限。
- **结论**：不采纳。

---

## 4. 决策

### ADR-034-A：头像资源移到服务端静态目录

头像资源从前端目录迁移到服务端：

```text
Source/PuddingPlatform/wwwroot/assets/agent-avatars/
├── agent-avatar-neutral.png
├── agent-avatar-smile.png
├── agent-avatar-sleepy.png
├── agent-avatar-thinking.png
├── agent-avatar-angry.png
├── agent-avatar-silver.png
├── agent-avatar-amber.png
├── agent-avatar-mint.png
└── avatars.json
```

运行时公开 URL：

```text
/assets/agent-avatars/{fileName}
```

`Source/PuddingPlatformAdmin/src/assets/avatars` 在前后端改造完成后删除。前端不得再通过 `import` 或相对路径引用头像文件。

### ADR-034-B：新增 `AgentAvatarEntity`

服务端新增头像元数据表 `platform.AgentAvatars`，用于管理预置头像：

| 字段 | 类型 | 说明 |
|------|------|------|
| `Id` | int | DB 主键 |
| `AvatarId` | string(128) | 稳定业务 ID，如 `neutral`、`thinking` |
| `Name` | string(128) | 展示名称 |
| `FileName` | string(256) | PNG 文件名 |
| `UrlPath` | string(512) | 服务端静态 URL |
| `Personality` | string(512) | 人格气质描述 |
| `HairColor` | string(128) | 发色描述 |
| `Expression` | string(128) | 表情描述 |
| `VisualTraitsJson` | text | 线条、光影、服装等标签 |
| `RecommendedUse` | string(512) | 推荐使用场景 |
| `IsBuiltIn` | bool | 是否系统内置 |
| `IsEnabled` | bool | 是否启用 |
| `SortOrder` | int | 默认排序 |
| `CreatedAt` / `UpdatedAt` | datetimeoffset | 审计时间 |

`AvatarId` 唯一。`IsEnabled = true` 且 `SortOrder` 最小的头像是系统默认头像。

### ADR-034-C：模板保存 `AvatarId`，DTO 返回 `AvatarUrl`

全局模板和 Workspace 模板新增：

```text
AvatarId: string?
```

DTO 同时返回：

```text
avatarId?: string
avatarUrl?: string
avatarName?: string
```

新功能主路径只写入 `avatarId`。`AvatarEmoji` 暂时保留，用于历史数据和非图片渠道 fallback，但全局模板编辑页不再展示自由 Emoji 输入框。

### ADR-034-D：未配置时默认选择第一个启用头像

创建模板时：

1. 前端加载头像列表；
2. 如果表单没有 `avatarId`，默认填入列表第一个启用头像；
3. 保存时提交 `avatarId`。

服务端仍必须兜底：

1. `avatarId` 为 null 或空字符串时，解析为默认头像；
2. `avatarId` 不存在或已禁用时，返回 `400 Bad Request`；
3. 列表为空时，允许 fallback 到 `AvatarEmoji` 或 `🤖`，并记录 warning。

### ADR-034-E：聊天头像来自 Agent 模板解析结果

聊天界面显示头像时，优先级为：

```text
WorkspaceAgent.AvatarId
-> WorkspaceAgent.SourceTemplateId 对应模板 AvatarId
-> WorkspaceAgent.AvatarUrl legacy fallback
-> 默认 AgentAvatar
-> AvatarEmoji legacy fallback
-> 🤖
```

对于当前需求，`WorkspaceAgent.SourceTemplateId` 关联的模板头像是关键路径。也就是说，Agent 模板配置了 `avatarId` 后，聊天页选中该 Agent、消息气泡、来源 metadata 都应使用模板解析出的 `avatarUrl`。

---

## 5. API 决策

新增只读头像 API：

```http
GET /api/agent-avatars?enabledOnly=true
GET /api/agent-avatars/{avatarId}
```

响应字段：

```json
{
  "avatarId": "neutral",
  "name": "默认助手",
  "url": "/assets/agent-avatars/agent-avatar-neutral.png",
  "personality": "冷静、稳定、通用",
  "hairColor": "炭紫黑",
  "expression": "无表情",
  "visualTraits": ["像素二次元", "圆形头像", "柔和边缘光"],
  "recommendedUse": "默认助手、通用客服",
  "isBuiltIn": true,
  "isEnabled": true,
  "sortOrder": 10
}
```

模板 API 扩展：

```http
GET /api/global-agent-templates
POST /api/global-agent-templates
PUT /api/global-agent-templates/{templateId}

GET /api/workspace-agent-templates
POST /api/workspace-agent-templates
PUT /api/workspace-agent-templates/{templateId}
```

均增加 `avatarId` 入参，并在 DTO 返回 `avatarUrl`。

Workspace Agent API 扩展：

```http
GET /api/workspaces/{workspaceId}/agents
GET /api/workspaces/{workspaceId}/agents/{agentId}
```

返回 `avatarId` 与解析后的 `avatarUrl`。如果 Agent 自身没有头像，应通过 `SourceTemplateId` 解析模板头像。

---

## 6. 前端决策

### 6.1 全局 Agent 模板编辑页

`Source/PuddingPlatformAdmin/src/pages/global-agent-template/index.tsx` 中：

- 删除或隐藏 `ProFormText name="avatarEmoji"`；
- 新增 `ProFormSelect name="avatarId"`；
- options 来自 `listAgentAvatars(true)`；
- 下拉菜单展示头像缩略图、名称、推荐使用场景；
- 创建模板时默认选中第一个启用头像；
- 卡片视图和表格视图优先展示 `avatarUrl`。

### 6.2 Workspace Agent 模板编辑页

Workspace 模板编辑如果存在同类 `avatarEmoji` 字段，也按同样规则切换为 `avatarId` 下拉选择。

### 6.3 Chat 界面

聊天页已有图片渲染基础：

- `AgentAvatar.tsx` 支持 `imageUrl`；
- `ChatMain.tsx` 的 Ant Design `Avatar` 支持 `src`；
- `ChatMessageBlock` 已有 `agentAvatarUrl` 字段。

Dev 需要补齐数据流：

- `WorkspaceAgentDto` 增加并使用 `avatarId`、`avatarUrl`；
- `ChatSource` 增加 `avatarUrl?: string`；
- `buildMessageBlocks` 将 `turn.source?.avatarUrl` 写入 `agentAvatarUrl`；
- `useChatState` 处理 metadata 时读取 `avatar_url` / `avatarUrl`；
- 缺 metadata 时使用 `selectedAgent.avatarUrl`。

---

## 7. 迁移策略

1. 先新增服务端头像资源和 API；
2. 新增 `AvatarId` 字段，但不删除 `AvatarEmoji`；
3. 启动种子将 `avatars.json` 写入 `AgentAvatars`；
4. 内置 `general-assistant` 模板默认写入第一个头像 ID；
5. 前端切到头像下拉；
6. 确认聊天页使用服务端 `avatarUrl` 后，删除前端 `src/assets/avatars`；
7. 未来单独 ADR 决定是否彻底移除 `AvatarEmoji`。

---

## 8. 风险与缓解

| 风险 | 影响 | 缓解 |
|------|------|------|
| 静态资源路径在宿主项目中不可访问 | 前端下拉有元数据但图片 404 | 施工时必须验证 `/assets/agent-avatars/agent-avatar-neutral.png`；若 referenced static web assets 不生效，则在 host 显式复制或配置静态资源。 |
| 旧模板没有头像 | 聊天页显示空头像 | 服务端解析默认头像，前端也用头像列表第一项兜底。 |
| 保存已禁用头像 | 模板引用不可用资源 | Upsert 模板时校验 `AgentAvatars.IsEnabled`。 |
| `AvatarUrl` 与 `AvatarId` 并存造成混乱 | 数据来源不一致 | 新功能只写 `AvatarId`；`AvatarUrl` 是解析输出或 legacy fallback，不作为模板配置主字段。 |
| metadata 未带头像 URL | 消息气泡仍显示 Emoji | 前端用 `selectedAgent.avatarUrl` fallback；后端后续补 metadata。 |

---

## 9. 验收标准

### 9.1 资源归属

- `Source/PuddingPlatformAdmin/src/assets/avatars` 不再作为运行时依赖。
- 服务端可访问 `/assets/agent-avatars/*.png`。
- `GET /api/agent-avatars?enabledOnly=true` 返回头像列表和可访问 URL。

### 9.2 模板配置

- 全局 Agent 模板编辑页显示头像下拉菜单。
- 创建模板时，如果用户没有主动选择，默认选择服务端返回的第一个启用头像。
- 保存不存在或禁用的 `avatarId` 时，服务端返回 400。
- 模板列表 / 卡片展示图片头像，不再依赖 Emoji。

### 9.3 聊天头像

- 聊天页顶部选中 Agent 的头像使用模板解析出的 `avatarUrl`。
- 新发送消息的 Agent 气泡使用同一个 `avatarUrl`。
- SSE / metadata 有 `avatarUrl` 时优先使用 metadata。
- metadata 缺失时使用 `selectedAgent.avatarUrl`。

### 9.4 兼容

- 历史 `AvatarEmoji` 数据不丢失。
- 旧 WorkspaceAgent 的 `AvatarUrl` 仍可作为 fallback。
- 没有任何头像数据时，界面仍显示 `🤖`。

---

## 10. 结论

采纳 **方案 B：服务端静态资源 + DB 元数据 + 模板存 `avatarId`**。

头像是 Agent 模板的一部分，不是前端装饰资源。服务端负责管理头像资源、默认值和合法性；前端只通过下拉选择 `avatarId`，并在聊天界面渲染服务端解析后的 `avatarUrl`。
