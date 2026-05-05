# QA 审阅报告 — Session 显式创建 & 消息游标分页

| 项 | 值 |
|----|-----|
| 日期 | 2026-05-05 |
| 审阅模型 | GPT-5.3-Codex (QA) |
| 变更范围 | Session 显式创建 + 消息游标分页 |
| 审阅结论 | **PASS_WITH_NOTES** |

---

## 变更摘要

将"新对话"从纯前端临时 ID 改为调用后端 API 显式创建真实 Session 记录；消息加载从一次性全量加载改为游标分页渐进式加载（滚动触发）。

---

## 检查结果

### ✅ 正确性检查

| 检查项 | 结果 | 说明 |
|--------|------|------|
| `agentTemplateId` 构造 | ✅ | 前端传 `global:${agentId}`，与项目中其他位置（如 `WorkspaceAgentDto.sourceTemplateId`）的命名约定一致 |
| `resetConversation` 状态设置 | ✅ | `sessionIdRef.current = session.sessionId`、`forceNewSessionRef.current = false`、`setSelectedSessionId` 均正确设置 |
| `CreateSessionRequest` 契约对齐 | ✅ | 后端 `CreateSessionRequest`(Controller + Platform) 与前端 `createSession` 的参数完全匹配 |
| 分页游标方向 | ✅ | 后端 `before` 语义为 `CreatedAt < cursor`，前端 `oldestMessageCursor` 传的是最早已加载消息的 `createdAt`，方向正确 |
| 旧消息拼接顺序 | ✅ | `setTurns(prev => [...olderTurns, ...prev])` 将更早的消息前置，与 `toTurnsFromHistory` 返回升序一致 |
| `hasMoreMessages` 判断 | ✅ | 后端用 `Take(limit+1)` 多取一条判断 hasMore，逻辑正确 |
| `oldestCreatedAt` 传递 | ✅ | 后端返回当前页最小 `CreatedAt`，前端存入 `oldestMessageCursor`，下次请求以此为 cursor |

### ⚠️ 安全性检查

#### P1-1: Session 创建端点缺少鉴权（P1 — 严重）

**位置**:
- [SessionController.cs](Source/PuddingController/Controllers/SessionController.cs) — `POST /api/session`
- [SessionApiController.cs](Source/PuddingPlatform/Controllers/Api/SessionApiController.cs) — `POST /api/sessions`

**问题**: 两个新增的创建端点均无 `[Authorize]` 属性。对比同模块 `MessageApiController` 已标注 `[Authorize]`，新增端点可被未认证用户调用，任何人都能创建 Session。

**另外**: `SessionController.Create` 中 `ChannelId` 和 `OwnerUserId` 硬编码为 `"admin"`，未从请求上下文提取真实用户身份。`AuthorizationService` 已存在但未被创建流程调用，无法校验 workspace 权限。

**修复建议**:
1. 为 `SessionApiController.Create` 添加 `[Authorize]`
2. `SessionController.Create` 至少校验 workspace 存在且未冻结；如需内网访问可暂不加 `[Authorize]` 但应做 IP 限制
3. 从 JWT Claim 或请求头中提取真实 `OwnerUserId`，替代硬编码

---

#### P1-2: 前端竞态 — 快速点击"新对话"重复创建 Session（P1 — 严重）

**位置**: [chat/index.tsx](Source/PuddingPlatformAdmin/src/pages/chat/index.tsx) — `resetConversation` + "新对话"按钮

**问题**: `resetConversation` 是 `async` 函数，"新对话"按钮 `onClick={()=>{void resetConversation();}}` 无 loading 守卫。用户快速连点会触发多次 `createSession` 调用，创建多个空 Session 记录。

**修复建议**: 增加 `creatingSession` 状态，`resetConversation` 执行期间禁用按钮：
```tsx
const [creatingSession, setCreatingSession] = useState(false);
// resetConversation 内部：
setCreatingSession(true);
try { ... } finally { setCreatingSession(false); }
// 按钮禁用：
<Button disabled={creatingSession} ...>新对话</Button>
```

