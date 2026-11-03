# PuddingComputer.Automation · code_map

Computer Use 的**纯规则**组件。只放无 IO 的判定：动作矩阵、坐标换算、候选标注、回执归约。
不引用 UIA、窗口、输入、IPC、DI、数据库 —— 那些属于 `PuddingComputer.Windows` / `Connection` / `Worker`。

依赖方向：`PuddingComputer.Automation → Pudding.Contracts`（单向，编译期强制）。

| 文件 | 职责与不变量 |
|---|---|
| `ComputerSnapshotAnnotator.cs` | 物理检测框 → 编号、窗口相对坐标、图内点击点；必须使用该图 SourceRect 与输出尺寸，保留候选来源/标签/置信度 |
| `ComputerActionMatrix.cs` | 12 个动作 × 2 个后端的完整矩阵；`SupportedBackend` 返回唯一后端（Drag 为 null）；`EmptyTextClearsContent` 区分「set_value 空串=清空」与「type_text 空串=no-op」；`MutatesTarget` 供回执判断是否需要后置验证 |
| `ComputerCoordinateMapper.cs` | 图内坐标 → 屏幕物理坐标。**先判采集方式（WindowRender 不签发可点坐标）再判边界**，顺序不可反；采集方式优先于调用方声明的 `CoordinateInputAllowed`；舍入固定 `AwayFromZero` |
| `ComputerReceiptEvaluator.cs` | 回执归约：NotStarted→Blocked（唯一允许直接重试）、Unknown→保守认为已生效、部分提交→Failed（不可重发整段）、验证通过→Verified、提交未验证→Dispatched |

## 关键不变量

- **只有 Blocked 可以安全重试**。其余情况重发都是重复副作用，且不会报错。
- **`Invoke` ≠ `Click`**：前者是控件语义点击（与窗口位置无关），后者是像素点击（依赖观察）。
- **`partial_input` 必须报 Failed 而不是 Dispatched**：报 Dispatched 会让调用方以为整段完成；报 Failed 又可能被重发，把已提交那部分再执行一次。

## 消费者

[PuddingComputer.Windows](../PuddingComputer.Windows/code_map.md) 消费坐标、标注与回执规则；Host/Runtime 的 computer_observe/input 经 Windows 组件使用这些生产路径。

## 独立测试

[AutomationTests](../PuddingComputer.AutomationTests/code_map.md)：包括真实标注输出到映射器的往返、副屏非零/负原点与缩放裁图；验证结果写修改日志。
