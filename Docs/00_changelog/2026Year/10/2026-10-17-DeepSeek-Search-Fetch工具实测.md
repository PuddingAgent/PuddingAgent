---
title: deepseek_search / deepseek_fetch 工具实测报告
author: hyfree
date: 2026-10-17
last_reviewed: 2026-10-17
status: active
description: 对 2026-10-17 落地的 deepseek_search 与 deepseek_fetch 做真实调用实测：8 次调用覆盖正常路径、count 边界、need_content 开关、site: 语法透传、URL 校验拒绝；两者均可用，参数与错误处理符合文档；同时记录三处观察项（部分页面正文被反斜杠转义、小众词检索质量弱、站点 404 页面返回 exit 0）与未覆盖项。
categories: [docs, changelog]
tags: [testing, tools, deepseek, verification]
related_docs: [Docs/00_changelog/2026Year/10/2026-10-17-DeepSeek-Search-Fetch工具接入.md, Docs/00_changelog/2026Year/10/2026-10-17-文档门禁环境漂移与新增组件门禁复核.md]
related_files: [Source/PuddingRuntime/Tools/BuiltIns/Search/DeepSeekSearchTool.cs, Source/PuddingRuntime/Tools/BuiltIns/Search/DeepSeekFetchTool.cs, Source/PuddingDeepSeekWeb/DeepSeekWebService.cs]
slug: changelog-2026-10-17-deepseek-search-fetch-实测
draft: false
---

# deepseek_search / deepseek_fetch 工具实测报告

> **性质**：**真实网络调用实测**（未改任何代码、未改任何配置）。
> 被测对象：`011e240` 落地的两个工具；运行时已在工具目录中暴露（`search_tools` 可加载）。

## 一、测试矩阵（8 次调用，逐条实测）

| # | 工具 | 参数 | 期望 | 实测结果 |
|---|---|---|---|---|
| 1 | `deepseek_search` | `query="DeepSeek API 官方文档 搜索接口"` `count=5` | 返回 5 条带元数据结果 | **PASS**：5 条；首条 `api-docs.deepseek.com/zh-cn/guides/search/`，`Authority=3` `Rerank=2.43`；字段含 `URL / Date / Site / Authority / Rerank / Snippet / Summary` |
| 2 | `deepseek_search` | `query="PuddingAgent site:github.com"` `count=2` | `site:` 语法透传、结果限于 github.com | **PASS（语法有效）**：2 条**均为 github.com**；但相关性差（`Rerank 0.773 / 0.121`，返回的是"拼多多客服 Agent"项目） |
| 3 | `deepseek_search` | `query="PuddingAgent 智能体框架"` `count=1` | `count` 生效 | **PASS**：输出标题即 `(1 results)`，仅 1 条 |
| 4 | `deepseek_search` | `query="site:api-docs.deepseek.com tools search fetch 计费"` `count=1` `need_content=true` | 出现 `Content:` 正文段 | **PASS**：`Content:` 段出现，正文为该页 API 参考（数百行），**未被反斜杠转义** |
| 5 | `deepseek_fetch` | `url="https://example.com"` | 返回标题 + 正文 | **PASS**：`title=Example Domain`；正文 6 语种占位文案（含 `learn more`） |
| 6 | `deepseek_fetch` | `url="https://api-docs.deepseek.com/zh-cn/guides/search/"` | 长文档正文 | **PASS**（内容完整：含表格、curl/python 代码块）；但**全文标点被反斜杠转义**（见二.1） |
| 7 | `deepseek_fetch` | `url=".../zh-cn/this-page-does-not-exist-xyz-2026"` | 明确报错 **或** 返回可用正文 | **exit 0**，`title=DeepSeek API Docs`，正文为站点自带 `Page Not Found`（见二.3） |
| 8 | `deepseek_fetch` | `url="ftp://example.com/file.txt"` | 参数校验拒绝 | **PASS（fail-closed）**：`exit 1`，`Error: Url must use the http or https scheme.` |

## 二、观察项（不是"工具坏了"，但会影响使用）

### 1. 部分页面的正文被**反斜杠转义**（内容相关，非本工具引入）

`deepseek_fetch` 取 DeepSeek 指南页时，正文形如：

```
\# 网页搜索与抓取
\- Search 接口按 query 搜索网页…
Base URL 为https\:\/\/api\.deepseek\.com：
API \| Endpoint
```

而同一工具取 `example.com` 时是 `permission\.` / `service\;`；**但**测试 4 的 `Content` 段（同为 DeepSeek 官方站点、另一页面）**完全没有转义**。

**归因证据（三步）**：
1. 组件侧无转义逻辑：`Source/PuddingDeepSeekWeb/**` 搜 `Escape|Replace(|Sanitize|Unescape` ⇒ 仅命中 **API Key 脱敏** 两处（`Message.Replace(apiKey, "***")`）。
2. 工具侧直接拼原文：`DeepSeekFetchTool.RenderFetchResponse` 为 `sb.Append(content.Trim())`，无加工。
3. **对照**：同一 URL 用 `http_fetch`（`output_format=text`）取回的是**无反斜杠**的纯文本。

