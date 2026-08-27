# Chat 图片消息回放与前端旧 Bundle 缓存修复方案

> 状态：Proposed（现场诊断完成，尚未实施）  
> 日期：2026-08-26  
> 关联任务：`ceba781342aa4353901654d1897092cb`  
> 优先级：P1  
> 架构基线：[ADR-077 主代理原生视觉理解与多模态消息链路](../07架构/92ADR-077主代理原生视觉理解与多模态消息链路ADR.md)

## 1. 目标

修复 Chat 中“用户上传图片后，历史消息只显示 picture 图标和‘图片’文字，不显示真实图片”的问题，并消除前端发布后仍运行旧 Bundle 的缓存漂移。

完成标准不是“代码中存在图片组件”，而是从 canonical 消息到浏览器 DOM 的全链路均可证明：

```text
ChatMessages.content_parts_json
  -> AgentConversationProjectionService.ContentParts
  -> AgentConversationView.messages[].contentParts
  -> ChatTurn.userMessage.contentParts
  -> ChatMessageBlock.visionArtifactIds
  -> authenticated vision-artifacts URL
  -> <img> rendered
```

## 2. 非目标

- 不改变主代理直接消费图片的 ADR-077 决策，也不重新引入 Image Reader 自动预观察。
- 不修改 Artifact 原始文件、图片质量或 `D:\data` 中已有消息数据。
- 不建立新的 metadata/contentParts 双写兼容层。
- 不把“前端编译成功”或“静态文件复制成功”视为产品内恢复。
- 不在本任务中完成 ADR-077 V3 Files API 或 V4 真实视觉模型验收。

## 3. 现场证据

### 3.1 目标消息与 Artifact

- Workspace：`default`
- Session：`206a9b48ec904ebb93e7541131fbb835`
- MessageId：`msg-1787742699644-228l5oa0`
- ArtifactId：`vision-e59326aeb17f49f28d011f95e7c18dc6`
- Artifact：PNG，429×133，13,856 字节

数据库中的 `ChatMessages.content_parts_json` 已包含有序 typed parts：文本部件和 `image/artifactId/detail=original`。对应 Workspace Artifact 文件与 metadata 均存在。因此上传、canonical 写入和 Artifact 存储不是故障点。

### 3.2 浏览器表现

目标消息 DOM 中没有 `<img>`，也没有图片资源 URL 或图片请求；只有 Ant Design `picture` 图标和“图片”文字。控制台没有相应图片加载错误。

这证明问题发生在图片 URL 构造之前，不是浏览器下载图片失败。

### 3.3 投影断点

后端 `Source/PuddingPlatform/Services/AgentChat/AgentConversationProjectionService.cs` 已读取 `ContentPartsJson`，并把安全摘要写入 `ConversationMessageView.ContentParts`。

前端存在两个断点：

1. `Source/PuddingPlatformAdmin/src/pages/chat/client/types.ts` 的 `ConversationMessageView` 没有 `contentParts` 字段。
2. `Source/PuddingPlatformAdmin/src/pages/chat/components/MessageList.tsx` 的 `createProjectedTurn()` 只复制 `metadata`，没有复制 `message.contentParts`。

`Source/PuddingPlatformAdmin/src/pages/chat/types.ts` 和 `UserMessageBubble.tsx` 已支持 typed parts；只要 Agent-first 会话投影没有把字段传到 `ChatTurn`，渲染层就只能走旧 metadata 回退。目标消息 metadata 只有 `inputMode=image` 和 `imageCount=1`，没有 Artifact ID，因此最终显示占位图标。

### 3.4 旧 Bundle 与缓存漂移

- 当前页面以及另开的新标签仍加载 `/admin/umi.ec3e4c41.js`。
- 当前服务器和 `Source/PuddingPlatformAdmin/dist/index.html` 已指向 `/admin/umi.918d59c5.js`。
- 旧页面引用的聊天异步 Chunk 已从服务器删除并返回 404。

