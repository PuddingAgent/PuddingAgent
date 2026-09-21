# ppt-master 集成 + Office 读写能力建设 · 实施规划

> 日期：2026-09-21 ｜ 作者：dsh(0e0) ｜ 状态：**规划（未实施）**
> 上游需求：用户指派 —— 「研究 ppt-master，规划 PuddingAgent 需要集成哪些基础设施；先回答怎么做，不要直接做」
> 本文只回答「怎么做」，不含任何代码改动。

---

## 0. 结论先行

### 0.1 三条必须先纠正的事实（否则整个方案会建在错的地基上）

| # | 任务书中的假设 | 实测事实 | 证据 |
|---|---|---|---|
| **C1** | 「doc 和 docx 可以使用 NPOI」 | **docx 可以，doc 不行**。NPOI 2.8.0 的 .NET 构建（`net10.0` 与 `netstandard2.0` 皆然）**不包含 HWPF**，即 legacy `.doc` 无支持 | 见 §6 证据 E1（字节级扫描四份程序集） |
| **C2** | 「PPT 可以重点用 NPOI」 | **NPOI 2.8.0 不含 XSLF**，即**没有任何 PPTX 读写能力**。`PuddingRuntime.csproj` 现有 NPOI 依赖对 pptx 完全无用 | 见 §6 证据 E1（`NPOI.OOXML.dll`: `XSLF=False`, `XWPF=True`, `XSSF=True`） |
| **C3** | （未被提及，但更致命） | PuddingAgent **没有文档制品链路**：制品存储只有图像(Vision)/音频(Audio)，全仓无 `DocumentArtifact`/`DocumentStorage`；聊天附件**只支持图片**。**即使生成出 pptx/docx，也没有任何通道交付给用户** | 见 §6 证据 E3 |

> C2 + C3 合起来意味着：**「加个 NPOI 就能写 PPT」这条最短路径不存在**。必须新建 PPTX 写能力 + 新建文档交付通道。

### 0.2 推荐路线（三步走，但先做 P1 打底）

```
P1 底座（与 ppt-master 无关，可独立交付）
   ① Office 工具层增强（读：补 .pptx/.csv/.doc 降级策略；写：新增 docx/xlsx）
   ② 文档制品链路（存储 + 端点 + 下发工具 + 前端渲染）  ← C3 缺口
   3 验收闭环：读 docx → 写 docx → 用户在聊天里收到并下载

P2 ppt-master 接入（依赖 P1 的底座）
   ④ 受管 Python venv bootstrap
   ⑤ 受控子进程执行通道（OfficeExec）
   6 技能包多文件分发 + 完整性门

P3 完整档位（模板/图表/讲稿/图像生成对接）
```

**不推荐**把 ppt-master 用 C# 重写：它的核心价值是「LLM 手写规范化 SVG → 编译为原生 DrawingML」，这是一个**上万行 Python + 大量规范文档**构成的编译器与工作流，重写成本数量级不划算（详见 §3.4）。

---

## 1. ppt-master 是什么（调研结论）

### 1.1 形态：不是库，是「Skill + 确定性脚本 + LLM 手写产物」的三层分工

| 层 | 载体 | 职责 |
|---|---|---|
| **工作流层** | `skills/ppt-master/SKILL.md` + `workflows/**` + `references/**` | 由**宿主 LLM 阅读并执行**的流程规范；路由（routing）决定加载哪一条路线 |
| **确定性工具层** | `skills/ppt-master/scripts/*.py` | 转换、校验、打包、可重复文件操作 —— 不含 AI 判断 |
| **创作层** | 由 LLM **手写** `svg_output/*.svg` | 页面设计本身，使用项目**规范化 SVG 中间语言** |

关键含义：**它的「智能」来自宿主 Agent 的模型，不来自仓库代码**。官方明说「最终质量上限仍由所选模型决定」，并推荐 Kimi K3 / Claude 驱动、配 `gpt-image-2` / `gemini-3.1-flash-image` 生图。

