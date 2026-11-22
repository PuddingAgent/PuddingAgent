# Context tests

上下文装配的测试：规范顺序、冒名与字段边界、确定性排序与裁剪。

> 源指纹: Source/PuddingContextTests/**=pending · 条目数: 3 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `ContextTestHost.cs` | 确定性分词器（可按需失败）与冻结的规范消息/贡献/请求夹具 | `CountingTokenizer`、`ContextFixtures` | `IContextTokenizer` | 测试替身，不是产品实现 |
| `ContextTests.cs` | 覆盖栅栏与哈希校验、冒名与重复 id；验证确定性排序与裁剪 | `ContextTests.Validator_RequiresTheCanonicalCurrentUserFence` | `ContextAssemblyRequest` | — |
| `PuddingContextTests.csproj` | 测试可执行工程，唯一工程引用为 `PuddingContext` | — | `PuddingContext` | — |
