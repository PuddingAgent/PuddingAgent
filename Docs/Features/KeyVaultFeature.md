# 密钥保管箱 (Key Vault) — 功能设计

> **状态**: 设计中 | **日期**: 2026-05-02 | **作者**: lead

## 0. 概述

### 0.1 问题

Agent 在执行工具调用时需要各种密钥（API Key、Token、密码等），目前这些密钥只能：
- 硬编码在系统提示词中（泄露风险）
- 通过环境变量注入（不灵活，不可通过 UI 管理）
- 出现在 LLM 上下文和记忆中（明文泄露风险）

### 0.2 目标

提供一个**端到端的密钥生命周期管理系统**：
1. **存储**：用户通过前端 UI 录入密钥，加密存储于 SQLite
2. **注入**：Agent 执行工具时，将密钥占位符自动解析为真实值
3. **剥离**：LLM 返回的消息和上下文中，自动将密钥明文替换为占位符
4. **隔离**：确保记忆文件、日志、会话历史中不存在密钥明文

### 0.3 范围

| 范围内 | 范围外 |
|--------|--------|
| 密钥 CRUD（创建/读取/更新/删除） | 多用户共享密钥（V2） |
| 前端密钥管理页面 | 密钥过期/轮换策略 |
| 工具调用的密钥注入 | 外部密钥源集成（Vault/KMS） |
| LLM 响应/记忆的密钥剥离 | 审批链集成 |
| 加密存储（AES-256-GCM） | 硬件安全模块 (HSM) |
| 保险库主密码保护 | P2P 节点间密钥同步 |

---

## 1. 架构概览

```
┌─────────────────────────────────────────────────────────┐
│                    Pudding Agent 进程                      │
│                                                           │
│  ┌─────────────────┐     ┌─────────────────────────────┐ │
│  │  KeyVault UI     │────▶│  KeyVaultController (API)    │ │
│  │  (前端页面)       │     │  GET/POST/PUT/DELETE         │ │
│  └─────────────────┘     └──────────┬──────────────────┘ │
│                                      │                     │
│  ┌────────────────────┐             │                     │
│  │  AgentExecutionService │◀────────┼─── 注入/剥离        │
│  │  (Agent Loop)       │             │                     │
│  └────────┬───────────┘             │                     │
│           │                          │                     │
│  ┌────────▼───────────┐     ┌───────▼───────────────────┐ │
│  │  MemoryEngine       │     │  KeyVaultService           │ │
│  │  (WriteBack 剥离)   │     │  ├─ Encrypt/Decrypt         │ │
│  └────────────────────┘     │  ├─ Inject (解析占位符)     │ │
│                              │  ├─ Strip (脱敏)           │ │
│                              │  └─ VaultState (解锁状态)  │ │
│                              └───────┬───────────────────┘ │
│                                      │                     │
│                              ┌───────▼───────────────────┐ │
│                              │  SQLite (pudding.db)        │ │
│                              │  └─ VaultKeyEntries 表      │ │
│                              │     (EncryptedValue BLOB)   │ │
│                              └─────────────────────────────┘ │
└─────────────────────────────────────────────────────────┘
```

### 1.1 模块归属

| 组件 | 所在项目 | 角色 |
|------|---------|------|
| `IKeyVaultStore`, `KeyVaultEntry` | `PuddingCore` | 数据模型 + 存储接口 |
| `KeyVaultService` | `PuddingRuntime` | 加密/解密/注入/剥离逻辑 |
| `KeyVaultController` | `PuddingAgent` | REST API 端点 |
| `KeyVaultPage` | `PuddingPlatformAdmin` | 前端管理页面 |

### 1.2 依赖关系

```
PuddingPlatformAdmin (UI)
        ↓ HTTP
PuddingAgent (Controller + Host)
        ↓ DI
PuddingRuntime (KeyVaultService)
        ↓ interface
PuddingCore (IKeyVaultStore, KeyVaultEntry)
        ↓ EF Core
SQLite (VaultKeyEntries 表)
```

---

## 2. 数据模型

### 2.1 核心实体

