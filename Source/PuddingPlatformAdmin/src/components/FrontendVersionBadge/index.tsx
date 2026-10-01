import { Tooltip } from 'antd';
import { createStyles } from 'antd-style';
import React from 'react';
import {
  formatFrontendVersionDetail,
  formatFrontendVersionLabel,
  getFrontendBuildInfo,
} from '../../utils/frontendBuild';

/**
 * FrontendVersionBadge — 页面角落的前端版本徽标（AGENTS.md「版本号约定」）。
 *
 * 显示 `v版本号[+dirty] · 短哈希 · 构建时间`，用于判断“眼前这个界面是哪一个前端构建”。
 * 刻意做得很小、低对比（caption 字号/次级色），不抢正文；悬停展开全哈希与时间。
 * 全局挂载于 app.tsx 的 rootContainer，因此对所有前端页面生效。
 */
const useStyles = createStyles(() => ({
  badge: {
    position: 'fixed' as const,
    right: 6,
    bottom: 4,
    zIndex: 1000,
    padding: '1px 6px',
    borderRadius: 6,
    fontSize: 10,
    lineHeight: '16px',
    fontVariantNumeric: 'tabular-nums',
    color: 'var(--pudding-chat-text-caption)',
    background:
      'color-mix(in srgb, var(--pudding-chat-surface) 72%, transparent)',
    border:
      '1px solid color-mix(in srgb, var(--pudding-chat-border) 70%, transparent)',
    opacity: 0.72,
    transition: 'opacity 120ms ease',
    userSelect: 'text' as const,
    '&:hover': { opacity: 1 },
    '@media (forced-colors: active)': {
      background: 'Canvas',
      borderColor: 'CanvasText',
    },
  },
}));

const FrontendVersionBadge: React.FC = () => {
  const { styles } = useStyles();
  const info = getFrontendBuildInfo();
  const label = formatFrontendVersionLabel(info);
  const detail = formatFrontendVersionDetail(info);

  return (
    <Tooltip
      placement="topRight"
      title={<span style={{ whiteSpace: 'pre-line' }}>{detail}</span>}
    >
      {/* 不用 aria-label：span 无 role 时 aria-label 不被支持（biome a11y），
          而且可见文本本身就是可访问名称；悬停详情由 Tooltip 补充。 */}
      <span className={styles.badge} data-testid="frontend-version-badge">
        {label}
      </span>
    </Tooltip>
  );
};

export default FrontendVersionBadge;
