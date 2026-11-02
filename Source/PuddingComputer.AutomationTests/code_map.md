# PuddingComputer.AutomationTests · code_map

`PuddingComputer.Automation` 的独立测试工程（MSTest.Sdk）。断言全部是纯函数行为，不接触真实桌面、不启动进程、不改剪贴板。

| 文件 | 覆盖的不变量 | 变异验证 |
|---|---|---|
| `ComputerActionMatrixTests.cs` | 12 动作 × 2 后端全组合；`Every_action_is_classified` 用集合比对防止「新增动作忘归类」；空串语义只对 set_value 成立；不改状态的动作只能是 Move / Drag | 把 IsSupported 里 Drag 改为 true → 2 红（精确命中 Drag 与 SupportedBackend 两条） |
| `ComputerCoordinateMapperTests.cs` | 一对一/半尺寸/双倍缩放；**WindowRender 永不出坐标**；采集方式优先于调用方标志；负坐标副屏；零尺寸除零防护 | 去掉「采集方式优先」→ 1 红（Capture_mode_wins_even_if_the_flag_lies） |
| `ComputerReceiptEvaluatorTests.cs` | 六种情况表逐条；`Only_blocked_is_ever_safe_to_retry` 穷举 execution×verification；NotStarted 永不报副作用 | 部分提交从 Failed 改为 Dispatched → 1 红（Partial_input_is_failed_not_dispatched） |

## 测试数字的来源

坐标用例的期望值取自 CU0 探针 #2 的**真实采集**（虚拟屏 4608x1440、截取矩形 (2104,600,400,240)、
图 400x240、图内 x=200 → 屏幕 x=2304），不是编出来的整数。

## 约定

- 断言用**相对关系或实测值**，不钉平台相关实现细节（换 SkiaSharp/运行时版本时不应假红）。
- 每条防漂移用例都写清「它在防什么」，避免后来者删掉它时以为只是重复。
- 变异锚点必须带上下文做唯一性断言（裸锚点曾重复出现两次）。
