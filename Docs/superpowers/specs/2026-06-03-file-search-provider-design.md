# File Search Provider Design

> 日期：2026-06-03
> 状态：draft
> 相关文档：[Tool Infrastructure Layering](../../07架构/tool-infrastructure-layering.md)

## 1. 目标

为 `file_search` 增加“文件搜索服务商”概念，让 agent 仍然只调用一个工具，但可以显式选择底层检索实现。

核心目标：

- `file_search` 支持列出可用 provider。
- `file_search` 搜索时必须由 agent 指定 provider。
- provider 不可用、调用失败、SDK 异常时，工具返回 agent 可读错误，不崩溃。
- `BuiltInRecursiveFileSearch` provider 复用现有递归文件名搜索逻辑。
- `Everything` provider 参考 voidtools 官方 C# SDK 示例实现，不直接引用陈旧 NuGet 包。
- `Everything64.dll` 作为项目运行时依赖提供；Pudding 是 64 位软件，不支持 32 位 Everything SDK。
- provider 必须支持 agent 指定目录下的文件检索，不能把 Everything 当作无约束全盘搜索结果直接返回。
- `file_search` 允许检索 workspace 外部目录，但返回结果必须包含醒目警告，提示 agent 当前结果来自 workspace 外部范围。

非目标：

- 不把 `file_search` 改成内容搜索工具。内容搜索仍由 ripgrep-backed `search_grep` 承担。
- 不引入 Everything HTTP API provider。HTTP API 是未来扩展点。
- 不支持 `Everything32.dll` 或 32 位运行时。
- 不让 provider 失败时自动 fallback 到另一个 provider。agent 必须看到明确错误并自行选择下一步。

## 2. 当前基线

当前 `file_search` 定义在 `Source/PuddingRuntime/Services/Tools/FileTools.cs`：

- 工具 id 是 `file_search`。
- 参数为 `Pattern`、`Directory`、`Recursive`、`MaxResults`。
- 默认 `Directory` 是 `HostFileToolPaths.WorkspaceRoot`。
- 默认 `Pattern` 是 `*`。
- 默认递归搜索。
- 搜索逻辑直接调用 `Directory.EnumerateFiles(...)`。

这个实现的问题是：底层搜索策略硬编码在 tool 内部，agent 无法选择或感知 provider，也无法使用 Everything 这类本地高性能索引服务。

## 3. 参数设计

`file_search` 参数扩展为：

| 参数 | 类型 | 必填 | 默认值 | 说明 |
|------|------|------|--------|------|
| `Action` | `string?` | 否 | `search` | `search` 或 `list`。`list` 返回所有 provider。 |
| `Provider` | `string?` | 搜索时必填 | 无 | 文件搜索服务商 id。支持 `Everything`、`BuiltInRecursiveFileSearch`。 |
| `Pattern` | `string?` | 否 | `*` | 文件名文本或 glob pattern。 |
| `Directory` | `string?` | 否 | workspace root | 搜索根目录，可以在 workspace 外，但必须是真实存在的目录。 |
| `Recursive` | `bool?` | 否 | `true` | 是否递归搜索子目录。 |
| `MaxResults` | `int?` | 否 | `50` | 最大结果数，clamp 到 `1..500`。 |

兼容性策略：

- `Action` 为空时按 `search` 处理。
- `Action=list` 时忽略 `Provider`、`Pattern`、`Directory`、`Recursive`。
- `Action=search` 且 `Provider` 为空时返回错误，不使用默认 provider。
- `Action` 不识别时返回错误，并列出合法 action。
- `Provider` 不识别时返回错误，并列出合法 provider。

缺失 provider 的错误信息必须包含示例：

```text
Provider is required for file_search search.
Use action=list to inspect providers, or call:
{"provider":"BuiltInRecursiveFileSearch","pattern":"*.cs","directory":"Source","recursive":true,"maxResults":50}
```

列出 provider 示例：

```json
{
  "action": "list"
}
```

搜索示例：

```json
{
  "provider": "BuiltInRecursiveFileSearch",
  "pattern": "*.cs",
  "directory": "Source/PuddingRuntime",
  "recursive": true,
  "maxResults": 100
}
```

Everything 搜索示例：

```json
{
  "provider": "Everything",
  "pattern": "FileTools.cs",
  "directory": "Source",
  "recursive": true,
  "maxResults": 20
}
```

