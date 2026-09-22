/**
 * PuddingDataTable styles
 */

export default {
  tableSurface: {
    margin: '0 28px 24px',
    background: 'var(--pudding-admin-surface)',
    borderRadius: 'var(--pudding-admin-radius)',
    border: '1px solid var(--pudding-admin-border)',
    // 用 `clip` 而非 `hidden`：`hidden` 会创建滚动容器，使表头 `position: sticky`
    // 以本元素为滚动容器而失效（本元素并不滚动）；`clip` 保留圆角裁切且不创建滚动容器。
    overflow: 'clip',
  },
};