### 1.2 集成契约（宿主必须提供什么）

SKILL.md 的 **Mandatory Load Order** 是硬约束：

1. 保留宿主给出的**绝对目录**（`SKILL_DIR`），**禁止 `cd`、禁止依赖 CWD、禁止假设 repo checkout**；不可用时**必须询问，禁止搜索或猜测**
2. **每次调用必须先跑**：`python3 "${SKILL_DIR}/scripts/attribution_guard.py"` —— **非零即立刻停止整个 Skill，不得检查/修复/绕过该完整性门**
3. 读 `workflows/routing.md` → **只选一条**顶层路线 + 其 profile → 只读该 route 的运行时权威文档
4. Windows 上若 `python3` 不可用，**同一条命令改用 `python`**

由此推出的宿主能力清单：

| 宿主能力 | 用途 | PuddingAgent 现状 |
|---|---|---|
| 绝对路径感知的**多文件技能目录** | 装载 SKILL.md + workflows + references + scripts + templates | ⚠️ 有技能系统（`agent_skill` + `relative_path`），但需确认大包与子目录行为 |
| **Python 执行** | 跑 scripts | ✅ 有 `shell`/`terminal_*`；✅ Python 3.12.10 |
| **进程内环境变量传递** | `PPT_MASTER_PROJECT_PATH`（自动记录器据此定位项目） | ✅ 子进程可传 |
| **长驻稳定上下文** | Executor 逐页手写 SVG，中途不落规划状态 | ❌ **与 compaction/心跳结构性冲突**（见 §5-R1） |
| **图像生成能力** | Image Acquisition 阶段 | ✅ 有 `generate_image` 工具链 |
| **文件交付** | 把 `exports/*.pptx` 给用户 | ❌ **完全缺失**（C3） |

### 1.3 运行时依赖（32 项，全部为可选/按需）

`requirements.txt` 的设计是「标准库优先，重依赖按能力分段安装」。按能力归类：

| 能力段 | 依赖 | 备注 |
|---|---|---|
| 核心转换（**必备**） | `python-pptx>=0.6.21`, `XlsxWriter>=3.0.0`, `skia-pathops>=0.9.2`, `uharfbuzz>=0.50.0` | svg_to_pptx.py；后两者是**原生 wheel**（Windows 可用性须实测） |
| 模板注册 | `PyYAML>=6.0` | register_template.py |
| 讲稿配音 | `edge-tts>=7.2.8` | 可选 |
| PDF 源 | `PyMuPDF>=1.23.0` | ⚠️ **AGPL-3.0**，仅 pdf_to_md.py 用；再分发需注意 |
| 文档→MD | `mammoth`, `markdownify`, `ebooklib`, `nbconvert` | docx/html/epub/ipynb 纯 Python 路径 |
| Excel→MD | `openpyxl>=3.1.0` | |
| 图片处理 | `Pillow>=9.0.0`, `numpy>=1.20.0` | |
| 网页→MD | `requests`, `beautifulsoup4`, `curl_cffi>=0.7.0`(可选) | curl_cffi 做 TLS 指纹模拟（微信公众号等） |
| AI 生图 | `google-genai>=1.0.0` | 或 OpenAI 兼容（复用 requests） |
| SVG 编辑器 | `flask>=3.0.0` | 本地 Web 标注工具（人类用） |
| 长尾格式 | pandoc（**系统二进制**） | 仅 .doc/.odt/.rtf/.tex 等 |

> ⚠️ 注意 `skills/ppt-master/requirements.txt` 的注释：**完整依赖列表内置于 skill 内部**，`update_repo.py` 会对依赖树算**指纹**。这意味着**技能版本与依赖版本是绑定的**，不能随意漂移（见 I4）。

### 1.4 流水线（默认路线）