```csharp
// PuddingCore/KeyVault/KeyVaultEntry.cs
public sealed record KeyVaultEntry
{
    /// <summary>主键，GUID</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>用户可见的名称，如 "GitHub Token"</summary>
    public string Name { get; init; } = "";

    /// <summary>分类标签：api-key, password, token, certificate, other</summary>
    public string Category { get; init; } = "api-key";

    /// <summary>加密后的密钥值 (AES-256-GCM)</summary>
    public byte[] EncryptedValue { get; init; } = [];

    /// <summary>加密使用的 IV (Nonce, 12 bytes)</summary>
    public byte[] IV { get; init; } = [];

    /// <summary>GCM 认证标签 (16 bytes)</summary>
    public byte[] Tag { get; init; } = [];

    /// <summary>创建时间</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>最后更新时间</summary>
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}
```

### 2.2 保险库主密钥

```
保险库主密钥不持久化到数据库，仅在内存中持有：

用户输入主密码
    ↓
PBKDF2-HMAC-SHA256 (迭代 600000 次, 16-byte salt)
    ↓
32-byte AES-256 密钥
    ↓
存储在 KeyVaultService._masterKey (byte[], 仅在解锁期间)
    ↓
进程重启后需重新输入主密码解锁
```

**设计决策**：
- 盐值 (Salt) 硬编码在 `appsettings.json` 的 `KeyVault:Salt` 中（不随条目变化，简化实现）
- V2 可升级为每个条目独立盐值
- 主密钥不在 SQLite 中存储任何形式（包括哈希），确保离线攻击无切入点

### 2.3 加密算法

- **算法**：AES-256-GCM（认证加密）
- **密钥**：32 bytes（PBKDF2 派生）
- **Nonce/IV**：12 bytes（随机生成，每个条目独立）
- **Tag**：16 bytes（GCM 自动生成，认证密文完整性）
- **AAD**：条目 Name 的 UTF-8 字节（防篡改名称）

> **密评备注**：若未来需要国密合规，可替换为 SM4-GCM 模式（需自行实现 SM4 的 GCM 封装）。本设计预留接口 `IKeyVaultCrypto` 便于切换。

---

## 3. 接口设计

### 3.1 IKeyVaultStore（存储层）

```csharp
// PuddingCore/KeyVault/IKeyVaultStore.cs
public interface IKeyVaultStore
{
    Task<IReadOnlyList<KeyVaultEntry>> ListAsync(CancellationToken ct = default);
    Task<KeyVaultEntry?> GetByIdAsync(string id, CancellationToken ct = default);
    Task AddAsync(KeyVaultEntry entry, CancellationToken ct = default);
    Task UpdateAsync(KeyVaultEntry entry, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
    Task<bool> IsUnlockedAsync(CancellationToken ct = default);
}
```

### 3.2 IKeyVaultCrypto（加密层，可替换）

```csharp
// PuddingCore/KeyVault/IKeyVaultCrypto.cs
public interface IKeyVaultCrypto
{
    /// <summary>从主密码派生 32-byte AES 密钥</summary>
    byte[] DeriveKey(string masterPassword, byte[] salt);

    /// <summary>加密明文，返回 (ciphertext, iv, tag)</summary>
    (byte[] Ciphertext, byte[] IV, byte[] Tag) Encrypt(byte[] key, string plaintext, byte[] associatedData);

    /// <summary>解密密文，返回明文</summary>
    string Decrypt(byte[] key, byte[] ciphertext, byte[] iv, byte[] tag, byte[] associatedData);
}
```

默认实现 `AesGcmCrypto` 放在 `PuddingRuntime/Services/KeyVault/`。

### 3.3 IKeyVaultService（业务层）

