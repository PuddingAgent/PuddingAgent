import React from 'react';

/**
 * HTML 草稿的隔离容器。
 *
 * ## 为什么必须是 iframe + `sandbox=""`
 *
 * 面板里的 HTML 来自模型生成，属于**不可信内容**。直接内联进宿主 DOM 等于
 * 把它当自己的模板渲染：一个 `<script>` 就能拿到登录态。
 *
 * `sandbox=""`（空值）是浏览器提供的最严格档位：禁脚本、禁表单提交、
 * 禁同源访问、禁插件。配合 spec 校验层的「禁外链」，草稿既跑不了代码，
 * 也发不出数据。
 *
 * ## 为什么不用 `allow-scripts`
 *
 * `allow-scripts` + `allow-same-origin` 同时开等于没有沙箱（草稿可自行移除
 * sandbox 属性）。本项目草稿不需要脚本，所以两者都不开。
 *
 * ## 高度
 *
 * 固定高度是刻意的：跨源（沙箱）iframe 读不到内容高度，做自适应要么放宽
 * 同源、要么加一条 postMessage 上报通道。本轮先固定，避免为了"好看一点"
 * 牺牲隔离强度。
 */
export interface SandboxedHtmlProps {
  html: string;
  title: string;
  height?: number;
}

export const SandboxedHtml: React.FC<SandboxedHtmlProps> = ({
  html,
  title,
  height = 320,
}) => (
  <iframe
    data-testid="sandboxed-html"
    title={title}
    // 空 sandbox = 最严格：无脚本、无表单、无同源。
    sandbox=""
    srcDoc={html}
    style={{ width: '100%', height, border: 0, background: '#fff', display: 'block' }}
  />
);

export default SandboxedHtml;