## 4. Provider 抽象

新增 runtime 内部抽象：

```csharp
public interface IFileSearchProvider
{
    string ProviderId { get; }
    string DisplayName { get; }
    string Description { get; }
    FileSearchProviderKind Kind { get; }
    Task<FileSearchProviderStatus> GetStatusAsync(CancellationToken ct);
    Task<FileSearchProviderResult> SearchAsync(FileSearchProviderRequest request, CancellationToken ct);
}
```

建议 DTO：

```csharp
public sealed record FileSearchProviderRequest(
    string Pattern,
    string RootDirectory,
    string WorkspaceRoot,
    bool IsOutsideWorkspace,
    bool Recursive,
    int MaxResults);

public sealed record FileSearchProviderItem(
    string FullPath,
    long? Size,
    DateTime? LastWriteTime);

public sealed record FileSearchProviderResult(
    bool Success,
    IReadOnlyList<FileSearchProviderItem> Items,
    string? Error);

public sealed record FileSearchProviderStatus(
    bool IsAvailable,
    string? Error);

public enum FileSearchProviderKind
{
    BuiltIn,
    NativeSdk,
    HttpApi
}
```

注册与解析：

- 使用 DI 注册多个 `IFileSearchProvider`。
- 新增 `IFileSearchProviderRegistry` 或在 `FileSearchTool` 构造函数中注入 `IEnumerable<IFileSearchProvider>` 并建立 case-insensitive map。
- provider id 使用稳定字符串，不做本地化。
- tool 输出 provider list 时包含 id、display name、kind、availability、error。

## 5. BuiltInRecursiveFileSearch

`BuiltInRecursiveFileSearch` 是当前逻辑的 provider 化版本。

行为：

- `Pattern` 包含 `*` 或 `?` 时作为 glob。
- `Pattern` 不含通配符时枚举 `*` 后按文件名和相对路径 `Contains` 模糊匹配。
- `**/` 前缀保持当前兼容处理，转换为普通 glob。
- `Directory` 由 tool 层解析为绝对路径并校验目录存在；允许 workspace 外部目录。
- provider 返回文件路径；`FileSearchTool` 在序列化前统一执行 `Path.GetFullPath`，
  对 Agent 暴露的所有结果一律为规范化绝对路径。
- workspace 内外不再使用不同的路径格式；当 `RootDirectory` 位于 workspace 外部时，
  仍需在结果顶部追加 workspace 外部范围警告。

错误：

- 目录不存在时返回 fail。
- `UnauthorizedAccessException`、`IOException` 等 provider 内异常返回 fail。
- 不抛出到 tool execution service。

## 6. Everything Provider

### 6.1 来源优先级

Everything provider 的权威来源是 voidtools 官方 SDK 和官方 C# 示例：

- https://www.voidtools.com/zh-cn/support/everything/sdk/csharp/
- https://www.voidtools.com/en-au/support/everything/sdk/

`pardahlman/everything` 和 NuGet `Everything 0.0.1-alpha2` 仅作为封装方式参考：

- https://github.com/pardahlman/everything
- https://www.nuget.org/packages/Everything/0.0.1-alpha2

原因：

- NuGet 包最后更新时间较早，版本仍为 alpha。
- 官方 SDK 示例是 voidtools 发布的权威 C# 调用方式。
- 第三方项目可参考线程约束、选项对象和结果对象建模，但不能成为生产依赖。

### 6.2 SDK 封装方式

新增项目内 P/Invoke wrapper，例如：

```csharp
internal interface IEverythingSdk
{
    bool IsAvailable(out string? error);
    EverythingQueryResult Query(EverythingQueryRequest request);
}
```

内部实现参考官方 C# 示例使用 `DllImport` 调用：

- `Everything_SetSearchW`
- `Everything_SetMatchPath`
- `Everything_SetMatchCase`
- `Everything_SetRegex`
- `Everything_SetMax`
- `Everything_QueryW`
- `Everything_GetNumResults`
- `Everything_GetTotResults`
- `Everything_GetResultFullPathNameW`
- `Everything_GetLastError`
- `Everything_Reset`

第一版只需要文件路径、大小和修改时间能为空即可。Everything SDK 取 size/date 的 API 可在第二步补齐，避免第一版过度复杂。