---

#### P1-3: `handleSelectSession` 异步竞态 — 切换后旧数据覆盖新数据（P1 — 严重）

**位置**: [chat/index.tsx](Source/PuddingPlatformAdmin/src/pages/chat/index.tsx) — `handleSelectSession`

**问题**: 快速点击 Session A → Session B，A 的 `listSessionMessages` 异步返回晚于 B 的请求，会覆盖 B 的 turns 数据。`handleSelectSession` 缺少 stale-check。

**修复建议**: 使用闭包捕获 sid，异步完成后校验是否仍为当前选中：
```tsx
const handleSelectSession = useCallback(async (sid: string) => {
    if (sid === selectedSessionId) return;
    // ...
    try {
      const res = await listSessionMessages(sid, undefined, MESSAGE_PAGE_SIZE);
      // stale check
      setSelectedSessionId(current => {
        if (current !== sid) return current; // 用户已切换，丢弃结果
        setTurns(toTurnsFromHistory(res));
        setHasMoreMessages(res.hasMore);
        if (res.oldestCreatedAt != null) setOldestMessageCursor(res.oldestCreatedAt);
        return sid;
      });
    } catch { ... }
```
或更简单的做法：用一个 `loadIdRef` 自增计数器，异步完成后比较是否过期。

---

### P2 改进建议

| # | 位置 | 问题 | 建议 |
|---|------|------|------|
| P2-1 | `SessionController.Create` | 无 workspaceId 存在性校验，可创建指向不存在 workspace 的 Session | 调用 `InMemoryWorkspaceCatalog` 验证 workspace 存在 |
| P2-2 | `CreateSessionRequest` 重复定义 | `SessionController.cs` 和 `SessionApiController.cs` 各自定义了同名 `CreateSessionRequest` record | 统一为共享 DTO 或通过命名空间区分（当前不同命名空间不影响编译，但维护时容易遗漏同步） |
| P2-3 | `loadMoreMessages` 缺少 stale-check | 与 P1-3 同理，`loadMoreMessages` 的 `selectedSessionId` 是 `useCallback` 闭包捕获值，若用户在加载中切换 Session，结果会混入错误 Session 的消息 | 在 `setTurns` 前校验 `selectedSessionId` 是否仍为开始加载时的值 |
| P2-4 | 滚动位置丢失 | `loadMoreMessages` 在头部插入旧消息后，`scrollTop` 位置会跳动（浏览器保持绝对滚动位置，但内容变长） | 加载完成后恢复相对位置：`const prevHeight = el.scrollHeight; ... requestAnimationFrame(() => { el.scrollTop = el.scrollHeight - prevHeight; })` |
| P2-5 | `toTurnsFromHistory` 未包裹 `useCallback` | 每次渲染重建函数，无功能影响但浪费 | 用 `useCallback` 包裹，依赖为空 |

---

## 架构合规性

| 检查项 | 结果 |
|--------|------|
| 依赖方向 | ✅ Platform → Controller（通过 PlatformApiClient HTTP 调用），无逆向引用 |
| 前端路由 | ✅ `/api/sessions` 代理到 Controller `/api/session`，路由回退逻辑不受影响 |
| DI 注册 | ✅ `InMemorySessionRepository` 已在 `AddPuddingController()` 中注册为 Singleton |
| 数据库 | ✅ 游标分页使用 `CreatedAt` 毫秒戳，SQLite 兼容 |

---

## 测试验证

- 单元测试：未发现针对新增 `POST /api/session` 端点的测试用例
- 建议补充：`SessionControllerTests` 覆盖创建成功、重复创建、无效参数场景

---

## 结论

**PASS_WITH_NOTES** — 核心逻辑正确，分页游标方向和消息顺序无误。但存在 3 个 P1 级问题（鉴权缺失、重复创建、异步竞态），建议修复 P1 后再合并。P2 项为改进建议，非阻断。