```csharp
// PuddingRuntime/Services/KeyVault/IKeyVaultService.cs
public interface IKeyVaultService
{
    // ── 保险库管理 ──
    bool IsUnlocked { get; }
    bool Unlock(string masterPassword);
    void Lock();
    Task<IReadOnlyList<KeyVaultEntry>> ListEntriesAsync(CancellationToken ct = default);
    Task AddEntryAsync(string name, string category, string plainValue, CancellationToken ct = default);
    Task UpdateEntryAsync(string id, string name, string category, string plainValue, CancellationToken ct = default);
    Task DeleteEntryAsync(string id, CancellationToken ct = default);

    // ── 注入与剥离 ──
    /// <summary>解析文本中的 {{vault:key-name}} 占位符为真实密钥值</summary>
    string Inject(string text);

    /// <summary>将文本中的已知密钥明文替换为 [REDACTED:key-name]</summary>
    string Strip(string text);
}
```

---

## 4. 密钥注入流程

### 4.1 占位符语法

```
{{vault:key-name}}
```

用户在工具参数、系统提示词等位置使用此占位符。例如：
```json
{
  "url": "https://api.github.com/repos/foo/bar",
  "headers": {
    "Authorization": "Bearer {{vault:github-token}}"
  }
}
```

### 4.2 注入时机

在 `AgentExecutionService` 的工具调用执行前：

```
AgentExecutionService.ExecuteAsync()
    │
    ├─ llmResp.ToolCalls [... ]
    │       │
    │       ▼
    │   foreach (var call in llmResp.ToolCalls)
    │   {
    │       // ★ 注入点：解析工具参数中的占位符
    │       var resolvedArgs = _keyVaultService.Inject(call.ArgumentsJson);
    │       // ...传递给 SkillRuntime.InvokeAsync()
    │   }
    │
    └─ loopResp.Tool (XML 路径)
            │
            ▼
        var argsJson = loopResp.Tool!.Args?.GetRawText() ?? "{}";
        // ★ 注入点
        argsJson = _keyVaultService.Inject(argsJson);
```

### 4.3 注入实现

```csharp
// KeyVaultService.Inject()
private static readonly Regex VaultPattern =
    new(@"\{\{vault:(?<name>[^}]+)\}\}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

public string Inject(string text)
{
    if (!IsUnlocked || string.IsNullOrEmpty(text)) return text;

    return VaultPattern.Replace(text, match =>
    {
        var name = match.Groups["name"].Value.Trim();
        var entry = _entriesCache.GetValueOrDefault(name);
        if (entry == null) return match.Value; // 未找到，保留原文

        var plain = DecryptEntry(entry);
        return plain ?? match.Value;
    });
}
```

---

## 5. 密钥剥离流程

### 5.1 剥离时机

有 **三个剥离点**，确保密钥不在任何持久化或传输路径中泄露：

| 剥离点 | 位置 | 剥离对象 |
|--------|------|---------|
| **A. LLM 响应** | `AgentExecutionService` — LLM 返回后、加入 history 前 | `rawText` / `loopResp.Message` |
| **B. 记忆写回** | `MemoryEngine.WriteBack()` — 写入前 | `content` 字段 |
| **C. 上下文构建** | `MemoryEngine.BuildMemoryContext()` — 返回前 | 召回的记忆文本 |

### 5.2 剥离实现

```csharp
// KeyVaultService.Strip()
public string Strip(string text)
{
    if (!IsUnlocked || string.IsNullOrEmpty(text)) return text;

    foreach (var (name, entry) in _entriesCache)
    {
        var plain = DecryptEntry(entry);
        if (string.IsNullOrEmpty(plain)) continue;

        // 精确匹配替换
        var placeholder = $"[REDACTED:{name}]";
        text = text.Replace(plain, placeholder);
    }
    return text;
}
```

### 5.3 性能考虑

- 保险库中预期条目数量较小（< 100），逐一替换可接受
- 使用 `_entriesCache` (ConcurrentDictionary) 避免每次解密
- 若未来条目增长，可改为 Aho-Corasick 多模式匹配

---

## 6. API 设计

### 6.1 REST 端点

