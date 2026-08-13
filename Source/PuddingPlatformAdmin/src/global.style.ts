import { injectGlobal } from 'antd-style';

injectGlobal`
  :root {
    --misty-blue: #d4e0f0;
    --warm-beige: #f5f0e8;
    --soft-white: #fafaf7;
    --pale-yellow-sunlight: #fef9e7;
    --earth-brown: #5c4a3a;
    --sky-soft: #e6f0fa;
    --desaturated-green: #7a9a7e;
    --text-primary: #1a1a2e;
    --text-secondary: var(--earth-brown);
    --accent-purple: #7c3aed;
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

    /* Runtime 语义色 */
    --runtime-bg: #F5F0E8;
    --runtime-bg-deep: #EDE5D9;
    --glass-surface: rgba(250,250,247,0.72);
    --glass-border: rgba(124,58,237,0.18);
    --neural-line: rgba(124,58,237,0.18);
    --memory-glow: #A78BFA;
    --tool-signal: #22D3EE;
    --success-signal: #22C55E;
    --warning-signal: #F97316;
    --error-signal: #EF4444;
    --text-muted: #5C4A3A;

    /* Pudding Chat Tokens — Light */
    --pudding-chat-bg: #f5f0e8;
    --pudding-chat-sidebar-bg: rgba(250, 250, 247, 0.7);
    --pudding-chat-header-bg: rgba(250, 250, 247, 0.7);
    --pudding-chat-surface: #fafaf7;
    --pudding-chat-surface-muted: #f2eee7;
    --pudding-chat-border: rgba(92, 74, 58, 0.12);
    --pudding-chat-border-strong: rgba(92, 74, 58, 0.2);
    --pudding-chat-text: #1a1a2e;
    --pudding-chat-text-muted: #5c4a3a;
    --pudding-chat-text-subtle: #8c7a6a;
    --pudding-chat-accent: #7c3aed;
    --pudding-chat-accent-soft: rgba(124, 58, 237, 0.08);
    --pudding-chat-danger: #b42318;
    --pudding-chat-success: #4f7f58;
    --pudding-chat-shadow: 0 8px 32px rgba(0, 0, 0, 0.12);

    /* Pudding Chat Design Tokens — Light（P0-4 附加：仅新增变量，不改既有值） */
    --pudding-chat-radius-sm: 6px;
    --pudding-chat-radius-md: 10px;
    --pudding-chat-radius-lg: 14px;
    --pudding-chat-shadow-sm: 0 1px 3px rgba(0, 0, 0, 0.04);
    --pudding-chat-shadow-md: 0 3px 12px rgba(63, 38, 95, 0.04);
    --pudding-chat-shadow-hover: 0 6px 18px rgba(63, 38, 95, 0.065);
    /* 状态色阶（§4.0 总表）：running=强调紫 / waiting=琥珀（与队列 #b36b1e 同族）/ success / error */
    --pudding-status-running: var(--accent-purple);
    --pudding-status-waiting: #d97706;
    --pudding-status-warning: #d97706;
    --pudding-status-success: #22c55e;
    --pudding-status-error: #ef4444;
    /* 代码块深底（P0-3：对齐 D4 对比度策略，浅色下代码表面独立加深一档） */
    --pudding-chat-code-bg: #1e2430;

    /* Pudding Admin Tokens — Light */
    --pudding-admin-bg: #f5f0e8;
    --pudding-admin-bg-subtle: #ede5d9;
    --pudding-admin-surface: #fafaf7;
    --pudding-admin-surface-muted: #f2eee7;
    --pudding-admin-border: rgba(92, 74, 58, 0.12);
    --pudding-admin-border-strong: rgba(92, 74, 58, 0.2);
    --pudding-admin-text: #1a1a2e;
    --pudding-admin-text-muted: #5c4a3a;
    --pudding-admin-accent: #7c3aed;
    --pudding-admin-accent-soft: rgba(124, 58, 237, 0.08);
    --pudding-admin-success: #4f7f58;
    --pudding-admin-warning: #b7791f;
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

  @keyframes neuralPulse {
    0%, 100% { box-shadow: 0 0 4px rgba(167,139,250,0.12); }
    50% { box-shadow: 0 0 12px rgba(167,139,250,0.24); }
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
    0% { box-shadow: 0 0 20px rgba(167,139,250,0.15); }
    100% { box-shadow: 0 0 0px rgba(167,139,250,0); }
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
    --warm-beige: var(--pudding-chat-bg);
    --soft-white: var(--pudding-chat-surface);
    --pale-yellow-sunlight: #3a2f1d;
    --earth-brown: var(--pudding-chat-text-muted);
    --text-secondary: var(--pudding-chat-text-muted);

    --runtime-bg: #070A12;
    --runtime-bg-deep: #0B1020;
    --glass-surface: rgba(17,24,39,0.68);
    --glass-border: rgba(167,139,250,0.22);
    --neural-line: rgba(167,139,250,0.24);
    --memory-glow: #A78BFA;
    --tool-signal: #22D3EE;
    --success-signal: #4ADE80;
    --warning-signal: #FB923C;
    --error-signal: #F87171;
    --text-primary: #E6EAF2;
    --text-muted: #94A3B8;

    /* Pudding Chat Tokens — Dark */
    --pudding-chat-bg: #11100d;
    --pudding-chat-sidebar-bg: rgba(24, 22, 18, 0.92);
    --pudding-chat-header-bg: rgba(24, 22, 18, 0.88);
    --pudding-chat-surface: #1c1a16;
    --pudding-chat-surface-muted: #26231d;
    --pudding-chat-border: rgba(224, 211, 190, 0.12);
    --pudding-chat-border-strong: rgba(224, 211, 190, 0.22);
    --pudding-chat-text: #f4efe7;
    --pudding-chat-text-muted: #d2c5b5;
    --pudding-chat-text-subtle: #a99c8d;
    --pudding-chat-accent: #a78bfa;
    --pudding-chat-accent-soft: rgba(167, 139, 250, 0.14);
    --pudding-chat-danger: #fca5a5;
    --pudding-chat-success: #86efac;
    --pudding-chat-shadow: 0 8px 32px rgba(0, 0, 0, 0.4);

    /* Pudding Chat Design Tokens — Dark（P0-4 附加：与浅色段一一对应；半径主题无关，为保持深色段自包含重复声明） */
    --pudding-chat-radius-sm: 6px;
    --pudding-chat-radius-md: 10px;
    --pudding-chat-radius-lg: 14px;
    --pudding-chat-shadow-sm: 0 1px 3px rgba(0, 0, 0, 0.28);
    --pudding-chat-shadow-md: 0 3px 12px rgba(0, 0, 0, 0.32);
    --pudding-chat-shadow-hover: 0 6px 18px rgba(0, 0, 0, 0.38);
    /* 状态色阶深色：running=浅紫（同 --pudding-chat-accent 深色）/ waiting=琥珀提亮档 / success=浅绿（同 --pudding-chat-success 深色）/ error=浅红（同 --pudding-chat-danger 深色） */
    --pudding-status-running: #a78bfa;
    --pudding-status-waiting: #f59e0b;
    --pudding-status-warning: #f59e0b;
    --pudding-status-success: #86efac;
    --pudding-status-error: #fca5a5;
    /* 代码块深底（P0-3：深色下与聊天表面拉开一档） */
    --pudding-chat-code-bg: #0d1117;

    /* Pudding Admin Tokens — Dark */
    --pudding-admin-bg: #0b1020;
    --pudding-admin-bg-subtle: #111827;
    --pudding-admin-surface: #172033;
    --pudding-admin-surface-muted: #1f2937;
    --pudding-admin-border: rgba(167, 139, 250, 0.18);
    --pudding-admin-border-strong: rgba(167, 139, 250, 0.28);
    --pudding-admin-text: #f8fafc;
    --pudding-admin-text-muted: #cbd5e1;
    --pudding-admin-accent: #a78bfa;
    --pudding-admin-accent-soft: rgba(167, 139, 250, 0.12);
    --pudding-admin-success: #86efac;
    --pudding-admin-warning: #facc15;
    --pudding-admin-danger: #fca5a5;
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