```
用户输入 (PDF/DOCX/XLSX/PPTX/URL/Markdown/主题)
  → source_to_md.py（按类型分派）           [+ topic-research 补事实缺口]
  → project_manager.py init <项目名>
  → project_manager.py import-sources ...   → sources/ + analysis/*.json|csv
  → [Strategist 角色] → design_spec.md + spec_lock.md
  → [Image Acquisition]                     （生图/搜图/切片 → images/）
  → [Executor 角色] 手写 svg_output/P01..PNN.svg
       ├─ 先 P01–P05 → svg_quality_checker.py --stage early --json
       ├─ 其余页连续生成
       └─ svg_quality_checker.py --stage final --json   （0 error 强制）
  → [verify-charts]（含数据图表时校准坐标）
  → finalize_svg.py → svg_final/
  → svg_to_pptx.py → exports/<name>_<ts>.pptx
  → validation/svg_quality_report.json + validation/<stem>.report.json + validation/workflow.log
  → backup/<ts>/svg_output/
```

**产物目录所有权**（关键设计，迁移时必须保留）：`svg_output/` = 作者状态（唯一手写）｜`svg_final/` = 派生视觉预览｜`exports/` + `backup/` = 派生交付/归档。

### 1.5 为什么是 SVG（决定了「不能重写」）

官方排除法：直接生成 DrawingML（AI 训练数据远少于 SVG，几十行 XML 换一个圆角矩形）❌；HTML/CSS（文档流 vs 画布，世界观不同）❌；WMF/EMF（AI 无训练数据）❌；SVG 当图片嵌入（丧失可编辑性）❌。

胜出理由：SVG 与 DrawingML **同为绝对坐标二维矢量格式**，概念一一对应：

| SVG | DrawingML |
|---|---|
| `<path d>` | `<a:custGeom>` |
| `<rect rx>` | `<a:prstGeom prst="roundRect">` |
| `<circle>/<ellipse>` | `<a:prstGeom prst="ellipse">` |
| `transform` | `<a:xfrm>` |
| `linearGradient` | `<a:gradFill>` |
| `fill-opacity` | `<a:alpha>` |

外加：**人类可用任意浏览器直接预览调试**。且它**不是通用 SVG**，而是「项目规范化 SVG 中间语言」——支持的元素/单位/metadata/结构合同由项目封闭定义（三种输入状态：规范创作 / 兼容读取 / 非法阻断）。

### 1.6 六条顶层路线（路由后**只能加载一条**）

| 路线/profile | 运行时权威 | 备注 |
|---|---|---|
| Generate — Default | `workflows/generate-pptx.md` | 完整规划+确认+质量门 |
| Generate — Quick | `workflows/profiles/quick-generate.md` | 短路独立规划/确认/预览终稿化；**无 `design_spec.md`/`spec_lock.md`** |
| Generate — Beautify | `workflows/profiles/beautify-pptx.md` | 既是生成也是增强 |
| Generate — Image to PPTX | `workflows/profiles/image-to-pptx.md` | **要求 Codex**、恒 Quick；其他宿主「不对其行为作支持或承诺」 |
| Create Template | `workflows/create-template.md` | 独立工作区生命周期 |
| Edit Native PPTX | `workflows/edit-native-pptx.md` | `pptx_to_svg.py --roundtrip` 往返 |

---

## 2. 差距分析：需要新建的 9 项基础设施

