import { injectGlobal } from 'antd-style';

injectGlobal`
  :root {
    /* IMG01（设计规格 §3）：暖米色 + 紫色强调收敛为中性层级 + 单一蓝色强调。
       下列 legacy 变量名保留（存量引用 200+ 处），值改为 §3 语义色；
       变量改名（--accent-purple → --pudding-accent 等）作为后续独立切片，
       不在换色时混做重构。 */
    --misty-blue: #dde4ec;
    --warm-beige: #f7f8fa;
    --soft-white: #ffffff;
    --pale-yellow-sunlight: #f3f6fa;
    --earth-brown: #526174;
    --sky-soft: #eaf0ff;
    --desaturated-green: #157347;
    --text-primary: #182230;
    --text-secondary: var(--earth-brown);
    --accent-purple: #2458d3;
    --avatar-0: #f97316;
    --avatar-1: #ef4444;
    --avatar-2: #8b5cf6;
    --avatar-3: #06b6d4;
    --avatar-4: #22c55e;
    --avatar-5: #eab308;
    --avatar-6: #ec4899;
    --avatar-7: #6366f1;
    --avatar-8: #14b8a6;
    --avatar-9: #f43f5e;

    /* SCROLL-001：原生滚动条与表单控件（textarea/Select/checkbox）跟随应用主题。
       缺此声明时 Chromium 在深色主题下仍绘制浅色原生滚动条（浅灰/白轨道），
       与 scrollbar-gutter:stable 叠加就形成常亮白条（IMG02 / SCROLL-001 根因之一）。
       显式选择浅/深必须用本声明同步，不能只依赖 prefers-color-scheme。 */
    color-scheme: light;

    /* SCROLL-001 滚动条皮肤 token（§14.2 目标外观表）。
       轨道不单列 token：渲染时用 transparent = 所属容器自身背景，
       这样代码块/表格跟自己的 surface、消息跟 chat bg，无需逐容器重复声明。
       thumb 圆角细条；hover/active 只换色不改宽度，避免 gutter 跳动。 */
    --pudding-scroll-thumb: #738197;
    --pudding-scroll-thumb-hover: #526174;
    --pudding-scroll-thumb-active: #2458d3;

    /* IMG01：下列短名 token 被 26 处样式以「var(短名, 暖色/紫色字面量)」形式引用，
       但从未定义 —— 实际渲染的一直是回退的暖灰/紫（#1d1b24 / #756b5f / #8b5cf6 /
       #b5543c / #f3eee7）。这里补成 §3 语义别名；被引用的 token 在深色分支重定义在
       同一元素上，因此别名无需在深色段重复声明即可随主题变化。 */
    --pudding-text: var(--pudding-chat-text);
    --pudding-text-muted: var(--pudding-chat-text-muted);
    --pudding-accent: var(--pudding-chat-accent);
    --pudding-danger: var(--pudding-chat-danger);
    --pudding-surface-soft: var(--pudding-chat-surface-muted);
    /* 任务看板列背景：原引用未定义，回退到中性 #f5f5f5；补为主题表面色 */
    --pudding-chat-panel-bg: var(--pudding-chat-surface);

    /* Runtime 语义色（IMG01：随 §3 中性层级收敛，信号色取可读档） */
    --runtime-bg: #f7f8fa;
    --runtime-bg-deep: #eef1f5;
    --glass-surface: rgba(255,255,255,0.72);
    --glass-border: rgba(36,88,211,0.18);
    --neural-line: rgba(36,88,211,0.18);
    --memory-glow: #6e93e8;
    --tool-signal: #0e7490;
    --success-signal: #157347;
    --warning-signal: #8a5700;
    --error-signal: #b42318;
    --text-muted: #526174;

    /* Pudding Chat Tokens — Light（IMG01/§3：bg/surface/text/border/accent 中性化） */
    --pudding-chat-bg: #f7f8fa;
    --pudding-chat-sidebar-bg: rgba(255, 255, 255, 0.7);
    --pudding-chat-header-bg: rgba(255, 255, 255, 0.7);
    --pudding-chat-surface: #ffffff;
    --pudding-chat-surface-muted: #eef1f5;
    --pudding-chat-border: #d8dee8;
    --pudding-chat-border-strong: #c3ccd9;
    --pudding-chat-text: #182230;
    --pudding-chat-text-muted: #526174;
    --pudding-chat-text-subtle: #5c6b7a;
    /* 文本四档灰（行为链升级 §3.1）：primary 正文 / secondary 次要 / tertiary 过程正文（思考、工具行）/ caption 装饰标签与耗时。
       muted/subtle 为 legacy 别名（= secondary/tertiary），存量引用不破坏。
       IMG04/§13.5：四档随 §3 重新标定，均在 bg 上 ≥4.5:1（caption 为装饰标签，≥3:1）。 */
    --pudding-chat-text-secondary: #526174;
    --pudding-chat-text-tertiary: #5c6b7a;
    --pudding-chat-text-caption: #6e7a88;
    --pudding-chat-accent: #2458d3;
    --pudding-chat-accent-soft: #eaf0ff;
    --pudding-chat-danger: #b42318;
    --pudding-chat-success: #157347;
    --pudding-chat-shadow: 0 8px 32px rgba(0, 0, 0, 0.12);

    /* Pudding Chat Design Tokens — Light（P0-4 附加：仅新增变量，不改既有值） */
    --pudding-chat-radius-sm: 6px;
    --pudding-chat-radius-md: 10px;
    --pudding-chat-radius-lg: 14px;
    --pudding-chat-shadow-sm: 0 1px 3px rgba(0, 0, 0, 0.04);
    --pudding-chat-shadow-md: 0 3px 12px rgba(15, 23, 42, 0.04);
    --pudding-chat-shadow-hover: 0 6px 18px rgba(15, 23, 42, 0.065);
    /* 状态色阶（§4.0 总表）：running=强调蓝 / waiting=琥珀 / success / error（IMG01：随 §3 状态色） */
    --pudding-status-running: var(--accent-purple);
    --pudding-status-waiting: #8a5700;
    --pudding-status-warning: #8a5700;
    --pudding-status-success: #157347;
    --pudding-status-error: #b42318;
    /* 代码块深底（P0-3：对齐 D4 对比度策略，浅色下代码表面独立加深一档） */
    --pudding-chat-code-bg: #1e2430;
    /* 工具 IN/OUT 参数面板（2026-08-24）：浅色下降为灰阶浅底——参数是结构化
       数据而非代码内容，大段深底在浅色消息流中视觉过重；终端/diff
       presentation 卡仍走 code-bg 深底。 */
    --pudding-toolcard-bg: #f6f8fa;
    --pudding-toolcard-fg: #3f4a5a;
    --pudding-toolcard-border: #d8dee8;

    /* Pudding Admin Tokens — Light（IMG01：与 Chat 同一套 §3 中性层级） */
    --pudding-admin-bg: #f7f8fa;
    --pudding-admin-bg-subtle: #eef1f5;
    --pudding-admin-surface: #ffffff;
    --pudding-admin-surface-muted: #eef1f5;
    --pudding-admin-border: #d8dee8;
    --pudding-admin-border-strong: #c3ccd9;
    --pudding-admin-text: #182230;
    --pudding-admin-text-muted: #526174;
    --pudding-admin-accent: #2458d3;
    --pudding-admin-accent-soft: #eaf0ff;
    --pudding-admin-success: #157347;
    --pudding-admin-warning: #8a5700;
    --pudding-admin-danger: #b42318;
    --pudding-admin-radius: 8px;
    --pudding-admin-shadow-low: 0 1px 6px rgba(0, 0, 0, 0.04);
  }

  html, body, #root {
    font-family: 'Noto Sans SC', 'PingFang SC', 'Microsoft YaHei', sans-serif;
  }

  .ant-select > .ant-select-dropdown {
    left: 0 !important;
    z-index: 1160;
  }

  .ant-select > .ant-select-dropdown-placement-bottomLeft,
  .ant-select > .ant-select-dropdown-placement-bottomRight {
    top: calc(100% + 4px) !important;
  }

  .ant-select > .ant-select-dropdown-placement-topLeft,
  .ant-select > .ant-select-dropdown-placement-topRight {
    bottom: calc(100% + 4px) !important;
  }

  @keyframes fadeIn {
    from {
      opacity: 0.32;
    }
    to {
      opacity: 1;
    }
  }

  @keyframes slideUp {
    from {
      opacity: 0;
      transform: translateY(8px);
    }
    to {
      opacity: 1;
      transform: translateY(0);
    }
  }

  @keyframes shake {
    0%, 100% {
      transform: translateX(0);
    }
    10%, 50%, 90% {
      transform: translateX(-4px);
    }
    30%, 70% {
      transform: translateX(4px);
    }
  }

  @keyframes puddingLogoPulse {
    0% {
      transform: scale(1);
    }
    50% {
      transform: scale(1.02);
    }
    100% {
      transform: scale(1);
    }
  }

  @keyframes messageIn {
    from {
      opacity: 0;
      transform: translateY(8px);
    }
    to {
      opacity: 1;
      transform: translateY(0);
    }
  }

  @keyframes stepIn {
    from {
      opacity: 0;
      transform: translateX(-4px);
    }
    to {
      opacity: 1;
      transform: translateX(0);
    }
  }

  @keyframes thinkingPulse {
    0%, 100% {
      opacity: 0.6;
    }
    50% {
      opacity: 1;
    }
  }

  @keyframes completeFade {
    from {
      color: var(--earth-brown);
    }
    to {
      color: var(--desaturated-green);
    }
  }

  @keyframes softBreath {
    0%, 100% { opacity: 0.6; }
    50% { opacity: 1; }
  }

  /* IMG01：光晕色随单一蓝色强调收敛（原为紫色 rgba(167,139,250,*)） */
  @keyframes neuralPulse {
    0%, 100% { box-shadow: 0 0 4px rgba(36,88,211,0.12); }
    50% { box-shadow: 0 0 12px rgba(36,88,211,0.24); }
  }

  @keyframes signalFlow {
    from { background-position: 0% 50%; }
    to { background-position: 200% 50%; }
  }

  @keyframes nodeAppear {
    from { opacity: 0; transform: translateX(-6px); }
    to { opacity: 1; transform: translateX(0); }
  }

  @keyframes blockCondense {
    from { opacity: 0; transform: translateY(4px); filter: blur(2px); }
    to { opacity: 1; transform: translateY(0); filter: blur(0); }
  }

  @keyframes glowSettle {
    0% { box-shadow: 0 0 20px rgba(36,88,211,0.15); }
    100% { box-shadow: 0 0 0px rgba(36,88,211,0); }
  }

  /* 页面进入 — Runtime 品牌页（chat/login/bootstrap） */
  @keyframes pageEnterRuntime {
    from { opacity: 0; transform: scale(0.98); }
    to { opacity: 1; transform: scale(1); }
  }

  /* 页面进入 — 后台页（快速） */
  @keyframes pageEnterAdmin {
    from { opacity: 0; }
    to { opacity: 1; }
  }

  /* 等待气泡：三点波浪弹跳 */
  @keyframes waitingBounce {
    0%, 80%, 100% {
      transform: translateY(0) scale(0.7);
      opacity: 0.35;
    }
    40% {
      transform: translateY(-7px) scale(1);
      opacity: 0.9;
    }
  }

  .runtime-page-enter {
    animation: pageEnterRuntime 200ms ease-out;
  }

  .admin-page-enter {
    animation: pageEnterAdmin 120ms ease-out;
  }

  .colorWeak {
    filter: invert(80%);
  }

  html,
  body,
  #root {
    background-color: var(--ant-colorBgLayout);
    color: var(--ant-colorText);
    transition: background-color 200ms ease, color 200ms ease;
  }

  /* ── SCROLL-001 滚动条皮肤（策略 B：Chromium 伪元素，§14.2/§14.3）──────────
     轨道用 transparent 而非写死色值 = 所属容器自身背景（"背景同色轨道"）：
     代码块/表格跟自己的 surface、消息跟 chat bg，无需逐容器重复声明。
     10px gutter 内绘制 6px 视觉 thumb（2px 透明边 + background-clip:content-box）
     → 圆角细条；hover/active 只换色不改宽度，gutter 不跳动。
     不隐藏 overflow、不 JS 自绘拖条、不建新节点：滚轮/触控板/拖动 thumb/
     点击轨道/键盘(方向、PageUp/Down、Home/End) 全部保留原生能力。
     已声明 scrollbar-width:thin 的块级容器（scrollTokens.ts）在 Chromium 中
     优先级更高，保持既有细条外观，仅经 token 统一色温。
     注意：不要在这里声明 scrollbar-color/-width —— 全局非 auto 会反过来
     停用 Chromium 伪元素，两种策略互相覆盖。 */
  ::-webkit-scrollbar {
    width: 10px;
    height: 10px;
  }

  ::-webkit-scrollbar-track,
  ::-webkit-scrollbar-corner {
    background: transparent;
  }

  ::-webkit-scrollbar-thumb {
    background-color: var(--pudding-scroll-thumb);
    border: 2px solid transparent;
    background-clip: content-box;
    border-radius: 999px;
  }

  ::-webkit-scrollbar-thumb:hover {
    background-color: var(--pudding-scroll-thumb-hover);
  }

  ::-webkit-scrollbar-thumb:active {
    background-color: var(--pudding-scroll-thumb-active);
  }

  /* 不绘制上下箭头：传统箭头是 SCROLL-001 描述的缺陷外观之一 */
  ::-webkit-scrollbar-button {
    display: none;
  }

  /* 非 Chromium（Firefox）回退：标准属性，轨迹仍保持容器背景 */
  @supports not selector(::-webkit-scrollbar) {
    * {
      scrollbar-width: thin;
      scrollbar-color: var(--pudding-scroll-thumb) transparent;
    }
  }

  /* 系统高对比：放弃自定义皮肤，恢复系统绘制（可用性优先，不强制细条） */
  @media (forced-colors: active) {
    ::-webkit-scrollbar,
    ::-webkit-scrollbar-track,
    ::-webkit-scrollbar-corner,
    ::-webkit-scrollbar-thumb,
    ::-webkit-scrollbar-thumb:hover,
    ::-webkit-scrollbar-thumb:active,
    ::-webkit-scrollbar-button {
      all: revert;
    }
  }

  .ant-layout {
    min-height: 100vh;
    background-color: var(--ant-colorBgLayout);
  }

  .ant-pro-sider.ant-layout-sider.ant-pro-sider-fixed {
    left: unset;
  }

  .ant-pro-layout .ant-pro-layout-content,
  .ant-pro-page-container {
    animation: fadeIn 200ms ease-out;
  }

  .ant-btn:not(.ant-btn-icon-only) {
    transition: border-color 200ms ease, background-color 200ms ease, box-shadow 200ms ease;
  }

  .ant-pro-global-header-logo img,
  .ant-pro-top-nav-header-logo img,
  .ant-pro-sider-logo img,
  .ant-pro-layout-logo img {
    transform-origin: center;
    animation: puddingLogoPulse 2400ms ease-in-out infinite;
  }

  .ant-form-item-has-error .ant-input,
  .ant-form-item-has-error .ant-input-affix-wrapper,
  .ant-form-item-has-error .ant-input-number,
  .ant-form-item-has-error .ant-select-selector,
  .ant-form-item-has-error textarea.ant-input {
    animation: shake 0.4s ease-in-out;
  }

  canvas {
    display: block;
  }

  body {
    text-rendering: optimizeLegibility;
    -webkit-font-smoothing: antialiased;
    -moz-osx-font-smoothing: grayscale;
  }

  ul,
  ol {
    list-style: none;
  }

  @media (max-width: 768px) {
    .ant-table {
      width: 100%;
      overflow-x: auto;
    }

    .ant-table-thead > tr > th,
    .ant-table-tbody > tr > td {
      white-space: pre;
    }

    .ant-table-thead > tr > th > span,
    .ant-table-tbody > tr > td > span {
      display: block;
    }
  }

  [data-pudding-theme='dark'] {
    /* SCROLL-001：深色主题下原生控件与滚动条跟随深色，消除浅色轨道/传统箭头。
       选择器与 ThemeMode 写入的 document.documentElement[data-pudding-theme] 一致。 */
    color-scheme: dark;
    --pudding-scroll-thumb: #65758c;
    --pudding-scroll-thumb-hover: #8b9db5;
    --pudding-scroll-thumb-active: #91b3ff;

    --warm-beige: var(--pudding-chat-bg);
    --soft-white: var(--pudding-chat-surface);
    --pale-yellow-sunlight: #2a3342;
    --earth-brown: var(--pudding-chat-text-muted);
    --text-secondary: var(--pudding-chat-text-muted);

    /* Runtime 语义色（IMG01：与浅色段成对，信号色取深色可读档） */
    --runtime-bg: #0e1218;
    --runtime-bg-deep: #11151b;
    --glass-surface: rgba(26,32,41,0.68);
    --glass-border: rgba(145,179,255,0.22);
    --neural-line: rgba(145,179,255,0.24);
    --memory-glow: #91b3ff;
    --tool-signal: #22d3ee;
    --success-signal: #75d6a4;
    --warning-signal: #f0c36a;
    --error-signal: #ff9e99;
    --text-primary: #e8edf4;
    --text-muted: #a8b5c7;

    /* Pudding Chat Tokens — Dark（IMG01/§3：暖黑 → 中性深色） */
    --pudding-chat-bg: #11151b;
    --pudding-chat-sidebar-bg: rgba(26, 32, 41, 0.92);
    --pudding-chat-header-bg: rgba(26, 32, 41, 0.88);
    --pudding-chat-surface: #1a2029;
    --pudding-chat-surface-muted: #242c37;
    --pudding-chat-border: #445166;
    --pudding-chat-border-strong: #56637a;
    --pudding-chat-text: #e8edf4;
    --pudding-chat-text-muted: #a8b5c7;
    --pudding-chat-text-subtle: #9aa8bb;
    /* 文本四档灰 — Dark（与浅色段一一对应，§3.1；IMG04：四档重新标定）
       均在 bg 上 ≥4.5:1，caption 为装饰标签 ≥3:1。 */
    --pudding-chat-text-secondary: #a8b5c7;
    --pudding-chat-text-tertiary: #9aa8bb;
    --pudding-chat-text-caption: #8b98a9;
    --pudding-chat-accent: #91b3ff;
    --pudding-chat-accent-soft: #243657;
    --pudding-chat-danger: #ff9e99;
    --pudding-chat-success: #75d6a4;
    --pudding-chat-shadow: 0 8px 32px rgba(0, 0, 0, 0.4);

    /* Pudding Chat Design Tokens — Dark（P0-4 附加：与浅色段一一对应；半径主题无关，为保持深色段自包含重复声明） */
    --pudding-chat-radius-sm: 6px;
    --pudding-chat-radius-md: 10px;
    --pudding-chat-radius-lg: 14px;
    --pudding-chat-shadow-sm: 0 1px 3px rgba(0, 0, 0, 0.28);
    --pudding-chat-shadow-md: 0 3px 12px rgba(0, 0, 0, 0.32);
    --pudding-chat-shadow-hover: 0 6px 18px rgba(0, 0, 0, 0.38);
    /* 状态色阶深色：running=浅蓝（同 --pudding-chat-accent 深色）/ waiting / success / error 随 §3 状态色 */
    --pudding-status-running: #91b3ff;
    --pudding-status-waiting: #f0c36a;
    --pudding-status-warning: #f0c36a;
    --pudding-status-success: #75d6a4;
    --pudding-status-error: #ff9e99;
    /* 代码块深底（P0-3：深色下与聊天表面拉开一档） */
    --pudding-chat-code-bg: #0d1117;
    /* 工具 IN/OUT 参数面板：深色维持终端深底（与浅色策略成对，见 Light 块注释） */
    --pudding-toolcard-bg: #0d1117;
    --pudding-toolcard-fg: #e6edf3;
    --pudding-toolcard-border: #3a4557;

    /* Pudding Admin Tokens — Dark（IMG01：与 Chat 同一套 §3 中性层级） */
    --pudding-admin-bg: #11151b;
    --pudding-admin-bg-subtle: #161b23;
    --pudding-admin-surface: #1a2029;
    --pudding-admin-surface-muted: #242c37;
    --pudding-admin-border: #445166;
    --pudding-admin-border-strong: #56637a;
    --pudding-admin-text: #e8edf4;
    --pudding-admin-text-muted: #a8b5c7;
    --pudding-admin-accent: #91b3ff;
    --pudding-admin-accent-soft: #243657;
    --pudding-admin-success: #75d6a4;
    --pudding-admin-warning: #f0c36a;
    --pudding-admin-danger: #ff9e99;
    --pudding-admin-shadow-low: 0 1px 8px rgba(0, 0, 0, 0.28);
  }

  @media (prefers-reduced-motion: reduce) {
    *, *::before, *::after {
      animation-duration: 0.01ms !important;
      animation-iteration-count: 1 !important;
      transition-duration: 0.01ms !important;
    }

    .ant-pro-layout .ant-pro-layout-content,
    .ant-pro-page-container {
      animation: none;
      opacity: 1;
    }

    .ant-pro-global-header-logo img,
    .ant-pro-top-nav-header-logo img,
    .ant-pro-sider-logo img,
    .ant-pro-layout-logo img {
      animation: none;
    }
  }
`;
