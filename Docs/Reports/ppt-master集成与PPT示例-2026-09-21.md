# ppt-master 集成与 PPT 示例 — 交付记录

日期：2026-09-21 ｜ 执行：dsh(0e0) ｜ 上游：https://github.com/hugohe3/ppt-master （MIT，SKILL 版本 6.6.0）

## 1. 结论

两件事都已完成并**逐项验证**：① ppt-master 已集成进本 Agent 的技能体系，可被加载；② 用它生成了一个 **4 页、全部为原生可编辑 DrawingML** 的 PPTX 示例。

## 2. 集成结果

| 项 | 值 |
|---|---|
| 上游 clone | `E:\github\AgentNetworkPlan\ppt-master`（`git clone --depth 1`，13,077 文件） |
| 技能包 | `skills\ppt-master` — 13,108 文件 / 84.5 MB |
| 受管 venv | `<clone>\.venv`（`python-pptx 1.0.2`、`XlsxWriter`、`PyYAML`、`Pillow`） |
| 挂载方式 | **目录联接** `mklink /J` → `D:\data\agents\<agentId>\skills\ppt-master` |
| 注册 | `manifest.json`（`skillId=ppt-master`，`version=6.6.0`）+ `PUDDING-INTEGRATION.md`（强制操作契约） |
| 验证 | `agent_skill(rebuild_index)` → 技能数 **140**；`agent_skill(get, ppt-master)` 成功解析且 `physicalPath` 正确 |

**选型理由**：技能包 13,108 文件 / 84.5 MB，整包复制进 DataRoot 会重一次索引且产生双份真源。用目录联接做到零复制、单一真源，并且上游更新路径（`git pull` / `update_repo.py`）保持可用。

## 3. PPT 示例

| 项 | 值 |
|---|---|
| 项目 | `projects\pudding-demo_ppt169_20260921`（画布 `ppt169` = 1280×720） |
| 页面 | `P01_title`(cover) / `P02_why_svg`(content) / `P03_layers`(content) / `P04_status`(ending)，全部手写规范化 SVG |
| 质量门 | `svg_quality_checker.py <project> --quick-generate --canonical-authoring --stage final --json` |
| 质量门结果 | 首轮 4 项**阻塞错误** → 修复 → **4/4 Fully passed，0 warning，0 error** |
| 导出 | `svg_to_pptx.py <project> -o <project>\exports\pudding-demo.pptx --quick-generate` |
| postflight | `status=passed-with-warnings`、`quality_gate=passed`、`slides=4` |
| 产物 | `E:\github\AgentNetworkPlan\ppt-master\projects\pudding-demo_ppt169_20260921\exports\pudding-demo.pptx`（20,433 字节） |

### 产物核验（不采信导出器自述，独立复检）

- `python-pptx`：4 页 / 18 形状 / **0 张图片** ⇒ 全部为原生 DrawingML 形状（GROUP、AUTO_SHAPE、TEXT_BOX）
- `ppt/slides/slide1.xml`：**25 个 CJK 字符直接落在 XML 里**（文字未栅格化）、`roundRect` 出现 1 次（`rx` → `roundRect` 映射生效）、`<p:pic>` 0 次、`blipFill` 0 次、`<p:txBody>` 4 处
- 页面尺寸 `12,192,000 × 6,858,000 EMU` = 13.333in × 7.5in（标准 16:9）

## 4. 过程中踩到的真实约束（已固化进 `PUDDING-INTEGRATION.md`）

| 现象 | 结论 |
|---|---|
| PyPI 直连 `TimeoutError` | 需国内镜像 + `--timeout 120 --retries 5` 才能装上 |
| 根级 `<g>` 未声明 bounds | **阻塞错误**：每个根级 `<g id="...">` 必须带 `data-pptx-bounds="x y w h"` |
| 同一段落拆成多个 `<text>` | 告警；应合并为一个 `<text>` + 直接 `<tspan x dy>` 子节点 |
| 用 `-s` 指定源目录导出报错 | `svg_to_pptx.py` 的 `project_path` 是**位置参数**；`-s` 指的是项目内子目录 |
| 导出器拒绝导出（`found not-provided`） | `--quick-generate` 强制要求**已落盘的 JSON 质量报告**，且必须由 `--canonical-authoring --json` 生成 |
| 顶层元素未分组 | 告警；页面框架保留为根图元并标 `data-pptx-role="background"/"decoration"`，内容按逻辑单元分组 |

## 5. 尚未集成（PuddingAgent 侧，需决策）

1. **文档制品没有存储 / 下发通道** —— 生成的 `.pptx` 只能以绝对路径上报，无法投递给用户（平台制品通道目前只有图片与音频）。**这是最大缺口，能力"最后一百米"断在这里。**
2. **无受控子进程工具** —— 脚本只能经 `shell` / `terminal` 手工调用，无法变成一次工具调用。
3. **无受管 Python 运行时** —— venv 由人工建在 clone 目录，迁移、升级、换机都没有平台保障。
4. **依赖仅装最小可跑子集** —— 上游共 32 项；PDF/EPUB/网页来源转换、语音旁白、公式排版等当前不可用。
5. **上下文模型冲突** —— Default 路线依赖长稳定上下文，与 PuddingAgent 的压缩 / 心跳机制结构性冲突 ⇒ 应固定走 Quick 路线。

## 6. 建议下一步（仍未获得 P0 决策，故未擅自动代码）

- **P0-A** 文档制品链路（I3）：否则本能力无法交付到用户手里。
- **P0-B** 受控子进程工具（I2）：把"模型手动敲脚本命令"收敛为工具调用。
- **P0-C** 受管 Python 运行时（I1）：把 venv 从人工搬迁升级为平台管理。