| ID | 基础设施 | 现状 | 缺口 | 建在哪 |
|---|---|---|---|---|
| **I1** | **受管 Python 运行时** | 系统 Python 3.12.10 + pip 25.0.1（`C:\Users\huany\AppData\Local\Programs\Python\Python312`）；`python-pptx`/`mammoth` **均未安装** | 无 venv、无依赖固定、无 bootstrap、无健康检查 | `{DataRoot}/runtimes/office-skill/` |
| **I2** | **受控子进程执行通道** | 有 `shell`/`terminal_*`；有 MCP 客户端能力（证据 E4） | 无脚本白名单、无参数校验、无超时/输出限额、无审计 | `PuddingRuntime`（沿用 `Runtime/Tools` 形态） |
| **I3** | **文档制品链路** ★最大缺口 | 制品只有图像/音频；聊天附件仅图片 | 实体 + 存储 + 下载端点 + 下发工具 + 前端渲染 + 保留策略 | `PuddingPlatform` + `PuddingPlatformAdmin` |
| **I4** | **技能包多文件分发** | `agent_skill` 支持 `relative_path`；管理端有 zip 技能包上传 | 大包/文件数上限、子目录保真、**版本↔依赖指纹绑定**、完整性门集成 | `PuddingPlatform`（技能服务）+ 管理端 |
| **I5** | **模型能力路由** | 有 LLM 资源池与 `llm_resource_pool` 工具 | 技能声明「偏好模型/最低能力」，委派时按声明解析路由 | 技能 manifest 扩展 |
| **I6** | **Office 工具层（C#）** ★任务书指定 | `read_office_document`（只读，20 万字符截断）；NPOI 2.8.0(XWPF/XSSF/HSSF) + PdfPig 0.1.9 | **写能力全缺**；**PPTX 无库**；`.doc` 无 HWPF | `PuddingRuntime/Tools/BuiltIns/Documents/` |
| **I7** | **质量门与可观测** | 有 TRX/日志；无产物级校验 | 接入 `svg_quality_report.json`/`validation/*.report.json`；长任务进度可见 | `PuddingRuntime` + 事件通道 |
| **I8** | **前端与管理端** | 技能管理页已有六 Tab（SKILL Hub） | 文档制品预览/下载；ppt-master 条目与依赖状态显示 | `PuddingPlatformAdmin` |
| **I9** | **安全与合规** | 有工具权限分级（Low/Medium/High） | 子进程最小权限、zip 路径穿越防护、**PyMuPDF AGPL 边界** | 跨层 |

---

## 3. 路线设计

### 3.1 L1：Office 工具层（纯 C#，与 ppt-master 解耦，可独立交付）

**读（增强现有 `read_office_document`）**

| 格式 | 现状 | 增强项 |
|---|---|---|
| `.docx` | ✅ XWPF | 结构化输出（标题层级/样式名/批注）；保留现有 Markdown 模式兼容 |
| `.xlsx/.xls` | ✅ XSSF/HSSF | 公式与计算值双读；命名区域；图表数值表 |
| `.pdf` | ✅ PdfPig | 表格识别增强；扫描件明确报「无文本层」 |
| `.pptx` | ❌ | **新增**（走 §3.2 选型库）：按页读形状文本/备注/图表数据 |
| `.doc` | ❌ 明确不支持 | **HWPF 不可用** → 二选一：① 明确报错并提示转 docx（保持现状，诚实）② 若宿主有 LibreOffice 则调用其 headless 转换（**本机未验证存在**） |
| `.csv` | ❌ | 直接按文本表读入（零依赖） |

**写（新增工具）**

| 工具 | 格式 | 实现 |
|---|---|---|
| `write_office_document` | `.docx` | NPOI **XWPF**（段落/表格/样式/图片/页眉页脚） |
| `write_office_document` | `.xlsx` | NPOI **XSSF/SXSSF**（含公式、图表基础、格式） |
| `write_office_document` | `.pptx` | 见 §3.2 选型（**NPOI 不可用**） |
| `office_template_fill` | 三格式 | 占位符 `{{key}}` 填充已有模板 —— 覆盖「套模板出文」的常见办公需求 |

> 定位边界：**L1 解决「简单、结构化、批量」的办公文档**；**L2（ppt-master）解决「高质量、有叙事与视觉设计」的演示文稿**。两者不重复。

### 3.2 PPTX 写能力选型（C2 的直接后果）

