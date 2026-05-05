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
`;
