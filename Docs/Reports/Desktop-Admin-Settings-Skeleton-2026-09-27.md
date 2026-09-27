# Desktop 原生设置骨架实施记录

日期：2026-09-27。用户要求本轮只搭设置骨架，完整业务迁移交给 DeepSeek。

## 交付

- 标题栏应用菜单、文件菜单、原生侧栏均可进入居中设置层；应用菜单提供外观/记忆直达。
- 左侧分类导航、右侧页签和圆角卡片：17 分类、49 页签、64 卡片；搜索字段/标题/任务 ID、无结果、保留所选页签、关闭/Esc、背景输入隔离和焦点恢复。
- 现有主题、材质、面板宽度及保存保持可用；运行中心入口仍使用既有实现。其他卡片明确标注待迁移，动作禁用，没有新增 API 调用或业务保存。
- `SettingsCatalog.json` 内嵌于既有 BCL Foundation，`SettingsCatalog.cs` 提供纯搜索；独立测试通过后才接到 Desktop。
- 后续任务以直接调用 Core 现有方法为准，无新增 SettingsClient/REST 包装层；保留现有编译期依赖边界。

## 交接文件

- [DeepSeek 任务书](../Tasks/Desktop-Admin-Settings-DeepSeek-2026-09-27.md)：用户裁定、依赖/边界、Core 复用与缺口、全部路由处置、DS-00 至 DS-17 共 18 个实施任务、64 卡片字段矩阵、验收门禁。
- [源证据附录](../Tasks/Desktop-Admin-Settings-Source-Inventory-2026-09-27.md)：72 个 Web 源文件的字段、标题、表格列、按钮及行为调用线索。
- [卡片清单 CSV](../Tasks/Desktop-Admin-Settings-Cards-2026-09-27.csv)：64 行，可逐项登记进度。

三份文件不包含生产密钥。所有 64 张卡片的源文件路径已检查存在。LLM 配额空端点、会话 Agent 下拉空列表、Web 首页静态“Core 就绪”分别记录为真实功能缺口，未用占位模拟成功。

## 验证

1. Foundation 独立 restore 后 `dotnet test Source/PuddingDesktop.FoundationTests/PuddingDesktop.FoundationTests.csproj --no-restore --artifacts-path temp/build/desktop-kernel`：**21 通过、0 失败**。新增 3 项覆盖目录稳定 ID/来源完整性、嵌套字段搜索/大小写/空结果、分类/任务过滤。既有编译期 BCL 叶边界保持。
2. Desktop 先 restore，再 Release build，均使用 `--artifacts-path temp/build/desktop-kernel`：**0 错误**。完整依赖构建存在既有 NuGet 漏洞、过时 API、nullable/平台等警告，本轮未处理无关依赖。
3. 真实 WinUI 进程使用独立 `--state-root` 与 `--smoke-report`，不启动 Core、不使用 `D:\data`。最终 **87 个检查通过**：17 分类、49 页签、字段搜索与空结果、原有外观、背景输入隔离/恢复、角色草稿、文档来源/关闭、主题/材质/保存、既有双 WebView2 隔离等。
4. 实际 Root 渲染 PNG 已检查：搜索结果卡片和外观页的分类、页签、卡片、保存入口可见，未把字段 API 名显示为用户表单。RenderTargetBitmap 不捕获系统 Mica/桌面背景，因此该图只用于控件布局核对，不作为系统材质验收。
5. `git diff --check` 无空白错误。只暂存本次路径；其他协作者的聊天、索引、Host 和运行脚本改动保留。

测试时发现的搜索事件时序问题已修正：NavigationView 只接收当前菜单项的有效选择，等待真实 UI 条件而非依赖固定延迟；最终隔离窗口 smoke 通过。

## 尚未验收

这次是设置骨架交付，业务表单/实际 Core 数据读写仍待 DeepSeek；未重启或替换用户当前 Desktop，不能宣称运行中的产品已经加载本次代码。多 DPI、高对比下的人工可访问性验证、Narrator、IME 和完整产品生命周期回归不在本次完成结论内。窗口自检不等于全部业务迁移完成。

## 复现

```powershell
dotnet restore Source/PuddingDesktop.FoundationTests/PuddingDesktop.FoundationTests.csproj --artifacts-path temp/build/desktop-kernel
dotnet test Source/PuddingDesktop.FoundationTests/PuddingDesktop.FoundationTests.csproj --no-restore --artifacts-path temp/build/desktop-kernel --results-directory E:/github/AgentNetworkPlan/PuddingAgent/temp/test-out/settings-foundation -p:CoverletOutput=E:/github/AgentNetworkPlan/PuddingAgent/temp/test-out/settings-foundation/coverage
dotnet restore Source/PuddingDesktop/PuddingDesktop.csproj --artifacts-path temp/build/desktop-kernel -p:Configuration=Release
dotnet build Source/PuddingDesktop/PuddingDesktop.csproj -c Release --no-restore --artifacts-path temp/build/desktop-kernel
```

在 `temp/test-out/` 创建唯一目录，通过 `Start-Process -WindowStyle Hidden` 启动 `temp/build/winui3/bin/PuddingDesktop/release_win-x64/PuddingDesktop.exe --state-root <该目录> --smoke-report <该目录>/smoke.json`，等待退出并检查 `success=true`；同目录自动生成布局截图。不要把 `--demo` 与 `--smoke-report` 合用，后者自检初始空角色状态。已有 `TestScripts/test-pudding-winui-skeleton.ps1` 也会执行扩充后的窗口 smoke。