⇒ **转义来自上游返回的正文本身，且与页面/站点有关**（同一工具、同类站点、不同页面表现不同），**不是 Pudding 侧统一施加的层**。
**影响**：把正文粘进文档前需要去转义（去掉 `\` 前缀），否则表格与标题语法被破坏。
**建议**（未实施）：工具侧对 `content` 做一次"仅去转义不改变结构"的后处理，需要先确认不会破坏代码块内的真实反斜杠。

### 2. 小众词的检索质量弱（上游排序问题）

`PuddingAgent`（本仓库专有名词）在公开网无对应实体，测试 2/3 返回的是 **Pudding/Pudu 机器人**与**拼多多客服 Agent** 等无关结果，`Rerank` 仅 **0.121~0.773**（文档：`≥1.5` 才算优质）。
⇒ **用法建议**：`Rerank` 可作可信度闸门；低分结果应视为"未检索到"，不要当事实使用。

### 3. "不存在的页面"仍可能 `exit 0`

测试 7 请求的路径在站点上不存在，但返回的是**站点自身的 404 页面**（`Page Not Found`，HTTP 层面可取到正文）⇒ 工具按"有正文"判成功。
⇒ **不等于缺陷**（API 的 404 语义是"取不到可用正文"），但**使用方须知**：`exit 0` 只表示"取到了内容"，**不表示目标内容存在**。判定仍要看 `title` / 正文。

## 三、未覆盖（如实列出）

- **`count > 50` 的夹取**（会真的返回 50 条，输出过大，未测）。
- **`count=0` / 负数 / 全空白 query** 的边界（文档说 `1~50`、不能全空白；本次未逐一触发校验）。
- **限流与配额类错误**：`429`（账号级 5 QPS、域名级）、`401/402`（鉴权/余额）、`408/413/504`。
- **60000 字符有界截断**是否真的发生（未构造超长页面）。
- **`localization`（按 IP 本地化）** 与 **`ori_question`（意图增强）** 的实际效果差异。
- **延迟/费用**：未计单次耗时，也未核账户计费（文档：每千次成功请求 5 元，仅成功且非空计费）。

## 四、复现命令

```text
deepseek_search(query="DeepSeek API 官方文档 搜索接口", count=5)
deepseek_search(query="PuddingAgent site:github.com", count=2)
deepseek_search(query="PuddingAgent 智能体框架", count=1)
deepseek_search(query="site:api-docs.deepseek.com tools search fetch 计费", count=1, need_content=true)
deepseek_fetch(url="https://example.com")
deepseek_fetch(url="https://api-docs.deepseek.com/zh-cn/guides/search/")
deepseek_fetch(url="https://api-docs.deepseek.com/zh-cn/this-page-does-not-exist-xyz-2026")
deepseek_fetch(url="ftp://example.com/file.txt")
```

## 五、结论

> **两个工具均可用**：参数（`count` / `need_content` / `site:` 语法）按文档生效；URL 非法时 **fail-closed 拒绝**而非静默降级；输出字段齐全（`Authority` / `Rerank` / `Date` / `Site`）。
> **唯一影响可用性的问题是上游正文转义**（观察项 1），与本次落地代码无关，属**内容清洗策略**议题。

## 关联

- 交付来源：`Docs/00_changelog/2026Year/10/2026-10-17-DeepSeek-Search-Fetch工具接入.md`（提交 `011e240`）
- 同日门禁复核：`Docs/00_changelog/2026Year/10/2026-10-17-文档门禁环境漂移与新增组件门禁复核.md`（提交 `0579f1e`）

## 六、复查记录（2026-10-18）

用户要求重新测试。串行调用 8 次（规避账号级 5 QPS），逐项结果：

- **基础检索** `count=3` ⇒ PASS。首条 `api-docs.deepseek.com`，`Authority=3` / `Rerank=2.383`，字段齐全。
- **`site:` 限定** `count=2` ⇒ PASS，2/2 命中该站点；`Rerank` 1.415 / 1.398（均低于 1.5 优质线，属"相关但不属优质"）。
- **超长 query（129 字符）** ⇒ `exit=1`，`Query must be at most 128 characters.` 属**客户端校验**，未发起请求。
- **`count=0`** ⇒ **被夹取为 1 条**（返回 `1 results`）。补上第 2 节"未覆盖"里的越界夹取项。
- **`need_content=true`**（`count=1`）⇒ PASS，返回 `Content:` 段完整正文（约 1.5k 字）。
- **`fetch` 正常页**（`https://example.com`）⇒ PASS。
- **`fetch` 非 http(s)**（`ftp://example.com/file.txt`）⇒ `exit=1`，`Url must use the http or https scheme.`，fail-closed。
- **`fetch` 不存在路径** ⇒ `exit=404`，含上游 `request_id`。

### 对前节结论的两处更正

1. **观察 1（正文转义）范围比先前判断更广。** 本次 `example.com` 同一页面内：**中文段落未转义**，英/法/俄/西/阿段落出现 `\.` `\;` `\,` ⇒ 转义**与段落/语言相关**，而非仅"与页面/站点相关"。第 2 节的表述按此收窄。

2. **观察 3（"不存在的页面仍 exit 0"）需按站点行为分类。**
   - 站点返回**真实 HTTP 404**（本次 `example.com` 未知路径）⇒ 工具 **`exit=404` 并报错**，不是静默成功。
   - 站点以 **200 + 自定义 404 页**返回（部分 SPA 文档站）⇒ `exit 0`，正文为 `Page Not Found`。

   ⇒ 正确表述：**`exit 0` 不保证目标内容存在；但 HTTP 404 会被工具明确拒绝**。两条证据不矛盾，是两类站点行为。

### 本轮仍未覆盖

`count > 50` 的夹取、`429/401/402` 限流与鉴权错误、60000 字符截断、`localization` / `ori_question` 的实际效果、单次延迟与账户计费核对。
