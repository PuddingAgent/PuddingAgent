# file_patch 参数反序列化缺陷 —— 只读诊断（2026-09-18）

> 关联卡：`f1d45a1501f04b62bc25e6c2afedf8f0`（p0 Ready，父卡范围混杂：终端生命周期 + 引号转义 + 工作目录隔离 + 本项）
> 诊断方式：**只读取证**（父级亲自执行），未改任何代码。
> 证据分级：【已证实】/【推断】/【待验证】

---

## 1. 卡面原始现象（来自卡片描述，非本次复现）
> 「【父代理实测】`file_patch` 的 operations 数组参数**两次调用均反序列化失败**（错误提示要求 `old_text`），最终用 `apply_patch` unified diff 绕过；疑似 operations 元素的字段名映射（`oldText`/`newText` vs `old_text`/`new_text`）存在序列化 bug。」

## 2. 本次已证实的事实

【已证实 F1】`PatchOperation` 各属性**只声明 snake_case 的 `JsonPropertyName`**，未见 camelCase 别名：
- `Source/PuddingRuntime/Tools/BuiltIns/Files/FilePatchTool.cs:1303` → `[JsonPropertyName("old_text")]`
- `:1307` → `[JsonPropertyName("new_text")]`
- `:1311` → `[JsonPropertyName("replace_all")]`
- `:1315` → `[JsonPropertyName("start_line")]`
- 外层 `FilePatchArgs`：`:898`（record 定义）、`:904` → `[JsonPropertyName("patch_text")]`

【已证实 F2】**错误提示宣称接受 camelCase，与映射不一致**：
- `:124-127` → `"replace operation in {relPath} requires 'old_text' (or 'oldText')."`
- `:301` → `"replace operation requires 'old_text' (or 'oldText') - skipped."`
⇒ 提示劝用户改用 `oldText`，而 `:1303` 的映射并不会匹配 `oldText`（除非宿主另有 case-insensitive 或命名策略，且该策略也无法跨下划线匹配）。

【已证实 F3】`old_text` 路径**可用**：父级本日（2026-09-18）用 `{"type":"replace","old_text":…,"new_text":…}` 调用 `file_patch` **三次全部成功**（S1-c 矩阵 §16/§17/§18 三次 patch），且 `new_text` 亦成功。
⇒ 失败并非"工具整体不可用"，而是**特定键名不被接受**（且失败信息误导）。

【已证实 F4】另有守卫 `:124` `if (string.IsNullOrEmpty(op.OldText))` 主动拒绝空 `old_text`；`replace/insert/replace_lines` 还要求 `new_text` 显式存在（可为空串）。

## 3. 候选根因（推断，未完全证实）

【推断 R1】**工具参数 schema 与实际反序列化键名不一致**：
若暴露给模型的参数 JSON schema 由 C# 属性名投影（`OldText` → `oldText`），而反序列化只认 `[JsonPropertyName("old_text")]`，则：
模型按 schema 传 `oldText` → 反序列化 `OldText == null` → 触发 `:124` 拒绝 → 错误提示又建议改用 `oldText`（**死循环式误导**）。
该机制可同时解释：①卡面"两次均反序列化失败"；②失败信息**恰好要求 old_text**（因为 `OldText` 为 null 走了该分支）；③改用 `apply_patch`（unified diff 走 `:248` 的另一条路径，不经过 operations 反序列化）即绕过。

【推断 R2】即便 schema 正确，`(or 'oldText')` 的提示本身也是**错误文档**：它承诺了一个不受支持的键名。

## 4. 待验证（下一步动作，未执行）

- V1：读 `PuddingToolBase<TArgs>` 与工具 schema 生成处，确认参数 schema 的属性名来源是 `JsonPropertyName` 还是 C# 属性名。
- V2：以 `{"type":"replace","oldText":"a","newText":"b"}` 构造单测，断言其**应当**成功（期望语义）或明确失败（现状），把行为固化。
- V3：以 `{"old_text":"a","new_text":"b"}` 构造单测，锁住当前可用路径（防回归）。
- V4：确认宿主反序列化 `JsonSerializerOptions`（是否 `PropertyNameCaseInsensitive` / 命名策略）。

## 5. 建议修复方向（需设计确认，勿直接实施）

- **A（优先）**：为 `PatchOperation` 增加 camelCase 别名支持（自定义 `JsonConverter` 或在反序列化入口接受两种键名），使 schema 与实现一致。
- **B（低成本，立即可做）**：修正 `:124-127` 与 `:301` 的提示文本——若确定不支持 camelCase，则删除 `(or 'oldText')`；若支持，则补齐映射。
- **C**：统一"工具 schema 键名"与"反序列化键名"的**单一事实来源**，避免同类不一致在其它工具上重现（应做一次全量排查）。

## 6. 与其它卡的关系（避免重复劳动）

- 卡面其余三项（终端 runner 语义 / 管道引号 / `cmd /c` 审批墙 / `git worktree` 30s 超时 / 子代理 `working_directory` 隔离）**与本文诊断无重叠**，其中 shell 路由部分卡片已标注由另一卡（`102f85ce…`）负责。
- 本诊断**只覆盖 `file_patch` 键名映射与提示文本**，建议将该卡按此拆分后再派发实施。

---
**结论一句话**：`file_patch` 的 snake_case 路径**可用**；不可用的是 camelCase 路径，而错误提示**恰好建议用户改用该不可用键名** —— 这解释了卡面"反序列化失败"的观感，修复优先级：提示文本（低成本）→ 键名统一（根治）。