### 6.3 DLL 运行时依赖

Pudding 是 64 位软件，第一版只支持 `Everything64.dll`。

`Everything64.dll` 需要作为项目运行时依赖提供，并在构建/发布时复制到应用输出目录。实现侧只声明和加载 64 位 DLL：

```csharp
[DllImport("Everything64.dll", CharSet = CharSet.Unicode)]
private static extern int Everything_SetSearchW(string lpSearchString);
```

如果 DLL 不存在、加载失败或位数不匹配：

```text
Everything provider unavailable: Everything64.dll was not found or could not be loaded.
Ensure Everything64.dll is present in the application output directory, or use provider BuiltInRecursiveFileSearch.
Example: {"provider":"BuiltInRecursiveFileSearch","pattern":"*.cs","directory":"Source","recursive":true,"maxResults":50}
```

如果 Everything 本体未运行或 IPC 查询失败，返回类似错误：

```text
Everything provider query failed: Everything appears unavailable or not running. LastError=...
Use action=list to inspect providers or retry with BuiltInRecursiveFileSearch.
```

### 6.4 Query 映射

Everything provider 输入仍使用 `Pattern` 和 `Directory`，避免把 Everything 搜索语法直接暴露为新的 agent 契约。

建议映射：

- `Directory` 是强约束。tool 层先把它解析为绝对路径并确认目录存在，provider 只能返回该目录范围内的文件。
- tool 层必须先验证 `Directory` 是否存在；目录不存在时直接返回错误，不调用 Everything SDK。
- `Pattern` 用于文件名或通配符匹配。
- Everything 查询可以先构造路径限定搜索，再对返回结果做二次过滤，确保目录约束不会因为 Everything 查询语法差异失效。
- `Recursive=true` 时返回 `Directory` 及子目录下的匹配文件。
- `Recursive=false` 时只保留目标目录直属文件。Everything 查询结果返回后再在 provider 内按目录深度过滤。
- `MaxResults` 映射到 `Everything_SetMax(maxResults)`。
- 使用 `Everything_SetMatchPath(true)`，让路径条件参与匹配。

Everything provider 的结果过滤规则：

- 结果路径必须位于 `RootDirectory` 内。
- `Recursive=false` 时，结果文件的父目录必须等于 `RootDirectory`。
- `Pattern` 包含 `*` 或 `?` 时，按当前 `BuiltInRecursiveFileSearch` glob 规则匹配文件名。
- `Pattern` 不含通配符时，按文件名和相对路径执行忽略大小写的 `Contains` 匹配。

注意：Everything 的搜索语法和 .NET glob 不完全一致。第一版以 provider 内二次过滤作为最终契约，保证 agent 指定目录不会被突破。

workspace 外部目录处理：

- `file_search` 搜索允许 workspace 外部目录，因为 Everything 这类本地文件索引服务的主要价值之一是跨目录检索。
- tool 层必须判断 `RootDirectory` 是否位于 `WorkspaceRoot` 外。
- 如果位于 workspace 外，成功结果和错误结果都应包含警告：

```text
WARNING: This search was executed outside the current workspace.
Directory: C:\...
Consequence: these results are outside the current workspace boundary. Mistaken operations may expose private files, modify unrelated projects, delete user data, or execute untrusted files.
User consent required: before reading, modifying, deleting, executing, or patching any out-of-workspace result, ask the user for explicit approval and confirm the exact path and operation.
```

- workspace 外部搜索不改变其他 file tools 的权限边界。`file_read`、`file_write`、`file_patch` 是否允许访问 workspace 外部目录，由它们自己的安全策略决定。

### 6.5 并发

Everything SDK 是全局搜索状态模型。Everything provider 必须串行执行查询：

- 使用 `SemaphoreSlim` 包裹 SDK 调用。
- 查询前设置状态。
- 查询后调用 `Everything_Reset()`。
- 所有异常转成 provider result error。

## 7. Tool 层职责

`FileSearchTool` 只负责：

- 参数验证。
- 目录路径解析和存在性校验。provider 调用前必须确认目录真实存在，目录不存在时直接返回 `ToolExecutionResult.Fail(...)`。
- 判断搜索目录是否位于 workspace 外部，并在结果中追加警告。
- provider list。
- provider 解析。
- 调用 provider。
- 将 provider item 格式化为当前工具输出。
- 将 provider 错误转换为 `ToolExecutionResult.Fail(...)`。

