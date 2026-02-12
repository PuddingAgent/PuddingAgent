import { createStyles } from 'antd-style';

export const useStyles = createStyles(({ token, css }) => ({
  drawer: css`
    .ant-drawer-content {
      height: 100%;
    }

    .ant-drawer-body {
      padding: 0;
      overflow: hidden;
      display: flex;
      flex-direction: column;
    }
  `,

  drawerAlert: css`
    flex: 0 0 auto;
    margin: 16px 16px 0;
  `,

  settingsForm: css`
    flex: 1 1 auto;
    min-height: 0;

    .ant-form {
      height: 100%;
    }
  `,

  settingsLayout: css`
    display: grid;
    grid-template-columns: 184px minmax(0, 1fr);
    height: 100%;
    min-height: 0;
    overflow: hidden;

    @media (max-width: 768px) {
      grid-template-columns: 1fr;
      grid-template-rows: auto minmax(0, 1fr);
    }
  `,

  settingsNav: css`
    height: 100%;
    min-height: 0;
    overflow-y: auto;
    padding: 16px 12px;
    border-right: 1px solid ${token.colorBorderSecondary};
    background: ${token.colorBgContainer};

    @media (max-width: 768px) {
      height: auto;
      overflow-y: visible;
      overflow-x: auto;
      border-right: none;
      border-bottom: 1px solid ${token.colorBorderSecondary};
      display: flex;
      gap: 4px;
      padding: 8px 12px;
    }
  `,

  navItem: css`
    display: flex;
    align-items: center;
    gap: 6px;
    padding: 8px 12px;
    border-radius: 8px;
    cursor: pointer;
    font-size: 13px;
    color: ${token.colorTextSecondary};
    transition: all 0.15s;
    white-space: nowrap;
    width: 100%;
    text-align: left;
    background: none;
    border: none;
    line-height: 1.4;

    &:hover {
      background: ${token.colorBgTextHover};
      color: ${token.colorText};
    }

    @media (max-width: 768px) {
      padding: 6px 10px;
      font-size: 12px;
      flex-shrink: 0;
      width: auto;
    }
  `,
  navItemActive: css`
    background: ${token.colorPrimaryBg};
    color: ${token.colorPrimary};
    font-weight: 500;
  `,
  navItemError: css`
    color: ${token.colorError};
  `,
  navDot: css`
    width: 6px;
    height: 6px;
    border-radius: 50%;
    flex-shrink: 0;

    &.dot-active {
      background: ${token.colorPrimary};
    }
    &.dot-error {
      background: ${token.colorError};
    }
    &.dot-normal {
      background: transparent;
    }
  `,
  navBadge: css`
    margin-left: auto;
    font-size: 11px;
    line-height: 1;
    padding: 1px 5px;
    border-radius: 8px;
    background: ${token.colorErrorBg};
    color: ${token.colorError};
  `,
  settingsContent: css`
    min-width: 0;
    min-height: 0;
    height: 100%;
    padding: 20px 24px 32px;
    overflow-y: auto;
    overflow-x: hidden;
  `,
  section: css`
    padding-bottom: 28px;
    margin-bottom: 24px;
    border-bottom: 1px solid ${token.colorBorderSecondary};

    &:last-child {
      border-bottom: none;
    }
  `,
  sectionTitle: css`
    margin-bottom: 16px;
    font-size: 15px;
    font-weight: 600;
    color: ${token.colorText};
  `,
  inlineLabel: css`
    font-size: 13px;
    color: ${token.colorTextSecondary};
    margin-bottom: 4px;
  `,
  selectPopup: css`
    left: 0 !important;
    top: calc(100% + 4px) !important;
    z-index: 1160;
  `,
}));
