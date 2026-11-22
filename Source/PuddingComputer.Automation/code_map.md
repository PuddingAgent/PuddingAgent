# PuddingComputer.Automation · code_map
> 源指纹: *.cs=6-52e4a59a8bd6, *.csproj=1-3c48dc760b91 · 条目数: 4 · 最近整理: 2026-11-03

Computer Use 的**纯规则**组件。只放无 IO 的判定：动作矩阵、坐标换算、候选标注、回执归约。
不引用 UIA、窗口、输入、IPC、DI、数据库 —— 那些属于 `PuddingComputer.Windows` / `Connection` / `Worker`。

依赖方向：`PuddingComputer.Automation → Pudding.Contracts`（单向，编译期强制）。

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `ComputerSnapshotAnnotator.cs` | 把物理检测框标成编号目标，并给出窗口相对坐标与图内点击点 | `ComputerSnapshotAnnotator`、`RawDetection`、`AnnotatedTarget` | `ComputerCoordinateMapper.cs` | 必须用该图 SourceRect 与输出尺寸；候选来源、标签与置信度不得丢 |
| `ComputerActionMatrix.cs` | 12 个动作 × 2 个后端的支持矩阵与副作用分类 | `ComputerActionMatrix` | `ComputerReceiptEvaluator.cs` | Drag 的 `SupportedBackend` 为 null；`set_value` 空串=清空而 `type_text` 空串=no-op |
| `ComputerCoordinateMapper.cs` | 图内坐标换算成屏幕物理坐标并判定是否可点 | `ComputerCoordinateMapper`、`CoordinateMapResult`、`CoordinateMapStatus` | `ComputerSnapshotAnnotator.cs` | 先判采集方式再判边界，顺序不可反；WindowRender 不签发可点坐标；舍入固定 `AwayFromZero` |
| `ComputerReceiptEvaluator.cs` | 把动作回执归约成五种结论 | `ComputerReceiptEvaluator`、`ComputerReceiptVerdict` | `ComputerActionMatrix.cs` | 只有 Blocked 可安全重试；partial_input 必须报 Failed 而非 Dispatched |

## 关键不变量

- **只有 Blocked 可以安全重试**。其余情况重发都是重复副作用，且不会报错。
- **`Invoke` ≠ `Click`**：前者是控件语义点击（与窗口位置无关），后者是像素点击（依赖观察）。
- **`partial_input` 必须报 Failed 而不是 Dispatched**：报 Dispatched 会让调用方以为整段完成；报 Failed 又可能被重发，把已提交那部分再执行一次。

## 消费者

[PuddingComputer.Windows](../PuddingComputer.Windows/code_map.md) 消费坐标、标注与回执规则；Host/Runtime 的 computer_observe/input 经 Windows 组件使用这些生产路径。

## 独立测试

[AutomationTests](../PuddingComputer.AutomationTests/code_map.md)：包括真实标注输出到映射器的往返、副屏非零/负原点与缩放裁图；验证结果写修改日志。
