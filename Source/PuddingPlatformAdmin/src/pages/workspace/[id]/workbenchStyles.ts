import { createStyles } from 'antd-style';

/**
 * 工作区 Agent 编辑工作台专用样式。
 *
 * 设计依据：Docs/Features/Agent-Settings-Redesign-2026-10-01.md 第 3 / 8 节。
 *
 * 刻意与 `agent-template-settings/styles.ts` 分离：全局 Agent 模板抽屉复用同一套
 * 184px 导航布局，本文件只服务工作区实例编辑，避免改工作区外观时影响模板编辑
 * （见设计文档第 9 节「修改前检查其他调用方」）。
 */
export const useWorkspaceAgentStyles = createStyles(({ token, css }) => ({
  // 近全屏容器：宽屏留出少量背景上下文，窄屏铺满。
  drawer: css`
    .ant-drawer-content-wrapper {
      box-shadow: -8px 0 32px rgba(15, 23, 42, 0.16);
    }

    .ant-drawer-content {
      height: 100%;
    }

    .ant-drawer-header {
      display: none;
    }

    .ant-drawer-body {
      padding: 0;
      overflow: hidden;
      display: flex;
      flex-direction: column;
      min-height: 0;
    }

    /* ≥1440px：接近全屏但保留右侧留白，避免整屏都是表单 */
    @media (min-width: 1440px) {
      .ant-drawer-content-wrapper {
        box-shadow: none;
      }
    }
  `,

  workbench: css`
    display: flex;
    flex-direction: column;
    min-height: 0;
    height: 100%;
    background: ${token.colorBgLayout};
  `,

  // ── 顶部身份栏 ──────────────────────────────────────────────
  header: css`
    flex: 0 0 auto;
    display: flex;
    align-items: center;
    gap: 16px;
    padding: 12px 20px;
    min-height: 72px;
    background: ${token.colorBgContainer};
    border-bottom: 1px solid ${token.colorBorderSecondary};

    @media (max-width: 767px) {
      flex-wrap: wrap;
      gap: 8px 12px;
      padding: 10px 12px;
      min-height: 0;
    }
  `,

  headerBack: css`
    flex: 0 0 auto;
    padding-inline: 8px;
  `,

  identity: css`
    display: flex;
    align-items: center;
    gap: 12px;
    min-width: 0;
    flex: 1 1 auto;
  `,

  identityCopy: css`
    min-width: 0;
  `,

  identityName: css`
    font-size: 18px;
    font-weight: 600;
    color: ${token.colorText};
    line-height: 1.35;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  `,

  identityMeta: css`
    font-size: 12px;
    color: ${token.colorTextSecondary};
    line-height: 1.5;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  `,

  headerActions: css`
    flex: 0 0 auto;
    display: flex;
    align-items: center;
    gap: 16px;

    @media (max-width: 767px) {
      width: 100%;
      justify-content: space-between;
      gap: 8px;
    }
  `,

  saveStatus: css`
    display: inline-flex;
    align-items: center;
    gap: 6px;
    font-size: 12px;
    color: ${token.colorTextSecondary};
    white-space: nowrap;
  `,

  saveStatusDirty: css`
    color: ${token.colorWarningText};
  `,

  saveStatusError: css`
    color: ${token.colorErrorText};
  `,

  statusDot: css`
    width: 6px;
    height: 6px;
    border-radius: 50%;
    background: ${token.colorSuccess};
    flex-shrink: 0;

    &.dot-dirty {
      background: ${token.colorWarning};
    }
    &.dot-error {
      background: ${token.colorError};
    }
  `,

  // ── 主体三段布局 ────────────────────────────────────────────
  body: css`
    flex: 1 1 auto;
    min-height: 0;
    display: grid;
    grid-template-columns: 184px minmax(0, 1fr);
    overflow: hidden;
    background: ${token.colorBgContainer};

    @media (max-width: 1023px) {
      grid-template-columns: 1fr;
      grid-template-rows: auto minmax(0, 1fr);
    }
  `,

  sectionNav: css`
    height: 100%;
    min-height: 0;
    overflow-y: auto;
    padding: 16px 12px;
    background: ${token.colorFillQuaternary};
    border-right: 1px solid ${token.colorBorderSecondary};

    @media (max-width: 1023px) {
      height: auto;
      overflow-y: visible;
      overflow-x: auto;
      display: flex;
      gap: 4px;
      padding: 8px 12px;
      border-right: none;
      border-bottom: 1px solid ${token.colorBorderSecondary};
    }
  `,

  pane: css`
    min-width: 0;
    min-height: 0;
    height: 100%;
    display: flex;
    flex-direction: column;
    overflow: hidden;
  `,

  // 普通表单分区：单列滚动，最大宽度约束，不把字段拉成长条
  formScroll: css`
    flex: 1 1 auto;
    min-height: 0;
    overflow-y: auto;
    overflow-x: hidden;
    padding: 20px 24px 32px;

    @media (max-width: 767px) {
      padding: 16px 12px 24px;
    }
  `,

  formColumn: css`
    max-width: 880px;
  `,

  sectionHeading: css`
    display: flex;
    align-items: baseline;
    justify-content: space-between;
    gap: 12px;
    margin-bottom: 16px;
  `,

  sectionTitle: css`
    font-size: 16px;
    font-weight: 600;
    color: ${token.colorText};
  `,

  sectionHint: css`
    font-size: 12px;
    color: ${token.colorTextSecondary};
  `,

  loadingPlaceholder: css`
    flex: 1 1 auto;
    display: flex;
    align-items: center;
    justify-content: center;
    gap: 8px;
    color: ${token.colorTextSecondary};
    font-size: 13px;
  `,

  // ── 文档目录 ────────────────────────────────────────────────
  documentNav: css`
    min-width: 0;
    min-height: 0;
    height: 100%;
    overflow-y: auto;
    padding: 16px 12px;
    background: ${token.colorFillQuaternary};
    border-right: 1px solid ${token.colorBorderSecondary};

    @media (max-width: 1023px) {
      display: none;
    }
  `,

  documentNavHeading: css`
    padding: 0 8px 8px;
  `,

  documentNavTitle: css`
    font-size: 13px;
    font-weight: 600;
    color: ${token.colorText};
  `,

  documentNavSubtitle: css`
    font-size: 12px;
    color: ${token.colorTextSecondary};
    margin-top: 2px;
  `,

  documentGroupLabel: css`
    margin: 12px 0 4px;
    padding: 0 8px;
    font-size: 11px;
    letter-spacing: 0.04em;
    color: ${token.colorTextTertiary};
  `,

  documentItem: css`
    display: flex;
    align-items: flex-start;
    gap: 8px;
    width: 100%;
    padding: 8px 10px;
    margin-bottom: 2px;
    border: none;
    border-radius: 8px;
    background: none;
    text-align: left;
    cursor: pointer;
    line-height: 1.4;
    transition: background 0.15s;

    &:hover {
      background: ${token.colorBgTextHover};
    }

    &:focus-visible {
      outline: 2px solid ${token.colorPrimaryBorder};
      outline-offset: -2px;
    }
  `,

  documentItemActive: css`
    background: ${token.colorPrimaryBg};

    &:hover {
      background: ${token.colorPrimaryBgHover};
    }
  `,

  documentItemError: css`
    color: ${token.colorError};
  `,

  documentItemCopy: css`
    min-width: 0;
    flex: 1 1 auto;
  `,

  documentItemLabel: css`
    font-size: 13px;
    color: ${token.colorText};
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  `,

  documentItemLabelActive: css`
    color: ${token.colorPrimary};
    font-weight: 600;
  `,

  documentItemTag: css`
    margin-top: 2px;
    font-size: 11px;
    color: ${token.colorTextTertiary};
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  `,

  documentItemTagDirty: css`
    color: ${token.colorWarningText};
  `,

  documentItemMark: css`
    flex: 0 0 auto;
    width: 6px;
    height: 6px;
    margin-top: 6px;
    border-radius: 50%;
    background: ${token.colorWarning};

    &.mark-error {
      background: ${token.colorError};
    }
  `,

  // 窄屏文档选择器（<1024px 用 Select 取代目录）
  documentPicker: css`
    display: none;
    flex: 0 0 auto;
    gap: 8px;
    align-items: center;
    padding: 10px 16px;
    border-bottom: 1px solid ${token.colorBorderSecondary};
    background: ${token.colorBgContainer};

    @media (max-width: 1023px) {
      display: flex;
    }

    @media (max-width: 767px) {
      padding: 8px 12px;
      flex-wrap: wrap;
    }
  `,

  // ── 整屏提示（校验错误摘要 / 保存失败）───────────────────────
  bannerStack: css`
    flex: 0 0 auto;
    padding: 12px 20px 0;

    @media (max-width: 767px) {
      padding: 10px 12px 0;
    }
  `,

  // ── 编辑器 ──────────────────────────────────────────────────
  editor: css`
    flex: 1 1 auto;
    min-height: 0;
    display: flex;
    flex-direction: column;
    padding: 16px 24px 0;
    background: ${token.colorBgContainer};

    @media (max-width: 767px) {
      padding: 12px 12px 0;
    }
  `,

  editorHeader: css`
    flex: 0 0 auto;
    display: flex;
    align-items: flex-start;
    justify-content: space-between;
    gap: 16px;
    flex-wrap: wrap;
  `,

  editorTitle: css`
    font-size: 16px;
    font-weight: 600;
    color: ${token.colorText};
    display: flex;
    align-items: center;
    gap: 8px;
  `,

  editorDescription: css`
    margin-top: 2px;
    font-size: 12px;
    color: ${token.colorTextSecondary};
    max-width: 720px;
  `,

  editorToolbar: css`
    display: flex;
    align-items: center;
    gap: 8px;
    flex: 0 0 auto;
  `,

  findBar: css`
    flex: 0 0 auto;
    display: flex;
    align-items: center;
    gap: 8px;
    margin-top: 12px;
    padding: 8px 10px;
    border: 1px solid ${token.colorBorderSecondary};
    border-radius: 8px;
    background: ${token.colorFillQuaternary};
  `,

  findCount: css`
    font-size: 12px;
    color: ${token.colorTextSecondary};
    min-width: 56px;
    text-align: center;
  `,

  editorSurface: css`
    flex: 1 1 auto;
    min-height: 0;
    display: flex;
    flex-direction: column;
    margin: 12px 0;
    border: 1px solid ${token.colorBorderSecondary};
    border-radius: 8px;
    background: ${token.colorBgContainer};
    overflow: hidden;

    &:focus-within {
      border-color: ${token.colorPrimaryBorder};
      box-shadow: 0 0 0 2px ${token.colorPrimaryBg};
    }
  `,

  textarea: css`
    flex: 1 1 auto;
    min-height: 0;
    width: 100%;
    height: 100%;
    padding: 16px 18px;
    border: none;
    outline: none;
    resize: none;
    background: transparent;
    color: ${token.colorText};
    font-size: 14px;
    line-height: 1.7;
    font-family: ${token.fontFamily};

    &::placeholder {
      color: ${token.colorTextPlaceholder};
    }

    &:disabled {
      background: ${token.colorFillQuaternary};
      color: ${token.colorTextSecondary};
    }

    @media (max-width: 767px) {
      padding: 12px;
      font-size: 15px;
    }
  `,

  // 带行内高亮底层的包装层（查找命中标记）
  highlightWrap: css`
    position: relative;
    flex: 1 1 auto;
    min-height: 0;
    display: flex;
  `,

  highlightLayer: css`
    position: absolute;
    inset: 0;
    margin: 0;
    padding: 16px 18px;
    border: none;
    overflow: hidden;
    white-space: pre-wrap;
    overflow-wrap: break-word;
    word-break: break-word;
    color: transparent;
    font-size: 14px;
    line-height: 1.7;
    font-family: ${token.fontFamily};
    pointer-events: none;

    mark {
      color: transparent;
      background: ${token.colorWarningBg};
      border-radius: 2px;
    }

    mark[data-current='true'] {
      background: ${token.colorWarningBorder};
    }

    @media (max-width: 767px) {
      padding: 12px;
      font-size: 15px;
    }
  `,

  previewScroll: css`
    flex: 1 1 auto;
    min-height: 0;
    overflow-y: auto;
    padding: 16px 18px;
    font-size: 14px;
    line-height: 1.75;
    color: ${token.colorText};
    overflow-wrap: break-word;

    h1,
    h2,
    h3,
    h4 {
      font-weight: 600;
      line-height: 1.4;
      margin: 16px 0 8px;
    }

    h1 {
      font-size: 20px;
    }
    h2 {
      font-size: 17px;
    }
    h3 {
      font-size: 15px;
    }

    p,
    ul,
    ol,
    blockquote,
    table {
      margin: 8px 0;
    }

    ul,
    ol {
      padding-left: 22px;
    }

    code {
      padding: 1px 5px;
      border-radius: 4px;
      background: ${token.colorFillTertiary};
      font-family: ${token.fontFamilyCode};
      font-size: 0.92em;
    }

    pre {
      padding: 12px 14px;
      border-radius: 8px;
      background: ${token.colorFillQuaternary};
      overflow-x: auto;

      code {
        padding: 0;
        background: none;
      }
    }

    blockquote {
      padding-left: 12px;
      border-left: 3px solid ${token.colorBorder};
      color: ${token.colorTextSecondary};
    }

    table {
      border-collapse: collapse;
      width: 100%;

      th,
      td {
        border: 1px solid ${token.colorBorderSecondary};
        padding: 6px 10px;
        text-align: left;
      }
    }

    a {
      color: ${token.colorLink};
    }
  `,

  emptyPreview: css`
    color: ${token.colorTextTertiary};
    font-size: 13px;
  `,

  editorFooter: css`
    flex: 0 0 auto;
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 12px;
    flex-wrap: wrap;
  `,

  editorFooterMeta: css`
    font-size: 12px;
    color: ${token.colorTextSecondary};
  `,

  metaDirty: css`
    color: ${token.colorWarningText};
  `,

  variableHint: css`
    flex: 0 0 auto;
    display: flex;
    align-items: center;
    gap: 8px;
    flex-wrap: wrap;
    margin-bottom: 12px;
    padding: 8px 10px;
    border-radius: 8px;
    background: ${token.colorFillQuaternary};
    font-size: 12px;
    color: ${token.colorTextSecondary};
  `,

  footer: css`
    flex: 0 0 auto;
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 12px;
    flex-wrap: wrap;
    padding: 10px 20px;
    min-height: 40px;
    border-top: 1px solid ${token.colorBorderSecondary};
    background: ${token.colorFillQuaternary};
    font-size: 12px;
    color: ${token.colorTextSecondary};

    @media (max-width: 767px) {
      padding: 10px 12px;
    }
  `,

  selectPopup: css`
    z-index: 1160;
  `,

  selectOptionRow: css`
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 12px;
  `,
}));
