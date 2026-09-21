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

---

## 5. 修复执行记录（2026-09-21，同日闭环）

### 5.1 安全性前置

- 技能目录**不在 git 中**（无版本回滚）⇒ 先建回滚点：
  `temp/skills-backup-20260921-1910.zip`（495,328 bytes，全量打包）。
- 修复器默认 **dry-run**，只有显式 `--apply` 才写盘；只改 `SKILL.md`，**绝不动 `manifest.json`**。
- 修复器已提交：`TestScripts/skill-spec/fix-skill-frontmatter.py`（可复现、可重跑）。
- 语义安全性依据：`Source/` 下**没有任何 C# 代码读取 SKILL.md 的 frontmatter**（全局搜 `frontmatter` 零命中）；
  平台读的是 `manifest.json`，SKILL.md 只是「喂给模型的正文」⇒ 这几类修复**不改变平台行为**。

### 5.2 dry-run 结果（与官方分类对账）

| 修复类型 | 技能数 |
|---|---|
| `tags-flow->block`（2/3/4/5/6/8/9 项） | **139**（102+12+10+8+5+1+1） |
| `strip-outer-fence`（剥外层代码围栏） | **21** |
| `description-quoted` | **15** |
| SKIP（真正没有 frontmatter，**不臆造元数据**） | **5** |

对账：`21 + 5 = 26` 正好等于官方的 `NO_FRONTMATTER` 计数
⇒ 26 条里 **21 条是「frontmatter 被代码围栏包住」，只有 5 条是真的没有 frontmatter**。

### 5.3 apply 后的官方复验

**YAML 语法层修复成功**：原来 111 条 `Invalid YAML…` 全部消失 ——
错误类别从「**语法错**」推进到「**字段错**」，这是修复生效的客观证据。

```
TOTAL=144  PASS=0  FAIL=144
139  OTHER         → Unexpected fields in frontmatter: tags, version.
                     Only ['allowed-tools','compatibility','description','license','metadata','name'] are allowed
  5  NO_FRONTMATTER → atomic-delegation-discipline / code-map-incremental-update / image-prompt-builder /
                     multi-engine-topic-research / smart-committee-workflow
```

另发现**新一类**缺陷：`name` 超长（规范要求 **≤64 字符**）。已知超限示例：

- `goal-driven-heartbeat-orchestration-loop-for-long-running-engineering`（69）
- `milestone-handoff-with-collaborator-notification-and-memory-persistence`（71）
- `parallel-full-stack-feature-implementation-via-async-sub-agent-delegation`（73）
- `persist-runtime-discovered-constraints-and-behavior-switches-to-memory`（70）
- `prove-existing-semantics-unchanged-via-git-log-s-plus-baseline-range`（68）
- `recover-from-file-patch-anchor-mismatch-by-re-reading-the-target-text`（69）

### 5.4 ⭐ 结论：缺陷是**分层暴露**的

修好 YAML 语法层之后，校验器**才**能开始检查 schema 层；schema 层过了才会检查 `name` 约束。

⇒ 这本身就是「**为什么必须做成门禁**」的最好论证：**一次性清理永远追不上** ——
每修一层就露出下一层，只有把校验接成**入库门禁**，新生成的技能才不会继续复制同样的缺陷。
（外部调研里「138K SKILL.md 有 89.3% 违反 spec」正是这种持续劣化的结果。）

### 5.5 剩余两层需要**决策**，不是纯机械修复

1. **`tags` / `version` 不是规范字段**：规范只允许
   `name / description / license / compatibility / metadata / allowed-tools`，
   额外信息必须放进 `metadata`（string → string）。
   但 `tags` 承载 provenance（`auto-generated`、`dedup-reviewed:1.0.1`、`source-session:…`），
   直接删会丢诊断线索 ⇒ 倾向搬进 `metadata`（逗号连接的字符串）。
   **这是内容取舍，需要明确决策**，不顺手改。
2. **`name` ≤64 字符**：违规技能的**目录名 = skillId**，改名会破坏身份
   （`manifest.json`、SkillHub 记录、记忆/文档里的引用都会失效）。
   ⇒ 需要在「**改名**」与「**显式豁免（grandfather 名单）**」之间做治理决策。

（本轮到此停止：不在没有决策的情况下继续批量改内容。）

---

## 6. 副作用自查与新增发现（同一轮，改完文件后核实）

批量改了 139 个 `SKILL.md` 之后，我主动核实平台侧是否仍自洽，结果发现两件事。

### 6.1 `contentHash` 已失效（我造成的，**严重度低**，已记录）