| 方案 | 优点 | 代价 | 建议 |
|---|---|---|---|
| **A. DocumentFormat.OpenXml**（微软官方 OOXML SDK，MIT） | 规范权威、读写 pptx/xlsx/docx 全覆盖、与绘图/母版模型一致 | 需新增 NuGet 依赖；API 较底层（比 NPOI 啰嗦）；本机 nuget 缓存**尚未有**该包 | ✅ **L1 首选**（pptx/xlsx/docx 统一） |
| B. 引入第三方商业库（Spire/Aspose） | API 友好 | 许可与成本；仓内零先例 | ❌ 不建议 |
| C. 只做 PPTX 读、写全交 ppt-master | 零新依赖 | 用户要「简单生成一页 PPT」时被迫起 Python 链路 | ⚠️ 可作 P2 前的过渡 |
| D. python-pptx（走 Python） | 与 ppt-master 同栈 | C# 工具层为写 pptx 引入 Python 依赖，架构不一致 | ❌ L1 不用 |

### 3.3 L2：ppt-master 接入形态

| 方案 | 形态 | 优点 | 缺点 |
|---|---|---|---|
| **① 技能包 + 受管 venv + 受控子进程**（**推荐**） | 把 `skills/ppt-master/` 整体作为 PuddingAgent 技能包导入；`scripts/*.py` 由新工具 `office_script_run` 在 venv 中执行 | 保留上游完整能力与升级路径；零重写；SKILL.md 天然适配 Agent | 需 I1/I2/I4 三项新基建；跨语言调试；依赖体积大 |
| ② 外部 CLI 子进程（同 ①，但把 skill 视为外部进程契约） | 与 ① 实质相同，差别在是否纳入技能目录 | 同上 | 同上 |
| ③ **MCP Server 包装** | 把 ppt-master 包成 MCP server，经现有 MCP 客户端调用 | 复用已有 MCP 通道（证据 E4）；工具化清晰 | ppt-master **本身不是 MCP server**，需另写包装层；其工作流高度依赖「LLM 读文档后手写 SVG」，MCP 工具边界会切断流程上下文 |
| ④ C# 重写 | 全原生 | 单栈 | **上万行 Python + 规范文档 + 187 个 Office preset 映射 + 编译器**，成本数量级不划算，且会永久落后上游 |

**结论：选 ①。** ③ 可作为未来「把确定性脚本段（如 `svg_to_pptx.py`）单独工具化」的补充，但**不替代**①，因为 ppt-master 的主流程必须由 LLM 在技能文档引导下连续执行。

### 3.4 为什么不能简单重写（量化）

`svg_to_pptx.py` 不是格式转换器，而是**有注册表、有保真度说明、可测试的编译器**，且维护「项目规范化 SVG ↔ DrawingML」的**逐项映射表**（含精确/归一化/fallback/sidecar/unsupported 五种映射状态），并配套：`svg_quality_checker.py`（error 阻塞/ warning 放行）、`finalize_svg.py`、`pptx_to_svg.py --roundtrip`。加上 `skia-pathops`（布尔合并）与 `uharfbuzz`（文字轮廓整形）两个**原生库**依赖，C# 侧没有等价物。重写等于重做一个产品。

---

## 4. 分期计划与验收

### P0 —— 决策冻结（0.5 天，产出本文档定稿）
- [ ] 确认 §3.2 选型 A（OpenXml SDK）
- [ ] 确认 §3.3 选型 ①（技能包 + venv + 受控子进程）
- [ ] 确认 ppt-master 进入哪一个 Agent 的职责域（**越权风险点，见 §5-R5**）
- [ ] 确认 AGPL（PyMuPDF）策略：默认不装，仅用户需要处理 PDF 源时按需装

**验收**：上述四项有明确书面结论。

