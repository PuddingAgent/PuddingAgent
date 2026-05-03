import { injectGlobal } from 'antd-style';

injectGlobal`
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
      transform: scale(1.035);
    }
    100% {
      transform: scale(1);
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
