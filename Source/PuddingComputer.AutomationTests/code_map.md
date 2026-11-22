# PuddingComputer.AutomationTests · code_map
> 源指纹: TestResults/**=2-1910e27341df, *.cs=6-0d69396cdba7, *.csproj=1-ca6c02767b54 · 条目数: 3 · 最近整理: 2026-11-02

`PuddingComputer.Automation` 的独立测试工程（MSTest.Sdk）。断言全部是纯函数行为，不接触真实桌面、不启动进程、不改剪贴板。

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `ComputerActionMatrixTests.cs` | 动作矩阵：12 动作 × 2 后端全组合与空串语义 | `ComputerActionMatrixTests` | `ComputerActionMatrix` | 把 IsSupported 里 Drag 改为 true 则 2 条红（`Drag` 与 `SupportedBackend`） |
| `ComputerCoordinateMapperTests.cs` | 图内坐标到屏幕坐标的换算回归 | `ComputerCoordinateMapperTests` | `ComputerCoordinateMapper` | 去掉「采集方式优先」则 `Capture_mode_wins_even_if_the_flag_lies` 红 |
| `ComputerReceiptEvaluatorTests.cs` | 回执归约六种情况表与「只有 Blocked 可重试」 | `ComputerReceiptEvaluatorTests` | `ComputerReceiptEvaluator` | 部分提交从 Failed 改为 Dispatched 则 `Partial_input_is_failed_not_dispatched` 红 |

## 测试数字的来源

坐标用例的期望值取自 CU0 探针 #2 的**真实采集**（虚拟屏 4608x1440、截取矩形 (2104,600,400,240)、
图 400x240、图内 x=200 → 屏幕 x=2304），不是编出来的整数。

## 约定

- 断言用**相对关系或实测值**，不钉平台相关实现细节（换 SkiaSharp/运行时版本时不应假红）。
- 每条防漂移用例都写清「它在防什么」，避免后来者删掉它时以为只是重复。
- 变异锚点必须带上下文做唯一性断言（裸锚点曾重复出现两次）。