| 方法 | 路径 | 说明 |
|------|------|------|
| `POST` | `/api/key-vault/unlock` | 解锁保险库 `{ "masterPassword": "..." }` |
| `POST` | `/api/key-vault/lock` | 锁定保险库 |
| `GET` | `/api/key-vault/status` | 获取状态 `{ "isUnlocked": true, "entryCount": 5 }` |
| `GET` | `/api/key-vault/entries` | 列出所有条目（不含明文值，仅元数据） |
| `POST` | `/api/key-vault/entries` | 创建条目 `{ "name", "category", "value" }` |
| `PUT` | `/api/key-vault/entries/{id}` | 更新条目 |
| `DELETE` | `/api/key-vault/entries/{id}` | 删除条目 |

### 6.2 安全约束

- 所有写操作需要保险库处于解锁状态，否则返回 `423 Locked`
- 读取条目列表返回元数据（Name, Category, Id, CreatedAt），**不返回** EncryptedValue
- 仅在 `Inject/Strip` 内部解密，API 层面绝不返回明文
- 无认证端点暂时允许本地访问（V1 单用户模型）

---

## 7. 前端设计

### 7.1 路由

- 路由：`/key-vault`
- 入口：左侧导航栏 "密钥保管箱" 图标（🔐）
- 参考现有页面风格：`llm-resource-pool`

### 7.2 页面结构

```
┌──────────────────────────────────────────────┐
│  🔐 密钥保管箱                    [锁定/解锁] │
├──────────────────────────────────────────────┤
│                                              │
│  ┌─ 保险库状态：🔒 已锁定 / 🔓 已解锁 ──────┐ │
│  │  [输入主密码] [解锁] [锁定]             │ │
│  └──────────────────────────────────────────┘ │
│                                              │
│  ┌─ 密钥列表 ───────────────────────────────┐ │
│  │ 名称          分类      创建时间    操作  │ │
│  │ ────────────  ────────  ────────  ─────  │ │
│  │ GitHub Token  api-key   05-02     ✎ 🗑   │ │
│  │ DB Password   password  05-01     ✎ 🗑   │ │
│  │                                      [+]  │ │
│  └──────────────────────────────────────────┘ │
│                                              │
│  ┌─ 新增/编辑密钥 (Modal) ──────────────────┐ │
│  │  名称: [_______________]                 │ │
│  │  分类: [api-key ▼]                       │ │
│  │  密钥: [_______________] 👁              │ │
│  │  引用: {{vault:github-token}} (自动生成) │ │
│  │              [取消]  [保存]              │ │
│  └──────────────────────────────────────────┘ │
└──────────────────────────────────────────────┘
```

### 7.3 交互细节

1. **解锁状态持久化**：仅在浏览器内存中保存解锁状态（sessionStorage），关闭标签页即锁定
2. **密钥值展示**：默认 `••••••••`，点击眼睛图标切换明文（3 秒后自动恢复掩码）
3. **引用复制**：每个条目旁有复制按钮，一键复制 `{{vault:key-name}}` 到剪贴板
4. **空状态**：未解锁时列表区显示 "请先解锁保险库"
5. **删除确认**：弹出确认对话框 "密钥删除后不可恢复，使用该密钥的工具将失效"

---

## 8. 集成点代码变更

### 8.1 AgentExecutionService 变更

在工具调用前注入，在 LLM 响应后剥离：

```csharp
// 注入点 1: function-call 路径
foreach (var call in llmResp.ToolCalls)
{
    var resolvedArgs = _keyVault.Inject(call.ArgumentsJson);
    var skillResult = await _skillRuntime.InvokeAsync(
        call.Name,
        new SkillInvokeRequest
        {
            // ...
            Input = ExtractInputFromJson(resolvedArgs),
            Parameters = ExtractParametersFromJson(resolvedArgs),
        },
        effectiveCapability, ct);
}

// 剥离点 A: LLM 响应文本
var rawText = _keyVault.Strip(llmResp.Content ?? "{}");
```

### 8.2 MemoryEngine 变更

```csharp
// MemoryEngine.WriteBack() — 剥离点 B
public void WriteBack(string llmReply, string sessionId, string? workspaceId, string source)
{
    llmReply = _keyVault.Strip(llmReply);  // ★ 新增
    // ... 原有逻辑
}

// MemoryEngine.BuildMemoryContext() — 剥离点 C
public string? BuildMemoryContext(string sessionId, string? workspaceId)
{
    // ... 召回逻辑
    var result = sb.ToString().Trim();
    result = _keyVault.Strip(result);  // ★ 新增
    return result.Length > 0 ? result : null;
}
```

