// ── ProviderBalanceIndicator：LLM 服务商余额指示器 ────

import { Popover } from 'antd';
import React from 'react';
import { useChatStyles } from '../styles';

interface ProviderBalanceIndicatorProps {
  provider?: string;
  balance?: number;
  currency?: string;
  /** 悬浮卡片明细行：赠送额（缺失则省略该段）。 */
  grantedBalance?: number;
  /** 悬浮卡片明细行：充值额（缺失则省略该段）。 */
  toppedUpBalance?: number;
  /** 上游查询时间（ISO 字符串），悬浮卡片展示本地化短格式；非法/缺失省略。 */
  queriedAt?: string;
  /** 悬浮卡片底部补充文案（查询失败原因 / 刷新提示等），始终为最后一行。 */
  detail?: string;
  /** 刷新请求在途：品牌图标轻微旋转反馈。 */
  loading?: boolean;
  /** 查询失败态：金额文本使用警示色（仅在余额不可用时生效）。 */
  error?: boolean;
}

/** 金额千分位格式化：保留 2 位小数，整数部分每 3 位加逗号（负号不受影响）。 */
const formatAmount = (value: number): string => {
  const [intPart, decPart] = value.toFixed(2).split('.');
  const grouped = intPart.replace(/\B(?=(\d{3})+(?!\d))/g, ',');
  return `${grouped}.${decPart}`;
};

/** 本地化短格式（YYYY-MM-DD HH:mm）；无效输入返回 undefined（优雅省略该段）。 */
const formatQueryTime = (iso: string | undefined): string | undefined => {
  if (!iso) return undefined;
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return undefined;
  const pad = (n: number): string => String(n).padStart(2, '0');
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(
    date.getDate(),
  )} ${pad(date.getHours())}:${pad(date.getMinutes())}`;
};

/** DeepSeek 风格 SVG 图标（占位） */
const ProviderIcon: React.FC<{ provider: string }> = ({ provider }) => {
  if (provider === 'deepseek' || provider === 'DeepSeek') {
    return (
      <svg
        width="14"
        height="14"
        viewBox="0 0 24 24"
        fill="none"
        style={{ verticalAlign: 'middle' }}
      >
        <circle cx="12" cy="12" r="10" fill="#4F46E5" />
        <path
          d="M7 12l3.5 3.5L17 9"
          stroke="#fff"
          strokeWidth="2"
          strokeLinecap="round"
          strokeLinejoin="round"
        />
      </svg>
    );
  }
  if (provider === 'mimo' || provider === 'Mimo') {
    return (
      <svg
        width="14"
        height="14"
        viewBox="0 0 24 24"
        fill="none"
        style={{ verticalAlign: 'middle' }}
      >
        <rect x="2" y="2" width="20" height="20" rx="4" fill="#f59e0b" />
        <text
          x="12"
          y="16"
          textAnchor="middle"
          fontSize="12"
          fontWeight="bold"
          fill="#fff"
        >
          M
        </text>
      </svg>
    );
  }
  return (
    <svg
      width="14"
      height="14"
      viewBox="0 0 24 24"
      fill="none"
      style={{ verticalAlign: 'middle' }}
    >
      <circle cx="12" cy="12" r="10" fill="var(--earth-brown)" opacity={0.6} />
      <text
        x="12"
        y="16"
        textAnchor="middle"
        fontSize="10"
        fontWeight="bold"
        fill="#fff"
      >
        ?
      </text>
    </svg>
  );
};

const ProviderBalanceIndicator: React.FC<ProviderBalanceIndicatorProps> = ({
  provider = 'DeepSeek',
  balance,
  currency = '¥',
  grantedBalance,
  toppedUpBalance,
  queriedAt,
  detail,
  loading = false,
  error = false,
}) => {
  const { styles } = useChatStyles();

  // 失败态：仅当明确传入 error 且当前无可用余额时警示，正常降级不误报
  const isError = error && balance == null;

  const display = balance != null ? `${currency}${formatAmount(balance)}` : '—';

  const labelClassName = isError
    ? `${styles.balanceBadgeLabel} ${styles.balanceBadgeLabelError}`
    : styles.balanceBadgeLabel;

  const amountClassName = isError
    ? `${styles.balanceCardAmount} ${styles.balanceCardAmountError}`
    : styles.balanceCardAmount;

  // 明细行：赠送/充值/查询时间，缺哪段省哪段
  const metaRows: { label: string; value: string }[] = [];
  if (grantedBalance != null) {
    metaRows.push({
      label: '赠送余额',
      value: `${currency}${formatAmount(grantedBalance)}`,
    });
  }
  if (toppedUpBalance != null) {
    metaRows.push({
      label: '充值余额',
      value: `${currency}${formatAmount(toppedUpBalance)}`,
    });
  }
  const queryTime = formatQueryTime(queriedAt);
  if (queryTime) {
    metaRows.push({ label: '查询时间', value: queryTime });
  }

  // 悬浮富卡片（HoverCard）：参考 DeepSeek 余额卡片布局，配色走 --pudding-chat-* 主题变量
  const hoverCard = (
    <div className={styles.balanceCard}>
      <div className={styles.balanceCardHeader}>
        <ProviderIcon provider={provider} />
        <span>{`${provider} 余额：${display}`}</span>
      </div>
      <div className={amountClassName}>{display}</div>
      {metaRows.length > 0 && (
        <>
          <div className={styles.balanceCardDivider} />
          <div className={styles.balanceCardMeta}>
            {metaRows.map((row) => (
              <div key={row.label} className={styles.balanceCardMetaRow}>
                <span className={styles.balanceCardMetaLabel}>{row.label}</span>
                <span className={styles.balanceCardMetaValue}>{row.value}</span>
              </div>
            ))}
          </div>
        </>
      )}
      {detail && <div className={styles.balanceCardFooter}>{detail}</div>}
    </div>
  );

  return (
    <Popover
      trigger="hover"
      placement="bottom"
      arrow={false}
      styles={{
        body: {
          padding: 0,
          background: 'transparent',
          boxShadow: 'none',
          borderRadius: 'var(--pudding-chat-radius-md)',
        },
      }}
      content={hoverCard}
    >
      <span className={styles.balanceBadgeGroup}>
        <span className={loading ? styles.balanceBadgeSpin : undefined}>
          <ProviderIcon provider={provider} />
        </span>
        <span className={labelClassName}>{display}</span>
      </span>
    </Popover>
  );
};

export default ProviderBalanceIndicator;