进一步的代码证据：

- `config/defaultSettings.ts` 已设置 `pwa: false`。
- `src/global.tsx` 只有在 `isHttps` 为真时才注销旧 Service Worker 和清理 Cache Storage。
- `http://localhost` 支持 Service Worker，但 `isHttps` 为假，所以旧 Worker/Cache 不会被注销。
- `src/service-worker.js` 会 precache 并注册 navigation route；历史版本残留后可以继续返回旧入口文档。
- `PuddingWebApplicationExtensions.cs` 当前没有为 `/admin/index.html`、SPA fallback 和哈希静态资源设置明确缓存合同。

因此第二个问题不是简单的“用户没有刷新”，而是 PWA 关闭后的 localhost 迁移路径与服务端缓存头都不完整。

## 4. 根因

### RC-1：Agent-first 前端投影丢失 typed contentParts

服务端 DTO 已包含图片事实，但前端 AgentConversation 类型和 `createProjectedTurn()` 没有传递该字段。现有 `MessageList` 测试只构造 `visionArtifactIds` metadata，因此没有覆盖真实 typed-parts 回放路径。

### RC-2：关闭 PWA 后未覆盖 localhost 的旧 Service Worker 清理

清理逻辑被 `isHttps` 限制，导致本地产品入口仍可能受历史 Service Worker 控制。即使服务器切换到新哈希入口，客户端仍可能得到旧 HTML，并继续引用已经删除的异步 Chunk。

### RC-3：静态文件缺少分层缓存合同和 Build Identity

入口 HTML、Service Worker 和哈希静态资源没有被明确区分。部署完成后也缺少一个可由 UI、日志和测试共同读取的 build identity，无法快速判定“源码、dist、Core wwwroot、WebView 当前页面”是否属于同一构建。

## 5. 兼容性设计

### 5.1 消息兼容边界

- `contentParts` 是当前 authoritative contract。
- 如果 typed image part 存在，任何 merge 都不得用 `undefined` 或旧 metadata 覆盖它。
- 仅对已有 metadata-only 历史消息保留 `visionArtifactId` / `visionArtifactIds` 回退。
- 不新增新的双写字段，不对 `D:\data` 做迁移或重写。
- malformed typed parts 必须显示可诊断失败态，不得静默伪装为普通文本或通用 picture 图标。

### 5.2 浏览器缓存兼容边界

- PWA 关闭时，在 HTTPS 和 localhost HTTP 两种允许 Service Worker 的环境中都必须注销 Pudding Admin scope 下的旧 registration。
- 只删除 Pudding/Ant Design Pro 自己命名的 Cache Storage 项，避免清空同源的其他业务缓存。
- 入口 HTML 和 Service Worker 文件使用 `no-store` 或 `no-cache, must-revalidate`。
- 带内容哈希的 JS/CSS/图片使用 `public, max-age=31536000, immutable`。
- 无内容哈希的资源不得设置 immutable。
- 清理逻辑必须幂等；正常启动无历史 Worker 时不产生错误、提示或明显延迟。

### 5.3 Desktop/WebView2 边界

- 前端静态部署仍由 Desktop/Core 既有边界负责，不把业务逻辑迁入 WPF。
- `FrontendBuildDeployService` 继续原子替换 `wwwroot/admin`，但部署结果必须记录入口文件引用的 build identity 和目标目录实际 identity。
- 当前承载自身的 Pudding 会话不能独立证明 Desktop/Core 重启和 WebView2 缓存回收；最终验收继续使用两段式门禁。

## 6. 文件级施工计划

### Task 1：补齐 AgentConversation typed parts 合同

**修改：**

- `Source/PuddingPlatformAdmin/src/pages/chat/client/types.ts`
  - 为 `ConversationMessageView` 增加 `contentParts?: ConversationContentPartView[] | null`。
  - 复用 `services/platform/api.ts` 中的安全摘要类型，避免定义第二套字段语义。
