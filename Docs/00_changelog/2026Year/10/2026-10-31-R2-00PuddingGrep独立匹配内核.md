# 2026-10-31 R2-00 起步：PuddingGrep 独立匹配内核（S1–S4）

## 交付

- `Source/PuddingGrep/`（新组件）：`GrepPattern` / `GrepSpan` / `GrepMatcher` + `code_map.md`。
- `Source/PuddingGrepTests/`（新测试工程）：40 条用例，含 oracle 对照、校验失败、超时、边界断言。

## 为什么先做这一块

施工计划 R2-00 要求「Grep 先完成独立匹配内核，全文接入留 R3」。选它作为 R2-A 的起点，是因为它**不需要**碰宿主、数据库或现有检索链路 —— 可以在自己的边界内完整构建与验证，符合组件化交付规程 S1–S4。

同时它针对的是 R1-01 暴露的真实问题：grep 的字面量/正则语义此前只存在于 `Runtime/Tools/BuiltIns/Search/SearchGrepTool.cs`（1200+ 行）里，没有单一真源。

## 设计取舍

| 取舍 | 理由 |
|---|---|
| 空字面量 / 空正则 / 非法正则**构造期**失败 | 留到运行时就是「静默不命中」，上层读成「没有匹配」—— 错误否定的同族问题 |
| 模式无效时**抛**而不是返回 false | 调用方无法区分「没匹配」与「模式坏了」 |
| `FindAll` 不允许重叠 | 与 `Regex.Matches` 语义一致；重叠会让同一行被计数两次 |
| 正则一律 `CultureInvariant` + 2s 超时 | 文化相关相等会随区域设置变结果；无超时会被灾难性回溯挂住 |
| 不引任何 Pudding* 程序集 | 边界由 `ComponentBoundaryTests` 反射断言，不靠文档 |

## 证据（实测）

- 构建：`dotnet test Source\PuddingGrepTests\PuddingGrepTests.csproj` → **40/40 通过**，覆盖率 Line **97.53%** / Branch **89.28%**。
- **变异取红**：把字面量比较固定为 `StringComparison.Ordinal`（丢掉大小写开关）⇒ **恰好 1 红**：`Literal_Matches_SameAs_BCL_IndexOf ("NEEDLE")`；按备份字节还原 SHA-256 位级一致后 0 红。
- oracle 对照：字面量与 `IndexOf`、正则与 `Regex` / `Regex.Matches` 逐条比对，覆盖大小写、`.` 不被当通配、中文、空行、重叠语义。

## 未做（明确）

- **未接入**：不进 `PuddingAgentNetwork.slnx`、不注册 DI、不替换 `SearchGrepTool` 的匹配逻辑 —— 按规程 S5 才做，且 R3 才做全文接入。
- 扫描层（文件枚举、排除目录、扩展名、候选索引、`maxResults`/预算）不在本组件内。
- 未与生产 `SearchGrepTool` 做行为等价验证（那需要 S5 之后的双跑对照）。

## 补充：登记进 `PuddingAgentNetwork.slnx`（构建门禁）

```
$ dotnet sln PuddingAgentNetwork.slnx list | findstr PuddingGrep
Source\PuddingGrep\PuddingGrep.csproj
Source\PuddingGrepTests\PuddingGrepTests.csproj
```

项目总数 93 → **95**，无重复路径，XML 结构校验通过。

### 为什么这一步不能省

组件已通过 S1–S4，但如果**不进解决方案**，全仓构建与门禁都不会构建/运行它 —— 那就是「存在但没人跑」的测试工程，迟早静默腐烂。

登记属**构建门禁**，不等于运行时接入：DI 注册、替换 `SearchGrepTool` 的匹配逻辑仍未做，那部分需要部署验证。

### 仍未做

- DI 注册、`SearchGrepTool` 匹配逻辑替换（S5 运行时部分）
- 与生产 `SearchGrepTool` 的行为等价对照（需先在 Runtime 测试侧暴露可测接缝）