### P1 —— 底座（不依赖 ppt-master，可独立交付并验证）
| 步骤 | 交付 | 验收 |
|---|---|---|
| P1.1 | I3 文档制品链路（实体+存储+`GET /api/artifacts/documents/{id}`+`send_document` 工具+前端附件渲染） | 上传一个 docx → 在聊天中收到卡片并能下载 ✅ |
| P1.2 | I6 写能力：`write_office_document`(docx/xlsx) + `office_template_fill` | 生成 docx/xlsx 各一份，Word/Excel 打开无修复提示、内容/样式正确 ✅ |
| P1.3 | I6 读增强：`.pptx` 读取 + `.csv` + `.doc` 降级策略 | 对真实 pptx 逐页读出文本与备注；`.doc` 给出可执行提示 ✅ |
| P1.4 | I6 PPTX 写（OpenXml SDK 最小版：标题页+要点页+表格） | 生成的 pptx 可在 PowerPoint 中编辑形状与文字（**非图片**）✅ |
| P1.5 | 端到端：读 docx → 改 → 写 docx → 下发 | 全链路一次通过 ✅ |

> **P1 的价值**：即使 ppt-master 最终不接入，P1 也独立解决「Office 读写 + 文档交付」——这正是用户需求的第一句。

### P2 —— ppt-master 接入
| 步骤 | 交付 | 验收 |
|---|---|---|
| P2.1 | I1 venv bootstrap（`{DataRoot}/runtimes/office-skill/`，requirements 固定 + hash 锁 + 首次按需安装 + 健康检查） | `python -c "import pptx, mammoth"` 在 venv 内成功；宿主 Python 未被污染 ✅ |
| P2.2 | I2 `office_script_run`（脚本白名单+参数校验+超时+输出限额+工作目录隔离+审计） | 越权路径/越界参数被拒；超时被回收；审计落盘 ✅ |
| P2.3 | I4 技能包导入（含 `attribution_guard.py` 强制门）+ I5 模型偏好声明 | 非零 guard → 技能被阻断且**不尝试绕过** ✅ |
| P2.4 | **最小闭环**：纯文本主题 → `quick-generate` → `exports/*.pptx` → 经 I3 下发 | 3–5 页原生可编辑 pptx 端到端成功 ✅ |
| P2.5 | 上下文稳定性改造（见 §5-R1） | 20 页生成过程中无上下文丢失导致的流程断裂 ✅ |

### P3 —— 完整档位（按需）
模板/品牌工作区（`create-template`）· 数据图表校准（`verify-charts`）· 讲稿与旁白（`edge-tts`）· 图像生成对接（`image_gen.py` ↔ PuddingAgent `generate_image`）· `edit-native-pptx` 往返 · 视觉自检（`visual-review`）。

### P4 —— 治理
质量门接入（I7）· 进度可见性 · 成本配额 · 技能与依赖的版本升级机制（对齐 `update_repo.py` 指纹）。

---

## 5. 风险与未决问题