- `Source/PuddingPlatformAdmin/src/pages/chat/components/MessageList.tsx`
  - `createProjectedTurn()` 将用户消息的 `message.contentParts` 传入 `ChatTurn.userMessage.contentParts`。
  - 只允许用户消息携带该字段；Agent/System 消息不得误投影为用户附件。

**测试：**

- 修改 `Source/PuddingPlatformAdmin/src/pages/chat/components/MessageList.test.tsx`。
- 使用 `contentParts: [{type:'text'}, {type:'image', artifactId:'vision-a', detail:'original'}]` 构造 canonical projection。
- 断言最终 `visionArtifactIds` 为 `vision-a`，且测试不依赖 metadata Artifact ID。

### Task 2：冻结 merge 和历史恢复语义

**修改：**

- `Source/PuddingPlatformAdmin/src/pages/chat/components/MessageList.tsx`
  - 审核 `mergeLocalTurnsAwaitingProjection()`、active-run overlay 和同 messageId 更新。
  - projected typed parts 非空时保持 projected 值；服务端尚未投影 typed parts 时才暂时保留本地 optimistic parts。
- `Source/PuddingPlatformAdmin/src/pages/chat/client/localCache.ts`
  - 确认 IndexedDB round-trip 不裁剪新增字段；必要时为缓存 schema 增加显式版本和一次性兼容读取。
- `Source/PuddingPlatformAdmin/src/pages/chat/hooks/useSessionHistoryProjection.ts`
  - 保持历史 API 与 Agent-first projection 使用同一 `ChatTurn.userMessage.contentParts` 语义。

**测试：**

- optimistic 图片消息 → canonical user-only projection → active run → terminal projection，全过程 Artifact ID 不丢失。
- IndexedDB 保存/恢复 typed parts。
- 分页历史合并不重排或覆盖 parts。
- metadata-only 旧消息仍可显示；typed parts 与 metadata 冲突时 typed parts 获胜。

### Task 3：补齐渲染失败态和 Artifact 请求验证

**修改：**

- `Source/PuddingPlatformAdmin/src/pages/chat/components/UserMessageBubble.tsx`
  - typed image part 存在时构造认证 Artifact URL。
  - 区分 loading、loaded、not-found、forbidden 和 decode-error。
  - 失败状态保留 Artifact 短 ID、重试入口和可复制诊断，不暴露宿主绝对路径。
- `Source/PuddingPlatformAdmin/src/pages/chat/components/MessageRow.tsx`
  - memo comparator 继续比较完整 Artifact ID 序列，确保 parts 更新触发重渲染。

**测试：**

- `UserMessageBubble` 渲染真实 `<img>` 和正确 URL。
- 401/403/404/图片解码失败显示明确状态，不回退为无信息的 picture 图标。
- 多图顺序与 typed parts 顺序一致。

### Task 4：移除旧 Service Worker 控制并建立缓存合同

**修改：**

- `Source/PuddingPlatformAdmin/src/global.tsx`
  - PWA 关闭时，只要 `navigator.serviceWorker` 可用就执行限定 scope 的注销，不再依赖 `isHttps`。
  - 删除 Pudding 自有 cache prefix；失败写一次结构化诊断但不阻塞页面启动。
- `Source/PuddingPlatformAdmin/src/service-worker.js`
  - 如果生产构建不再使用 PWA，则停止输出/注册该 Worker；如果保留文件用于显式 PWA 模式，则 navigation route 必须匹配 `/admin/index.html`，且升级清理旧 cache name。
- `Source/PuddingHost/Extensions/PuddingWebApplicationExtensions.cs`
  - 为物理静态文件和 `/admin/{*path:nonfile}` fallback 设置分层 Cache-Control。
  - fallback 返回入口 HTML 时显式 `no-store`，避免中间缓存复用旧哈希引用。
