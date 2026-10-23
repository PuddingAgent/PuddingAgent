# PuddingContextPruningTests CodeMAP

> 职责：`PuddingContextPruning` 的**独立**测试工程（S2/S3/S4）——不启动宿主、不连库、不读配置，`dotnet test` 直接跑。
> 边界：只 `ProjectReference` 被测工程（含 `PuddingCore` 传递引用）；**不得**引用 `PuddingRuntime` / `PuddingHost` / `PuddingPlatform` / `PuddingCodeIntelligence` / `PuddingCodeIndex`，`ComponentBoundaryTests` 会在 CI 里把这条钉死。

## 关键文件

| 文件 | 覆盖 | 何时读 |
|---|---|---|
| `DeterministicPayloadCompactionTests.cs` | 17 例行为用例：开关关闭 = 原样返回、尾部按引用保留、配对回退、去重留最新、阈值边界、首尾摘要、参数截断、两条 reclaim 回退、逐字节确定性、入参不可变、四种 no-op 原因、多模态/摘要保护 | 改 `DeterministicPayloadCompaction` 前先看它锁了什么 |
| `PruneOptionsValidationTests.cs` | 9 例：冻结默认值 + 每条非法配置的异常类型与参数名（含 `MinReclaimRatio = NaN` 这种静默通过型陷阱） | 调 `Validate()` 规则时 |
| `ComponentBoundaryTests.cs` | 3 例边界：探测器**自检**（必须恰好标出 8 个禁用名，否则说明检测逻辑坏了）、进程实际加载面、`deps.json` 依赖闭包 | 怀疑「隔离」被破坏时 |
| `PuddingContextPruningTests.csproj` | 单引用 + 程序集级 `Parallelize(MethodLevel)`（纯内存用例，无共享状态） | 加包/加引用时 |

## 断言约定

- `Assert.AreSame(input, outcome.Messages)` 不是笔误：**所有 no-op 路径都必须返回入参列表实例**，改成 `AreEqual` 会放过「新建了一份内容相同的列表」这种破坏前缀缓存的回归。
- 确定性用例用「两次独立构造相同输入 → 结果指纹相等」的方式断言，不要靠单次输出的字面量快照（快照会把实现细节焊死）。
- 变异取红纪律：临时改被测源码（例如把尾部回退的 `while` 改成 `if`），确认对应用例**变红**，再逐位复原并 `dotnet build -t:Rebuild` 证明回绿。只跑绿不跑红，等于没验证。

## 变更日志

| 日期 | 变更 |
|---|---|
| 2026-10-23 | 初版：29 例（行为 17 / 校验 9 / 边界 3）全绿；构建 0 警告 0 错误；变异取红已验证 |
