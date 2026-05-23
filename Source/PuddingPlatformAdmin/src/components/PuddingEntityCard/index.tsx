/**
 * PuddingEntityCard — 实体卡片辅助视图
 *
 * 移动端和少量实体摘要辅助视图。
 * 桌面默认不把卡片作为首选。
 * 不使用大面积 blur 或 glow。
 */
import React from 'react';
import { Card } from 'antd';
import classNames from 'classnames';
import styles from './styles';

export interface PuddingEntityCardProps {
  title: React.ReactNode;
  description?: React.ReactNode;
  status?: React.ReactNode;
  meta?: Array<{ label: React.ReactNode; value: React.ReactNode }>;
  actions?: React.ReactNode;
  loading?: boolean;
  className?: string;
}

export const PuddingEntityCard: React.FC<PuddingEntityCardProps> = ({
  title,
  description,
  status,
  meta,
  actions,
  loading,
  className,
}) => (
  <Card
    className={classNames(styles.card, className)}
    loading={loading}
    bordered={false}
  >
    <div className={styles.header}>
      <div className={styles.titleRow}>
        <span className={styles.title}>{title}</span>
        {status && <span className={styles.status}>{status}</span>}
      </div>
      {description && <p className={styles.description}>{description}</p>}
    </div>
    {meta && meta.length > 0 && (
      <div className={styles.metaGrid}>
        {meta.map((item, index) => (
          // biome-ignore lint/suspicious/noArrayIndexKey: meta items are static label/value pairs, no stable id
          <div key={index} className={styles.metaItem}>
            <span className={styles.metaLabel}>{item.label}</span>
            <span className={styles.metaValue}>{item.value}</span>
          </div>
        ))}
      </div>
    )}
    {actions && <div className={styles.actions}>{actions}</div>}
  </Card>
);

export default PuddingEntityCard;
