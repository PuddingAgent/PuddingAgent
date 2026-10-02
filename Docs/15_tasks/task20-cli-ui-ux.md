# Task 20 - CLI/TUI 交互与信息架构设计
状态：`partial`（UI v1/v2/v3 已落地，UI v4 与 UI v5 最小版已落地）  
优先级：`P0`  
最后更新：2026-02-20

## 1. 目标
在 CLI 中实现“丝滑、克制、信息充分”的编码交互体验，并逐步演进到三栏 TUI 工作台。

## 2. 当前实现进度
### 已实现（UI v1）
- 底部固定输入行（输入焦点稳定，避免光标漂移）。
- Slash 命令菜单（输入 `/` 即触发，支持上下选择与回车确认）。
- 命令候选滚动窗口（选中项始终可见）。
- 底部快捷键提示行（可发现性增强）。
- 输入编辑增强：
  - `Ctrl+K` 清空输入
  - `Ctrl+P` / `Ctrl+N` 历史输入切换
  - `Tab` 在 slash 模式下补全当前选中命令
  - `Esc` 退出当前选择模式

### 已实现（UI v2 - 最小版）
- 会话/运行双面板状态栏（每轮输入前刷新）：
  - 会话：项目、模型、sub-stream、运行时长、左侧交互流（最近事件）
  - 运行：turn 数、token 估算、context 估算、tool 调用/结果、错误数、active workers、todo 统计
- 新增 `/status` 命令输出详细运行快照。
- 新增 `/todo` 命令族（`list/add/done/remove`），并将数据持久化到项目级 `.pudding/todo.json`。
- 工作台重绘模式：每轮输入前清屏重绘面板，并展示 `Latest Output` 区域以减少滚屏干扰。
- 左侧交互流支持分页滚动：
  - `PgUp` / `PgDn` 快捷滚动
  - `/ui scroll up|down|top|bottom` 命令滚动
  - Windows 控制台下支持鼠标滚轮滚动（输入框为空时触发）
- 左侧面板支持 Tab 切换（`main/swarm/todo`）：
  - `Alt+[` / `Alt+]` 快捷切换
  - `Alt+1` / `Alt+2` / `Alt+3` 直达切换
  - `/ui tab next|prev|main|swarm|todo` 命令切换
- 工作台渲染已结构化为左/右面板构建函数，并加入行预算与文本截断，降低面板互相挤压。
- 固定工作台模式下，处理中间事件输出已收敛到左栏交互流与 `Latest Output`，减少实时直出造成的界面抖动。

### 已实现（UI v3 - 最小版）
- 工作台渲染升级为真正三栏：左导航（tab）、中交互流（可滚动）与右运行状态（固定指标）。
- 中栏集中承载 Interaction Stream 与 Latest Output，避免左栏信息拥挤。
- 底部输入栏保持不变，继续通过每轮重绘维持输入焦点稳定。

### 已实现（UI v4 - 最小版）
- 中栏支持多视图切换：`stream / swarm / todo`。
- 新增 worker 焦点切换：`/ui worker next|prev|clear|<id>`，用于多 worker 浏览。
- swarm 中栏接入会话任务状态：显示任务状态汇总与焦点 worker 的任务列表。
- 新增快捷键：`Alt+4`（切换中栏视图）、`Alt+5/Alt+6`（切换 swarm worker 焦点）、`Alt+7`（清除焦点）。

### 已实现（UI v5 - 最小版）
- 新增 review 视图：中栏支持 `review`，展示审批状态与 diff 预览。
- 新增 review 命令族：`/review status|diff|list|use|approve|reject|reset`。
- 新增 review 持久化队列：`/review diff` 入队，审批结果可追踪并可按 `id` 选择。
- `approve` 动作已接入 Git 快照：审批时可生成可追踪的 snapshot hash。
- 新增 `approve apply`：审批后可执行 `.pudding/review/hooks.txt` 中的检查命令序列，并把结果写入审批记录。
- `reject` 当前为非破坏性拒绝：仅写入拒绝状态，不自动丢弃工作区改动。
- 新增破坏性拒绝路径：`/review reject discard [--yes]`，默认二次确认后执行 `git reset --hard HEAD` 丢弃 tracked 改动。
- `reject discard` 在执行前会展示将丢弃的 tracked 文件预览列表，降低误操作风险。
- 新增快捷键：`Alt+8` 直达 review 视图。

### 未实现（UI v2+）
- 右侧状态面板中的 active agents 拓扑、token/cost 精确统计、context window 精确占用。
- 多 agent Tab 与快捷切换（Swarm 全局面板 + worker 面板）。
- diff 审批与可折叠思维流的完整 UI。

## 3. 交互设计约束
- 输入栏必须固定在底部，避免输出流打断输入光标。
- 所有菜单和提示由统一重绘函数维护，避免残影/错位。
- 快捷键优先遵循 Linux 终端习惯（可预测、可组合）。

## 4. 快捷键（当前）
- `Enter`：提交输入
- `Esc`：退出 slash 选择
- `Up/Down`：移动 slash 选项
- `Tab`：补全 slash 选项
- `Ctrl+P / Ctrl+N`：历史输入上一条/下一条
- `Ctrl+K`：清空输入
- `PgUp / PgDn`：滚动左侧交互流
- `Alt+1 / Alt+2 / Alt+3`：切换左侧 tab（main / swarm / todo）
- `Alt+4`：切换中栏视图（stream / swarm / todo）
- `Alt+5 / Alt+6`：切换 swarm worker 焦点（下一项/上一项）
- `Alt+7`：清除 swarm worker 焦点
- `Alt+8`：切换到 review 视图

## 5. 后续里程碑
1. `UI v5+`：可折叠思维流、主题化渲染、审批历史与批量审批。
