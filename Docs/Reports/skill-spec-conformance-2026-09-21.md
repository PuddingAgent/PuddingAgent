# 技能与开放标准（Agent Skills spec）符合性检查 — 2026-09-21

- 检查对象：`D:\data\agents\default.global_general-assistant.6a8\skills`（144 个技能目录）
- 检查工具：**官方校验器** `agentskills/agentskills` 仓库的 `skills-ref`（Python 包，Apache-2.0）
- 方式：只读批量校验（`skills_ref.validate`），**未修改任何技能文件**
- 触发：RSI 设计文档 §9「外部调研整合」的 T0 切片

## 0. 结论

```
TOTAL=144  PASS=0  FAIL=144
```

**144 个技能没有一个通过官方格式校验。** 缺陷恰好分成三类，且 104 + 26 + 14 = 144（每技能归入唯一主类）：

| 缺陷类别 | 技能数 | 占比 | 根因 | 修法 |
|----------|--------|------|------|------|
| `TAGS_FLOW_STYLE` | **104** | 72.2% | `tags: [a, b]` 用 **YAML 流式数组**，被官方严格 YAML 判为 `ugly disallowed JSONesque flow mapping` | 改成块序列（每行 `- tag`），或整体移入 `metadata` |
| `NO_FRONTMATTER` | **26** | 18.1% | `SKILL.md` **不是**以 `---` 开头，而是以包裹用的 ```` ```markdown ```` 代码围栏开头 —— frontmatter 被关在代码块里 | 剥掉外层代码围栏 |
| `UNQUOTED_COLON_DESC` | **14** | 9.7% | `description` 未加引号且中间含 `: `，YAML 把它解析成嵌套映射（`mapping values are not allowed here`） | 给 description 加引号，或去掉冒号 |

> `NO_FRONTMATTER` 的证据（直接读原文件）：`agent-repo-health-check/SKILL.md` 首行是 ```` ```markdown ````，
> `---` 在其后；同一现象也被 `grep '^name:'` 独立佐证 —— 命中行号分两群，**line 2**（正常 frontmatter）
> 与 **line 3**（被代码围栏顶下去）。

## 1. ⚠️ 自我修正：我上一轮的判断不准确

上一轮我写「我们的技能格式与开放标准**完全不同**（元数据在 `manifest.json`、没有 frontmatter）」。**这是错的**：

- 实测 **118/144（82%）的 `SKILL.md` 本来就有 YAML frontmatter**，`name` 也符合「与父目录同名」；
- 真正的差距是两件事：
  1. **frontmatter 的机器有效性**（144/144 不通过 —— 流式 `tags`、代码围栏包裹、未引号冒号）；
  2. **description 的内容质量**：官方要求 `description` 同时写「做什么」和**「何时用」**并含助识别关键词；
     我们的 description 只写「做什么」（例：`agent-repo-health-check` 的描述完全不提 when to use）。

## 2. 工具链陷阱（可复用）

1. **`skills-ref` 在 zh-CN Windows 上直接崩**：`validator.py:172` 的 `skill_md.read_text()` **未指定 encoding**，
   而中文 Windows 的默认编码是 **GBK**，我们全部 `SKILL.md` 是 UTF-8 ⇒
   `UnicodeDecodeError: 'gbk' codec can't decode byte 0xae in position 352`。
   **解法**：设环境变量 `PYTHONUTF8=1`（强制 Python UTF-8 模式），**无需改它源码**。
2. **cmd 陷阱**：`set PYTHONUTF8=1 && …` 会把 `&&` 前的**空格**吃进变量值 ⇒
   `Fatal Python error: preconfig_init_utf8_mode: invalid PYTHONUTF8 environment variable value`。
   必须写 `set "PYTHONUTF8=1" && …`。
3. **官方 README 自陈**："This library is intended for **demonstration purposes only. It is not meant to be used in production.**"
   ⇒ 纠正调研中的表述：**许可**确实是 Apache-2.0（可用），但它**不能被当作生产依赖**；
   定位应为**参考校验器 / lint 门禁候选**。

复现命令（cmd）：

```cmd
set "PYTHONUTF8=1" && uv run --project <repo>\temp\agentskills-spec\skills-ref python <repo>\temp\validate-skills.py
```

（`temp/agentskills-spec` 为 `git clone --depth 1` 的官方仓库；`temp/validate-skills.py` 为批量校验+分类脚本。
两者都在不参与提交的 `temp/` 下，属一次性产物；若要把它接成门禁，应提升为正式脚本。）

## 3. 意义

1. **这类缺陷只能靠外部权威门禁发现。** 我们此前自研审计（`report-skill-portfolio.ps1` 的 content-audit / near-duplicate）
   看不到任何一条 —— 它检查的是「内容有没有用」，而官方校验器检查的是「**元数据机器可读吗**」。
   两者互补，不能互相替代。
2. **与 SkillsBench「自生成技能收益 ≈0」互相印证**：若自动生成的技能**连格式层都不合规**，
   就不能指望它的**路由元数据层**可靠 —— 而路由元数据正是渐进披露三层里的第一层（决定技能能不能被发现）。
3. **可直接复用的产物**：`skills-ref to-prompt` 能生成官方推荐的 L1 投影格式：
   ```xml
   <available_skills>
     <skill><name>…</name><description>…</description><location>/…/SKILL.md</location></skill>
   </available_skills>
   ```
   这正是我们设计的**元数据层**所需形态 ⇒ **直接采用，不必自研**（`<location>` 就是「模型按需去读正文」的入口）。

## 4. 下一步

1. 把 3 类缺陷修好（104 条改 `tags` 写法即可，机械且低风险；26 条剥围栏；14 条加引号），修完复跑校验，
   目标 **PASS 从 0 → 144**。⚠️ 属「改技能文件」，需作为独立切片并逐个可回滚（对应 C4）。
2. 之后把该校验接成**门禁**（新技能入库前必须 PASS），否则新生成的技能会继续复制同样的缺陷。
3. 同步把「description 必须写 when to use」列为**生成器侧契约** —— 这是自动化流水线的源头修复。
