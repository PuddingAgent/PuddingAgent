# PuddingContext
> 源指纹: *.cs=2-e094aa7e2e18, *.csproj=1-f1b24b2c7fb3 · 条目数: 2 · 最近整理: 2026-11-22

上下文装配（PA-05D）：按 fence 与 token 预算把 canonical 历史与外部贡献装成有界上下文，纯类库、不依赖宿主。

> 源指纹: Source/PuddingContext/**=pending · 条目数: 3 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `ContextContributionValidator.cs` | 按 fence 与角色规则校验装配请求是否可用 | `ContextContributionValidator`、`ValidateRequest`、`ValidateContribution`、`VerifyHash` | `ContextAssemblyRequest`、`ContextException` | 外部/记忆/工具/调用方内容只能是 Tool 角色，禁止冒充 user 或 system；来源为 canonical 的贡献一律拒绝 |
| `ContextAssembler.cs` | 按优先级降序确定性装配，只从最低优先级裁剪 | `ContextAssembler`、`AssembleAsync` | `IContextTokenizer`、`ContextAssemblyResult` | canonical 历史本身超预算必须抛 `BudgetExceeded`，不得静默截断；tokenizer 异常要转成 `TokenizerUnavailable` 而非吞掉 |
| `PuddingContext.csproj` | 定义本工程的编译期边界：只能引用 Context 端口契约 | — | `../PuddingContext.Contracts/PuddingContext.Contracts.csproj` | `EnforceContextBoundary` 目标会在构建期失败：引用了非 Contracts 工程或任何 PackageReference 都直接报错 |
