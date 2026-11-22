# PuddingContextPruningTests CodeMAP
> 源指纹: TestResults/**=3-145e5ffbee52, *.cs=3-c3c77c2a5e74, *.csproj=1-85b3eb84bee9 · 条目数: 4 · 最近整理: 2026-10-23

> 职责：`PuddingContextPruning` 的**独立**测试工程（S2/S3/S4）——不启动宿主、不连库、不读配置，`dotnet test` 直接跑。
> 边界：只 `ProjectReference` 被测工程（含 `PuddingCore` 传递引用）；**不得**引用 `PuddingRuntime` / `PuddingHost` / `PuddingPlatform` / `PuddingCodeIntelligence` / `PuddingCodeIndex`，`ComponentBoundaryTests` 会在 CI 里把这条钉死。

## 关键文件

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `DeterministicPayloadCompactionTests.cs` | 17 例行为：尾部保护与去重留最新等剪枝语义 | `DeterministicPayloadCompactionTests` | `DeterministicPayloadCompaction` | no-op 路径必须 `Assert.AreSame` 入参实例；改成 `AreEqual` 会放过新建列表的回归 |
| `PruneOptionsValidationTests.cs` | 9 例配置校验：冻结默认值与非法配置的异常类型和参数名 | `PruneOptionsValidationTests` | `DeterministicPruneOptions` | 必须覆盖 `MinReclaimRatio = NaN` 这类静默通过型陷阱 |
| `ComponentBoundaryTests.cs` | 3 例边界：探测器自检与 `deps.json` 依赖闭包 | `ComponentBoundaryTests` | `../PuddingContextPruning/PuddingContextPruning.csproj` | 探测器自检必须恰好标出 8 个禁用名，否则说明检测逻辑本身坏了 |
| `PuddingContextPruningTests.csproj` | 独立测试工程：单引用被测叶子 + 方法级并行 | `—` | `../PuddingContextPruning/PuddingContextPruning.csproj` | 不得引用 Runtime/Host/Platform，边界由 `ComponentBoundaryTests` 钉死 |

## 断言约定

- `Assert.AreSame(input, outcome.Messages)` 不是笔误：**所有 no-op 路径都必须返回入参列表实例**，改成 `AreEqual` 会放过「新建了一份内容相同的列表」这种破坏前缀缓存的回归。
- 确定性用例用「两次独立构造相同输入 → 结果指纹相等」的方式断言，不要靠单次输出的字面量快照（快照会把实现细节焊死）。
- 变异取红纪律：临时改被测源码（例如把尾部回退的 `while` 改成 `if`），确认对应用例**变红**，再逐位复原并 `dotnet build -t:Rebuild` 证明回绿。只跑绿不跑红，等于没验证。

## 变更日志

