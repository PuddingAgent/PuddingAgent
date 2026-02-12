# QA 报告 — Agent 个性与记忆系统后端

**审阅日期**: 2026-05-05
**审阅模型**: GPT-5.3-Codex (copilot)
**审阅范围**: Agent Persona / Workspace UserProfile / AgentMemory / 分层提示词 4 个 P0 任务

---

## 结论: **FAIL**

存在 1 个 P0 阻断问题 + 1 个 P1 问题。

---

## P0 阻断问题

### P0-1: Runtime 不读取数据库中的 PersonaPrompt/ToolsDescription/AvatarEmoji，分层提示词对用户配置完全无效

**位置**: `Source/PuddingRuntime/Services/AgentExecutionService.cs` L108-110

**现象**:
`BuildLayeredSystemPromptAsync` 从 `template`（类型 `AgentTemplateDefinition`）读取 `PersonaPrompt`/`ToolsDescription`/`AvatarEmoji`，但 `template` 的来源是：

```csharp
var template = BuiltInAgentTemplates.FindById(canonicalTemplateId)
               ?? BuiltInAgentTemplates.WorkspaceServiceAgent;
```

**根本原因**: Runtime 仅从 `BuiltInAgentTemplates`（硬编码的 4 个内置模板）中查找模板定义，**不读取 Platform 数据库**。所有内置模板的 `PersonaPrompt`/`ToolsDescription`/`AvatarEmoji` 均为 `null`。

因此，无论用户在 Platform Admin 中如何配置 PersonaPrompt/ToolsDescription，Runtime 生成的系统提示词中 SOUL/TOOLS 层永远为空，IDENTITY 层的 Avatar 也永远为空。

**影响**: Persona 分层的核心功能不可用，用户在 Admin UI 配置的个性字段全部静默丢弃。

**修复方向**: Runtime 需要通过 `_platformDbFactory` 从 Platform 数据库读取 Entity 配置，将 PersonaPrompt/ToolsDescription/AvatarEmoji 映射到 `AgentTemplateDefinition`，或直接在 `BuildLayeredSystemPromptAsync` 中从 DB 读取字段并合并到分层提示词。

---

## P1 问题

### P1-1: PuddingRuntime → PuddingPlatform 项目引用造成架构层级违反

**位置**: `Source/PuddingRuntime/PuddingRuntime.csproj` L14

```xml
<ProjectReference Include="..\PuddingPlatform\PuddingPlatform.csproj" />
```

**架构约束**: 依赖方向应为 UI → Controller → Runtime → Core。Runtime 引用 Platform 属于**同层或逆向引用**。

**当前影响**: PuddingPlatform 引用 PuddingCore（共享底层），PuddingRuntime 引用 PuddingPlatform + PuddingCore。不构成循环依赖（Platform 未引用 Runtime），但违反了分层架构的依赖方向。

**风险**: 随着功能增长，Platform ↔ Runtime 可能产生循环依赖；同时 Runtime 直接耦合了 Platform 的 EF DbContext，增加了部署耦合度。

**建议**: 
1. 短期：通过接口抽象（如 `IWorkspaceProfileProvider`）解耦，Runtime 只依赖接口，Platform 提供实现。
2. 长期：将共享的 Entity/DTO 下沉到 PuddingCore 或新建 PuddingPlatform.Contracts 项目。

---

## 审阅检查项总结

| 检查项 | 结果 | 备注 |
|--------|------|------|
| **循环依赖** | ✅ 无循环 | Platform 不引用 Runtime，但架构方向违反（P1-1） |
| **向后兼容** | ✅ 通过 | 所有新增字段 nullable，现有 API 行为不变 |
| **DTO 反序列化** | ✅ 通过 | 新字段用默认值 null，新旧格式兼容 |
| **分层提示词正确性** | ❌ **阻断** | SOUL/TOOLS/USER 层逻辑正确，但 SOUL/TOOLS 的数据源不接数据库（P0-1） |
| **空值处理** | ✅ 通过 | `IsNullOrWhiteSpace` 判断，空时不输出 block 内容 |
| **AGENTS 层保持** | ✅ 通过 | `template.SystemPrompt ?? "You are a helpful assistant."` 保持原有内容 |
| **Migration Up** | ✅ 通过 | 9 列 nullable TEXT，与 Entity 定义一致 |
| **Migration Down** | ✅ 通过 | 正确 DropColumn 所有新增列 |
| **AgentMemories 表** | ✅ 通过 | 索引设计合理，EF ModelBuilder 与 SQL 一致 |
| **编译** | ✅ 通过 | 零 error |
| **单元测试** | ✅ 通过 | MemoryEngine 6/6 PASS |
| **Entity 字段同步** | ✅ 通过 | Global/Workspace Template Entity ↔ DTO ↔ AgentTemplateDefinition 字段对齐 |
| **Workspace UserProfile** | ⚠️ 部分 | LoadWorkspaceUserProfileAsync 正确读取 DB，但仅用于 USER 层（不受 P0-1 影响） |

---

## P2 改进建议

1. **BootstrapTemplate 未使用**: Entity/DTO/AgentTemplateDefinition 均声明了 `BootstrapTemplate`，但 `BuildLayeredSystemPromptAsync` 中没有使用它。如果不是本轮交付范围，建议添加 `// TODO: BootstrapTemplate injection` 注释。
2. **AgentMemoryEntity 日期索引缺失**: `AgentMemories` 表有 `DateKey` 字段但无独立索引，若按日期查询（如 daily 记忆），性能不佳。
3. **LoadWorkspaceUserProfileAsync 异常处理**: catch 全部 Exception 并返回 null，可能导致 DB 连接问题被掩盖。建议区分 DB 连接异常（记录并降级）和查询异常。

---

## 文件清单

| 文件 | 状态 |
|------|------|
| `GlobalAgentTemplateEntity.cs` | ✅ |
| `WorkspaceAgentTemplateEntity.cs` | ✅ |
| `WorkspaceEntity.cs` | ✅ |
| `PlatformDtos.cs` | ✅ |
| `AgentMemoryEntity.cs` | ✅ |
| `MemoryDbContext.cs` | ✅ |
| `init_memory.sql` | ✅ |
| `AgentExecutionService.cs` | ❌ P0-1 |
| `PuddingRuntime.csproj` | ⚠️ P1-1 |
| `Migration 20260505103000` | ✅ |