### 8.3 DI 注册

```csharp
// PuddingAgent/Program.cs 或 PuddingRuntime 扩展方法
builder.Services.AddSingleton<IKeyVaultCrypto, AesGcmCrypto>();
builder.Services.AddSingleton<IKeyVaultService, KeyVaultService>();
builder.Services.AddDbContext<KeyVaultDbContext>(...);
builder.Services.AddScoped<IKeyVaultStore, EfKeyVaultStore>();
```

---

## 9. 数据库迁移

新增 `VaultKeyEntries` 表（通过 EF Core Migration 添加到现有 `pudding.db`）：

```sql
CREATE TABLE VaultKeyEntries (
    Id              TEXT PRIMARY KEY NOT NULL,
    Name            TEXT NOT NULL,
    Category        TEXT NOT NULL DEFAULT 'api-key',
    EncryptedValue  BLOB NOT NULL,
    IV              BLOB NOT NULL,
    Tag             BLOB NOT NULL,
    CreatedAt       TEXT NOT NULL,
    UpdatedAt       TEXT NOT NULL
);
```

> 遵循项目规范：通过 EF Core Migration 管理，不手动编辑数据库 schema。

---

## 10. 安全威胁模型

| 威胁 | 缓解措施 |
|------|---------|
| SQLite 文件被盗 | 密钥以 AES-256-GCM 加密，主密钥不存储在数据库中 |
| 内存转储攻击 | 条目缓存仅保留解密后的值在 Inject/Strip 期间短暂解密（V2：使用 SecureString） |
| LLM 输出泄露密钥 | Strip 在 history.Add() 前执行，LLM 输出的密钥立即被替换 |
| 日志泄露密钥 | 日志仅记录 key-name，不记录明文值 |
| 暴力破解主密码 | PBKDF2 600K 迭代 + 不存储密码哈希（无法离线验证） |
| 前端 XSS 窃取 | 密钥明文仅在 Modal 编辑时短暂显示，3 秒自动掩码 |

---

## 11. 实现任务分解

| Task ID | 标题 | 内容 | 预估复杂度 |
|---------|------|------|-----------|
| task-20260502-018 | KeyVault 数据模型与加密层 | IKeyVaultStore, KeyVaultEntry, IKeyVaultCrypto, AesGcmCrypto, EF Core 实体与迁移 | 中等 (1-2 模块) |
| task-20260502-019 | KeyVaultService 与 API | KeyVaultService (注入/剥离/解锁), KeyVaultController REST 端点 | 中等 (1-2 模块) |
| task-20260502-020 | Agent Loop 集成 | AgentExecutionService + MemoryEngine 注入点/剥离点改造 | 轻量 (单模块) |
| task-20260502-021 | 前端密钥保管箱页面 | KeyVaultPage UI (列表/解锁/CRUD Modal), 路由注册 | 中等 (独立页面) |

---

## 12. 风险与回滚

- **风险**：密钥注入后工具可能泄露明文到 Shell 日志 → 暂不在 ShellTool 中使用占位符，文档中标注风险
- **风险**：主密码遗忘导致密钥永久不可恢复 → V1 不解决（V2 可加恢复短语）
- **回滚**：各模块独立，可逐任务回滚；若 Agent Loop 集成有问题，注入/剥离调用为 NOP（保险库未解锁时自动跳过）

---

## 13. 验收用例

1. **存储**：前端录入密钥 → SQLite 中 EncryptedValue 非明文 → API GET 不返回明文
2. **注入**：系统提示词含 `{{vault:token}}` → Agent 调用工具时参数中为真实 token
3. **剥离**：LLM 返回了 token 明文 → history 中自动替换为 `[REDACTED:token]`
4. **隔离**：会话结束后检查 SQLite Message/Memory 表 → 无 token 明文
5. **锁定**：浏览器关闭 → 重新打开 → KeyVault 处于锁定状态 → 工具注入不生效