证据（源码）：

```csharp
// Source/PuddingRuntime/Services/Skills/AgentSkillFileService.cs L517-L532
private static string ComputeContentHash(AgentSkillManifest manifest, string markdown)
{
    var canonical = JsonSerializer.Serialize(new
    {
        manifest.SkillId, manifest.Name, manifest.Version, manifest.Description,
        manifest.Summary, manifest.Tags, manifest.Enabled,
        SkillMarkdown = markdown,          // ← 包含 SKILL.md 正文
    }, JsonOptions);
    return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
}
```

`ContentHash` 只在 `CreateAsync` / `UpdateAsync` / `SetEnabledAsync` 里刷新（L107 / L207 / L246）。
我是**直接写盘**改的 `SKILL.md`，没有走这三个入口 ⇒ 139 个 manifest 的 `contentHash` 是**旧值**。
（`agent_skill rebuild_index` 也**不会**重算它：索引里的 `contentHash` 仍与改前一致 —— 已实测。）

**严重度评估：低。** 理由：该 hash 的用途是**变更检测**（本地 vs Hub 版本比对、publish/check-updates）。
当前状态是「磁盘内容已变、记录未变」⇒ 未来的变更检测会判定为**已变更**（与事实相符），
方向是安全的；反过来（内容变了却判定未变）才会造成静默错误。

**修复选项**（留作后续决策，不擅自手改 manifest）：
（a）走受支持入口逐个刷新（`agent_skill update` / `set_enabled`）—— 139 次调用，成本高；
（b）写脚本按 `ComputeContentHash` 复刻算法批量重算 —— 需复刻 `JsonOptions` 与字段顺序，**有算错风险**；
（c）随下一次技能治理（schema 层修复，本来就要改 `SKILL.md`）走受支持入口一并刷新 ⇒ **推荐**。

### 6.2 ⭐ 新发现：`DeriveSummary` 把 frontmatter 的 `name:` 行当成了摘要（平台缺陷）

`agent_skill get` / `get_index` 返回的 `summary` 全量是这样的：

```
"summary": "name: adjudicate-parallel-subagent-work-in-progress"
"summary": "name: agent-health-status-check"
"summary": "name: agent-repo-health-check"
...
```

根因（源码 L500-L515）：

```csharp
private static string DeriveSummary(string markdown)
{
    foreach (var rawLine in markdown.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
    {
        var line = rawLine.Trim();
        if (line.Length == 0 || line is "---")   // ← 只跳过 '---' 这一行
            continue;                            //    没有跳过 frontmatter 的**字段行**
        if (line.StartsWith('#')) line = line.TrimStart('#').Trim();
        ...
        return line.Length <= 240 ? line : line[..240];
    }
    return string.Empty;
}
```

⇒ 只要 `SKILL.md` 有 YAML frontmatter，第一个非 `---` 的非空行就是 `name: <skillId>`，
于是**摘要变成了 `name: <skillId>` 这种垃圾值**（对全部带 frontmatter 的技能成立，实测 139/144）。
只有那 5 个**真正没有 frontmatter** 的手写技能摘要是正常的（它们的首行是 `# 标题`）。

**为什么这值得修**：`summary` 是**元数据层**的一部分（渐进披露第一层的候选内容，
也是 `ImproveSkillsAsync`/去重判断的输入）。摘要层被垃圾值污染，等于第一层元数据质量被破坏 ——
这与外部调研「26.4% 技能无 routing description / 路由元数据弱则检索失败」是同一类问题。

**建议修法**（下一轮作为独立原子任务，需带单测）：
`DeriveSummary` 应**先跳过 YAML frontmatter 块**（开头 `---` 到下一个 `---`），
再从正文里取首个有效行；且**优先取** frontmatter 的 `description`（若存在）而不是正文首行。

### 6.3 顺带确认（正面证据）：既有「取代」机制真的在工作

`agent-health-status-check` 的索引条目：

```
"enabled": false,
"tags": ["auto-generated", "self-evolution", "superseded", "superseded-by:agent-status-health-check"]
```

⇒ 「**取代而非新增**」在本仓库是**已在运行**的真实机制（不是设计愿景），
这为 RSI 的 `replaced_by` / `superseded` 语义提供了现成范本与落点。

### 6.4 本轮状态

- 平台侧一致性核查：`agent_skill rebuild_index` 已执行（144 条索引重建成功，属受支持入口，无副作用）。
- 未修改任何生产代码；`manifest.json` 一律未动。
- 回滚点仍在：`temp/skills-backup-20260921-1910.zip`。
