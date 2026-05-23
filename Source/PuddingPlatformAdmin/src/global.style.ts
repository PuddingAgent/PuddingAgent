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