| ID | 风险 | 说明 | 缓解 |
|---|---|---|---|
| **R1** ★ | **上下文稳定性与 ppt-master 的设计假设冲突** | 官方明示：Default 路线的规划产物「保留在有效的当前上下文中」，且「**有效上下文丢失后无法重建或续接**」。而 PuddingAgent 有 compaction、心跳轮次、会话边界 | ① 优先用 `quick-generate`（状态更少）② 把「项目目录」当外部状态（`design_spec.md`/`spec_lock.md`/`svg_output/` 已落盘）③ 按页委派子代理，每页独立无状态 ④ 必要时为长任务引入「不压缩」会话档位 |
| **R2** | 模型能力不足 → SVG 质量崩塌 | 上游推荐 K3/Claude；质量上限由模型决定 | I5 声明最低能力；用现有 LLM 资源池显式解析路由（不裸传 modelId） |
| **R3** | 原生 wheel 在 Windows 可用性 | `skia-pathops` / `uharfbuzz` 为编译型依赖 | P2.1 内实测；不可用则降级（禁「合并形状」能力）并如实标注 |
| **R4** | AGPL 传染 | PyMuPDF 为 AGPL-3.0 | 默认不装；仅在需要 PDF 源时按需安装；**再分发前必须复核** |
| **R5** ★ | **职责域冲突** | 本任务由用户直接指派；但「改产品代码」涉及协作协议的声明制边界 | 按协议 A1 例外条款与 §11 与 6a8 对齐排期；在途声明先行 |
| **R6** | 子进程安全面扩大 | 允许执行 Python 脚本 = 任意代码执行面 | 白名单脚本目录 + 参数校验 + 无网络默认 + 最小权限 + 审计 |
| **R7** | 依赖体积与安装挫败 | 32 项依赖，首次安装可能数分钟且需网络 | bootstrap 进度可视化；离线 wheel 缓存；按能力分段安装（PDF/生图/配音等可选段默认不装） |
| **R8** | 技能包与宿主路径假设不符 | SKILL.md 硬要求「绝对路径、禁 CWD、禁搜索猜测」 | I4 导入时把 `SKILL_DIR` 注入技能 manifest，并在加载器里展开 `${SKILL_DIR}` |
| **R9** | `image-to-pptx` 路线不可用 | 官方明确「要求 Codex，其他宿主不对其行为作支持或承诺」 | **不纳入范围**，如实告知用户 |

---

## 6. 证据附录（本机实测，2026-09-21）

| ID | 结论 | 证据 |
|---|---|---|
| **E1** | NPOI 2.8.0 的 .NET 构建**无 XSLF、无 HWPF** | 对 `~/.nuget/packages/npoi/2.8.0/lib/net10.0/*.dll` 与 `netstandard2.0/*.dll` 做程序集字符串堆扫描：`NPOI.OOXML.dll :: XSLF=False XWPF=True HWPF=False XSSF=True HSSF=True`；`NPOI.Core.dll :: HSSF=True`，其余 False |
| **E2** | 本机 Python 可用但依赖缺失 | `python --version` → `Python 3.12.10`；`python3` → **不存在**（exit 9009，与官方「Windows 用 `python`」一致）；`pip 25.0.1`；`import pptx` → `ModuleNotFoundError`；`import mammoth` → `ModuleNotFoundError` |
| **E3** | 无文档制品链路 | 制品存储仅图像/音频；全仓无 `DocumentArtifact`/`DocumentStorage`/`IDocumentArtifact`；聊天附件组装仅 `imageParts` |
| **E4** | 运行时具备 MCP 客户端能力 | 本会话工具目录存在 `mcp__codex_mcp_1f577d69__*` 系列工具（`codex_task_start` / `pudding_build_restart` 等），说明 MCP 通道已在运行时存在 |
| **E5** | 现有 Office 依赖 | `Source/PuddingRuntime/PuddingRuntime.csproj:23` → `NPOI 2.8.0`；`:25` → `PdfPig 0.1.9`；`:9` → `AcceptNPOIOSMFLicense=true`（OSMF 许可开关） |
| **E6** | 上游版本与许可 | `skills/ppt-master/SKILL.md` → `metadata.version: 6.6.0`，`license: MIT`；仓库 MIT；PyMuPDF 为 AGPL-3.0（README 与 requirements 均标注） |
| **E7** | OpenXml SDK 未被使用 | `documentformat.openxml` 不在本机 nuget 缓存；仓内 csproj 扫描未命中 |

---

## 7. 待用户裁定（P0 四项）

1. **PPTX 写能力选型**：采纳 OpenXml SDK（§3.2-A）？
2. **ppt-master 接入形态**：采纳「技能包 + 受管 venv + 受控子进程」（§3.3-①）？
3. **职责域**：本任务（含 P1 产品代码改动）由 dsh 实施，还是按协作协议转 6a8 排期？
4. **AGPL 与依赖策略**：PyMuPDF 默认不装（按需）是否可接受？首次安装依赖允许联网？

---

*本文为规划，不含代码改动。所有实测证据均可复现（命令见 §6）。*