- `Source/PuddingDesktop/Debug/FrontendBuildDeployService.cs`
  - 部署后解析并校验 `index.html` 引用的所有本地哈希资源确实存在。
  - 输出 build identity、入口哈希和复制目标，不只报告文件数量。

**测试：**

- localhost HTTP 下存在旧 registration/cache 时能够完成注销和限定清理。
- 无 registration/cache 时幂等成功。
- `/admin/chat` fallback 响应不可陈旧缓存，哈希资源响应为 immutable。
- 部署产物缺少入口引用 Chunk 时构建部署失败，不报告成功。

### Task 5：增加 Build Identity 与链路诊断

**新增/修改：**

- 在前端构建阶段生成 `build-info.json`，至少包含 commit、UTC build time 和入口资源哈希。
- Core 启动日志记录实际 `wwwroot/admin/build-info.json` 摘要。
- Chat 诊断面板显示当前页面 build identity 与 Core-served identity；不默认占用聊天空间。
- 图片投影诊断只记录 `messageId`、parts count、Artifact ID 不可逆摘要和丢失层级，不记录图片字节、用户正文或绝对路径。

**验收：**

当再次出现“服务器已更新但页面仍是旧 UI”时，能够在一次诊断中指出偏差发生在 dist、Core wwwroot、HTTP response、Service Worker 或当前页面中的哪一层。

## 7. 验证命令

前端定向测试：

```powershell
Set-Location Source\PuddingPlatformAdmin
pnpm jest src/pages/chat/components/MessageList.test.tsx src/pages/chat/components/UserMessageBubble.test.tsx --runInBand
pnpm tsc
pnpm run build
```

后端定向测试与构建：

```powershell
dotnet test Source\PuddingPlatformTests\PuddingPlatformTests.csproj --no-restore --nologo --filter "AgentConversationProjection|VisionArtifact"
dotnet test Tests\PuddingDesktop.Tests\PuddingDesktop.Tests.csproj --no-restore --nologo --filter "FrontendBuildDeploy"
dotnet build PuddingRuntime --no-restore
```

文档与补丁检查：

```powershell
git diff --check
```

## 8. 两段式产品验收

### Gate A：ready-for-external-deploy

- 定向测试、TypeScript 检查和生产前端构建通过。
- `dist/index.html` 的所有本地资源引用存在。
- Core 输出目录的 build identity 与 dist 相同。
- 当前 Agent 只交付“可供外部部署”，不声称当前进程已经加载新代码。

### Gate B：in-product-functional-complete

由进程外控制器重启到明确的新构建后，在新的 Pudding 测试会话执行：

1. 打开 `/admin/chat`，确认页面和 Core build identity 一致。
2. 验证不存在控制 `/admin/` 的旧 Service Worker，Pudding 自有旧 cache 已清理。
3. 打开包含 `msg-1787742699644-228l5oa0` 的会话。
4. 确认 DOM 存在真实 `<img>`，Artifact 请求返回 200，图片尺寸正确。
5. 发送一张新图片，验证 optimistic、运行中、完成、刷新、分页历史和 Desktop 重启后均显示。
6. 验证 metadata-only 历史夹具仍可回放，typed parts 冲突夹具以 typed parts 为准。

最终仍由外部控制器判定 Desktop 启动、重启、退出回收和 WebView2 进程生命周期，不把浏览器单页 smoke 扩大为产品生命周期验收。

## 9. 完成定义

- 目标消息和新图片消息均渲染真实图片，而不是 picture 占位。
- canonical typed parts 在所有前端投影、缓存、合并和重启路径不丢失。
- 新发布不会继续加载已删除的旧 Bundle/Chunk。
- 缓存清理限定在 Pudding 自有资源，兼容 localhost HTTP 和 HTTPS。
- 构建、部署和页面三层 identity 可比对。
- 看板任务引用本文件，并分别记录代码验证、外部部署和产品内 smoke 证据。