`FileSearchTool` 不负责：

- Everything SDK 细节。
- HTTP provider 协议。
- provider 可用性探测细节。
- provider 内部查询语法转换。

## 8. 错误语义

所有错误都返回 `ToolExecutionResult.Fail(...)`：

| 场景 | 结果 |
|------|------|
| 搜索时缺少 provider | fail，带 action=list 和搜索示例 |
| 未知 provider | fail，列出 provider |
| provider 不可用 | fail，说明不可用原因和替代示例 |
| provider 内异常 | fail，说明 provider id 和异常摘要 |
| 目录路径非法 | fail，例如空路径、包含 null 字符、路径无法解析 |
| 目录不存在 | fail，由 `FileSearchTool` 在调用 provider 前返回，不传给 Everything |
| 目录在 workspace 外 | success 或 fail 都附加 workspace 外部范围警告，不因越界直接失败 |
| 无匹配结果 | success，输出 `(no matching files)` |

禁止行为：

- 不自动 fallback。
- 不让 native exception 冒泡到 `PuddingToolBase` 的通用 catch。
- 不把 provider 错误伪装成空结果。

## 9. 测试策略

新增或更新 `Source/PuddingRuntimeTests/Tools/PuddingToolInfrastructureTests.cs`，覆盖：

1. `Action=list` 返回 `Everything` 和 `BuiltInRecursiveFileSearch`。
2. 搜索时不传 provider 返回 fail，并包含 `BuiltInRecursiveFileSearch` 示例。
3. 未知 provider 返回 fail，并包含 provider 列表。
4. 目录不存在时 tool 返回 fail，且 fake provider 未被调用。
5. workspace 外部目录存在时允许搜索，并在输出中包含 warning。
6. `BuiltInRecursiveFileSearch` 搜索结果与当前逻辑一致。
7. provider 抛异常时 tool 返回 fail，不抛出。
8. Everything provider 不可用时返回明确 fail。
9. Everything provider 对 fake SDK 返回的全盘结果执行目录过滤，只返回 agent 指定目录内文件。
10. Everything provider 对 `Recursive=false` 执行直属目录过滤。
11. Everything SDK wrapper 可用 fake 实现单元测试，不依赖本机安装 Everything 本体。
12. 任意 provider 返回相对路径时，`FileSearchTool` 以已解析的搜索根目录为基准转换为绝对路径。
13. Everything 与 BuiltIn provider 的成功结果均为绝对路径，fallback 不得改变路径格式。

Everything provider 的 native 集成测试默认不启用。后续可增加显式环境变量门控：

```text
PUDDING_TEST_EVERYTHING_SDK=1
```

只有设置该变量且本机存在 `Everything64.dll` 和 Everything 本体时才运行真实集成测试。

## 10. 验收标准

- `file_search` 的 schema 包含 `Action` 和 `Provider`。
- `file_search` 搜索不传 `Provider` 时返回错误，不使用默认 provider。
- `file_search` 在调用 provider 前校验 `Directory`，目录不存在时直接返回错误。
- `file_search` 允许搜索 workspace 外部目录，但输出必须包含 workspace 外部范围 warning。
- `file_search` 支持 `{"action":"list"}`。
- `BuiltInRecursiveFileSearch` provider 保持当前搜索行为。
- `Everything` provider 不直接引用 NuGet 包。
- `Everything64.dll` 纳入运行时输出；不支持 `Everything32.dll`。
- `Everything` provider 只返回 agent 指定目录范围内的文件，不返回无关全盘结果。
- 所有 provider 和 fallback 路径对 Agent 暴露的搜索结果均为规范化绝对路径。
- Everything 不可用时 agent 收到明确错误和替代示例。
- 所有 provider 异常都被包装为 `ToolExecutionResult.Fail(...)`。
- 现有 tool registry 自动发现链路不需要改动。

## 11. 后续扩展

后续可以增加：

- `HttpFileSearchProvider`，用于连接本地 HTTP 文件索引服务。
- provider 配置 API，让 Admin UI 配置 SDK 路径、HTTP base URL、默认限制。
- Everything 结果字段增强，补齐 size、created、modified。
- provider health check 页面。
