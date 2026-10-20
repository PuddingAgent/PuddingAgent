---
title: D1 只读侦察 ·烧录图登记为 vision-artifact 的具体缺口
author: hyfree
date: 2026-10-21
last_reviewed: 2026-10-21
status: active
description: 查清「Desktop 烧录图无法进入 generate_image 的 referenceArtifactIds」卡在哪一层：上传端点的认证要求、存储入口、以及 Desktop 现有凭据的不匹配点；给出三选项的代价对比与下一步最小验证。
categories: [docs, features]
tags: [panel, craft, vision-artifact, auth]
related_docs: [Docs/12_features/交互面板-交互式编辑与制成品管理-设计方案-2026-10-20.md, Docs/07_architecture/113ADR-099交互面板编辑状态与制成品归属ADR.md]
related_files: [Source/PuddingPlatform/Controllers/Api/VisionArtifactApiController.cs, Source/PuddingPlatform/Services/VisionArtifactStorageService.cs, Source/PuddingPlatformAdmin/src/services/platform/api.ts]
slug: 12_features-D1-烧录图登记vision-artifact-只读侦察-2026-10-21
draft: false
---

# D1 只读侦察：烧录图登记为 vision-artifact

> 只读侦察，**未改任何代码**。目的是把「D1 该走 A0 还是 A1」从猜测变成有依据的选择。
> 事实都带 `文件:行`；查不到的显式标「未复核」。

## 一、这一层到底卡在哪

`generate_image(mode=precision)` 的 `referenceArtifactIds` 只吃 `vision-*` 工件，
而 Desktop 烧录出来的图只是 `{DataRoot}/panel-marks/*.jpg` 普通文件。
所以形式上「差一次上传」，实质上差的是**Desktop 没有可用的上传凭据**。

## 二、事实（已核对源码）

### 2.1 上传端点

`Source/PuddingPlatform/Controllers/Api/VisionArtifactApiController.cs:15`

| 项 | 值 |
|---|---|
路由 | `api/workspaces/{workspaceId}/vision-artifacts` |
方法 | `POST`（multipart form：`file` / `width` / `height` / `capturedAt`） |
认证 | **`[Authorize]`（类级）** —— 必须有平台身份 |
请求上限 | `[RequestSizeLimit(65MB)]`（`:23`） |
支持类型 | jpeg / png / webp / gif / bmp（`:87-95`） |
前置校验 | 目标 workspace 必须已在库里存在，否则 404（`:35-38`） |

**同一个控制器的读接口是公开的**：`GET .../{artifactId}` 标了 `[AllowAnonymous]`（`:73-75`）。
即：**写要登录，读不需要**。这条不对称是后面所有选项的起点。

### 2.2 存储入口

`Source/PuddingPlatform/Services/VisionArtifactStorageService.cs`

- `SaveAsync(...)`（`:43`）/ `SaveIdempotentAsync(...)`（`:74`）是唯一写入通道；
- 落盘位置 `{WorkspaceRoot}/vision-artifacts`（`:298`）；
- 返回 `VisionArtifactUploadResult(ArtifactId, MimeType, Width, Height, CapturedAt)`（`:10`）。

调用方目前**全在进程内**：`ImageGenerationService`（`:239`）、`RemoteImageArtifactImportService`（`:91`）、
预处理路径。也就是说，Agent 工具能拿 artifact id，是因为它和存储服务在同一个进程里，
**不是因为有一条 Desktop 可用的公开上传 API**。

### 2.3 前端怎么上传的

`Source/PuddingPlatformAdmin/src/services/platform/api.ts:3253-3265`

```ts
uploadVisionArtifact(...) → request(`/api/workspaces/${workspaceId}/vision-artifacts`, { method: 'POST', ... })
```

即：前端带着**用户登录态**（同一域名下的平台会话）打这个端点。
Desktop 是**另一个进程、另一套身份**（Bridge ControlToken，不是平台 JWT）——
这就是 D1 的全部矛盾点。

> 未复核：Desktop 现有凭据集合的完整清单（control token / connector 凭据 / 是否已有可复用的平台 token）。
> 本轮只按 ADR-099 的既有记录认定「Desktop 没有平台 JWT」，未逐文件核对。

## 三、三个选项的实际代价（基于上面的事实）

| 选项 | 做法 | 代价 | 风险 |
|---|---|---|---|
**A0 复用工登录态** | Desktop 内嵌/复用工作台会话去打同一个 POST | 要解决 Desktop 如何取得并保管平台会话（登录、过期、多账号） | 长期凭据散落到第二个进程 |
**A1 受限凭据** | 发一个**只能 POST vision-artifacts** 的短期令牌 | 需要一个签发端点 + 作用域校验 + 轮换 | 作用域写错就变成写盘口子 |
**A2 签名匿名入口** | 时间戳 + HMAC 的匿名上传端点 | 要设计签名与防重放 | 密钥仍在 Desktop |
**A3 端点改匿名** | 直接 `[AllowAnonymous]` | 0 | **否决**：等于对公网开任意写盘 |

### 与设计文档的分歧（需要显式记录）

设计方案 §9.2 D1 当时**建议 A0**。那时的理由是「Desktop 与工作台共享登录态最省事」。
本轮查到的事实让这个理由变弱：

1. 上传端点是 `[Authorize]` 的**纯 API**，不依赖页面来源；
2. 前端也只是带着会话打同一个 POST，没有别的魔法；
3. 而 Desktop 是独立进程，取「工作台会话」这件事本身就是 A0 的主要工程量。

**因此更可能的答案是 A1（最小作用域令牌）**：验证成本低（一次 POST 就能证伪），
爆炸半径比共享登录态小。但这是**需要用户裁定的选择**，本文件不代替决定。

## 四、下一步最小验证（一次，可回滚）

不做任何凭据改造，先证明「这条链路本身通」：

1. 用一次**已有平台身份**的调用，把一张 `panel-marks/*.jpg` POST 到
   `POST /api/workspaces/{workspaceId}/vision-artifacts`；
2. 断言返回 `artifactId` 以 `vision-` 开头；
3. 立刻把该 id 传进 `generate_image(mode=precision, referenceArtifactIds=[...])`，确认**模型真的看到了那张带笔迹的图**。

第 3 步是真正的判据：**上传成功不算通，能进 referenceArtifactIds 并在结果里体现才算通。**
如果第 3 步失败，说明问题不在认证而在「烧录图的宽高/mime 与 precision 路径不匹配」，
那就与 D1 无关，是另一条缺陷 —— 这个区分价值最大。

## 五、明确不在本文件内的

- Desktop 侧 UI（上传进度、失败提示）—— 等链路证明通了再谈。
- 草稿/会话归属（哪张图进哪条会话）—— ADR-099 §2 未决。
- 烧录图是否应**替代** `panel-marks` 临时文件（即上传后删除）—— 取决于 D2 的配额结论。
